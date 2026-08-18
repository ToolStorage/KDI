using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kylin.DI
{
    public enum RootScopeMode
    {
        Primary,
        Isolated
    }

    /// <summary>
    /// Scene hierarchy scope. Dependencies are pushed into IInjectable components;
    /// child LifetimeScope objects remain injection boundaries.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public abstract class LifetimeScope : MonoBehaviour
    {
        private enum LifetimeScopeState
        {
            Inactive,
            Initializing,
            Active,
            Disposing
        }

        private static readonly List<LifetimeScope> _activeScopes = new();
        private static readonly List<LifetimeScope> _cascadeRestartScopes = new();

        internal static void ResetStatic()
        {
            if (_activeScopes.Count > 0)
            {
                var staleScopes = _activeScopes.ToArray();
                for (var i = staleScopes.Length - 1; i >= 0; i--)
                {
                    var stale = staleScopes[i];
                    if (stale == null) continue;
                    try
                    {
                        // Stop behaviours while their previous-session injected fields
                        // are still available, then revoke the stale graph.
                        if (stale.gameObject != null && stale.gameObject.activeSelf)
                            stale.gameObject.SetActive(false);
                        stale.Dispose();
                    }
                    catch (Exception exception) { Debug.LogException(exception, stale); }
                }
                Debug.LogWarning(
                    "[KDI] Live LifetimeScopes survived SubsystemRegistration. They were disposed/deactivated " +
                    "to prevent stale injection state. Disable the simultaneous Domain Reload + Scene Reload " +
                    "override, or reinitialize the scopes explicitly from an editor bootstrap.");
            }
            _activeScopes.Clear();
            _cascadeRestartScopes.Clear();
            SceneManager.sceneLoaded -= ValidateLoadedScene;
            SceneManager.sceneLoaded += ValidateLoadedScene;
        }

        [Header("Scope Hierarchy")]
        [SerializeField]
        [Tooltip("Parent LifetimeScope. When empty, this scope becomes the single primary RootScope.")]
        private LifetimeScope _parent;

        [SerializeField]
        [Tooltip("When enabled, Initialize is called from Awake.")]
        private bool _autoInitialize = true;

        [SerializeField]
        [Tooltip("Parentless scopes are Primary by default. Use Isolated for additive-scene or preview contexts that must not replace the app root.")]
        private RootScopeMode _rootMode = RootScopeMode.Primary;

        private IScope _scope;
        private IScope _runtimeParentScope;
        private LifetimeScope _runtimeParent;
        private LifetimeScopeState _state;
        private int _hierarchyDepth;
        private bool _restartAfterCascade;
        private bool _reactivateAfterCascade;
        private bool _isDestroying;
        private bool _wasActiveBeforeScopeDisposal;

        public IScope Scope => _scope;
        public bool IsInitialized => _state == LifetimeScopeState.Active;

        protected void Awake()
        {
            if (_autoInitialize)
                Initialize();
        }

        protected void OnDestroy()
        {
            _isDestroying = true;
            CancelCascadeRestart();
            try
            {
                // Unity destruction is not a user-requested Dispose. If it occurs from
                // Configure/PostInject/factory code, defer the concrete graph cleanup
                // through the ambient transaction before public state guards run.
                if (_scope is Scope concreteScope && concreteScope.DeferDisposalForDestroyedLifetimeScope())
                {
                    _state = LifetimeScopeState.Disposing;
                    return;
                }
                if (_state == LifetimeScopeState.Initializing && _scope == null)
                {
                    _state = LifetimeScopeState.Inactive;
                    return;
                }
                Dispose();
            }
            finally
            {
                // HandleScopeDisposed runs inside Dispose and must never leave a
                // destroyed Unity wrapper rooted in a static restart queue.
                CancelCascadeRestart();
                _activeScopes.Remove(this);
            }
        }

        public void Initialize()
        {
            KDI.EnsureMainThread();
            ActivationCallbackGuard.ThrowIfConfigureMutation("LifetimeScope.Initialize");
            if (_state == LifetimeScopeState.Active)
                return;
            if (_state == LifetimeScopeState.Initializing)
                throw new InvalidOperationException(
                    $"[KDI] LifetimeScope parent cycle detected while initializing {GetType().Name}.");
            if (_state == LifetimeScopeState.Disposing)
                throw new InvalidOperationException($"[KDI] {GetType().Name} is currently disposing.");

            var wasActiveAtStart = gameObject != null && gameObject.activeSelf;
            var restartingAfterCascade = _restartAfterCascade;
            var reactivateAfterInitialize = _reactivateAfterCascade;
            var wasPendingCascadeRestart = false;
            if (restartingAfterCascade)
            {
                wasPendingCascadeRestart = _cascadeRestartScopes.Remove(this);
                _restartAfterCascade = false;
                _reactivateAfterCascade = false;
            }

            _state = LifetimeScopeState.Initializing;
            try
            {
                ValidateLifecycleMessages();
                if (ReferenceEquals(_parent, this))
                    throw new InvalidOperationException($"[KDI] {GetType().Name} cannot be its own parent.");
                if (ReferenceEquals(_runtimeParent, this))
                    throw new InvalidOperationException($"[KDI] {GetType().Name} cannot be its own runtime parent.");

                IScope parentScope = null;
                if (_parent != null)
                {
                    if (!_parent.IsInitialized)
                        _parent.Initialize();
                    parentScope = _parent.Scope;
                }
                else if (_runtimeParent != null)
                {
                    if (!_runtimeParent.IsInitialized)
                        _runtimeParent.Initialize();
                    parentScope = _runtimeParent.Scope;
                    _runtimeParentScope = parentScope;
                }
                else if (_runtimeParentScope != null)
                {
                    if (_runtimeParentScope is Scope concreteParent && concreteParent.IsDisposed)
                        throw new ObjectDisposedException(concreteParent.Name);
                    parentScope = _runtimeParentScope;
                }

                var builder = new ScopeBuilder();
                using (ActivationCallbackGuard.EnterConfigure())
                {
                    Configure(builder);
                }
                if (this == null || gameObject == null)
                    throw new InvalidOperationException(
                        $"[KDI] {GetType().Name} destroyed its GameObject during Configure.");
                _scope = builder.Build(parentScope, GetType().Name);

                if (_scope is Scope concreteScope)
                {
                    concreteScope.ScopeDisposing += HandleScopeDisposing;
                    concreteScope.ScopeDisposed += HandleScopeDisposed;
                }

                if (_parent == null && _runtimeParent == null && _runtimeParentScope == null &&
                    _rootMode == RootScopeMode.Primary)
                    KDI.SetRootScope(_scope);

                _hierarchyDepth = ComputeDepth(transform);
                _activeScopes.Add(this);

                // The concrete Scope tracks every successful push-injection lease.
                // If a later component fails, disposing the scope revokes the earlier ones.
                InjectSelf();
                InjectChildren();

                _state = LifetimeScopeState.Active;
                RestartCascadeChildren();
                if (restartingAfterCascade && reactivateAfterInitialize && !gameObject.activeSelf)
                    gameObject.SetActive(true);
                Debug.Log($"[LifetimeScope] {GetType().Name} initialized.");
            }
            catch (Exception initializationException)
            {
                // Awake exceptions are not a reliable control-flow signal to the code
                // that called GameObject.SetActive. Preserve the first KDI lifecycle
                // failure in the surrounding prefab activation attempt explicitly.
                UnityActivationAttempt.ReportFailure(this, initializationException);
                // Stop enabled behaviours before revoking their fields. This keeps the
                // failed object graph from running and lets OnDisable observe the still
                // injected state when activation reached that far.
                if (wasActiveAtStart && this != null && gameObject != null && gameObject.activeSelf)
                    gameObject.SetActive(false);

                _activeScopes.Remove(this);
                var failedScope = _scope;
                KDI.ClearRootScope(failedScope);
                _state = LifetimeScopeState.Disposing;
                try { failedScope?.Dispose(); }
                catch (Exception disposeException) { Debug.LogException(disposeException); }
                _scope = null;
                _state = LifetimeScopeState.Inactive;
                if (restartingAfterCascade || wasActiveAtStart)
                {
                    _restartAfterCascade = true;
                    _reactivateAfterCascade = reactivateAfterInitialize || wasActiveAtStart;
                    if (wasPendingCascadeRestart && !_cascadeRestartScopes.Contains(this))
                        _cascadeRestartScopes.Add(this);
                }
                throw;
            }
        }

        public void Dispose()
        {
            KDI.EnsureMainThread();
            if (!_isDestroying)
                ActivationCallbackGuard.ThrowIfConfigureMutation("LifetimeScope.Dispose");
            if (_state == LifetimeScopeState.Inactive)
            {
                CancelCascadeRestart();
                return;
            }
            if (_state == LifetimeScopeState.Initializing)
                throw new InvalidOperationException(
                    $"[KDI] {GetType().Name} cannot be disposed while Initialize/Configure is still running.");
            if (_state == LifetimeScopeState.Disposing)
                return;

            var deactivateAfterDispose = !_isDestroying && gameObject != null && gameObject.activeSelf;
            var previousState = _state;
            _state = LifetimeScopeState.Disposing;
            var currentScope = _scope;
            if (currentScope == null)
            {
                HandleScopeDisposed(null);
                return;
            }

            try
            {
                if (_isDestroying && currentScope is Scope concreteScope &&
                    concreteScope.DeferDisposalForDestroyedLifetimeScope())
                {
                    return;
                }
                if (_isDestroying && currentScope is Scope destroyedOwnerScope)
                    destroyedOwnerScope.DisposeFromDestroyedLifetimeScope();
                else
                    currentScope.Dispose();
            }
            catch
            {
                _state = previousState;
                throw;
            }
            // Custom IScope implementations do not raise ScopeDisposed.
            if (_scope != null)
                HandleScopeDisposed(currentScope as Scope);

            if (deactivateAfterDispose && this != null && gameObject != null)
            {
                _restartAfterCascade = true;
                _reactivateAfterCascade = true;
                gameObject.SetActive(false);
            }
        }

        private void HandleScopeDisposed(Scope disposedScope)
        {
            if (disposedScope != null && !ReferenceEquals(_scope, disposedScope))
                return;

            if (_scope is Scope concreteScope)
            {
                concreteScope.ScopeDisposing -= HandleScopeDisposing;
                concreteScope.ScopeDisposed -= HandleScopeDisposed;
            }

            var wasCascaded = _state == LifetimeScopeState.Active || _state == LifetimeScopeState.Initializing;
            var oldScope = _scope;
            _activeScopes.Remove(this);
            KDI.ClearRootScope(oldScope);
            _scope = null;
            _state = LifetimeScopeState.Inactive;

            if (wasCascaded && !_isDestroying && this != null && gameObject != null)
            {
                _restartAfterCascade = true;
                _reactivateAfterCascade = _wasActiveBeforeScopeDisposal || gameObject.activeSelf;
                if (!_cascadeRestartScopes.Contains(this))
                    _cascadeRestartScopes.Add(this);
                if (gameObject.activeSelf)
                    gameObject.SetActive(false);
            }
            else if (_isDestroying)
            {
                CancelCascadeRestart();
            }
            _wasActiveBeforeScopeDisposal = false;
            Debug.Log($"[LifetimeScope] {GetType().Name} disposed.");
        }

        private void HandleScopeDisposing(Scope disposingScope)
        {
            if (!ReferenceEquals(_scope, disposingScope) || _isDestroying || this == null || gameObject == null)
                return;

            _wasActiveBeforeScopeDisposal = gameObject.activeSelf;
            if (_wasActiveBeforeScopeDisposal)
                gameObject.SetActive(false);
        }

        internal bool HasSerializedParent => _parent != null;

        internal void PrepareRuntimeParent(IScope parentScope, LifetimeScope parentOwner)
        {
            KDI.EnsureMainThread();
            ValidateLifecycleMessages();
            if (_parent != null) return;
            if (_state != LifetimeScopeState.Inactive)
                throw new InvalidOperationException(
                    $"[KDI] Runtime parent for {GetType().Name} must be assigned before initialization.");
            if (parentScope == null && parentOwner == null)
                throw new ArgumentNullException(nameof(parentScope));
            if (ReferenceEquals(parentOwner, this))
                throw new InvalidOperationException($"[KDI] {GetType().Name} cannot be its own runtime parent.");

            _runtimeParent = parentOwner;
            _runtimeParentScope = parentOwner != null && parentOwner.IsInitialized
                ? parentOwner.Scope
                : parentScope;
        }

        private static void ValidateLoadedScene(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var scopes = roots[i].GetComponentsInChildren<LifetimeScope>(true);
                for (var j = 0; j < scopes.Length; j++)
                {
                    var scope = scopes[j];
                    if (scope == null) continue;
                    try { scope.ValidateLifecycleMessages(); }
                    catch (Exception exception)
                    {
                        // A derived Awake can hide the framework Awake, so Initialize
                        // never gets a chance to fail closed. Scene validation is the
                        // runtime fallback: quarantine only the invalid hierarchy and
                        // continue validating the rest of the loaded scene.
                        if (scope.gameObject != null && scope.gameObject.activeSelf)
                            scope.gameObject.SetActive(false);
                        Debug.LogException(exception, scope);
                    }
                }
            }
        }

        internal static LifetimeScope FindOwner(IScope scope)
        {
            for (var i = 0; i < _activeScopes.Count; i++)
            {
                var candidate = _activeScopes[i];
                if (candidate != null && ReferenceEquals(candidate._scope, scope))
                    return candidate;
            }
            return null;
        }

        private void RestartCascadeChildren()
        {
            if (_cascadeRestartScopes.Count == 0) return;
            var snapshot = _cascadeRestartScopes.ToArray();
            for (var i = 0; i < snapshot.Length; i++)
            {
                var child = snapshot[i];
                if (child == null)
                {
                    _cascadeRestartScopes.Remove(child);
                    continue;
                }
                if (!ReferenceEquals(child._parent, this) && !ReferenceEquals(child._runtimeParent, this))
                    continue;

                child._runtimeParentScope = _scope;
                child.Initialize();
            }
        }

        private void CancelCascadeRestart()
        {
            _cascadeRestartScopes.Remove(this);
            _restartAfterCascade = false;
            _reactivateAfterCascade = false;
        }

        private void ValidateLifecycleMessages()
        {
            for (var type = GetType(); type != null && type != typeof(LifetimeScope); type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                if (DeclaresParameterless(type, nameof(Awake), flags) ||
                    DeclaresParameterless(type, nameof(OnDestroy), flags))
                {
                    throw new InvalidOperationException(
                        $"[KDI] {type.FullName} declares Awake/OnDestroy and bypasses LifetimeScope lifecycle. " +
                        "Use Configure and injected collaborators instead of Unity lifecycle messages on a LifetimeScope.");
                }
            }
        }

        private static bool DeclaresParameterless(Type type, string name, BindingFlags flags)
        {
            var methods = type.GetMember(name, MemberTypes.Method, flags);
            for (var i = 0; i < methods.Length; i++)
            {
                if (methods[i] is MethodInfo method && method.GetParameters().Length == 0)
                    return true;
            }
            return false;
        }

        private void InjectSelf()
        {
            var injectables = GetComponents<IInjectable>();
            foreach (var injectable in injectables)
            {
                if (injectable is LifetimeScope) continue;
                injectable.Inject(_scope);
            }

            WarnNonInjectableComponents(gameObject);
        }

        private void InjectChildren() => InjectHierarchy(transform);

        private void InjectHierarchy(Transform current)
        {
            for (var i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);
                if (child.TryGetComponent<LifetimeScope>(out _))
                    continue;

                var injectables = child.GetComponents<IInjectable>();
                foreach (var injectable in injectables)
                    injectable.Inject(_scope);

                WarnNonInjectableComponents(child.gameObject);
                InjectHierarchy(child);
            }
        }

        private static void WarnNonInjectableComponents(GameObject target)
        {
            var behaviours = target.GetComponents<MonoBehaviour>();
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null || behaviour is IInjectable || behaviour is LifetimeScope) continue;
                DependencyInjector.WarnIfHasInjectFieldsWithoutIInjectable(behaviour);
            }
        }

        public static LifetimeScope Find(Transform from) => FindInternal(from);
        public static LifetimeScope Find(GameObject from) => FindInternal(from?.transform);
        public static LifetimeScope Find(Component from) => FindInternal(from?.transform);

        private static LifetimeScope FindInternal(Transform from)
        {
            if (from == null) return null;

            LifetimeScope best = null;
            var bestDepth = -1;
            for (var i = 0; i < _activeScopes.Count; i++)
            {
                var scope = _activeScopes[i];
                if (scope == null || scope._scope == null ||
                    scope._state != LifetimeScopeState.Active && scope._state != LifetimeScopeState.Initializing)
                    continue;

                if (!from.IsChildOf(scope.transform) || scope._hierarchyDepth <= bestDepth)
                    continue;

                bestDepth = scope._hierarchyDepth;
                best = scope;
            }
            return best;
        }

        public static LifetimeScope FindRoot()
        {
            for (var i = 0; i < _activeScopes.Count; i++)
            {
                var scope = _activeScopes[i];
                if (scope != null && scope._parent == null && scope._runtimeParent == null &&
                    scope._runtimeParentScope == null && scope._rootMode == RootScopeMode.Primary &&
                    scope._state == LifetimeScopeState.Active)
                    return scope;
            }
            return null;
        }

        private static int ComputeDepth(Transform value)
        {
            var depth = 0;
            while (value.parent != null)
            {
                depth++;
                value = value.parent;
            }
            return depth;
        }

        protected abstract void Configure(ScopeBuilder builder);
    }
}
