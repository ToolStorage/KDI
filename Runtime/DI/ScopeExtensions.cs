using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kylin.DI
{
    public static class ScopeExtensions
    {
        private static Transform _inactiveStagingRoot;

        internal static void ResetStatic()
        {
            if (_inactiveStagingRoot != null)
            {
                var staleObject = _inactiveStagingRoot.gameObject;
                _inactiveStagingRoot = null;
                if (staleObject != null)
                {
                    if (Application.isPlaying)
                        UnityEngine.Object.Destroy(staleObject);
                    else
                        UnityEngine.Object.DestroyImmediate(staleObject);
                }
            }
            else
            {
                _inactiveStagingRoot = null;
            }
        }

        /// <summary>
        /// Push-injects a hierarchy. A nested LifetimeScope is an ownership boundary.
        /// If any target fails, injections performed by this call are revoked.
        /// </summary>
        public static void InjectGameObject(this IScope scope, GameObject gameObject)
        {
            KDI.EnsureMainThread();
            ActivationCallbackGuard.ThrowIfConfigureMutation("Scope.InjectGameObject");
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (gameObject == null) throw new ArgumentNullException(nameof(gameObject));

            var concreteScope = ResolverAuthorityGuard.RequireConcreteScope(scope, "InjectGameObject");
            concreteScope.ExecuteInActivationTransaction(() => InjectHierarchy(concreteScope, gameObject.transform));
        }

        private static void InjectHierarchy(IScope scope, Transform current)
        {
            if (current == null) return;
            if (current.TryGetComponent<LifetimeScope>(out _)) return;

            var injectables = current.GetComponents<IInjectable>();
            foreach (var injectable in injectables)
                injectable.Inject(scope);

            var behaviours = current.GetComponents<MonoBehaviour>();
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null || behaviour is IInjectable || behaviour is LifetimeScope) continue;
                DependencyInjector.WarnIfHasInjectFieldsWithoutIInjectable(behaviour);
            }

            for (var i = 0; i < current.childCount; i++)
                InjectHierarchy(scope, current.GetChild(i));
        }

        /// <summary>
        /// Instantiates under an inactive staging parent, injects the complete hierarchy,
        /// and only then moves it into its final active hierarchy.
        /// PostInject therefore runs before Awake/OnEnable for active prefabs.
        /// </summary>
        public static GameObject Instantiate(this IScope scope, GameObject prefab)
        {
            return InstantiatePrepared(scope, prefab, null, null, null, false);
        }

        public static GameObject Instantiate(this IScope scope, GameObject prefab, Transform parent)
        {
            return InstantiatePrepared(scope, prefab, parent, null, null, false);
        }

        public static GameObject Instantiate(this IScope scope, GameObject prefab, Vector3 position, Quaternion rotation)
        {
            return InstantiatePrepared(scope, prefab, null, position, rotation, true);
        }

        public static GameObject Instantiate(
            this IScope scope,
            GameObject prefab,
            Vector3 position,
            Quaternion rotation,
            Transform parent)
        {
            return InstantiatePrepared(scope, prefab, parent, position, rotation, true);
        }

        private static GameObject InstantiatePrepared(
            IScope scope,
            GameObject prefab,
            Transform parent,
            Vector3? position,
            Quaternion? rotation,
            bool preserveWorldTransform)
        {
            KDI.EnsureMainThread();
            ActivationCallbackGuard.ThrowIfConfigureMutation("Scope.Instantiate");
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            var hasRequestedParent = !ReferenceEquals(parent, null);
            if (hasRequestedParent && parent == null)
                throw new ArgumentException("[KDI] Instantiate parent has already been destroyed.", nameof(parent));
            var activateAfterCommit = prefab.activeSelf;
            if (activateAfterCommit && hasRequestedParent && !parent.gameObject.activeInHierarchy)
            {
                throw new InvalidOperationException(
                    "[KDI] An active prefab cannot be instantiated under an inactive hierarchy. " +
                    "Its Awake/OnEnable callbacks would run later, outside KDI's atomic activation attempt. " +
                    "Activate the destination hierarchy first or use an intentionally inactive prefab.");
            }
            var concreteScope = ResolverAuthorityGuard.RequireConcreteScope(scope, "Instantiate");
            if (concreteScope.IsActivationInProgress)
            {
                throw new InvalidOperationException(
                    "[KDI] Instantiate cannot run inside a factory, PostInject, or another activation. " +
                    "Create Unity objects after the owning graph has committed so activation rollback cannot leave a live clone.");
            }

            GameObject instance = null;
            var activationAttempt = UnityActivationAttempt.Begin();
            try
            {
                GameObject PrepareAndPlace()
                {
                    var destinationScene = hasRequestedParent ? parent.gameObject.scene : SceneManager.GetActiveScene();
                    var stagingRoot = GetInactiveStagingRoot(destinationScene);
                    var stagingSentinel = GetStagingSentinel(stagingRoot);
                    instance = UnityEngine.Object.Instantiate(prefab, stagingRoot, false);
                    activationAttempt.BindRoot(instance);

                    // Preserve the prefab's desired state separately, but make the
                    // clone root inactive before any KDI preparation or user callback.
                    // An active staging hierarchy alone can therefore never release
                    // Awake/OnEnable from the clone while injection is in progress.
                    if (instance.activeSelf)
                        instance.SetActive(false);

                    concreteScope?.TrackOwnedGameObject(instance);
                    var stagingGuard = stagingSentinel.Arm(instance.transform);
                    try
                    {
                        PrepareRuntimeChildScopes(concreteScope, instance);

                        if (position.HasValue) instance.transform.position = position.Value;
                        if (rotation.HasValue) instance.transform.rotation = rotation.Value;

                        concreteScope.InjectGameObject(instance);

                        if (instance == null)
                        {
                            throw new InvalidOperationException(
                                "[KDI] The prefab root was destroyed during clone injection.");
                        }

                        // KDI owns staging placement and activation until injection
                        // commits. The guard remembers transient staging activation or
                        // hierarchy movement even when PostInject restores the final
                        // Transform state before returning.
                        var escapedStaging = stagingRoot == null ||
                                             instance.transform.parent != stagingRoot ||
                                             stagingRoot.parent != null ||
                                             stagingRoot.gameObject.activeSelf ||
                                             instance.activeInHierarchy;
                        if (stagingGuard.Mutation != null ||
                            stagingSentinel == null ||
                            !stagingSentinel.enabled ||
                            escapedStaging)
                        {
                            var mutation = stagingGuard.Mutation;
                            throw new InvalidOperationException(
                                "[KDI] PostInject changed or transiently escaped the clone's inactive staging hierarchy. " +
                                "Root placement and activation are owned by Scope.Instantiate until injection commits." +
                                (mutation == null ? string.Empty : " Detected mutation: " + mutation));
                        }

                        // The clone root is deliberately false throughout preparation,
                        // regardless of the prefab's desired final state. A root left
                        // true by user code would make lifecycle timing depend on an
                        // internal staging detail, so final activation remains KDI-owned.
                        if (instance.activeSelf)
                        {
                            throw new InvalidOperationException(
                                "[KDI] PostInject changed the clone root's activeSelf value. " +
                                "Root activation is owned by Scope.Instantiate until injection commits; change a child " +
                                "object or perform root activation after Instantiate returns instead.");
                        }

                        if (hasRequestedParent && parent == null)
                        {
                            throw new InvalidOperationException(
                                "[KDI] Instantiate destination parent was destroyed during clone injection. " +
                                "The owned clone will be rolled back instead of being placed at the scene root.");
                        }
                    }
                    finally
                    {
                        // Disarm before KDI performs the legitimate final reparenting.
                        // Restore also quarantines a staging object that user code
                        // activated or moved before the outer rollback destroys clone.
                        stagingGuard.Dispose();
                        RestoreStagingRoot(stagingRoot, destinationScene);
                    }

                    if (hasRequestedParent)
                    {
                        instance.transform.SetParent(parent, preserveWorldTransform);
                        if (activateAfterCommit && !parent.gameObject.activeInHierarchy)
                        {
                            throw new InvalidOperationException(
                                "[KDI] The destination hierarchy became inactive during prefab preparation. " +
                                "KDI cannot defer active-prefab callbacks beyond the atomic activation attempt.");
                        }
                    }
                    else
                    {
                        instance.transform.SetParent(null, true);
                        if (destinationScene.IsValid() && destinationScene.isLoaded && instance.scene != destinationScene)
                            SceneManager.MoveGameObjectToScene(instance, destinationScene);
                    }

                    return instance;
                }

                concreteScope.ExecuteInActivationTransaction(PrepareAndPlace);

                if (activateAfterCommit)
                {
                    if (hasRequestedParent && (parent == null || !parent.gameObject.activeInHierarchy))
                    {
                        throw new InvalidOperationException(
                            "[KDI] The destination hierarchy became unavailable before final prefab activation.");
                    }
                    instance.SetActive(true);
                    if (instance == null || !instance.activeInHierarchy)
                    {
                        throw new InvalidOperationException(
                            "[KDI] An active prefab did not remain active after its synchronous activation callbacks. " +
                            "The clone will be rolled back instead of returning a deferred or partial graph.");
                    }
                }
                // Unity may log and swallow Awake/OnEnable exceptions. KDI lifecycle
                // callbacks signal the attempt explicitly, so success is decided only
                // after the complete synchronous activation pass has returned.
                activationAttempt.ThrowIfFailed();
                activationAttempt.Complete();
                return instance;
            }
            catch
            {
                // The ordinary Scope transaction has already committed by the time an
                // active clone enters Awake/OnEnable. Reverse those committed records,
                // plus any Scope graphs constructed by the callbacks, before falling
                // back to direct clone destruction.
                try { activationAttempt.Rollback(); }
                catch (Exception rollbackException) { Debug.LogException(rollbackException); }
                if (instance != null)
                {
                    if (Application.isPlaying)
                        UnityEngine.Object.Destroy(instance);
                    else
                        UnityEngine.Object.DestroyImmediate(instance);
                }
                throw;
            }
            finally
            {
                activationAttempt.Dispose();
            }
        }

        private static void PrepareRuntimeChildScopes(IScope scope, GameObject instance)
        {
            var lifetimeScopes = instance.GetComponentsInChildren<LifetimeScope>(true);
            if (lifetimeScopes.Length == 0) return;

            var externalOwner = LifetimeScope.FindOwner(scope);
            var root = instance.transform;
            for (var i = 0; i < lifetimeScopes.Length; i++)
            {
                var lifetimeScope = lifetimeScopes[i];
                if (lifetimeScope == null || lifetimeScope.HasSerializedParent) continue;

                LifetimeScope nearestOwner = null;
                var current = lifetimeScope.transform.parent;
                while (current != null && (ReferenceEquals(current, root) || current.IsChildOf(root)))
                {
                    if (current.TryGetComponent<LifetimeScope>(out nearestOwner))
                        break;
                    current = current.parent;
                }

                lifetimeScope.PrepareRuntimeParent(scope, nearestOwner ?? externalOwner);
            }
        }

        private static Transform GetInactiveStagingRoot(Scene destinationScene)
        {
            if (_inactiveStagingRoot != null)
            {
                RestoreStagingRoot(_inactiveStagingRoot, destinationScene);
                return _inactiveStagingRoot;
            }

            var gameObject = new GameObject("[KDI] Inactive Injection Staging")
            {
                hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave
            };
            gameObject.SetActive(false);
            if (destinationScene.IsValid() && destinationScene.isLoaded && gameObject.scene != destinationScene)
                SceneManager.MoveGameObjectToScene(gameObject, destinationScene);
            _inactiveStagingRoot = gameObject.transform;
            return _inactiveStagingRoot;
        }

        private static InjectionStagingSentinel GetStagingSentinel(Transform stagingRoot)
        {
            var sentinel = stagingRoot.GetComponent<InjectionStagingSentinel>();
            if (sentinel == null)
                sentinel = stagingRoot.gameObject.AddComponent<InjectionStagingSentinel>();

            sentinel.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
            sentinel.enabled = true;
            return sentinel;
        }

        private static void RestoreStagingRoot(Transform stagingRoot, Scene destinationScene)
        {
            if (stagingRoot == null) return;

            var stagingObject = stagingRoot.gameObject;
            if (stagingObject.activeSelf)
                stagingObject.SetActive(false);
            if (stagingRoot.parent != null)
                stagingRoot.SetParent(null, false);
            if (destinationScene.IsValid() && destinationScene.isLoaded && stagingObject.scene != destinationScene)
                SceneManager.MoveGameObjectToScene(stagingObject, destinationScene);
        }
    }

    /// <summary>
    /// Internal, non-serialized observer for the reusable inactive staging object.
    /// A managed guard carries the mutation result so destroying this component cannot
    /// erase an already-started attempt's evidence. No sentinel is attached to a clone.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    internal sealed class InjectionStagingSentinel : MonoBehaviour
    {
        private InjectionStagingGuard _guard;

        internal InjectionStagingGuard Arm(Transform expectedCloneRoot)
        {
            if (_guard != null)
            {
                throw new InvalidOperationException(
                    "[KDI] The inactive injection staging sentinel is already in use.");
            }
            if (expectedCloneRoot == null || expectedCloneRoot.parent != transform)
            {
                throw new InvalidOperationException(
                    "[KDI] The clone root was not placed directly under the inactive staging hierarchy.");
            }

            _guard = new InjectionStagingGuard(this);
            return _guard;
        }

        internal void Disarm(InjectionStagingGuard guard)
        {
            if (ReferenceEquals(_guard, guard))
                _guard = null;
        }

        private void OnEnable()
        {
            _guard?.Record("the staging GameObject became active in the hierarchy");
        }

        private void OnTransformParentChanged()
        {
            _guard?.Record("the staging root's parent changed");
        }

        private void OnTransformChildrenChanged()
        {
            _guard?.Record("a direct child entered or left the staging root");
        }

        private void OnDestroy()
        {
            _guard?.Record("the staging sentinel was destroyed");
        }
    }

    internal sealed class InjectionStagingGuard : IDisposable
    {
        private InjectionStagingSentinel _owner;
        private bool _disposed;

        internal InjectionStagingGuard(InjectionStagingSentinel owner)
        {
            _owner = owner;
        }

        internal string Mutation { get; private set; }

        internal void Record(string mutation)
        {
            if (!_disposed && Mutation == null)
                Mutation = mutation;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var owner = _owner;
            _owner = null;
            owner?.Disarm(this);
        }
    }
}
