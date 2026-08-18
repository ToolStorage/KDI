using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Kylin.DI.Tests
{
    public sealed class LifecycleAtomicityRegressionTests
    {
        [SetUp]
        public void SetUp()
        {
            AtomicReleaseDependency.Reset();
            DestroyRootDuringFinalActivation.Reset();
            DestroySelfDuringFinalActivation.Reset();
            DisposeChildScopeDuringFinalActivation.Reset();
        }

        [Test]
        public void ActivePrefab_InactiveDestinationParent_FailsBeforeCloneOrInjection()
        {
            var sourceHolder = new GameObject("inactive-parent-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("active-prefab-for-inactive-parent");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<AtomicReleaseProbe>();
            var destination = new GameObject("inactive-destination");
            destination.SetActive(false);
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicReleaseDependency>().To<AtomicReleaseDependency>().AsScoped();
            var scope = builder.Build(name: "inactive-destination-fail-fast");

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(
                    () => scope.Instantiate(prefab, destination.transform));

                StringAssert.Contains("inactive hierarchy", exception.Message);
                Assert.That(destination.transform.childCount, Is.Zero);
                Assert.That(AtomicReleaseDependency.Created, Is.Zero,
                    "Fail-fast must run before clone injection can resolve dependencies.");
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(sourceHolder);
                UnityEngine.Object.DestroyImmediate(destination);
            }
        }

        [Test]
        public void InactivePrefab_PostInjectCannotTurnRootActiveUnderInactiveParent()
        {
            InactiveRootActivationMutationProbe.Reset();
            var prefab = new GameObject("inactive-root-mutation-prefab");
            prefab.SetActive(false);
            prefab.AddComponent<InactiveRootActivationMutationProbe>();
            var destination = new GameObject("inactive-root-mutation-destination");
            destination.SetActive(false);
            var scope = new ScopeBuilder().Build(name: "inactive-root-mutation");

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(
                    () => scope.Instantiate(prefab, destination.transform));

                StringAssert.Contains("activeSelf", exception.Message);
                Assert.That(InactiveRootActivationMutationProbe.LastClone == null, Is.True);
                Assert.That(destination.transform.childCount, Is.Zero,
                    "A PostInject root-state mutation must roll back instead of becoming deferred activation.");
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(prefab);
                UnityEngine.Object.DestroyImmediate(destination);
                InactiveRootActivationMutationProbe.Reset();
            }
        }

        [Test]
        public void PostInject_ActivatingStagingHierarchy_IsDetectedRolledBackAndStagingRecovers()
        {
            StagingEscapeProbe.Reset();
            var sourceHolder = new GameObject("staging-escape-source-holder");
            sourceHolder.SetActive(false);
            var maliciousPrefab = new GameObject("staging-escape-prefab");
            maliciousPrefab.transform.SetParent(sourceHolder.transform, false);
            maliciousPrefab.AddComponent<StagingEscapeProbe>();
            var safePrefab = new GameObject("safe-prefab-after-staging-escape");
            safePrefab.transform.SetParent(sourceHolder.transform, false);
            var scope = new ScopeBuilder().Build(name: "staging-escape");
            GameObject recovered = null;

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(() => scope.Instantiate(maliciousPrefab));

                StringAssert.Contains("staging", exception.Message);
                Assert.That(StagingEscapeProbe.EnableCalls, Is.Zero,
                    "The clone root must already be inactive when staging is transiently activated.");
                Assert.That(StagingEscapeProbe.LastClone == null, Is.True);

                recovered = scope.Instantiate(safePrefab);
                Assert.That(recovered, Is.Not.Null);
                Assert.That(recovered.activeInHierarchy, Is.True,
                    "A mutated staging root must be quarantined before the next Instantiate call.");
            }
            finally
            {
                scope.Dispose();
                if (recovered != null) UnityEngine.Object.DestroyImmediate(recovered);
                UnityEngine.Object.DestroyImmediate(sourceHolder);
                StagingEscapeProbe.Reset();
            }
        }

        [Test]
        public void PostInject_LeavingAndReenteringStaging_IsHistoricallyDetected()
        {
            StagingReparentRoundTripProbe.Reset();
            var sourceHolder = new GameObject("staging-reparent-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("staging-reparent-round-trip-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<StagingReparentRoundTripProbe>();
            var scope = new ScopeBuilder().Build(name: "staging-reparent-round-trip");

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(() => scope.Instantiate(prefab));

                StringAssert.Contains("staging", exception.Message);
                Assert.That(StagingReparentRoundTripProbe.LastClone == null, Is.True,
                    "Restoring the final parent must not erase a transient staging escape.");
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(sourceHolder);
                StagingReparentRoundTripProbe.Reset();
            }
        }

        [Test]
        public void ActivePrefab_PostInjectRootTrueFalse_RemainsLifecycleIsolatedThenActivatesNormally()
        {
            RootActivationRoundTripProbe.Reset();
            var sourceHolder = new GameObject("root-active-round-trip-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("root-active-round-trip-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<RootActivationRoundTripProbe>();
            var scope = new ScopeBuilder().Build(name: "root-active-round-trip");
            GameObject clone = null;

            try
            {
                clone = scope.Instantiate(prefab);

                Assert.That(clone, Is.Not.Null);
                Assert.That(clone.activeInHierarchy, Is.True);
                Assert.That(RootActivationRoundTripProbe.EarlyEnableCalls, Is.Zero,
                    "A true-to-false root round trip inside inactive staging must not release user lifecycle.");
                Assert.That(RootActivationRoundTripProbe.EnableCalls, Is.EqualTo(1),
                    "The preserved active-prefab contract must still produce the normal final OnEnable.");
            }
            finally
            {
                scope.Dispose();
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(sourceHolder);
                RootActivationRoundTripProbe.Reset();
            }
        }

        [Test]
        public void ActivePrefab_PostInjectLeavesRootActive_IsRejectedBeforeFinalPlacement()
        {
            ActiveRootLeftEnabledProbe.Reset();
            var sourceHolder = new GameObject("root-left-active-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("root-left-active-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<ActiveRootLeftEnabledProbe>();
            var scope = new ScopeBuilder().Build(name: "root-left-active");

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(() => scope.Instantiate(prefab));

                StringAssert.Contains("activeSelf", exception.Message);
                Assert.That(ActiveRootLeftEnabledProbe.EnableCalls, Is.Zero,
                    "Leaving the root true under inactive staging must fail without running user OnEnable.");
                Assert.That(ActiveRootLeftEnabledProbe.LastClone == null, Is.True);
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(sourceHolder);
                ActiveRootLeftEnabledProbe.Reset();
            }
        }

        [Test]
        public void FinalActivation_DestroyedRootWithoutThrow_RollsBackCommittedGraph()
        {
            var sourceHolder = new GameObject("destroy-root-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("destroy-root-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<DestroyRootDuringFinalActivation>();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicReleaseDependency>().To<AtomicReleaseDependency>().AsScoped();
            var scope = builder.Build(name: "destroy-root-final-activation");

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(() => scope.Instantiate(prefab));

                StringAssert.Contains("Final prefab activation failed", exception.Message);
                Assert.That(DestroyRootDuringFinalActivation.LastRoot == null, Is.True);
                Assert.That(AtomicReleaseDependency.Created, Is.EqualTo(1));
                Assert.That(AtomicReleaseDependency.Disposed, Is.EqualTo(1));

                Assert.That(scope.Resolve<IAtomicReleaseDependency>(), Is.Not.Null);
                Assert.That(AtomicReleaseDependency.Created, Is.EqualTo(2),
                    "A compensated dependency must not remain in the parent cache.");
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(sourceHolder);
            }
        }

        [Test]
        public void FinalActivation_DestroyedInjectedChildComponentWithoutThrow_RollsBackRoot()
        {
            var sourceHolder = new GameObject("destroy-child-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("destroy-child-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            var child = new GameObject("injected-child");
            child.transform.SetParent(prefab.transform, false);
            child.AddComponent<DestroySelfDuringFinalActivation>();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicReleaseDependency>().To<AtomicReleaseDependency>().AsScoped();
            var scope = builder.Build(name: "destroy-child-final-activation");

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(() => scope.Instantiate(prefab));

                StringAssert.Contains("Final prefab activation failed", exception.Message);
                Assert.That(DestroySelfDuringFinalActivation.LastComponent == null, Is.True);
                Assert.That(DestroySelfDuringFinalActivation.LastRoot == null, Is.True,
                    "Losing one tracked child component must compensate the complete owned clone.");
                Assert.That(DestroySelfDuringFinalActivation.PostDestroyDisposals, Is.EqualTo(1),
                    "Subscriptions added after synchronous destruction must hit the terminal bucket immediately.");
                Assert.That(AtomicReleaseDependency.Disposed, Is.EqualTo(1));
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(sourceHolder);
            }
        }

        [Test]
        public void FinalActivation_DisposedConstructedChildScopeWithoutThrow_RollsBackRoot()
        {
            var sourceHolder = new GameObject("dispose-child-scope-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("dispose-child-scope-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<DisposeChildScopeDuringFinalActivation>();
            var child = new GameObject("runtime-child-scope");
            child.transform.SetParent(prefab.transform, false);
            child.AddComponent<EmptyActivationLifetimeScope>();
            var parent = new ScopeBuilder().Build(name: "dispose-child-scope-parent");

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(() => parent.Instantiate(prefab));

                StringAssert.Contains("Final prefab activation failed", exception.Message);
                Assert.That(DisposeChildScopeDuringFinalActivation.LastRoot == null, Is.True);
                Assert.That(DisposeChildScopeDuringFinalActivation.DisposedScope, Is.Not.Null);
                Assert.Throws<ObjectDisposedException>(() =>
                    DisposeChildScopeDuringFinalActivation.DisposedScope.Resolve<IInstantiator>());
            }
            finally
            {
                parent.Dispose();
                UnityEngine.Object.DestroyImmediate(sourceHolder);
            }
        }

        [Test]
        public void OnInjectedDisable_Reactivation_ReconcilesToLiveSecondBucket()
        {
            var target = new GameObject("disable-reactivation-target");
            var behaviour = target.AddComponent<DisableReactivationProbe>();
            var scope = new ScopeBuilder().Build(name: "disable-reactivation");

            try
            {
                scope.InjectGameObject(target);
                var firstBucket = behaviour.CurrentBucket;

                target.SetActive(false);

                Assert.That(target.activeSelf, Is.True);
                Assert.That(behaviour.EnableCalls, Is.EqualTo(2));
                Assert.That(behaviour.DisableCalls, Is.EqualTo(1));
                Assert.That(IsCompositeDisposed(firstBucket), Is.True);
                Assert.That(IsCompositeDisposed(behaviour.CurrentBucket), Is.False,
                    "The outer disable transition must not close the bucket opened by nested reactivation.");

                var secondBucket = behaviour.CurrentBucket;
                behaviour.Reactivate = false;
                target.SetActive(false);
                Assert.That(IsCompositeDisposed(secondBucket), Is.True);
                Assert.That(behaviour.DisableCalls, Is.EqualTo(2));
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void OnInjectedDisableAndBucketCleanupFailure_PreservesDisableException()
        {
            var target = new GameObject("disable-first-error-target");
            var behaviour = target.AddComponent<DisableAndCleanupFailureProbe>();
            var scope = new ScopeBuilder().Build(name: "disable-first-error");

            try
            {
                scope.InjectGameObject(target);
                var exit = typeof(DIBehaviour).GetMethod(
                    "ExitInjectedActive", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(exit, Is.Not.Null);
                LogAssert.Expect(LogType.Exception, new Regex("expected bucket cleanup failure"));

                var reflectionException = Assert.Throws<TargetInvocationException>(
                    () => exit.Invoke(behaviour, null));

                Assert.That(reflectionException.InnerException, Is.TypeOf<InvalidOperationException>());
                StringAssert.Contains("expected disable failure", reflectionException.InnerException.Message,
                    "Bucket cleanup failure must not replace the earlier OnInjectedDisable exception.");
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void NormalReenable_ResolveFromLifecycleCallback_IsRejectedWithoutCreatingService()
        {
            LifecycleResolveProbe.Reset();
            var target = new GameObject("normal-reenable-resolve-guard");
            var behaviour = target.AddComponent<LifecycleResolveProbe>();
            var builder = new ScopeBuilder();
            builder.Bind<LifecycleHiddenDependency>().ToSelf().AsScoped();
            var scope = builder.Build(name: "normal-reenable-resolve-guard");
            LifecycleResolveProbe.CapturedScope = scope;

            try
            {
                scope.InjectGameObject(target);
                behaviour.ResolveOnNextEnable = true;
                target.SetActive(false);
                target.SetActive(true);

                Assert.That(behaviour.ResolveWasRejected, Is.True);
                Assert.That(LifecycleHiddenDependency.Created, Is.Zero);
                Assert.That(scope.Resolve<LifecycleHiddenDependency>(), Is.Not.Null,
                    "Resolve remains valid after the lifecycle boundary has returned.");
            }
            finally
            {
                LifecycleResolveProbe.CapturedScope = null;
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void Configure_CapturedBuilderBuild_IsRejectedBeforeBuilderIsConsumed()
        {
            var capturedBuilder = new ScopeBuilder();
            var lifetimeObject = CreateManualLifetimeScope("configure-build-guard", out var lifetime);
            lifetime.ConfigureAction = _ => capturedBuilder.Build(name: "escaped-configure-scope");
            IScope recovered = null;

            try
            {
                Assert.Throws<InvalidOperationException>(lifetime.Initialize);
                recovered = capturedBuilder.Build(name: "build-after-configure-boundary");
                Assert.That(recovered, Is.Not.Null,
                    "The rejected Build must not consume or partially commit the captured builder.");
            }
            finally
            {
                recovered?.Dispose();
                UnityEngine.Object.DestroyImmediate(lifetimeObject);
            }
        }

        [Test]
        public void Configure_CapturedInjectAndInstantiate_AreRejectedBeforeMutation()
        {
            var builder = new ScopeBuilder();
            builder.Bind<IConfigurationBoundaryDependency>()
                .To<ConfigurationBoundaryDependency>()
                .AsScoped();
            var capturedScope = builder.Build(name: "configure-external-operations");
            var target = new GameObject("configure-injection-target");
            var injectable = target.AddComponent<ConfigurationInjectionTarget>();
            var prefab = new GameObject("configure-instantiation-prefab");
            prefab.SetActive(false);
            var lifetimeObject = CreateManualLifetimeScope("configure-operation-guard", out var lifetime);

            try
            {
                lifetime.ConfigureAction = _ => injectable.Inject(capturedScope);
                Assert.Throws<InvalidOperationException>(lifetime.Initialize);
                Assert.That(injectable.Dependency, Is.Null);

                lifetime.ConfigureAction = _ => capturedScope.Instantiate(prefab);
                Assert.Throws<InvalidOperationException>(lifetime.Initialize);
                Assert.That(GameObject.Find("configure-instantiation-prefab(Clone)"), Is.Null);

                injectable.Inject(capturedScope);
                Assert.That(injectable.Dependency, Is.Not.Null,
                    "The same injection must remain valid at the composition boundary.");
                var clone = capturedScope.Instantiate(prefab);
                Assert.That(clone, Is.Not.Null);
            }
            finally
            {
                capturedScope.Dispose();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(prefab);
                UnityEngine.Object.DestroyImmediate(lifetimeObject);
            }
        }

        [Test]
        public void Configure_CapturedScopeDispose_IsRejectedAndScopeRemainsUsable()
        {
            var capturedScope = new ScopeBuilder().Build(name: "configure-dispose-captured-scope");
            var lifetimeObject = CreateManualLifetimeScope("configure-dispose-guard", out var lifetime);
            lifetime.ConfigureAction = _ => capturedScope.Dispose();

            try
            {
                Assert.Throws<InvalidOperationException>(lifetime.Initialize);
                Assert.That(capturedScope.Resolve<IInstantiator>(), Is.Not.Null);
            }
            finally
            {
                capturedScope.Dispose();
                UnityEngine.Object.DestroyImmediate(lifetimeObject);
            }
        }

        [Test]
        public void Configure_UpdateLoopInstance_DoesNotCreateGlobalObjectBeforeRejecting()
        {
            ResetUpdateLoopManager();
            var lifetimeObject = CreateManualLifetimeScope("configure-update-manager-guard", out var lifetime);
            lifetime.ConfigureAction = _ =>
            {
                var ignored = UpdateLoopManager.Instance;
            };

            try
            {
                Assert.Throws<InvalidOperationException>(lifetime.Initialize);
                Assert.That(TryGetUpdateLoopManager(), Is.Null,
                    "Reading the public singleton from Configure must fail before creating its GameObject.");

                Assert.That(UpdateLoopManager.Instance, Is.Not.Null,
                    "The singleton remains available after returning to the composition boundary.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lifetimeObject);
                ResetUpdateLoopManager();
            }
        }

        private static GameObject CreateManualLifetimeScope(string name, out ConfigurableLifetimeScope lifetime)
        {
            var gameObject = new GameObject(name);
            gameObject.SetActive(false);
            lifetime = gameObject.AddComponent<ConfigurableLifetimeScope>();
            return gameObject;
        }

        private static bool IsCompositeDisposed(object bucket)
        {
            Assert.That(bucket, Is.Not.Null);
            var property = bucket.GetType().GetProperty("IsDisposed", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null);
            return (bool)property.GetValue(bucket);
        }

        private static void ResetUpdateLoopManager()
        {
            var method = typeof(UpdateLoopManager).GetMethod(
                "ResetStatic", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, null);
        }

        private static UpdateLoopManager TryGetUpdateLoopManager()
        {
            var method = typeof(UpdateLoopManager).GetMethod(
                "TryGetInstance", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (UpdateLoopManager)method.Invoke(null, null);
        }
    }

    public interface IAtomicReleaseDependency { }

    public sealed class AtomicReleaseDependency : IAtomicReleaseDependency, IDependencyObject, IDisposable
    {
        public static int Created { get; private set; }
        public static int Disposed { get; private set; }

        public AtomicReleaseDependency() => Created++;
        public void Dispose() => Disposed++;

        public static void Reset()
        {
            Created = 0;
            Disposed = 0;
        }
    }

    public sealed class AtomicReleaseProbe : DIBehaviour
    {
#pragma warning disable CS0169
        [Inject] private IAtomicReleaseDependency _dependency;
#pragma warning restore CS0169
    }

    public sealed class DestroyRootDuringFinalActivation : DIBehaviour
    {
#pragma warning disable CS0169
        [Inject] private IAtomicReleaseDependency _dependency;
#pragma warning restore CS0169
        public static GameObject LastRoot { get; private set; }

        protected override void OnInjectedEnable()
        {
            LastRoot = gameObject;
            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        public static void Reset() => LastRoot = null;
    }

    public sealed class DestroySelfDuringFinalActivation : DIBehaviour
    {
#pragma warning disable CS0169
        [Inject] private IAtomicReleaseDependency _dependency;
#pragma warning restore CS0169
        public static DestroySelfDuringFinalActivation LastComponent { get; private set; }
        public static GameObject LastRoot { get; private set; }
        public static int PostDestroyDisposals { get; private set; }

        protected override void OnInjectedEnable()
        {
            LastComponent = this;
            LastRoot = transform.root.gameObject;
            UnityEngine.Object.DestroyImmediate(this);
            ReentrantRevokeBehaviour.AddToActiveBucket(
                this,
                new PostDestroyDisposable(() => PostDestroyDisposals++));
        }

        public static void Reset()
        {
            LastComponent = null;
            LastRoot = null;
            PostDestroyDisposals = 0;
        }

        private sealed class PostDestroyDisposable : IDisposable
        {
            private Action _onDispose;

            internal PostDestroyDisposable(Action onDispose) => _onDispose = onDispose;

            public void Dispose()
            {
                var callback = _onDispose;
                _onDispose = null;
                callback?.Invoke();
            }
        }
    }

    public sealed class InactiveRootActivationMutationProbe : MonoBehaviour, IInjectable, IPostInjectable
    {
        public static GameObject LastClone { get; private set; }

        public void PostInject()
        {
            LastClone = gameObject;
            gameObject.SetActive(true);
        }

        public static void Reset() => LastClone = null;
    }

    public sealed class StagingEscapeProbe : MonoBehaviour, IInjectable, IPostInjectable
    {
        public static GameObject LastClone { get; private set; }
        public static int EnableCalls { get; private set; }

        public void PostInject()
        {
            LastClone = gameObject;
            var stagingObject = transform.parent.gameObject;
            stagingObject.SetActive(true);
            stagingObject.SetActive(false);
        }

        private void OnEnable() => EnableCalls++;

        public static void Reset()
        {
            LastClone = null;
            EnableCalls = 0;
        }
    }

    public sealed class StagingReparentRoundTripProbe : MonoBehaviour, IInjectable, IPostInjectable
    {
        public static GameObject LastClone { get; private set; }

        public void PostInject()
        {
            LastClone = gameObject;
            var stagingRoot = transform.parent;
            transform.SetParent(null, false);
            transform.SetParent(stagingRoot, false);
        }

        public static void Reset() => LastClone = null;
    }

    public sealed class RootActivationRoundTripProbe : MonoBehaviour, IInjectable, IPostInjectable
    {
        private static bool _postInjectReturned;

        public static int EnableCalls { get; private set; }
        public static int EarlyEnableCalls { get; private set; }

        public void PostInject()
        {
            gameObject.SetActive(true);
            gameObject.SetActive(false);
            _postInjectReturned = true;
        }

        private void OnEnable()
        {
            EnableCalls++;
            if (!_postInjectReturned)
                EarlyEnableCalls++;
        }

        public static void Reset()
        {
            _postInjectReturned = false;
            EnableCalls = 0;
            EarlyEnableCalls = 0;
        }
    }

    public sealed class ActiveRootLeftEnabledProbe : MonoBehaviour, IInjectable, IPostInjectable
    {
        public static GameObject LastClone { get; private set; }
        public static int EnableCalls { get; private set; }

        public void PostInject()
        {
            LastClone = gameObject;
            gameObject.SetActive(true);
        }

        private void OnEnable() => EnableCalls++;

        public static void Reset()
        {
            LastClone = null;
            EnableCalls = 0;
        }
    }

    public sealed class DisposeChildScopeDuringFinalActivation : DIBehaviour
    {
        public static IScope DisposedScope { get; private set; }
        public static GameObject LastRoot { get; private set; }

        protected override void OnInjectedEnable()
        {
            LastRoot = gameObject;
            var lifetime = GetComponentInChildren<EmptyActivationLifetimeScope>(true);
            DisposedScope = lifetime.Scope;
            if (DisposedScope == null)
                throw new InvalidOperationException("The runtime child Scope did not initialize before the final callback.");
            DisposedScope.Dispose();
        }

        public static void Reset()
        {
            DisposedScope = null;
            LastRoot = null;
        }
    }

    public sealed class DisableReactivationProbe : DIBehaviour
    {
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public bool Reactivate { get; set; } = true;
        public object CurrentBucket { get; private set; }

        protected override void OnInjectedEnable()
        {
            EnableCalls++;
            CurrentBucket = _cd;
        }

        protected override void OnInjectedDisable()
        {
            DisableCalls++;
            if (Reactivate)
                gameObject.SetActive(true);
        }
    }

    public sealed class DisableAndCleanupFailureProbe : DIBehaviour
    {
        protected override void OnInjectedEnable()
        {
            ReentrantRevokeBehaviour.AddToActiveBucket(this, new ThrowingCleanupDisposable());
        }

        protected override void OnInjectedDisable()
        {
            throw new InvalidOperationException("expected disable failure");
        }

        private sealed class ThrowingCleanupDisposable : IDisposable
        {
            private bool _isDisposed;

            public void Dispose()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                throw new InvalidOperationException("expected bucket cleanup failure");
            }
        }
    }

    public sealed class LifecycleHiddenDependency : IDependencyObject
    {
        public static int Created { get; private set; }
        public LifecycleHiddenDependency() => Created++;
        public static void Reset() => Created = 0;
    }

    public sealed class LifecycleResolveProbe : DIBehaviour
    {
        public static IScope CapturedScope;
        public bool ResolveOnNextEnable { get; set; }
        public bool ResolveWasRejected { get; private set; }

        protected override void OnInjectedEnable()
        {
            if (!ResolveOnNextEnable) return;
            ResolveOnNextEnable = false;
            try
            {
                CapturedScope.Resolve<LifecycleHiddenDependency>();
            }
            catch (InvalidOperationException)
            {
                ResolveWasRejected = true;
            }
        }

        public static void Reset()
        {
            CapturedScope = null;
            LifecycleHiddenDependency.Reset();
        }
    }

    public interface IConfigurationBoundaryDependency { }

    public sealed class ConfigurationBoundaryDependency : IConfigurationBoundaryDependency, IDependencyObject { }

    public sealed class ConfigurationInjectionTarget : MonoBehaviour, IInjectable
    {
        [Inject] private IConfigurationBoundaryDependency _dependency;
        public IConfigurationBoundaryDependency Dependency => _dependency;
    }
}
