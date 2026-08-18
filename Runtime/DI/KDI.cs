using System;
using System.Threading;
using UnityEngine;

namespace Kylin.DI
{
    /// <summary>KDI static lifecycle and primary-root coordination.</summary>
    public static class KDI
    {
        private static IScope _rootScope;
        private static bool _isAutoRoot;
        private static int _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        internal static IScope RootScope
        {
            get
            {
                EnsureMainThread();
                if (_rootScope == null)
                {
                    Debug.LogWarning("[KDI] No primary RootScope is active. Creating a compatibility AutoRootScope.");
                    _rootScope = new ScopeBuilder().Build(parent: null, name: "AutoRootScope");
                    _isAutoRoot = true;
                }
                return _rootScope;
            }
        }

        internal static void SetRootScope(IScope scope)
        {
            EnsureMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (ReferenceEquals(_rootScope, scope)) return;

            if (_rootScope != null)
            {
                if (_isAutoRoot)
                {
                    _rootScope.Dispose();
                    _rootScope = null;
                    _isAutoRoot = false;
                }
                else
                {
                    throw new InvalidOperationException(
                        "[KDI] More than one primary RootScope was initialized. Assign a parent to the additional LifetimeScope.");
                }
            }

            _rootScope = scope;
            _isAutoRoot = false;
        }

        internal static void ClearRootScope(IScope scope)
        {
            if (!ReferenceEquals(_rootScope, scope)) return;
            _rootScope = null;
            _isAutoRoot = false;
        }

        internal static void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException("[KDI] Scope, injection, lifetime, and update-loop APIs are main-thread only.");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            // One coordinator owns reset order. Unity does not order separate
            // SubsystemRegistration methods across types, and user cleanup can touch
            // the update loop while stale Scopes are being revoked.
            RunResetStage("LifetimeScope state", LifetimeScope.ResetStatic);
            var previousRoot = _rootScope;
            _rootScope = null;
            _isAutoRoot = false;
            if (previousRoot != null && !(previousRoot is Scope))
                RunResetStage("custom root Scope", previousRoot.Dispose);
            RunResetStage("Scope state", Scope.ResetState);
            RunResetStage("injector state", DependencyInjector.ResetState);
            RunResetStage("instantiation staging state", ScopeExtensions.ResetStatic);
            RunResetStage("update-loop state", UpdateLoopManager.ResetStatic);
        }

        private static void RunResetStage(string stage, Action reset)
        {
            try
            {
                reset();
            }
            catch (Exception ex)
            {
                // SubsystemRegistration is the last fail-close boundary. One user
                // cleanup failure must not leave unrelated static state alive for the
                // next play session when Domain Reload is disabled.
                Debug.LogError($"[KDI] Failed to reset {stage}; remaining reset stages will continue.\n{ex}");
            }
        }
    }
}
