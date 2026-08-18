using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace Kylin.DI
{
    public static class DependencyInjector
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> _fieldCache = new();
        private static ConditionalWeakTable<object, ActiveInjection> _activeInjections = new();

        public static FieldInfo[] GetCachedInjectableFields(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            return _fieldCache.GetOrAdd(type, t =>
            {
                var fieldList = new List<FieldInfo>();
                var currentType = t;
                while (currentType != null && currentType != typeof(MonoBehaviour) && currentType != typeof(object))
                {
                    var fields = currentType
                        .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                        .Where(f => f.GetCustomAttribute<InjectAttribute>() != null);
                    fieldList.AddRange(fields);
                    currentType = currentType.BaseType;
                }
                return fieldList.ToArray();
            });
        }

        /// <summary>
        /// 지정한 Scope로 target을 원자적으로 주입한다. 실패는 로그로 변환하지 않고
        /// 호출자에게 전달하며, 이미 변경한 필드는 이전 값으로 복원한다.
        /// </summary>
        public static void Inject(this IInjectable target, IScope scope)
        {
            KDI.EnsureMainThread();
            ActivationCallbackGuard.ThrowIfConfigureMutation("DependencyInjector.Inject");
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            var concreteScope = ResolverAuthorityGuard.RequireConcreteScope(scope, "Injection");

            concreteScope.ExecuteInActivationTransaction(() =>
            {
                var lease = InjectWithLease(target, concreteScope, observeExternalIdentity: true);
                if (lease != null)
                    concreteScope.TrackExternalInjection(lease);
            });
        }

        internal static InjectionLease InjectWithLease(
            IInjectable target,
            IScope scope,
            bool observeExternalIdentity = false)
        {
            KDI.EnsureMainThread();
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            var concreteScope = ResolverAuthorityGuard.RequireConcreteScope(scope, "Injection");
            scope = concreteScope;
            if (target is UnityEngine.Object unityObject && unityObject == null)
                throw new ArgumentException("[KDI] A destroyed Unity object cannot be injected.", nameof(target));
            if (UpdateLoopManager.IsManuallyRegistered(target))
                throw new InvalidOperationException(
                    $"[KDI] {target.GetType().Name} is manually registered with UpdateLoopManager and cannot be injected. " +
                    "Unregister it first, then expose it through FromInstance when Scope-managed update ownership is required.");

            if (!TryBeginInjection(target, scope, out var ownership))
                return null;

            var behaviour = target as DIBehaviour;
            FieldInfo[] fields;
            try
            {
                if (observeExternalIdentity)
                    concreteScope.ObserveExternalInjectionIdentity(target);
                fields = GetCachedInjectableFields(target.GetType());
            }
            catch
            {
                ReleaseFailedInjection(target, ownership);
                throw;
            }
            var resolved = new object[fields.Length];
            var previous = new object[fields.Length];

            var assignedCount = 0;
            var postInjectStarted = false;
            var postInjectCompleted = false;
            try
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    if (fields[i].IsInitOnly)
                        throw new InvalidOperationException(
                            $"[KDI] readonly [Inject] field is not supported: {target.GetType().Name}.{fields[i].Name}");

                    if (ResolverAuthorityGuard.IsResolverType(fields[i].FieldType))
                    {
                        throw new InvalidOperationException(
                            $"[KDI] [Inject] field {target.GetType().Name}.{fields[i].Name} requests " +
                            $"resolver authority ({fields[i].FieldType.Name}). Inject explicit dependencies or " +
                            "IInstantiator instead; resolver capabilities cannot outlive activation.");
                    }

                    try
                    {
                        resolved[i] = concreteScope.ResolveInjectedDependency(fields[i].FieldType);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"[KDI] Failed to resolve {target.GetType().Name}.{fields[i].Name} ({fields[i].FieldType.Name}).", ex);
                    }

                    if (resolved[i] == null)
                        throw new InvalidOperationException(
                            $"[KDI] Resolve returned null for {target.GetType().Name}.{fields[i].Name} ({fields[i].FieldType.Name}).");

                    previous[i] = fields[i].GetValue(target);
                }

                // A later dependency factory can destroy this external Unity target
                // before its InjectionLease/lifetime host exists. Validate after all
                // resolves but before PrepareInjection, field assignment, or any user
                // lifecycle callback can observe that invalid graph.
                ValidateActivationGraph(scope);
                ValidateActivationUnityObjects(target, fields, resolved);
                behaviour?.PrepareInjection(scope);

                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i].SetValue(target, resolved[i]);
                    assignedCount++;
                }

                if (target is IPostInjectable postInjectable)
                {
                    postInjectStarted = true;
                    postInjectable.PostInject();
                }
                postInjectCompleted = true;

                ValidateActivationGraph(scope);
                ValidateActivationUnityObjects(target, fields, resolved);
                behaviour?.CompleteInjection();
                // CompleteInjection can enter OnInjectedEnable immediately, so verify
                // once more after every activation callback has returned.
                ValidateActivationGraph(scope);
                ValidateActivationUnityObjects(target, fields, resolved);
                var lease = new InjectionLease(target, scope, fields, previous, behaviour, postInjectCompleted);
                ownership.Complete(lease);
                lease.AttachToUnityLifetime(concreteScope);
                return lease;
            }
            catch (Exception ex)
            {
                if (ownership.Lease != null)
                {
                    try { ownership.Lease.Dispose(); }
                    catch (Exception cleanupException) { Debug.LogException(cleanupException); }
                    ReleaseFailedInjection(target, ownership);
                    throw new InvalidOperationException($"[KDI] Injection failed: {target.GetType().Name}.", ex);
                }

                if (behaviour != null)
                {
                    behaviour.AbortInjection(scope, postInjectStarted || postInjectCompleted);
                }
                else if (postInjectStarted && target is IPreUninjectable preUninjectable)
                {
                    try
                    {
                        using (ActivationCallbackGuard.EnterLifecycle())
                            preUninjectable.PreUninject();
                    }
                    catch (Exception cleanupException) { Debug.LogException(cleanupException); }
                }
                for (var i = assignedCount - 1; i >= 0; i--)
                {
                    try { fields[i].SetValue(target, previous[i]); }
                    catch (Exception restoreException)
                    {
                        Debug.LogException(restoreException);
                    }
                }

                ReleaseFailedInjection(target, ownership);

                throw new InvalidOperationException($"[KDI] Injection failed: {target.GetType().Name}.", ex);
            }
        }

        private static void ValidateActivationUnityObjects(
            IInjectable target,
            FieldInfo[] fields,
            object[] resolved)
        {
            if (target is UnityEngine.Object unityTarget && unityTarget == null)
            {
                throw new InvalidOperationException(
                    $"[KDI] {target.GetType().Name} destroyed itself during PostInject/activation.");
            }

            for (var i = 0; i < resolved.Length; i++)
            {
                if (!(resolved[i] is UnityEngine.Object unityDependency) || unityDependency != null) continue;
                throw new InvalidOperationException(
                    $"[KDI] {target.GetType().Name}.{fields[i].Name} was destroyed during PostInject/activation.");
            }
        }

        private static void ValidateActivationGraph(IScope scope)
        {
            (scope as Scope)?.ThrowIfActivationGraphInvalid();
        }

        public static bool TryInject(this IInjectable target, IScope scope, out Exception error)
        {
            try
            {
                target.Inject(scope);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        public static void WarnIfHasInjectFieldsWithoutIInjectable(object target)
        {
            if (target == null || target is IInjectable) return;

            var fields = GetCachedInjectableFields(target.GetType());
            if (fields.Length == 0) return;

            var fieldNames = string.Join(", ", fields.Select(f => f.Name));
            Debug.LogWarning(
                $"[KDI] {target.GetType().Name} has [Inject] fields ({fieldNames}) but does not implement IInjectable. Injection was skipped.");
        }

        internal static void ReleaseOwnership(object target, IScope scope, InjectionLease lease)
        {
            if (target == null) return;
            if (!_activeInjections.TryGetValue(target, out var ownership)) return;
            if (!ReferenceEquals(ownership.Scope, scope) || !ReferenceEquals(ownership.Lease, lease)) return;
            _activeInjections.Remove(target);
        }

        internal static bool IsActivelyInjected(object target)
        {
            return target != null && _activeInjections.TryGetValue(target, out _);
        }

        private static bool TryBeginInjection(object target, IScope scope, out ActiveInjection ownership)
        {
            if (_activeInjections.TryGetValue(target, out var active))
            {
                if (ReferenceEquals(active.Scope, scope) && active.IsComplete)
                {
                    ownership = active;
                    return false;
                }

                var state = active.IsComplete ? "already injected" : "currently being injected";
                throw new InvalidOperationException(
                    $"[KDI] {target.GetType().Name} is {state} by Scope {DescribeScope(active.Scope)}. " +
                    $"Re-injection by Scope {DescribeScope(scope)} is not allowed until the first injection is revoked.");
            }

            ownership = new ActiveInjection(scope);
            _activeInjections.Add(target, ownership);
            return true;
        }

        private static void ReleaseFailedInjection(object target, ActiveInjection ownership)
        {
            if (_activeInjections.TryGetValue(target, out var active) && ReferenceEquals(active, ownership))
                _activeInjections.Remove(target);
        }

        private static string DescribeScope(IScope scope)
        {
            return scope is Scope concrete ? $"'{concrete.Name}'" : $"'{scope?.GetType().Name ?? "null"}'";
        }

        public static void ClearCache()
        {
            _fieldCache.Clear();
        }

        internal static void ResetState()
        {
            _fieldCache.Clear();
            _activeInjections = new ConditionalWeakTable<object, ActiveInjection>();
        }

        private sealed class ActiveInjection
        {
            internal readonly IScope Scope;
            internal InjectionLease Lease { get; private set; }
            internal bool IsComplete => Lease != null;

            internal ActiveInjection(IScope scope) => Scope = scope;
            internal void Complete(InjectionLease lease) => Lease = lease;
        }
    }

    internal sealed class InjectionLease : IDisposable, IUnityLifetimeMonitorLease
    {
        private object _target;
        private readonly IScope _scope;
        private readonly FieldInfo[] _fields;
        private readonly object[] _previous;
        private readonly DIBehaviour _behaviour;
        private readonly bool _postInjectCompleted;
        private InjectionLifetimeHost _lifetimeHost;
        private UpdateLoopManager _lifetimeMonitor;
        private CancellationTokenRegistration _destroyRegistration;
        private bool _hasDestroyRegistration;
        private bool _cleanupRevoked;
        internal object Target => _target;
        internal bool RequiresPolling { get; }
        public bool IsTargetDestroyed =>
            _target is UnityEngine.Object unityTarget && unityTarget == null;
        internal InjectionLease(
            object target,
            IScope scope,
            FieldInfo[] fields,
            object[] previous,
            DIBehaviour behaviour,
            bool postInjectCompleted)
        {
            _target = target;
            _scope = scope;
            _fields = fields;
            _previous = previous;
            _behaviour = behaviour;
            _postInjectCompleted = postInjectCompleted;
            RequiresPolling = target is UnityEngine.Object unityTarget &&
                              !(unityTarget is MonoBehaviour) && !(unityTarget is GameObject);
        }

        public void Dispose()
        {
            Exception firstError = null;
            try { RevokeCleanup(); }
            catch (Exception ex) { firstError = ex; }
            try { RestoreFields(); }
            catch (Exception ex) when (firstError == null) { firstError = ex; }
            catch (Exception ex) { Debug.LogException(ex); }

            if (firstError != null) throw firstError;
        }

        internal void AttachToUnityLifetime(Scope owner)
        {
            if (!(_target is UnityEngine.Object unityTarget) || unityTarget == null) return;

            // MonoBehaviour destruction is immediate through its token and a whole
            // GameObject is covered by the host. Poll only hostless objects and
            // non-MonoBehaviour Components that can be removed independently.
            var monitor = RequiresPolling ? UpdateLoopManager.GetOrCreateForKDI() : null;
            if (monitor != null)
            {
                _lifetimeMonitor = monitor;
                monitor.RegisterUnityLifetime(this);
            }

            if (!(_target is Component component) || component == null) return;
            if (component is MonoBehaviour monoBehaviour)
            {
                _hasDestroyRegistration = true;
                _destroyRegistration = monoBehaviour.destroyCancellationToken.Register(
                    () => HandleUnityDestroyed(owner));
            }
            var host = component.GetComponent<InjectionLifetimeHost>();
            if (host == null)
                host = component.gameObject.AddComponent<InjectionLifetimeHost>();
            _lifetimeHost = host;
            host.Track(owner, this);
        }

        private void HandleUnityDestroyed(Scope owner)
        {
            // CancellationTokenRegistration.Dispose may wait for an in-flight callback.
            // Clear ownership first because this method is that callback.
            _hasDestroyRegistration = false;
            _destroyRegistration = default;
            owner?.ReleaseInjectionLease(this);
        }

        public void HandleMonitoredTargetDestroyed()
        {
            (_scope as Scope)?.ReleaseInjectionLease(this);
        }

        public void RebindMonitor(UpdateLoopManager previous, UpdateLoopManager replacement)
        {
            if (ReferenceEquals(_lifetimeMonitor, previous))
                _lifetimeMonitor = replacement;
        }

        /// <summary>
        /// Runs the injection lifecycle cleanup while injected fields are still available.
        /// Scope uses this before disposing a container-owned instance.
        /// </summary>
        internal void RevokeCleanup()
        {
            var target = _target;
            if (target == null || _cleanupRevoked) return;
            _cleanupRevoked = true;
            // Scope/rollback has taken over the lease. Detach before user cleanup so
            // an OnBeforeUninject callback that destroys the GameObject cannot re-enter
            // Scope cleanup through InjectionLifetimeHost.
            DetachLifetimeHost();

            if (_postInjectCompleted)
            {
                if (_behaviour != null)
                    _behaviour.RevokeInjection(_scope);
                else if (target is IPreUninjectable preUninjectable)
                {
                    using (ActivationCallbackGuard.EnterLifecycle())
                        preUninjectable.PreUninject();
                }
            }
        }

        internal void RestoreFields()
        {
            var target = _target;
            if (target == null)
            {
                DetachLifetimeHost();
                return;
            }
            _target = null;

            Exception firstError = null;

            for (var i = _fields.Length - 1; i >= 0; i--)
            {
                try { _fields[i].SetValue(target, _previous[i]); }
                catch (Exception ex) when (firstError == null)
                {
                    firstError = ex;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            DependencyInjector.ReleaseOwnership(target, _scope, this);
            DetachLifetimeHost();

            if (firstError != null) throw firstError;
        }

        private void DetachLifetimeHost()
        {
            var monitor = _lifetimeMonitor;
            _lifetimeMonitor = null;
            if (monitor != null)
                monitor.UnregisterUnityLifetime(this);

            if (_hasDestroyRegistration)
            {
                _hasDestroyRegistration = false;
                _destroyRegistration.Dispose();
                _destroyRegistration = default;
            }

            var host = _lifetimeHost;
            _lifetimeHost = null;
            if (host != null)
                host.Detach(this);
        }
    }
}
