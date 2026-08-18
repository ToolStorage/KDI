using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Kylin.DI
{
    public class UpdateLoopManager : MonoBehaviour
    {
        private static UpdateLoopManager _instance;
        private static bool _applicationQuitting;

        internal static void ResetStatic()
        {
            var staleInstance = _instance;
            _instance = null;
            if (staleInstance != null)
            {
                var staleObject = staleInstance.gameObject;
                staleInstance.enabled = false;
                if (staleObject != null)
                {
                    staleObject.SetActive(false);
                    if (Application.isPlaying)
                        Destroy(staleObject);
                    else
                        DestroyImmediate(staleObject);
                }
            }
            _applicationQuitting = false;
        }

        public static UpdateLoopManager Instance
        {
            get
            {
                KDI.EnsureMainThread();
                if (_applicationQuitting) return null;
                if (_instance != null) return _instance;

                if (Scope.HasActiveTransaction || UnityActivationAttempt.HasActiveAttempt ||
                    ActivationCallbackGuard.IsActive)
                {
                    throw new InvalidOperationException(
                        "[UpdateLoopManager] Instance cannot create the global manager during KDI activation. " +
                        "Obtain and mutate it from the composition boundary; KDI uses a separate internal path " +
                        "for transaction-owned update registrations.");
                }

                return CreateInstance();
            }
        }

        internal static UpdateLoopManager GetOrCreateForKDI()
        {
            KDI.EnsureMainThread();
            if (_applicationQuitting) return null;
            return _instance != null ? _instance : CreateInstance();
        }

        internal static UpdateLoopManager TryGetInstance()
        {
            return _applicationQuitting ? null : _instance;
        }

        internal static bool IsManuallyRegistered(object service)
        {
            if (service == null || _applicationQuitting || _instance == null)
                return false;
            lock (_instance._lock)
                return _instance._manualRegistrationCounts.ContainsKey(service);
        }

        private readonly List<IUpdatable> _updatables = new();
        private readonly List<IFixedUpdatable> _fixedUpdatables = new();
        private readonly List<ILateUpdatable> _lateUpdatables = new();
        private readonly HashSet<IUnityLifetimeMonitorLease> _unityLifetimes =
            new(UnityLifetimeReferenceComparer.Instance);
        private readonly List<IUnityLifetimeMonitorLease> _unityLifetimeSnapshot = new();
        private readonly Queue<Action> _pendingOperations = new();
        // Structural loop lists contain one entry; this tracks how many scopes own it.
        private readonly Dictionary<object, int> _registrationCounts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> _manualRegistrationCounts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, Dictionary<Scope, int>> _scopeRegistrationCounts =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<Scope> _scopeGuardSnapshot = new();
        private readonly HashSet<object> _retired = new(ReferenceEqualityComparer.Instance);
        private readonly object _lock = new();

        private bool _updatablesDirty;
        private bool _fixedUpdatablesDirty;
        private bool _lateUpdatablesDirty;
        private bool _isPollingUnityLifetimes;
        private int _structuralSortDepth;

        public void Register(object service)
        {
            KDI.EnsureMainThread();
            ThrowIfNotCanonicalInstance();
            if (IsNullOrDestroyed(service)) return;
            if (service.GetType().IsValueType)
                throw new ArgumentException(
                    "[UpdateLoopManager] Value-type update services are not supported because each boxing operation " +
                    "creates a different registration identity.", nameof(service));
            if (!IsManagedUpdateService(service))
                throw new ArgumentException(
                    "[UpdateLoopManager] A manually registered service must implement IUpdatable, " +
                    "IFixedUpdatable, or ILateUpdatable.", nameof(service));
            if (Scope.HasActiveTransaction || UnityActivationAttempt.HasActiveAttempt ||
                ActivationCallbackGuard.IsActive)
                throw new InvalidOperationException(
                    "[UpdateLoopManager] Manual registration during KDI activation is not supported. " +
                    "Scoped update services are registered atomically when the activation transaction commits.");
            if (DependencyInjector.IsActivelyInjected(service))
                throw new InvalidOperationException(
                    "[UpdateLoopManager] An injected identity cannot be registered manually. " +
                    "Register it with FromInstance so its update callback and injection lease share one Scope owner.");
            Register(service, null);
        }

        internal void Register(object service, Scope owner)
        {
            KDI.EnsureMainThread();
            if (IsNullOrDestroyed(service)) return;

            lock (_lock)
            {
                if (owner == null && _scopeRegistrationCounts.ContainsKey(service) ||
                    owner != null && _manualRegistrationCounts.ContainsKey(service))
                {
                    throw new InvalidOperationException(
                        "[UpdateLoopManager] Manual and Scope-managed registrations cannot own the same service identity. " +
                        "Let the Scope manage resolved update services, or keep the service exclusively manual.");
                }

                if (_registrationCounts.TryGetValue(service, out var registrationCount))
                {
                    if (registrationCount == int.MaxValue)
                        throw new InvalidOperationException(
                            "[UpdateLoopManager] The registration count exceeded the supported range.");

                    _registrationCounts[service] = registrationCount + 1;
                    AddRegistrationOwner(service, owner);
                    return;
                }

                _registrationCounts.Add(service, 1);
                AddRegistrationOwner(service, owner);
                _retired.Remove(service);
                _pendingOperations.Enqueue(() =>
                {
                    if (service is IUpdatable updatable && !ContainsReference(_updatables, updatable))
                    {
                        _updatables.Add(updatable);
                        _updatablesDirty = true;
                    }
                    if (service is IFixedUpdatable fixedUpdatable && !ContainsReference(_fixedUpdatables, fixedUpdatable))
                    {
                        _fixedUpdatables.Add(fixedUpdatable);
                        _fixedUpdatablesDirty = true;
                    }
                    if (service is ILateUpdatable lateUpdatable && !ContainsReference(_lateUpdatables, lateUpdatable))
                    {
                        _lateUpdatables.Add(lateUpdatable);
                        _lateUpdatablesDirty = true;
                    }
                });
            }
        }

        public void Unregister(object service)
        {
            KDI.EnsureMainThread();
            ThrowIfNotCanonicalInstance();
            if (Scope.HasActiveTransaction || UnityActivationAttempt.HasActiveAttempt ||
                ActivationCallbackGuard.IsActive)
                throw new InvalidOperationException(
                    "[UpdateLoopManager] Manual unregistration during KDI activation is not supported. " +
                    "External player-loop mutations cannot participate in activation rollback.");
            Unregister(service, null);
        }

        internal void Unregister(object service, Scope owner)
        {
            KDI.EnsureMainThread();
            if (service == null) return;

            lock (_lock)
            {
                if (!_registrationCounts.TryGetValue(service, out var registrationCount))
                    return;
                if (!RemoveRegistrationOwner(service, owner))
                {
                    if (owner == null && _scopeRegistrationCounts.ContainsKey(service))
                        throw new InvalidOperationException(
                            "[UpdateLoopManager] A Scope-managed registration cannot be removed through the public API. " +
                            "Dispose its owning Scope instead.");
                    return;
                }

                if (registrationCount > 1)
                {
                    _registrationCounts[service] = registrationCount - 1;
                    return;
                }

                _registrationCounts.Remove(service);
                _manualRegistrationCounts.Remove(service);
                _scopeRegistrationCounts.Remove(service);

                // Retire synchronously. A structural removal may wait for the next
                // phase boundary, but no later callback may observe this object.
                _retired.Add(service);
                _pendingOperations.Enqueue(() =>
                {
                    if (service is IUpdatable updatable)
                        RemoveReference(_updatables, updatable);
                    if (service is IFixedUpdatable fixedUpdatable)
                        RemoveReference(_fixedUpdatables, fixedUpdatable);
                    if (service is ILateUpdatable lateUpdatable)
                        RemoveReference(_lateUpdatables, lateUpdatable);
                    _retired.Remove(service);
                });
            }
        }

        private void AddRegistrationOwner(object service, Scope owner)
        {
            if (owner == null)
            {
                if (_manualRegistrationCounts.TryGetValue(service, out var count))
                    _manualRegistrationCounts[service] = count + 1;
                else
                    _manualRegistrationCounts.Add(service, 1);
                return;
            }

            if (!_scopeRegistrationCounts.TryGetValue(service, out var owners))
            {
                owners = new Dictionary<Scope, int>(ScopeReferenceEqualityComparer.Instance);
                _scopeRegistrationCounts.Add(service, owners);
            }

            if (owners.TryGetValue(owner, out var ownerCount))
                owners[owner] = ownerCount + 1;
            else
                owners.Add(owner, 1);
        }

        private bool RemoveRegistrationOwner(object service, Scope owner)
        {
            if (owner == null)
            {
                if (!_manualRegistrationCounts.TryGetValue(service, out var count))
                    return false;
                if (count > 1)
                    _manualRegistrationCounts[service] = count - 1;
                else
                    _manualRegistrationCounts.Remove(service);
                return true;
            }

            if (!_scopeRegistrationCounts.TryGetValue(service, out var owners) ||
                !owners.TryGetValue(owner, out var ownerCount))
                return false;

            if (ownerCount > 1)
                owners[owner] = ownerCount - 1;
            else
                owners.Remove(owner);
            if (owners.Count == 0)
                _scopeRegistrationCounts.Remove(service);
            return true;
        }

        internal void RegisterUnityLifetime(IUnityLifetimeMonitorLease lifetime)
        {
            KDI.EnsureMainThread();
            if (lifetime == null) return;
            _unityLifetimes.Add(lifetime);
        }

        internal void UnregisterUnityLifetime(IUnityLifetimeMonitorLease lifetime)
        {
            KDI.EnsureMainThread();
            if (lifetime == null) return;
            _unityLifetimes.Remove(lifetime);
        }

        private void Update()
        {
            if (!ReferenceEquals(_instance, this)) return;
            PollUnityLifetimes();
            if (!ReferenceEquals(_instance, this)) return;
            ProcessPendingOperations();
            if (_updatablesDirty)
            {
                SortByPriorityGuarded(_updatables);
                _updatablesDirty = false;
            }

            var deltaTime = Time.deltaTime;
            for (var i = 0; i < _updatables.Count; i++)
            {
                if (!ReferenceEquals(_instance, this)) return;
                var updatable = _updatables[i];
                if (IsNullOrDestroyed(updatable))
                {
                    RetireDestroyed(updatable);
                    continue;
                }
                if (IsRetired(updatable)) continue;
                if (RejectInvalidScopeGraph(updatable)) continue;
                if (!ReferenceEquals(_instance, this)) return;
                try { updatable.KDIUpdate(deltaTime); }
                catch (Exception ex) { Debug.LogError($"[UpdateLoopManager] Error in KDIUpdate: {ex}"); }
            }
        }

        private void PollUnityLifetimes()
        {
            if (_isPollingUnityLifetimes) return;
            _isPollingUnityLifetimes = true;
            // Reuse storage so the Update/FixedUpdate/LateUpdate hot path does not
            // allocate an array every phase. The copy is still required because a
            // destroyed target disposes its Scope and re-enters unregister logic.
            try
            {
                _unityLifetimeSnapshot.Clear();
                _unityLifetimeSnapshot.AddRange(_unityLifetimes);
                for (var i = _unityLifetimeSnapshot.Count - 1; i >= 0; i--)
                {
                    var lifetime = _unityLifetimeSnapshot[i];
                    if (lifetime == null)
                        continue;
                    if (!lifetime.IsTargetDestroyed) continue;

                    // Remove first: invalidation disposes the owning Scope and re-enters
                    // UnregisterUnityLifetime through this same lease.
                    _unityLifetimes.Remove(lifetime);
                    lifetime.HandleMonitoredTargetDestroyed();
                }
            }
            finally
            {
                _unityLifetimeSnapshot.Clear();
                _isPollingUnityLifetimes = false;
            }
        }

        private void FixedUpdate()
        {
            if (!ReferenceEquals(_instance, this)) return;
            PollUnityLifetimes();
            if (!ReferenceEquals(_instance, this)) return;
            ProcessPendingOperations();
            if (_fixedUpdatablesDirty)
            {
                SortByPriorityGuarded(_fixedUpdatables);
                _fixedUpdatablesDirty = false;
            }

            var deltaTime = Time.fixedDeltaTime;
            for (var i = 0; i < _fixedUpdatables.Count; i++)
            {
                if (!ReferenceEquals(_instance, this)) return;
                var updatable = _fixedUpdatables[i];
                if (IsNullOrDestroyed(updatable))
                {
                    RetireDestroyed(updatable);
                    continue;
                }
                if (IsRetired(updatable)) continue;
                if (RejectInvalidScopeGraph(updatable)) continue;
                if (!ReferenceEquals(_instance, this)) return;
                try { updatable.KDIFixedUpdate(deltaTime); }
                catch (Exception ex) { Debug.LogError($"[UpdateLoopManager] Error in KDIFixedUpdate: {ex}"); }
            }
        }

        private void LateUpdate()
        {
            if (!ReferenceEquals(_instance, this)) return;
            PollUnityLifetimes();
            if (!ReferenceEquals(_instance, this)) return;
            ProcessPendingOperations();
            if (_lateUpdatablesDirty)
            {
                SortByPriorityGuarded(_lateUpdatables);
                _lateUpdatablesDirty = false;
            }

            var deltaTime = Time.deltaTime;
            for (var i = 0; i < _lateUpdatables.Count; i++)
            {
                if (!ReferenceEquals(_instance, this)) return;
                var updatable = _lateUpdatables[i];
                if (IsNullOrDestroyed(updatable))
                {
                    RetireDestroyed(updatable);
                    continue;
                }
                if (IsRetired(updatable)) continue;
                if (RejectInvalidScopeGraph(updatable)) continue;
                if (!ReferenceEquals(_instance, this)) return;
                try { updatable.KDILateUpdate(deltaTime); }
                catch (Exception ex) { Debug.LogError($"[UpdateLoopManager] Error in KDILateUpdate: {ex}"); }
            }
        }

        private bool IsRetired(object service)
        {
            lock (_lock) return _retired.Contains(service);
        }

        private void RetireDestroyed(object service)
        {
            if (service == null) return;
            lock (_lock)
            {
                _registrationCounts.Remove(service);
                _manualRegistrationCounts.Remove(service);
                _scopeRegistrationCounts.Remove(service);
                if (!_retired.Add(service)) return;
                _pendingOperations.Enqueue(() =>
                {
                    if (service is IUpdatable updatable)
                        RemoveReference(_updatables, updatable);
                    if (service is IFixedUpdatable fixedUpdatable)
                        RemoveReference(_fixedUpdatables, fixedUpdatable);
                    if (service is ILateUpdatable lateUpdatable)
                        RemoveReference(_lateUpdatables, lateUpdatable);
                    _retired.Remove(service);
                });
            }
        }

        private void ProcessPendingOperations()
        {
            lock (_lock)
            {
                while (_pendingOperations.Count > 0)
                    _pendingOperations.Dequeue()?.Invoke();
            }
        }

        private static bool ContainsReference<T>(List<T> list, T value) where T : class
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], value)) return true;
            }
            return false;
        }

        private static void RemoveReference<T>(List<T> list, T value) where T : class
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i], value))
                    list.RemoveAt(i);
            }
        }

        private static void SortByPriority<T>(List<T> list)
        {
            list.Sort((left, right) =>
            {
                var leftPriority = GetPrioritySafely(left);
                var rightPriority = GetPrioritySafely(right);
                return leftPriority.CompareTo(rightPriority);
            });
        }

        private void SortByPriorityGuarded<T>(List<T> list)
        {
            _structuralSortDepth++;
            try
            {
                SortByPriority(list);
            }
            finally
            {
                _structuralSortDepth--;
                if (_structuralSortDepth == 0 && !ReferenceEquals(_instance, this))
                    ClearStructuralLists();
            }
        }

        private static int GetPrioritySafely(object service)
        {
            if (IsNullOrDestroyed(service) || !(service is IUpdatePriority priority)) return 0;
            try { return priority.UpdatePriority; }
            catch (Exception ex)
            {
                Debug.LogError($"[UpdateLoopManager] Error reading UpdatePriority: {ex}");
                return 0;
            }
        }

        private static bool IsNullOrDestroyed(object service)
        {
            return service == null || service is UnityEngine.Object unityObject && unityObject == null;
        }

        private static bool IsManagedUpdateService(object service)
        {
            return service is IUpdatable || service is IFixedUpdatable || service is ILateUpdatable;
        }

        private void ThrowIfNotCanonicalInstance()
        {
            if (ReferenceEquals(_instance, this)) return;
            throw new InvalidOperationException(
                "[UpdateLoopManager] Public registrations must use UpdateLoopManager.Instance. " +
                "Scene-added or manually constructed manager components are not ownership authorities.");
        }

        public (int update, int fixedUpdate, int lateUpdate) GetRegisteredCount()
        {
            KDI.EnsureMainThread();
            ThrowIfNotCanonicalInstance();
            return (_updatables.Count, _fixedUpdatables.Count, _lateUpdatables.Count);
        }

        [ContextMenu("Print Registered Services")]
        private void PrintRegisteredServices()
        {
            Debug.Log($"[UpdateLoopManager] Update={_updatables.Count}, Fixed={_fixedUpdatables.Count}, Late={_lateUpdatables.Count}");
        }

        private void OnApplicationQuit() => _applicationQuitting = true;

        private void OnDestroy()
        {
            if (!ReferenceEquals(_instance, this)) return;
            _instance = null;
            if (_applicationQuitting) return;

            try
            {
                var replacement = CreateInstance();
                TransferStateTo(replacement);
                Debug.LogWarning(
                    "[KDI] UpdateLoopManager was destroyed unexpectedly. Registrations and Unity lifetime monitoring " +
                    "were transferred to a replacement manager.");
            }
            catch (Exception ex)
            {
                _instance = null;
                Debug.LogError($"[KDI] Failed to replace an unexpectedly destroyed UpdateLoopManager.\n{ex}");
            }
        }

        private static UpdateLoopManager CreateInstance()
        {
            var gameObject = new GameObject("[KDI] UpdateLoopManager");
            var instance = gameObject.AddComponent<UpdateLoopManager>();
            _instance = instance;
            DontDestroyOnLoad(gameObject);
            return instance;
        }

        private void TransferStateTo(UpdateLoopManager replacement)
        {
            // Counts are the authoritative desired state. Pending closures only
            // mutate structural lists and may have been queued from inside an active
            // priority comparison; executing them here would mutate List.Sort's
            // in-flight source. Rebuild the replacement directly from the ledger.
            _pendingOperations.Clear();

            foreach (var pair in _registrationCounts)
            {
                if (replacement._registrationCounts.TryGetValue(pair.Key, out var existing))
                    replacement._registrationCounts[pair.Key] = checked(existing + pair.Value);
                else
                    replacement._registrationCounts.Add(pair.Key, pair.Value);
            }
            foreach (var pair in _manualRegistrationCounts)
            {
                if (replacement._manualRegistrationCounts.TryGetValue(pair.Key, out var existing))
                    replacement._manualRegistrationCounts[pair.Key] = checked(existing + pair.Value);
                else
                    replacement._manualRegistrationCounts.Add(pair.Key, pair.Value);
            }
            foreach (var serviceOwners in _scopeRegistrationCounts)
            {
                if (!replacement._scopeRegistrationCounts.TryGetValue(serviceOwners.Key, out var destinationOwners))
                {
                    destinationOwners = new Dictionary<Scope, int>(ScopeReferenceEqualityComparer.Instance);
                    replacement._scopeRegistrationCounts.Add(serviceOwners.Key, destinationOwners);
                }
                foreach (var owner in serviceOwners.Value)
                {
                    if (destinationOwners.TryGetValue(owner.Key, out var existing))
                        destinationOwners[owner.Key] = checked(existing + owner.Value);
                    else
                        destinationOwners.Add(owner.Key, owner.Value);
                }
            }
            RebuildStructuralLists(replacement);

            foreach (var lifetime in _unityLifetimes)
            {
                replacement._unityLifetimes.Add(lifetime);
                lifetime.RebindMonitor(this, replacement);
            }

            if (_structuralSortDepth == 0)
                ClearStructuralLists();
            _registrationCounts.Clear();
            _manualRegistrationCounts.Clear();
            _scopeRegistrationCounts.Clear();
            _scopeGuardSnapshot.Clear();
            _retired.Clear();
            _unityLifetimes.Clear();
            if (!_isPollingUnityLifetimes)
                _unityLifetimeSnapshot.Clear();
        }

        private static void RebuildStructuralLists(UpdateLoopManager manager)
        {
            manager._updatables.Clear();
            manager._fixedUpdatables.Clear();
            manager._lateUpdatables.Clear();
            foreach (var service in manager._registrationCounts.Keys)
            {
                if (service is IUpdatable updatable)
                    manager._updatables.Add(updatable);
                if (service is IFixedUpdatable fixedUpdatable)
                    manager._fixedUpdatables.Add(fixedUpdatable);
                if (service is ILateUpdatable lateUpdatable)
                    manager._lateUpdatables.Add(lateUpdatable);
            }
            manager._updatablesDirty = manager._updatables.Count > 1;
            manager._fixedUpdatablesDirty = manager._fixedUpdatables.Count > 1;
            manager._lateUpdatablesDirty = manager._lateUpdatables.Count > 1;
        }

        private void ClearStructuralLists()
        {
            _updatables.Clear();
            _fixedUpdatables.Clear();
            _lateUpdatables.Clear();
        }

        private bool RejectInvalidScopeGraph(object service)
        {
            _scopeGuardSnapshot.Clear();
            lock (_lock)
            {
                if (!_scopeRegistrationCounts.TryGetValue(service, out var owners))
                    return false;
                foreach (var owner in owners.Keys)
                    _scopeGuardSnapshot.Add(owner);
            }

            for (var i = 0; i < _scopeGuardSnapshot.Count; i++)
            {
                var owner = _scopeGuardSnapshot[i];
                if (owner != null && owner.CanInvokeUpdateCallback()) continue;

                _scopeGuardSnapshot.Clear();
                return true;
            }

            _scopeGuardSnapshot.Clear();
            return IsRetired(service);
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class UnityLifetimeReferenceComparer : IEqualityComparer<IUnityLifetimeMonitorLease>
        {
            internal static readonly UnityLifetimeReferenceComparer Instance = new();
            public bool Equals(IUnityLifetimeMonitorLease x, IUnityLifetimeMonitorLease y) => ReferenceEquals(x, y);
            public int GetHashCode(IUnityLifetimeMonitorLease obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class ScopeReferenceEqualityComparer : IEqualityComparer<Scope>
        {
            internal static readonly ScopeReferenceEqualityComparer Instance = new();
            public bool Equals(Scope x, Scope y) => ReferenceEquals(x, y);
            public int GetHashCode(Scope obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
