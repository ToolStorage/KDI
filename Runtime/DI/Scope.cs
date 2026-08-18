using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Kylin.DI
{
    public interface IScope : IDisposable
    {
        IScope Parent { get; }
        T Resolve<T>() where T : class;
        object Resolve(Type type);
    }

    public class Scope : IScope
    {
        [ThreadStatic]
        private static ActivationTransaction _currentTransaction;
        private static ConditionalWeakTable<object, ContainerOwnershipMarker> _containerOwnership = new();
        private static readonly List<Scope> _liveScopes = new();
        private static bool _isResetting;

        private readonly Dictionary<Type, Registration> _registrations;
        private readonly Dictionary<Type, object> _instances = new();
        private readonly HashSet<object> _declaredExternalInstances = new(ReferenceEqualityComparer.Instance);
        private readonly IScope _parent;
        private readonly List<IScope> _children = new();
        private readonly List<ActivationRecord> _activationOrder = new();
        private readonly List<ActivationRecord> _deferredUnityLifetimeRecords = new();
        private readonly object _lock = new();
        private readonly string _name;
        private bool _isDisposed;
        private bool _isDisposing;
        private bool _disposeAfterActivation;
        private string _deferredInvalidationReason;

        public IScope Parent => _parent;
        internal string Name => _name;
        internal IReadOnlyDictionary<Type, Registration> Registrations => _registrations;
        internal bool IsDisposed => _isDisposed || _isDisposing;
        internal bool IsActivationInProgress => _currentTransaction != null;
        internal static bool HasActiveTransaction => _currentTransaction != null;
        internal event Action<Scope> ScopeDisposing;
        internal event Action<Scope> ScopeDisposed;

        internal bool IsResolved(Type type)
        {
            lock (_lock)
            {
                return _instances.ContainsKey(type);
            }
        }

        internal Scope(Dictionary<Type, Registration> registrations, IScope parent, string name)
        {
            KDI.EnsureMainThread();
            UnityActivationAttempt.ThrowIfRollingBack();
            if (_isResetting)
                throw new InvalidOperationException("[KDI] A Scope cannot be built while KDI is resetting its subsystem state.");
            if (parent != null && !(parent is Scope))
                throw new NotSupportedException(
                    "[KDI] ScopeBuilder parents must be concrete KDI Scopes. A custom IScope has no shared " +
                    "activation/lifetime ledger, so child resolution cannot provide atomic rollback.");
            if (_currentTransaction != null)
                throw new InvalidOperationException("[KDI] A child Scope cannot be built from inside an activation factory or PostInject.");

            _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
            _parent = parent;
            _name = string.IsNullOrEmpty(name) ? "Scope" : name;
            _instances[typeof(IInstantiator)] = new Instantiator(this);

            var transaction = new ActivationTransaction();
            _currentTransaction = transaction;
            var attachedToParent = false;
            try
            {
                transaction.TrackScope(this);
                transaction.ThrowIfInvalidated();
                ObserveInstanceRegistrationIdentities();
                InjectInstanceRegistrations();
                InstantiateEntryPoints();

                if (parent is Scope parentScope)
                {
                    lock (parentScope._lock)
                    {
                        if (parentScope.IsDisposed)
                            throw new ObjectDisposedException(parentScope._name);
                        parentScope._children.Add(this);
                        attachedToParent = true;
                    }
                }

                transaction.ThrowIfInvalidated();
                transaction.Commit();
                RegisterLiveScope(this);
                UnityActivationAttempt.TrackConstructedScope(this);
            }
            catch
            {
                if (attachedToParent && parent is Scope parentScope)
                {
                    lock (parentScope._lock)
                    {
                        parentScope._children.Remove(this);
                    }
                }

                RollbackOutermost(transaction);
                _instances.Clear();
                _declaredExternalInstances.Clear();
                _isDisposed = true;
                throw;
            }
            finally
            {
                _currentTransaction = null;
                transaction.DisposeInvalidatedScopes();
            }
        }

        private void CacheInstance(Registration registration, object instance)
        {
            _instances[registration.ServiceType] = instance;
            if (registration.AliasTypes == null) return;
            foreach (var alias in registration.AliasTypes)
                _instances[alias] = instance;
        }

        private void RemoveCachedInstance(Registration registration, object instance)
        {
            RemoveCachedKey(registration.ServiceType, instance);
            if (registration.AliasTypes == null) return;
            foreach (var alias in registration.AliasTypes)
                RemoveCachedKey(alias, instance);
        }

        private void RemoveCachedKey(Type key, object instance)
        {
            if (_instances.TryGetValue(key, out var cached) && ReferenceEquals(cached, instance))
                _instances.Remove(key);
        }

        private void InjectInstanceRegistrations()
        {
            var seen = new HashSet<Registration>();
            foreach (var registration in _registrations.Values)
            {
                if (registration.Instance == null || !seen.Add(registration)) continue;
                ResolveInjectedDependency(registration.ServiceType);
            }
        }

        private void ObserveInstanceRegistrationIdentities()
        {
            var seen = new HashSet<Registration>();
            foreach (var registration in _registrations.Values)
            {
                var instance = registration.Instance;
                if (instance == null || !seen.Add(registration)) continue;
                if (_declaredExternalInstances.Add(instance))
                    ObserveExternalIdentity(instance, registration.ServiceType);
            }
        }

        private void InstantiateEntryPoints()
        {
            var seen = new HashSet<Registration>();
            foreach (var registration in _registrations.Values)
            {
                if (!registration.IsEntryPoint || !seen.Add(registration)) continue;
                ResolveInjectedDependency(registration.ServiceType);
            }
        }

        public T Resolve<T>() where T : class => (T)Resolve(typeof(T));

        public object Resolve(Type type)
        {
            KDI.EnsureMainThread();
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (ResolverAuthorityGuard.IsResolverType(type))
            {
                throw new InvalidOperationException(
                    $"[KDI] {type.Name} is resolver authority and cannot be resolved as a service. " +
                    "Resolve explicit dependencies at the composition boundary instead.");
            }
            if (_currentTransaction != null || UnityActivationAttempt.HasActiveAttempt ||
                ActivationCallbackGuard.IsActive)
            {
                throw new InvalidOperationException(
                    "[KDI] Public IScope.Resolve cannot be called from LifetimeScope.Configure, a factory, " +
                    "PostInject, final Unity activation, or another activation callback. Declare the dependency as an [Inject] field " +
                    "so it participates in the current activation ledger.");
            }
            ThrowIfUnavailable();

            return ExecuteInActivationTransaction(() => ResolveCore(type, this));
        }

        internal object ResolveInjectedDependency(Type type)
        {
            KDI.EnsureMainThread();
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (ResolverAuthorityGuard.IsResolverType(type))
            {
                throw new InvalidOperationException(
                    $"[KDI] {type.Name} is resolver authority and cannot be injected as a dependency.");
            }
            ThrowIfUnavailable();

            var transaction = _currentTransaction ?? throw new InvalidOperationException(
                "[KDI] Internal dependency resolution requires an active activation transaction.");
            transaction.ThrowIfRollingBack();
            transaction.TrackScope(this);
            transaction.ThrowIfSignaledFailure();
            var result = ResolveCore(type, this);
            transaction.ThrowIfSignaledFailure();
            return result;
        }

        internal IInstantiator GetInstantiator()
        {
            ThrowIfUnavailable();
            return (IInstantiator)_instances[typeof(IInstantiator)];
        }

        internal void ExecuteInActivationTransaction(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            ExecuteInActivationTransaction(() =>
            {
                action();
                return true;
            });
        }

        internal T ExecuteInActivationTransaction<T>(Func<T> action)
        {
            KDI.EnsureMainThread();
            if (action == null) throw new ArgumentNullException(nameof(action));
            UnityActivationAttempt.ThrowIfRollingBack();
            ThrowIfUnavailable();

            var ownsTransaction = _currentTransaction == null;
            if (ownsTransaction)
                _currentTransaction = new ActivationTransaction();
            var transaction = _currentTransaction;
            transaction.ThrowIfRollingBack();
            var checkpoint = transaction.RecordCount;

            try
            {
                transaction.TrackScope(this);
                if (ownsTransaction)
                    transaction.ThrowIfInvalidated();
                else
                    transaction.ThrowIfSignaledFailure();
                var result = action();
                if (ownsTransaction)
                    transaction.ThrowIfInvalidated();
                else
                    transaction.ThrowIfSignaledFailure();
                if (ownsTransaction)
                    transaction.Commit();
                return result;
            }
            catch
            {
                if (ownsTransaction)
                    RollbackOutermost(transaction);
                else
                    transaction.RollbackFrom(checkpoint);
                throw;
            }
            finally
            {
                if (ownsTransaction)
                {
                    _currentTransaction = null;
                    transaction.DisposeInvalidatedScopes();
                }
            }
        }

        private static void RollbackOutermost(ActivationTransaction transaction)
        {
            TryObserveDestroyedDeferredUnityLifetimes(transaction);
            try
            {
                transaction.Rollback(preserveTrackedScopes: true);
            }
            catch (Exception rollbackException)
            {
                // Preserve the activation exception that entered the surrounding
                // catch. Rollback cleanup is best-effort and already isolates each
                // individual record; this is a final defensive boundary.
                Debug.LogException(rollbackException);
            }
            finally
            {
                // Cleanup callbacks/Dispose can themselves destroy a previously
                // committed hostless service. Scan again before losing the touched
                // Scope work-list so finally can drain every invalidated Scope.
                TryObserveDestroyedDeferredUnityLifetimes(transaction);
                transaction.ClearTrackedScopes();
            }
        }

        private static void TryObserveDestroyedDeferredUnityLifetimes(ActivationTransaction transaction)
        {
            try { transaction.AuditDestroyedDeferredUnityLifetimes(); }
            catch (Exception observationException) { Debug.LogException(observationException); }
        }

        internal void ThrowIfActivationGraphInvalid()
        {
            KDI.EnsureMainThread();
            var transaction = _currentTransaction;
            if (transaction == null) return;
            transaction.TrackScope(this);
            transaction.ThrowIfInvalidated();
        }

        private object ResolveCore(Type type, Scope origin)
        {
            ThrowIfUnavailable();

            object cached;
            lock (_lock)
            {
                if (!_instances.TryGetValue(type, out cached))
                    cached = null;
            }
            if (cached != null)
                return ValidateCachedService(type, cached);

            if (_registrations.TryGetValue(type, out var registration))
            {
                var transaction = _currentTransaction ?? throw new InvalidOperationException("[KDI] Missing activation transaction.");
                transaction.Enter(this, registration);
                try
                {
                    object racedCached;
                    lock (_lock)
                    {
                        if (!_instances.TryGetValue(type, out racedCached))
                            racedCached = null;
                    }
                    if (racedCached != null)
                        return ValidateCachedService(type, racedCached);

                    var result = CreateInstance(registration);
                    var record = new ActivationRecord(
                        this, registration, result.Instance, result.Injection, result.ContainerCreated);
                    transaction.Record(record);

                    if (registration.Lifetime == Lifetime.Scoped || registration.Lifetime == Lifetime.Singleton)
                    {
                        lock (_lock)
                        {
                            CacheInstance(registration, result.Instance);
                            record.IsCached = true;
                        }
                        record.UnityLifetime = UnityServiceLifetimeLease.Attach(this, result.Instance);
                    }

                    return result.Instance;
                }
                finally
                {
                    transaction.Exit(this, registration);
                }
            }

            if (_parent is Scope parentScope)
                return parentScope.ResolveCore(type, origin);
            if (_parent != null)
                throw new InvalidOperationException(
                    "[KDI] Scope hierarchy contains a non-KDI parent without a shared activation ledger.");

            throw new InvalidOperationException(
                $"[KDI] {origin.BuildScopeChain()} does not contain a registration for {type.FullName}.");
        }

        private CreationResult CreateInstance(Registration registration)
        {
            object instance = null;
            InjectionLease injection = null;
            var factoryResult = registration.Instance == null;
            var containerOwned = false;
            try
            {
                if (registration.Instance != null)
                    instance = registration.Instance;
                else if (registration.Factory != null)
                    instance = registration.Factory();
                else if (registration.Activator != null)
                    instance = registration.Activator();
                else
                    throw new InvalidOperationException($"[KDI] {registration.ServiceType.Name} has no instance, factory, or activator.");

                if (instance == null || instance is UnityEngine.Object unityObject && unityObject == null)
                    throw new InvalidOperationException($"[KDI] {registration.ServiceType.Name} produced a null or destroyed instance.");

                // Validate before ownership bookkeeping. A factory that returns a
                // captured resolver (possibly behind object/a benign alias) must not
                // make the Scope container-owned or allow it to enter the cache.
                ResolverAuthorityGuard.ThrowIfResolverInstance(
                    instance, $"Activation of {registration.ServiceType.Name}");

                // A manual player-loop registration is an external ownership claim.
                // Reject before ClaimContainerOwnership so a failed factory activation
                // cannot Dispose an object whose manual callback remains registered.
                if (factoryResult && UpdateLoopManager.IsManuallyRegistered(instance))
                    throw new InvalidOperationException(
                        $"[KDI] Factory result {instance.GetType().Name} for {registration.ServiceType.Name} is manually " +
                        "registered with UpdateLoopManager and cannot become Scope-owned. Unregister it first or expose " +
                        "the external identity through FromInstance.");

                if (!factoryResult)
                    ObserveExternalIdentity(instance, registration.ServiceType);

                // Factory/activator results are owned by the first activation that
                // exposes their reference identity. A previously tracked external
                // instance remains external. IDisposable identities may never be
                // claimed by another Scope because that would let one Scope dispose an
                // object still used by another.
                if (factoryResult && !IsInstanceAlreadyTracked(instance))
                {
                    // A FromInstance declaration anywhere in this builder establishes
                    // external ownership before activation begins. This makes ownership
                    // independent of dictionary/registration order when a factory also
                    // exposes that exact identity from the same Scope.
                    if (!_declaredExternalInstances.Contains(instance) && instance is IDisposable)
                        ClaimContainerOwnership(instance, registration.ServiceType);
                    containerOwned = !_declaredExternalInstances.Contains(instance);
                }
                if (!registration.ServiceType.IsInstanceOfType(instance))
                    throw new InvalidOperationException(
                        $"[KDI] {instance.GetType().Name} is not assignable to {registration.ServiceType.Name}.");

                if (registration.AliasTypes != null)
                {
                    foreach (var alias in registration.AliasTypes)
                    {
                        if (!alias.IsInstanceOfType(instance))
                            throw new InvalidOperationException(
                                $"[KDI] {instance.GetType().Name} is not assignable to alias {alias.Name}.");
                    }
                }

                if (registration.Lifetime == Lifetime.Transient)
                {
                    if (instance is UnityEngine.Object)
                        throw new InvalidOperationException(
                            $"[KDI] Transient Unity service {registration.ServiceType.Name} is not supported. " +
                            "Use a Scoped binding so unexpected Unity destruction can invalidate the graph, " +
                            "or keep the object external and inject its GameObject explicitly.");
                    if (IsUpdatable(instance))
                        throw new InvalidOperationException(
                            $"[KDI] Transient {registration.ServiceType.Name} cannot implement an update interface.");
                    if (instance is IDisposable)
                        Debug.LogWarning(
                            $"[KDI] Transient {registration.ServiceType.Name} implements IDisposable. " +
                            "The Scope owns it after a successful Resolve and disposes it at Scope shutdown; " +
                            "do not dispose it separately.");
                }

                if (instance is IInjectable injectable)
                    injection = DependencyInjector.InjectWithLease(injectable, this);
                else
                    DependencyInjector.WarnIfHasInjectFieldsWithoutIInjectable(instance);

                return new CreationResult(instance, injection, containerOwned);
            }
            catch
            {
                try { injection?.RevokeCleanup(); }
                catch (Exception rollbackException) { Debug.LogException(rollbackException); }

                if (containerOwned && instance is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception disposeException) { Debug.LogException(disposeException); }
                }

                try { injection?.RestoreFields(); }
                catch (Exception restoreException) { Debug.LogException(restoreException); }
                throw;
            }
        }

        internal static void ResetState()
        {
            KDI.EnsureMainThread();
            _currentTransaction = null;
            UnityActivationAttempt.ResetStatic();
            ActivationCallbackGuard.ResetStatic();
            _isResetting = true;
            try
            {
                var liveScopes = new List<Scope>();
                for (var i = 0; i < _liveScopes.Count; i++)
                {
                    var scope = _liveScopes[i];
                    if (scope != null && !scope._isDisposed)
                        liveScopes.Add(scope);
                }

                // Parents are registered before their children. Disposing in creation
                // order lets the parent perform its normal child cascade and preserves
                // owned-hierarchy deactivation ordering. Later entries then no-op.
                for (var i = 0; i < liveScopes.Count; i++)
                {
                    try { liveScopes[i].Dispose(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            finally
            {
                _liveScopes.Clear();
                // Keep weak identity markers when Domain Reload is disabled. Objects
                // may survive into the next play session through external statics; an
                // external object must never become factory-owned, and a disposed
                // factory result must never be claimed again. A real domain reload
                // naturally creates a fresh table without retaining any key strongly.
                _currentTransaction = null;
                _isResetting = false;
            }
        }

        private static void RegisterLiveScope(Scope scope)
        {
            for (var i = _liveScopes.Count - 1; i >= 0; i--)
            {
                if (_liveScopes[i] == null || _liveScopes[i]._isDisposed)
                    _liveScopes.RemoveAt(i);
            }
            _liveScopes.Add(scope);
        }

        private static void UnregisterLiveScope(Scope scope)
        {
            for (var i = _liveScopes.Count - 1; i >= 0; i--)
            {
                var target = _liveScopes[i];
                if (target == null || ReferenceEquals(target, scope))
                    _liveScopes.RemoveAt(i);
            }
        }

        private void ClaimContainerOwnership(object instance, Type serviceType)
        {
            if (_containerOwnership.TryGetValue(instance, out var existing))
            {
                var ownership = existing.IsExternal
                    ? "already registered as externally owned"
                    : $"already container-owned by Scope '{existing.ScopeName}'";
                throw new InvalidOperationException(
                    $"[KDI] Factory result {instance.GetType().Name} for {serviceType.Name} is {ownership}. " +
                    "Share one instance through FromInstance/AlsoBind instead of " +
                    "returning it from factories in multiple Scopes.");
            }

            _containerOwnership.Add(instance, ContainerOwnershipMarker.Container(_name));
        }

        private void ObserveExternalIdentity(object instance, Type serviceType)
        {
            if (_containerOwnership.TryGetValue(instance, out var existing))
            {
                if (existing.IsExternal) return;
                throw new InvalidOperationException(
                    $"[KDI] FromInstance {instance.GetType().Name} for {serviceType.Name} is already container-owned " +
                    $"by Scope '{existing.ScopeName}' and may be disposed there. Register shared objects as FromInstance " +
                    "before exposing them from any factory.");
            }

            _containerOwnership.Add(instance, ContainerOwnershipMarker.External());
        }

        internal void ObserveExternalInjectionIdentity(object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            ObserveExternalIdentity(instance, instance.GetType());
        }

        private bool IsInstanceAlreadyTracked(object instance)
        {
            if (instance == null) return false;
            if (_currentTransaction != null && _currentTransaction.ContainsInstance(instance, this))
                return true;

            for (Scope scope = this; scope != null; scope = scope._parent as Scope)
            {
                for (var i = scope._activationOrder.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(scope._activationOrder[i].Instance, instance))
                        return true;
                }
            }
            return false;
        }

        private static bool IsUpdatable(object instance)
        {
            return instance is IUpdatable || instance is IFixedUpdatable || instance is ILateUpdatable;
        }

        private object ValidateCachedService(Type serviceType, object instance)
        {
            if (instance is UnityEngine.Object unityObject && unityObject == null)
            {
                ReleaseDestroyedCachedInstance(instance);
                throw new InvalidOperationException(
                    $"[KDI] Cached Unity service {serviceType.Name} was destroyed outside its owning Scope.");
            }
            return instance;
        }

        private void ReleaseDestroyedCachedInstance(object instance)
        {
            if (_currentTransaction != null && _currentTransaction.ReleaseDestroyedInstance(this, instance))
                return;

            for (var i = _activationOrder.Count - 1; i >= 0; i--)
            {
                var record = _activationOrder[i];
                if (!ReferenceEquals(record.Owner, this) || !ReferenceEquals(record.Instance, instance)) continue;
                InvalidateForDestroyedCachedUnityService(record.Instance);
                return;
            }
        }

        private void InvalidateForDestroyedCachedUnityService(object instance)
        {
            if (_isDisposed || _isDisposing) return;

            var typeName = instance?.GetType().Name ?? "Unity service";
            var reason = $"[KDI] Cached Unity service {typeName} was destroyed outside Scope '{_name}'. " +
                         "The complete Scope is being disposed because existing consumers may hold that reference.";
            InvalidateScope(reason);
        }

        private void InvalidateScope(string reason)
        {
            if (_isDisposed || _isDisposing) return;
            if (!_disposeAfterActivation)
                Debug.LogWarning(reason);

            if (_currentTransaction != null)
            {
                _disposeAfterActivation = true;
                _deferredInvalidationReason = reason;
                _currentTransaction.DeferInvalidation(this);
                return;
            }

            Dispose();
        }

        internal bool DeferDisposalForDestroyedLifetimeScope()
        {
            KDI.EnsureMainThread();
            if (_currentTransaction == null || _isDisposed || _isDisposing)
                return false;

            InvalidateScope(
                $"[KDI] The Unity owner of Scope '{_name}' was destroyed during activation. " +
                "The transaction will fail and dispose the Scope at its outermost boundary.");
            return true;
        }

        internal bool CanInvokeUpdateCallback()
        {
            KDI.EnsureMainThread();

            // A child can only resolve from itself or an ancestor. Poll that complete
            // lifetime chain immediately before each callback so a hostless cached
            // Unity dependency destroyed by an earlier callback cannot be observed
            // through an intermediate plain C# service in this same loop phase.
            for (var current = this; current != null; current = current._parent as Scope)
            {
                if (current._isDisposed || current._isDisposing || current._disposeAfterActivation)
                    return false;

                current.DetectDestroyedDeferredUnityLifetime();
                if (current._isDisposed || current._isDisposing || current._disposeAfterActivation)
                    return false;
            }

            return true;
        }

        private static bool RegisterToUpdateLoop(ActivationRecord record)
        {
            var instance = record.Instance;
            if (!IsUpdatable(instance)) return false;
            var manager = UpdateLoopManager.GetOrCreateForKDI();
            if (manager == null) return false;
            manager.Register(instance, record.Owner);
            return true;
        }

        internal void TrackExternalInjection(InjectionLease injection)
        {
            if (injection == null) return;
            KDI.EnsureMainThread();
            if (IsDisposed)
            {
                injection.Dispose();
                throw new ObjectDisposedException(_name);
            }
            if (_currentTransaction != null)
            {
                _currentTransaction.RecordExternalInjection(this, injection);
                return;
            }

            injection.Dispose();
            throw new InvalidOperationException("[KDI] External injection was not enclosed by an activation transaction.");
        }

        internal void TrackOwnedGameObject(GameObject instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            KDI.EnsureMainThread();
            if (_currentTransaction == null)
                throw new InvalidOperationException("[KDI] An owned GameObject must be created inside an activation transaction.");

            var record = new ActivationRecord(this, null, instance, null, false)
            {
                OwnedGameObject = instance
            };
            _currentTransaction.Record(record);
            record.UnityLifetime = UnityServiceLifetimeLease.Attach(this, instance);
        }

        internal void ReleaseInjectionLease(InjectionLease injection)
        {
            if (injection == null) return;
            KDI.EnsureMainThread();
            if (_currentTransaction != null && _currentTransaction.ReleaseInjection(this, injection))
                return;

            for (var i = _activationOrder.Count - 1; i >= 0; i--)
            {
                var record = _activationOrder[i];
                if (!ReferenceEquals(record.Owner, this) || !ReferenceEquals(record.Injection, injection)) continue;
                UnityActivationAttempt.ReportUnexpectedRelease(record, "injection target");
                if (record.IsCached)
                {
                    InvalidateForDestroyedCachedUnityService(record.Instance);
                    return;
                }
                record.IsReleased = true;
                _activationOrder.RemoveAt(i);
                RemoveDeferredUnityLifetimeRecord(record);

                try { record.UnityLifetime?.Dispose(); }
                catch (Exception ex) { Debug.LogException(ex); }

                if (record.IsUpdateRegistered)
                {
                    try { UpdateLoopManager.TryGetInstance()?.Unregister(record.Instance, record.Owner); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
                if (record.IsCached)
                {
                    lock (_lock)
                    {
                        RemoveCachedInstance(record.Registration, record.Instance);
                    }
                }

                try { record.Injection.RevokeCleanup(); }
                catch (Exception ex) { Debug.LogException(ex); }

                if (record.ContainerCreated && record.Instance is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }

                try { record.Injection.RestoreFields(); }
                catch (Exception ex) { Debug.LogException(ex); }
                return;
            }

            // The Scope may already have revoked the lease. Dispose is idempotent and
            // also releases a host that outlived its Scope.
            try { injection.Dispose(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        internal void ReleaseUnityServiceLifetime(UnityServiceLifetimeLease lifetime)
        {
            if (lifetime == null) return;
            KDI.EnsureMainThread();
            if (_currentTransaction != null && _currentTransaction.ReleaseUnityLifetime(this, lifetime))
                return;

            for (var i = _activationOrder.Count - 1; i >= 0; i--)
            {
                var record = _activationOrder[i];
                if (!ReferenceEquals(record.Owner, this) ||
                    !ReferenceEquals(record.UnityLifetime, lifetime)) continue;
                UnityActivationAttempt.ReportUnexpectedRelease(record, "Scope-owned Unity object");
                if (record.IsCached)
                {
                    InvalidateForDestroyedCachedUnityService(record.Instance);
                    return;
                }
                record.IsReleased = true;
                _activationOrder.RemoveAt(i);
                RemoveDeferredUnityLifetimeRecord(record);

                try { record.UnityLifetime.Dispose(); }
                catch (Exception ex) { Debug.LogException(ex); }

                if (record.IsUpdateRegistered)
                {
                    try { UpdateLoopManager.TryGetInstance()?.Unregister(record.Instance, record.Owner); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
                if (record.IsCached)
                {
                    lock (_lock)
                    {
                        RemoveCachedInstance(record.Registration, record.Instance);
                    }
                }

                try { record.Injection?.RevokeCleanup(); }
                catch (Exception ex) { Debug.LogException(ex); }

                if (record.ContainerCreated && record.Instance is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }

                try { record.Injection?.RestoreFields(); }
                catch (Exception ex) { Debug.LogException(ex); }
                return;
            }

            try { lifetime.Dispose(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private void RemoveDeferredUnityLifetimeRecord(ActivationRecord record)
        {
            for (var i = _deferredUnityLifetimeRecords.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_deferredUnityLifetimeRecords[i], record))
                    _deferredUnityLifetimeRecords.RemoveAt(i);
            }
        }

        private void DetectDestroyedDeferredUnityLifetime()
        {
            // Cleanup callbacks may re-enter Scope.Dispose or release other leases,
            // mutating or clearing the complete list. Process at most one external
            // lease per scan and restart from the current list shape after cleanup.
            while (!_isDisposed && !_isDisposing)
            {
                InjectionLease destroyedExternalInjection = null;
                for (var i = _deferredUnityLifetimeRecords.Count - 1; i >= 0; i--)
                {
                    var record = _deferredUnityLifetimeRecords[i];
                    if (record.IsReleased) continue;

                    if (record.IsCached && record.UnityLifetime != null &&
                        record.UnityLifetime.RequiresPolling && record.UnityLifetime.IsTargetDestroyed)
                    {
                        InvalidateForDestroyedCachedUnityService(record.Instance);
                        return;
                    }

                    if (record.Injection != null && record.Injection.RequiresPolling &&
                        record.Injection.IsTargetDestroyed)
                    {
                        destroyedExternalInjection = record.Injection;
                        break;
                    }
                }

                if (destroyedExternalInjection == null)
                    return;
                ReleaseInjectionLease(destroyedExternalInjection);
            }
        }

        private string BuildScopeChain()
        {
            var builder = new StringBuilder(_name);
            var parent = _parent;
            while (parent is Scope parentScope)
            {
                builder.Append(" -> ").Append(parentScope._name);
                parent = parentScope._parent;
            }
            return builder.ToString();
        }

        private static void DestroyOwnedGameObject(GameObject instance)
        {
            if (instance == null) return;
            if (instance.activeSelf)
                instance.SetActive(false);
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(instance);
            else
                UnityEngine.Object.DestroyImmediate(instance);
        }

        /// <summary>
        /// Reverses records that already crossed an ordinary Scope transaction's
        /// commit boundary but still belong to an uncommitted Unity activation.
        /// A record is cleaned only if it is still present in its owner's ledger;
        /// child Scope disposal or a Unity lifetime callback may have released it
        /// first. This keeps compensation idempotent and preserves the hostless
        /// lifetime ledger invariants used by normal Scope shutdown.
        /// </summary>
        internal static void RollbackCommittedActivationRecords(IReadOnlyList<ActivationRecord> records)
        {
            KDI.EnsureMainThread();
            if (records == null || records.Count == 0) return;

            var disposedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (var i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                if (record == null || !record.Owner.TryDetachCommittedRecord(record)) continue;

                try { record.UnityLifetime?.Dispose(); }
                catch (Exception exception) { Debug.LogException(exception); }
                if (record.OwnedGameObject != null)
                {
                    try { DestroyOwnedGameObject(record.OwnedGameObject); }
                    catch (Exception exception) { Debug.LogException(exception); }
                    continue;
                }

                if (record.IsUpdateRegistered)
                {
                    try { UpdateLoopManager.TryGetInstance()?.Unregister(record.Instance, record.Owner); }
                    catch (Exception exception) { Debug.LogException(exception); }
                }

                if (record.IsCached)
                {
                    lock (record.Owner._lock)
                    {
                        record.Owner.RemoveCachedInstance(record.Registration, record.Instance);
                    }
                }

                try { record.Injection?.RevokeCleanup(); }
                catch (Exception exception) { Debug.LogException(exception); }

                if (record.ContainerCreated && record.Instance != null &&
                    disposedInstances.Add(record.Instance) && record.Instance is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception exception) { Debug.LogException(exception); }
                }

                try { record.Injection?.RestoreFields(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        private bool TryDetachCommittedRecord(ActivationRecord record)
        {
            lock (_lock)
            {
                for (var i = _activationOrder.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(_activationOrder[i], record)) continue;
                    _activationOrder.RemoveAt(i);
                    RemoveDeferredUnityLifetimeRecord(record);
                    record.IsReleased = true;
                    return true;
                }
            }
            return false;
        }

        internal bool ContainsCommittedRecord(ActivationRecord record)
        {
            if (record == null) return false;
            lock (_lock)
            {
                for (var i = _activationOrder.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_activationOrder[i], record)) return true;
                }
            }
            return false;
        }

        private void ThrowIfUnavailable()
        {
            if (_isResetting)
                throw new InvalidOperationException("[KDI] Scope APIs are unavailable while KDI is resetting its subsystem state.");
            if (_isDisposed || _isDisposing)
                throw new ObjectDisposedException(_name);
        }

        public void Dispose()
        {
            DisposeCore(allowConfigureCleanup: false);
        }

        internal void DisposeFromDestroyedLifetimeScope()
        {
            DisposeCore(allowConfigureCleanup: true);
        }

        private void DisposeCore(bool allowConfigureCleanup)
        {
            KDI.EnsureMainThread();
            if (_isDisposed || _isDisposing) return;
            if (_currentTransaction != null)
                throw new InvalidOperationException($"[KDI] Scope {_name} cannot be disposed during activation.");
            if (!allowConfigureCleanup)
                ActivationCallbackGuard.ThrowIfConfigureMutation("Scope.Dispose");
            UnityActivationAttempt.ReportUnexpectedScopeDisposal(this);

            _isDisposing = true;
            _disposeAfterActivation = false;
            _deferredInvalidationReason = null;
            var errors = new List<Exception>();

            var disposingHandler = ScopeDisposing;
            ScopeDisposing = null;
            if (disposingHandler != null)
            {
                foreach (Action<Scope> handler in disposingHandler.GetInvocationList())
                {
                    try { handler(this); }
                    catch (Exception ex) { errors.Add(ex); }
                }
            }

            var activationSnapshot = _activationOrder.ToArray();
            _activationOrder.Clear();
            _deferredUnityLifetimeRecords.Clear();

            // Stop Scope-owned hierarchies before disposing child Scopes. A dynamic
            // prefab can itself contain a LifetimeScope child, and its OnDisable hooks
            // must still observe injected fields from that child Scope.
            for (var i = activationSnapshot.Length - 1; i >= 0; i--)
            {
                var owned = activationSnapshot[i].OwnedGameObject;
                if (owned == null || !owned.activeSelf) continue;
                try { owned.SetActive(false); }
                catch (Exception ex) { errors.Add(ex); }
            }

            if (_parent is Scope parentScope)
            {
                lock (parentScope._lock)
                {
                    parentScope._children.Remove(this);
                }
            }

            List<IScope> childrenSnapshot;
            lock (_lock)
            {
                childrenSnapshot = new List<IScope>(_children);
                _children.Clear();
            }
            for (var i = childrenSnapshot.Count - 1; i >= 0; i--)
            {
                try { childrenSnapshot[i].Dispose(); }
                catch (Exception ex) { errors.Add(ex); }
            }

            var disposedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            // Destruction happens in reverse activation order after child Scopes and
            // component injection leases have been revoked.
            for (var i = activationSnapshot.Length - 1; i >= 0; i--)
            {
                var record = activationSnapshot[i];
                record.IsReleased = true;
                try { record.UnityLifetime?.Dispose(); }
                catch (Exception ex) { errors.Add(ex); }
                if (record.OwnedGameObject != null)
                {
                    try { DestroyOwnedGameObject(record.OwnedGameObject); }
                    catch (Exception ex) { errors.Add(ex); }
                    continue;
                }
                if (record.IsUpdateRegistered)
                {
                    try { UpdateLoopManager.TryGetInstance()?.Unregister(record.Instance, record.Owner); }
                    catch (Exception ex) { errors.Add(ex); }
                }

                try { record.Injection?.RevokeCleanup(); }
                catch (Exception ex) { errors.Add(ex); }

                if (record.ContainerCreated && record.Instance != null && disposedInstances.Add(record.Instance) && record.Instance is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception ex) { errors.Add(ex); }
                }

                try { record.Injection?.RestoreFields(); }
                catch (Exception ex) { errors.Add(ex); }
            }
            lock (_lock)
            {
                _instances.Clear();
            }
            _declaredExternalInstances.Clear();

            _isDisposed = true;
            _isDisposing = false;
            UnregisterLiveScope(this);

            var disposedHandler = ScopeDisposed;
            ScopeDisposed = null;
            if (disposedHandler != null)
            {
                foreach (Action<Scope> handler in disposedHandler.GetInvocationList())
                {
                    try { handler(this); }
                    catch (Exception ex) { errors.Add(ex); }
                }
            }

            if (errors.Count > 0)
                Debug.LogException(new AggregateException($"[KDI] Errors occurred while disposing Scope {_name}.", errors));
        }

        private readonly struct CreationResult
        {
            internal readonly object Instance;
            internal readonly InjectionLease Injection;
            internal readonly bool ContainerCreated;

            internal CreationResult(object instance, InjectionLease injection, bool containerCreated)
            {
                Instance = instance;
                Injection = injection;
                ContainerCreated = containerCreated;
            }
        }

        internal sealed class ActivationRecord
        {
            internal readonly Scope Owner;
            internal readonly Registration Registration;
            internal readonly object Instance;
            internal readonly InjectionLease Injection;
            internal readonly bool ContainerCreated;
            internal bool IsCached;
            internal bool IsUpdateRegistered;
            internal bool IsExternalInjection;
            internal bool IsReleased;
            internal UnityServiceLifetimeLease UnityLifetime;
            internal GameObject OwnedGameObject;
            internal bool RequiresLifetimeTracking =>
                IsExternalInjection || IsCached || Injection != null || OwnedGameObject != null ||
                ContainerCreated && Instance is IDisposable;

            internal ActivationRecord(Scope owner, Registration registration, object instance, InjectionLease injection, bool containerCreated)
            {
                Owner = owner;
                Registration = registration;
                Instance = instance;
                Injection = injection;
                ContainerCreated = containerCreated;
            }
        }

        internal sealed class ActivationTransaction
        {
            private readonly List<ActivationRecord> _records = new();
            private readonly List<ActivationKey> _path = new();
            private readonly HashSet<ActivationKey> _active = new(ActivationKeyComparer.Instance);
            private readonly HashSet<Scope> _invalidatedScopes = new();
            private readonly HashSet<Scope> _touchedScopes = new();
            private readonly List<Scope> _touchedScopeOrder = new();
            private readonly List<ActivationFailure> _failures = new();
            private bool _isRollingBack;

            internal int RecordCount => _records.Count;

            internal bool ContainsInstance(object instance, Scope consumer)
            {
                for (var i = _records.Count - 1; i >= 0; i--)
                {
                    var record = _records[i];
                    if (record.IsReleased || !ReferenceEquals(record.Instance, instance)) continue;

                    // Reusing an identity is safe only when its first owner is this
                    // Scope or an ancestor. A sibling/descendant has an independent or
                    // shorter lifetime and must pass through the global ownership check.
                    for (var scope = consumer; scope != null; scope = scope._parent as Scope)
                    {
                        if (ReferenceEquals(record.Owner, scope))
                            return true;
                    }
                }
                return false;
            }

            internal void ThrowIfRollingBack()
            {
                if (_isRollingBack)
                    throw new InvalidOperationException(
                        "[KDI] Resolving or activating services from PreUninject/Dispose during transaction rollback is not allowed.");
            }

            internal void DeferInvalidation(Scope scope)
            {
                if (scope != null)
                    _invalidatedScopes.Add(scope);
            }

            internal void TrackScope(Scope scope)
            {
                for (var current = scope; current != null; current = current._parent as Scope)
                {
                    if (_touchedScopes.Add(current))
                        _touchedScopeOrder.Add(current);
                }
            }

            internal void MarkUnexpectedRelease(ActivationRecord record, string releasedKind)
            {
                if (_isRollingBack || record == null) return;
                for (var i = 0; i < _failures.Count; i++)
                {
                    if (ReferenceEquals(_failures[i].Record, record)) return;
                }

                var typeName = record.Instance?.GetType().Name ?? "Unity object";
                _failures.Add(new ActivationFailure(
                    record,
                    $"[KDI] {releasedKind} {typeName} was destroyed or released during activation. " +
                    "The activation transaction will roll back instead of committing a partial graph."));
            }

            internal void ThrowIfInvalidated()
            {
                DetectDestroyedDeferredUnityLifetimes();
                ThrowIfSignaledFailure();
            }

            internal void ThrowIfSignaledFailure()
            {
                if (_invalidatedScopes.Count > 0)
                {
                    foreach (var scope in _invalidatedScopes)
                    {
                        throw new InvalidOperationException(scope._deferredInvalidationReason ??
                            $"[KDI] Scope '{scope._name}' became invalid during activation.");
                    }
                }
                if (_failures.Count > 0)
                    throw new InvalidOperationException(_failures[0].Message);
            }

            private void DetectDestroyedDeferredUnityLifetimes()
            {
                // Cleanup may resolve through another Scope and append to this
                // transaction. Index iteration over the append-only work list is
                // stable and also validates newly touched scopes before returning.
                for (var i = 0; i < _touchedScopeOrder.Count; i++)
                {
                    var scope = _touchedScopeOrder[i];
                    scope.DetectDestroyedDeferredUnityLifetime();
                    if (_invalidatedScopes.Count > 0 || _failures.Count > 0)
                        return;
                }

                for (var i = _records.Count - 1; i >= 0; i--)
                {
                    var record = _records[i];
                    if (record.IsReleased) continue;
                    if (record.IsCached && record.UnityLifetime != null &&
                        record.UnityLifetime.RequiresPolling && record.UnityLifetime.IsTargetDestroyed)
                    {
                        record.Owner.InvalidateForDestroyedCachedUnityService(record.Instance);
                        return;
                    }
                    if (record.Injection != null && record.Injection.RequiresPolling &&
                        record.Injection.IsTargetDestroyed)
                    {
                        ReleaseInjection(record.Owner, record.Injection);
                        return;
                    }
                }
            }

            internal void AuditDestroyedDeferredUnityLifetimes()
            {
                var wasRollingBack = _isRollingBack;
                _isRollingBack = true;
                try
                {
                    // Failure cleanup must audit every touched Scope even when an
                    // earlier Scope already signaled invalidation. Otherwise a parent
                    // and child destroyed in the same failure could close only one.
                    for (var i = 0; i < _touchedScopeOrder.Count; i++)
                        _touchedScopeOrder[i].DetectDestroyedDeferredUnityLifetime();

                    for (var i = _records.Count - 1; i >= 0; i--)
                    {
                        var record = _records[i];
                        if (record.IsReleased) continue;
                        if (record.IsCached && record.UnityLifetime != null &&
                            record.UnityLifetime.RequiresPolling && record.UnityLifetime.IsTargetDestroyed)
                        {
                            record.Owner.InvalidateForDestroyedCachedUnityService(record.Instance);
                            continue;
                        }
                        if (record.Injection != null && record.Injection.RequiresPolling &&
                            record.Injection.IsTargetDestroyed)
                        {
                            ReleaseInjection(record.Owner, record.Injection);
                        }
                    }
                }
                finally
                {
                    _isRollingBack = wasRollingBack;
                }
            }

            internal void DisposeInvalidatedScopes()
            {
                if (_invalidatedScopes.Count == 0) return;
                var scopes = new List<Scope>(_invalidatedScopes);
                _invalidatedScopes.Clear();
                scopes.Sort((left, right) => GetScopeDepth(left).CompareTo(GetScopeDepth(right)));
                for (var i = 0; i < scopes.Count; i++)
                {
                    try { scopes[i].Dispose(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }

            private static int GetScopeDepth(Scope scope)
            {
                var depth = 0;
                for (var parent = scope?._parent as Scope; parent != null; parent = parent._parent as Scope)
                    depth++;
                return depth;
            }

            internal void Enter(Scope scope, Registration registration)
            {
                var key = new ActivationKey(scope, registration);
                if (!_active.Add(key))
                {
                    var path = new StringBuilder();
                    foreach (var item in _path)
                    {
                        if (path.Length > 0) path.Append(" -> ");
                        path.Append(item.Scope._name).Append(':').Append(item.Registration.ServiceType.Name);
                    }
                    if (path.Length > 0) path.Append(" -> ");
                    path.Append(scope._name).Append(':').Append(registration.ServiceType.Name);
                    throw new InvalidOperationException($"[KDI] Circular dependency detected: {path}");
                }
                _path.Add(key);
            }

            internal void Exit(Scope scope, Registration registration)
            {
                var key = new ActivationKey(scope, registration);
                _active.Remove(key);
                for (var i = _path.Count - 1; i >= 0; i--)
                {
                    if (!ActivationKeyComparer.Instance.Equals(_path[i], key)) continue;
                    _path.RemoveAt(i);
                    break;
                }
            }

            internal void Record(ActivationRecord record)
            {
                ThrowIfRollingBack();
                _records.Add(record);
            }

            internal void RecordExternalInjection(Scope owner, InjectionLease injection)
            {
                ThrowIfRollingBack();
                _records.Add(new ActivationRecord(owner, null, injection.Target, injection, false)
                {
                    IsExternalInjection = true
                });
            }

            internal void Commit()
            {
                // The owning Execute/Scope-construction boundary already performed
                // the O(N) hostless Unity scan. Commit only checks synchronous signals
                // so nested Resolve graphs do not rescan a growing record set O(N²).
                ThrowIfSignaledFailure();
                // A service cannot enter the player loop until the complete graph has
                // activated successfully. If registration fails, the caller rolls every
                // record back and unregisters the already queued registrations.
                foreach (var record in _records)
                {
                    if (record.IsReleased) continue;
                    if (record.IsCached)
                        record.IsUpdateRegistered = RegisterToUpdateLoop(record);
                }

                foreach (var record in _records)
                {
                    if (record.IsReleased) continue;
                    if (record.RequiresLifetimeTracking)
                        record.Owner._activationOrder.Add(record);
                    if (record.IsCached && record.UnityLifetime != null && record.UnityLifetime.RequiresPolling ||
                        record.Injection != null && record.Injection.RequiresPolling)
                    {
                        record.Owner._deferredUnityLifetimeRecords.Add(record);
                    }
                    if (record.RequiresLifetimeTracking)
                        UnityActivationAttempt.TrackCommitted(record);
                }
                _records.Clear();
                _active.Clear();
                _path.Clear();
                ClearTrackedScopes();
                _failures.Clear();
            }

            internal void Rollback(bool preserveTrackedScopes = false)
            {
                RollbackFrom(0);
                _active.Clear();
                _path.Clear();
                if (!preserveTrackedScopes)
                    ClearTrackedScopes();
                _failures.Clear();
            }

            internal void ClearTrackedScopes()
            {
                _touchedScopes.Clear();
                _touchedScopeOrder.Clear();
            }

            internal void RollbackFrom(int checkpoint)
            {
                if (checkpoint < 0 || checkpoint > _records.Count)
                    throw new ArgumentOutOfRangeException(nameof(checkpoint));
                if (_isRollingBack)
                    throw new InvalidOperationException("[KDI] An activation transaction is already rolling back.");

                var rollbackRecords = _records.GetRange(checkpoint, _records.Count - checkpoint);
                if (_records.Count > checkpoint)
                    _records.RemoveRange(checkpoint, _records.Count - checkpoint);
                RemoveFailuresFor(rollbackRecords);

                _isRollingBack = true;
                try
                {
                    var disposedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    for (var i = rollbackRecords.Count - 1; i >= 0; i--)
                    {
                        var record = rollbackRecords[i];
                        if (record.IsReleased) continue;
                        record.IsReleased = true;
                        try { record.UnityLifetime?.Dispose(); }
                        catch (Exception ex) { Debug.LogException(ex); }
                        if (record.OwnedGameObject != null)
                        {
                            try { DestroyOwnedGameObject(record.OwnedGameObject); }
                            catch (Exception ex) { Debug.LogException(ex); }
                            continue;
                        }
                        if (record.IsUpdateRegistered)
                        {
                            try { UpdateLoopManager.TryGetInstance()?.Unregister(record.Instance, record.Owner); }
                            catch (Exception ex) { Debug.LogException(ex); }
                        }

                        if (record.IsCached)
                        {
                            lock (record.Owner._lock)
                            {
                                record.Owner.RemoveCachedInstance(record.Registration, record.Instance);
                            }
                        }

                        try { record.Injection?.RevokeCleanup(); }
                        catch (Exception ex) { Debug.LogException(ex); }

                        if (record.ContainerCreated && record.Instance != null &&
                            disposedInstances.Add(record.Instance) && record.Instance is IDisposable disposable)
                        {
                            try { disposable.Dispose(); }
                            catch (Exception ex) { Debug.LogException(ex); }
                        }

                        try { record.Injection?.RestoreFields(); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }
                }
                finally
                {
                    _isRollingBack = false;
                }
            }

            private void RemoveFailuresFor(List<ActivationRecord> rollbackRecords)
            {
                for (var i = _failures.Count - 1; i >= 0; i--)
                {
                    for (var j = 0; j < rollbackRecords.Count; j++)
                    {
                        if (!ReferenceEquals(_failures[i].Record, rollbackRecords[j])) continue;
                        _failures.RemoveAt(i);
                        break;
                    }
                }
            }

            internal bool ReleaseInjection(Scope owner, InjectionLease injection)
            {
                for (var i = _records.Count - 1; i >= 0; i--)
                {
                    var record = _records[i];
                    if (!ReferenceEquals(record.Owner, owner) ||
                        !ReferenceEquals(record.Injection, injection)) continue;
                    if (record.IsReleased) return true;

                    record.IsReleased = true;
                    if (record.IsCached)
                        record.Owner.InvalidateForDestroyedCachedUnityService(record.Instance);
                    else
                        MarkUnexpectedRelease(record, "injection target");
                    try { record.UnityLifetime?.Dispose(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    if (record.IsUpdateRegistered)
                    {
                        try { UpdateLoopManager.TryGetInstance()?.Unregister(record.Instance, record.Owner); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }
                    if (record.IsCached)
                    {
                        lock (record.Owner._lock)
                        {
                            record.Owner.RemoveCachedInstance(record.Registration, record.Instance);
                        }
                    }

                    try { record.Injection.RevokeCleanup(); }
                    catch (Exception ex) { Debug.LogException(ex); }

                    if (record.ContainerCreated && record.Instance is IDisposable disposable)
                    {
                        try { disposable.Dispose(); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }

                    try { record.Injection.RestoreFields(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    return true;
                }
                return false;
            }

            internal bool ReleaseUnityLifetime(Scope owner, UnityServiceLifetimeLease lifetime)
            {
                for (var i = _records.Count - 1; i >= 0; i--)
                {
                    var record = _records[i];
                    if (!ReferenceEquals(record.Owner, owner) ||
                        !ReferenceEquals(record.UnityLifetime, lifetime)) continue;
                    if (record.IsReleased) return true;

                    record.IsReleased = true;
                    if (record.IsCached)
                        record.Owner.InvalidateForDestroyedCachedUnityService(record.Instance);
                    else
                        MarkUnexpectedRelease(record, "Scope-owned Unity object");
                    try { record.UnityLifetime.Dispose(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    if (record.IsUpdateRegistered)
                    {
                        try { UpdateLoopManager.TryGetInstance()?.Unregister(record.Instance, record.Owner); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }
                    if (record.IsCached)
                    {
                        lock (record.Owner._lock)
                        {
                            record.Owner.RemoveCachedInstance(record.Registration, record.Instance);
                        }
                    }

                    try { record.Injection?.RevokeCleanup(); }
                    catch (Exception ex) { Debug.LogException(ex); }

                    if (record.ContainerCreated && record.Instance is IDisposable disposable)
                    {
                        try { disposable.Dispose(); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }

                    try { record.Injection?.RestoreFields(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    return true;
                }
                return false;
            }

            internal bool ReleaseDestroyedInstance(Scope owner, object instance)
            {
                var found = false;
                var disposedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
                for (var i = _records.Count - 1; i >= 0; i--)
                {
                    var record = _records[i];
                    if (!ReferenceEquals(record.Owner, owner) ||
                        !ReferenceEquals(record.Instance, instance) || record.IsReleased) continue;

                    record.IsReleased = true;
                    found = true;
                    if (record.IsCached)
                        record.Owner.InvalidateForDestroyedCachedUnityService(record.Instance);
                    try { record.UnityLifetime?.Dispose(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    if (record.IsUpdateRegistered)
                    {
                        try { UpdateLoopManager.TryGetInstance()?.Unregister(record.Instance, record.Owner); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }
                    if (record.IsCached)
                    {
                        lock (record.Owner._lock)
                        {
                            record.Owner.RemoveCachedInstance(record.Registration, record.Instance);
                        }
                    }

                    try { record.Injection?.RevokeCleanup(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    if (record.ContainerCreated && disposedInstances.Add(record.Instance) &&
                        record.Instance is IDisposable disposable)
                    {
                        try { disposable.Dispose(); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }
                    try { record.Injection?.RestoreFields(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
                return found;
            }

            private readonly struct ActivationFailure
            {
                internal readonly ActivationRecord Record;
                internal readonly string Message;

                internal ActivationFailure(ActivationRecord record, string message)
                {
                    Record = record;
                    Message = message;
                }
            }
        }

        private readonly struct ActivationKey
        {
            internal readonly Scope Scope;
            internal readonly Registration Registration;

            internal ActivationKey(Scope scope, Registration registration)
            {
                Scope = scope;
                Registration = registration;
            }
        }

        private sealed class ActivationKeyComparer : IEqualityComparer<ActivationKey>
        {
            internal static readonly ActivationKeyComparer Instance = new();

            public bool Equals(ActivationKey x, ActivationKey y)
            {
                return ReferenceEquals(x.Scope, y.Scope) && ReferenceEquals(x.Registration, y.Registration);
            }

            public int GetHashCode(ActivationKey obj)
            {
                unchecked
                {
                    return RuntimeHelpers.GetHashCode(obj.Scope) * 397 ^ RuntimeHelpers.GetHashCode(obj.Registration);
                }
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class ContainerOwnershipMarker
        {
            internal readonly string ScopeName;
            internal readonly bool IsExternal;

            private ContainerOwnershipMarker(string scopeName, bool isExternal)
            {
                ScopeName = scopeName;
                IsExternal = isExternal;
            }

            internal static ContainerOwnershipMarker Container(string scopeName) =>
                new ContainerOwnershipMarker(scopeName, false);

            internal static ContainerOwnershipMarker External() =>
                new ContainerOwnershipMarker(null, true);
        }
    }
}
