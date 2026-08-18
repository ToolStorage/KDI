using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kylin.DI.Tests
{
    public sealed class UnityLifetimeTests
    {
        [Test]
        public void InstantiateWithoutParent_UsesActiveSceneAndInjectsBeforeAwake()
        {
            var prefab = new GameObject("awake-prefab");
            prefab.AddComponent<PrefabAwakeProbe>();
            PrefabAwakeProbe.ResetObservations();

            var dependency = new AtomicDependency();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(dependency);
            var scope = builder.Build(name: "prefab-awake");
            GameObject instance = null;
            try
            {
                var destination = SceneManager.GetActiveScene();
                instance = scope.Instantiate(prefab);

                Assert.That(instance.scene, Is.EqualTo(destination));
                Assert.That(PrefabAwakeProbe.AwakeCalls, Is.EqualTo(1));
                Assert.That(PrefabAwakeProbe.LastAwakeHadDependency, Is.True);
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                UnityEngine.Object.DestroyImmediate(prefab);
                scope.Dispose();
            }
        }

        [Test]
        public void DestroyedComponent_ReleasesItsInjectionBeforeScopeShutdown()
        {
            DestroyCleanupProbe.PreUninjectCalls = 0;
            var target = new GameObject("destroy-cleanup");
            var component = target.AddComponent<DestroyCleanupProbe>();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(new AtomicDependency());
            var scope = builder.Build(name: "destroy-cleanup");

            scope.InjectGameObject(target);
            UnityEngine.Object.DestroyImmediate(component);
            Assert.That(DestroyCleanupProbe.PreUninjectCalls, Is.EqualTo(1));

            scope.Dispose();
            Assert.That(DestroyCleanupProbe.PreUninjectCalls, Is.EqualTo(1));
            UnityEngine.Object.DestroyImmediate(target);
        }

        [Test]
        public void DynamicLifetimeScopePrefab_BecomesChildOfCallingScope()
        {
            var prefab = new GameObject("scope-prefab");
            prefab.SetActive(false);
            var prefabScope = prefab.AddComponent<TestLifetimeScope>();
            SetAutoInitialize(prefabScope, false);
            var rootScope = new ScopeBuilder().Build(name: "dynamic-parent");
            GameObject instance = null;
            try
            {
                instance = rootScope.Instantiate(prefab);
                var child = instance.GetComponent<TestLifetimeScope>();
                child.Initialize();

                Assert.That(child.IsInitialized, Is.True);
                Assert.That(child.Scope.Parent, Is.SameAs(rootScope));
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                UnityEngine.Object.DestroyImmediate(prefab);
                rootScope.Dispose();
            }
        }

        [Test]
        public void InstantiateDuringActivation_FailsBeforeCreatingAClone()
        {
            var prefab = new GameObject("activation-prefab");
            var builder = new ScopeBuilder();
            builder.RegisterFactory(
                () => new ActivationSpawnResult(prefab), Lifetime.Scoped);
            var scope = builder.Build(name: "activation-spawn");
            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<ActivationSpawnResult>());
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void InstantiatedClone_IsOwnedAndDestroyedByScope()
        {
            var prefab = new GameObject("owned-prefab");
            var scope = new ScopeBuilder().Build(name: "owned-prefab");
            GameObject instance = null;
            try
            {
                instance = scope.Instantiate(prefab);
                Assert.That(instance, Is.Not.Null);

                scope.Dispose();
                Assert.That(instance == null, Is.True,
                    "Scope.Instantiate clones must not outlive the dependencies injected into them.");
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                UnityEngine.Object.DestroyImmediate(prefab);
                scope.Dispose();
            }
        }

        [Test]
        public void Instantiate_RollsBackWhenPostInjectDestroysRequestedParent()
        {
            var parentObject = new GameObject("destroyed-destination-parent");
            var prefab = new GameObject("parent-destroying-prefab");
            prefab.AddComponent<DestinationParentDestroyingProbe>();
            var scope = new ScopeBuilder().Build(name: "destroyed-destination-parent");
            DestinationParentDestroyingProbe.DestinationParent = parentObject;
            DestinationParentDestroyingProbe.LastInstance = null;

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Instantiate(prefab, parentObject.transform));
                Assert.That(DestinationParentDestroyingProbe.LastInstance == null, Is.True,
                    "The owned clone must be destroyed instead of becoming a scene-root orphan.");
            }
            finally
            {
                if (DestinationParentDestroyingProbe.LastInstance != null)
                    UnityEngine.Object.DestroyImmediate(DestinationParentDestroyingProbe.LastInstance);
                if (parentObject != null) UnityEngine.Object.DestroyImmediate(parentObject);
                UnityEngine.Object.DestroyImmediate(prefab);
                DestinationParentDestroyingProbe.DestinationParent = null;
                DestinationParentDestroyingProbe.LastInstance = null;
                scope.Dispose();
            }
        }

        [Test]
        public void ExternalScopeDispose_DeactivatesLifetimeScopeBeforeRevokingFields()
        {
            SceneScopeDisableProbe.ResetObservation();
            var scopeObject = new GameObject("scene-scope-disable-order");
            scopeObject.SetActive(false);
            var lifetime = scopeObject.AddComponent<ConfigurableLifetimeScope>();
            var probe = scopeObject.AddComponent<SceneScopeDisableProbe>();
            SetAutoInitialize(lifetime, false);
            lifetime.ConfigureAction = builder =>
                builder.Bind<OwnedPrefabDependency>().ToSelf().AsScoped();
            scopeObject.SetActive(true);
            lifetime.Initialize();
            SceneScopeDisableProbe.ResetObservation();
            var concreteScope = lifetime.Scope;

            try
            {
                concreteScope.Dispose();

                Assert.That(SceneScopeDisableProbe.DisableCalls, Is.EqualTo(1));
                Assert.That(SceneScopeDisableProbe.LastDisableHadDependency, Is.True,
                    "A scene LifetimeScope hierarchy must stop while its injected fields are still available.");
                Assert.That(probe.Dependency, Is.Null);
            }
            finally
            {
                if (scopeObject != null) UnityEngine.Object.DestroyImmediate(scopeObject);
                concreteScope?.Dispose();
                SceneScopeDisableProbe.ResetObservation();
            }
        }

        [Test]
        public void LifetimeScopeDestroyedDuringActivation_IsDisposedAtOutermostBoundary()
        {
            var lifetimeObject = new GameObject("lifetime-owner-destroyed-in-activation");
            lifetimeObject.SetActive(false);
            var lifetime = lifetimeObject.AddComponent<ConfigurableLifetimeScope>();
            SetAutoInitialize(lifetime, false);
            lifetimeObject.SetActive(true);
            lifetime.Initialize();
            var ownedScope = lifetime.Scope;

            var dependency = new AtomicDependency();
            var activationBuilder = new ScopeBuilder();
            activationBuilder.Bind<IAtomicDependency>().FromInstance(dependency);
            activationBuilder.RegisterFactory<LifetimeOwnerDestroyer>(() =>
            {
                UnityEngine.Object.DestroyImmediate(lifetimeObject);
                return new LifetimeOwnerDestroyer();
            }, Lifetime.Scoped);
            var activationScope = activationBuilder.Build(name: "destroy-lifetime-owner");

            try
            {
                Assert.Throws<InvalidOperationException>(() => activationScope.Resolve<LifetimeOwnerDestroyer>());
                Assert.That(lifetimeObject == null, Is.True);
                Assert.Throws<ObjectDisposedException>(() => ownedScope.Resolve<IInstantiator>());
                Assert.That(activationScope.Resolve<IAtomicDependency>(), Is.SameAs(dependency));
            }
            finally
            {
                if (lifetimeObject != null) UnityEngine.Object.DestroyImmediate(lifetimeObject);
                activationScope.Dispose();
                ownedScope?.Dispose();
            }
        }

        [Test]
        public void ParentManualDispose_DeactivatesAndReinitializesLiveChildScope()
        {
            var parentObject = new GameObject("parent-scope");
            parentObject.SetActive(false);
            var childObject = new GameObject("child-scope");
            childObject.SetActive(false);
            childObject.transform.SetParent(parentObject.transform, false);
            var parent = parentObject.AddComponent<TestLifetimeScope>();
            var child = childObject.AddComponent<TestLifetimeScope>();
            SetAutoInitialize(parent, false);
            SetAutoInitialize(child, false);
            SetSerializedParent(child, parent);
            childObject.SetActive(true);
            parentObject.SetActive(true);

            try
            {
                parent.Initialize();
                child.Initialize();
                Assert.That(child.IsInitialized, Is.True);

                parent.Dispose();
                Assert.That(child.IsInitialized, Is.False);
                Assert.That(childObject.activeSelf, Is.False,
                    "An enabled child must not keep running after its dependencies are revoked.");

                parent.Initialize();
                Assert.That(child.IsInitialized, Is.True);
                Assert.That(childObject.activeSelf, Is.True);
                Assert.That(child.Scope.Parent, Is.SameAs(parent.Scope));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parentObject);
            }
        }

        [Test]
        public void DisposeRejectedDuringActivation_RestoresLifetimeScopeState()
        {
            var gameObject = new GameObject("dispose-during-activation");
            gameObject.SetActive(false);
            var lifetime = gameObject.AddComponent<ConfigurableLifetimeScope>();
            SetAutoInitialize(lifetime, false);
            lifetime.ConfigureAction = builder =>
                builder.RegisterFactory<DisposeDuringActivationResult>(() =>
                {
                    lifetime.Dispose();
                    return new DisposeDuringActivationResult();
                }, Lifetime.Scoped);
            try
            {
                lifetime.Initialize();
                Assert.Throws<InvalidOperationException>(() => lifetime.Scope.Resolve<DisposeDuringActivationResult>());
                Assert.That(lifetime.IsInitialized, Is.True);

                lifetime.Dispose();
                Assert.That(lifetime.IsInitialized, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DisposeDuringConfigure_FailsInitializationAndAllowsCleanRetry()
        {
            var gameObject = new GameObject("dispose-during-configure");
            gameObject.SetActive(false);
            var lifetime = gameObject.AddComponent<ConfigurableLifetimeScope>();
            SetAutoInitialize(lifetime, false);
            lifetime.ConfigureAction = _ => lifetime.Dispose();

            try
            {
                Assert.Throws<InvalidOperationException>(() => lifetime.Initialize());
                Assert.That(lifetime.IsInitialized, Is.False);

                lifetime.ConfigureAction = null;
                lifetime.Initialize();
                Assert.That(lifetime.IsInitialized, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DestroyedLifetimeScope_IsNotRetainedByCascadeRestartQueue()
        {
            var gameObject = new GameObject("destroyed-lifetime-scope");
            gameObject.SetActive(false);
            var lifetime = gameObject.AddComponent<TestLifetimeScope>();
            SetAutoInitialize(lifetime, false);
            lifetime.Initialize();

            UnityEngine.Object.DestroyImmediate(gameObject);

            var field = typeof(LifetimeScope).GetField(
                "_cascadeRestartScopes", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var pending = field.GetValue(null) as System.Collections.IList;
            Assert.That(pending, Is.Not.Null);
            for (var i = 0; i < pending.Count; i++)
                Assert.That(ReferenceEquals(pending[i], lifetime), Is.False);
        }

        [Test]
        public void InitializeFailure_DeactivatesAndSuccessfulRetryReactivates()
        {
            var gameObject = new GameObject("failed-initialize");
            gameObject.SetActive(false);
            var lifetime = gameObject.AddComponent<ConfigurableLifetimeScope>();
            SetAutoInitialize(lifetime, false);
            lifetime.ConfigureAction = builder =>
                builder.Bind<FailingLifetimeEntryPoint>()
                    .FromFactory(() => throw new InvalidOperationException("expected activation failure"))
                    .AsEntryPoint()
                    .AsScoped();
            gameObject.SetActive(true);

            try
            {
                Assert.Throws<InvalidOperationException>(() => lifetime.Initialize());
                Assert.That(lifetime.IsInitialized, Is.False);
                Assert.That(gameObject.activeSelf, Is.False,
                    "A hierarchy with rolled-back fields must not remain enabled.");

                lifetime.ConfigureAction = null;
                lifetime.Initialize();
                Assert.That(lifetime.IsInitialized, Is.True);
                Assert.That(gameObject.activeSelf, Is.True,
                    "A successful explicit retry must restore the pre-failure activation state.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DestroyedNonInjectableUnityService_DisposesScopeAndStopsUpdating()
        {
            var gameObject = new GameObject("non-injectable-unity-service");
            var service = gameObject.AddComponent<NonInjectableUnityUpdateProbe>();
            var builder = new ScopeBuilder();
            builder.Bind<NonInjectableUnityUpdateProbe>().FromFactory(() => service).AsScoped();
            var scope = builder.Build(name: "non-injectable-unity-service");
            var manager = UpdateLoopManager.Instance;

            try
            {
                Assert.That(scope.Resolve<NonInjectableUnityUpdateProbe>(), Is.SameAs(service));
                InvokeUpdate(manager);
                Assert.That(NonInjectableUnityUpdateProbe.UpdateCalls, Is.EqualTo(1));

                UnityEngine.Object.DestroyImmediate(service);
                InvokeUpdate(manager);
                Assert.That(NonInjectableUnityUpdateProbe.UpdateCalls, Is.EqualTo(1));
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<NonInjectableUnityUpdateProbe>());
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(gameObject);
                NonInjectableUnityUpdateProbe.UpdateCalls = 0;
            }
        }

        [Test]
        public void DestroyedCachedUnityDependency_DisposesScopeAndRevokesExistingConsumers()
        {
            var dependencyObject = new GameObject("cached-unity-dependency");
            var dependency = dependencyObject.AddComponent<CachedUnityDependency>();
            var consumer = new CachedUnityConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<CachedUnityDependency>().FromFactory(() => dependency).AsScoped();
            builder.Bind<CachedUnityConsumer>().FromInstance(consumer);
            var scope = builder.Build(name: "cached-unity-dependency");

            try
            {
                Assert.That(consumer.Dependency, Is.SameAs(dependency));
                UnityEngine.Object.DestroyImmediate(dependency);

                Assert.That(consumer.Dependency, Is.Null);
                Assert.That(consumer.PreUninjectCalls, Is.EqualTo(1));
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<CachedUnityDependency>());
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(dependencyObject);
            }
        }

        [Test]
        public void TransientUnityService_IsRejectedBecauseConsumersCannotBeRevokedSafely()
        {
            var serviceObject = new GameObject("transient-unity-service");
            var service = serviceObject.AddComponent<CachedUnityDependency>();
            var builder = new ScopeBuilder();
            builder.Bind<CachedUnityDependency>().FromFactory(() => service).AsTransient();
            var scope = builder.Build(name: "transient-unity-service");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<CachedUnityDependency>());
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(serviceObject);
            }
        }

        [Test]
        public void CrossScopeTransaction_DrainsDeferredUnityInvalidationAtOutermostBoundary()
        {
            var dependencyObject = new GameObject("cross-scope-unity-dependency");
            var dependency = dependencyObject.AddComponent<CachedUnityDependency>();
            var consumer = new CachedUnityConsumer();
            var dependencyBuilder = new ScopeBuilder();
            dependencyBuilder.Bind<CachedUnityDependency>().FromFactory(() => dependency).AsScoped();
            dependencyBuilder.Bind<CachedUnityConsumer>().FromInstance(consumer);
            var dependencyScope = dependencyBuilder.Build(name: "cross-scope-dependency");

            var activationBuilder = new ScopeBuilder();
            activationBuilder.RegisterFactory<CrossScopeInvalidationActivation>(() =>
            {
                UnityEngine.Object.DestroyImmediate(dependency);
                return new CrossScopeInvalidationActivation();
            }, Lifetime.Scoped);
            var activationScope = activationBuilder.Build(
                parent: dependencyScope,
                name: "cross-scope-activation");

            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => activationScope.Resolve<CrossScopeInvalidationActivation>());
                Assert.That(consumer.Dependency, Is.Null);
                Assert.That(consumer.PreUninjectCalls, Is.EqualTo(1));
                Assert.Throws<ObjectDisposedException>(
                    () => dependencyScope.Resolve<CachedUnityDependency>());
            }
            finally
            {
                activationScope.Dispose();
                dependencyScope.Dispose();
                UnityEngine.Object.DestroyImmediate(dependencyObject);
            }
        }

        [Test]
        public void HostlessUnityService_IsDetectedByGlobalLifetimeMonitor()
        {
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var consumer = new MonitoredScriptableConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.Bind<MonitoredScriptableConsumer>().FromInstance(consumer);
            var scope = builder.Build(name: "hostless-unity-service");
            var manager = UpdateLoopManager.Instance;

            try
            {
                Assert.That(consumer.Dependency, Is.SameAs(dependency));
                UnityEngine.Object.DestroyImmediate(dependency);
                InvokeUpdate(manager);

                Assert.That(consumer.Dependency, Is.Null);
                Assert.That(consumer.PreUninjectCalls, Is.EqualTo(1));
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
            }
        }

        [Test]
        public void HostlessExternalInjection_IsReleasedByGlobalLifetimeMonitor()
        {
            MonitoredExternalScriptableTarget.PreUninjectCalls = 0;
            var target = ScriptableObject.CreateInstance<MonitoredExternalScriptableTarget>();
            var dependency = new AtomicDependency();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(dependency);
            var scope = builder.Build(name: "hostless-external-injection");

            try
            {
                target.Inject(scope);
                var manager = UpdateLoopManager.Instance;
                UnityEngine.Object.DestroyImmediate(target);
                InvokeUpdate(manager);

                Assert.That(MonitoredExternalScriptableTarget.PreUninjectCalls, Is.EqualTo(1));
                Assert.That(scope.Resolve<IAtomicDependency>(), Is.SameAs(dependency),
                    "Destroying an external injection target must release only its lease, not its Scope.");
            }
            finally
            {
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                scope.Dispose();
                MonitoredExternalScriptableTarget.PreUninjectCalls = 0;
            }
        }

        [Test]
        public void HostlessExternalInjectionDestroyedDuringActivation_FailsThatTransaction()
        {
            MonitoredExternalScriptableTarget.PreUninjectCalls = 0;
            var target = ScriptableObject.CreateInstance<MonitoredExternalScriptableTarget>();
            var dependency = new AtomicDependency();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(dependency);
            builder.RegisterFactory<ExternalInjectionDestroyer>(() =>
            {
                UnityEngine.Object.DestroyImmediate(target);
                return new ExternalInjectionDestroyer();
            }, Lifetime.Scoped);
            var scope = builder.Build(name: "hostless-external-activation-destroy");

            try
            {
                target.Inject(scope);
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<ExternalInjectionDestroyer>());
                Assert.That(MonitoredExternalScriptableTarget.PreUninjectCalls, Is.EqualTo(1));
                Assert.That(scope.Resolve<IAtomicDependency>(), Is.SameAs(dependency),
                    "A destroyed external target fails the activation but does not invalidate an otherwise valid Scope.");
            }
            finally
            {
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                scope.Dispose();
                MonitoredExternalScriptableTarget.PreUninjectCalls = 0;
            }
        }

        [Test]
        public void FactoryThrowAfterDestroyingCommittedHostlessService_DisposesScopeBeforeReturning()
        {
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterFactory<DestroyCommittedHostlessAndThrow>(() =>
            {
                UnityEngine.Object.DestroyImmediate(dependency);
                throw new InvalidOperationException("expected factory failure");
            }, Lifetime.Scoped);
            var scope = builder.Build(name: "destroy-committed-hostless-then-throw");
            scope.Resolve<MonitoredScriptableDependency>();

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<DestroyCommittedHostlessAndThrow>());
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
            }
        }

        [Test]
        public void RollbackCleanupDestroyingCommittedHostlessService_DisposesScopeBeforeReturning()
        {
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            RollbackHostlessDestroyingDisposable created = null;
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterFactory<RollbackHostlessDestroyingDisposable>(
                () => created = new RollbackHostlessDestroyingDisposable(dependency), Lifetime.Scoped);
            builder.RegisterFactory(
                () => new RollbackHostlessDestroyCoordinator(), Lifetime.Scoped);
            var scope = builder.Build(name: "rollback-cleanup-destroys-hostless");
            scope.Resolve<MonitoredScriptableDependency>();

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<RollbackHostlessDestroyCoordinator>());
                Assert.That(created, Is.Not.Null);
                Assert.That(created.DisposeCalls, Is.EqualTo(1));
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
            }
        }

        [Test]
        public void FailureAudit_DisposesEveryTouchedScopeWithDestroyedHostlessCache()
        {
            var parentDependency = ScriptableObject.CreateInstance<ParentHostlessDependency>();
            var childDependency = ScriptableObject.CreateInstance<ChildHostlessDependency>();
            var parentBuilder = new ScopeBuilder();
            parentBuilder.Bind<ParentHostlessDependency>().FromFactory(() => parentDependency).AsScoped();
            var parent = parentBuilder.Build(name: "failure-audit-parent");
            parent.Resolve<ParentHostlessDependency>();

            var childBuilder = new ScopeBuilder();
            childBuilder.Bind<ChildHostlessDependency>().FromFactory(() => childDependency).AsScoped();
            childBuilder.RegisterFactory<DestroyParentAndChildHostlessThenThrow>(() =>
            {
                UnityEngine.Object.DestroyImmediate(childDependency);
                UnityEngine.Object.DestroyImmediate(parentDependency);
                throw new InvalidOperationException("expected multi-scope failure");
            }, Lifetime.Scoped);
            var child = childBuilder.Build(parent, "failure-audit-child");
            child.Resolve<ChildHostlessDependency>();

            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    child.Resolve<DestroyParentAndChildHostlessThenThrow>());
                Assert.Throws<ObjectDisposedException>(() => child.Resolve<ChildHostlessDependency>());
                Assert.Throws<ObjectDisposedException>(() => parent.Resolve<ParentHostlessDependency>());
            }
            finally
            {
                child.Dispose();
                parent.Dispose();
                if (childDependency != null) UnityEngine.Object.DestroyImmediate(childDependency);
                if (parentDependency != null) UnityEngine.Object.DestroyImmediate(parentDependency);
            }
        }

        [Test]
        public void PostInjectDestroyingCommittedHostlessService_ImmediatelyDisposesOwningScope()
        {
            HostlessPostDestroyingConsumer.Reset();
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var consumer = new HostlessPostDestroyingConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            var scope = builder.Build(name: "post-inject-destroys-committed-hostless");
            scope.Resolve<MonitoredScriptableDependency>();

            try
            {
                Assert.Throws<InvalidOperationException>(() => consumer.Inject(scope));
                Assert.That(HostlessPostDestroyingConsumer.PostInjectCalls, Is.EqualTo(1));
                Assert.That(HostlessPostDestroyingConsumer.PreUninjectCalls, Is.EqualTo(1));
                Assert.That(consumer.Dependency, Is.Null);
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
                HostlessPostDestroyingConsumer.Reset();
            }
        }

        [Test]
        public void ExternalTargetCleanup_CannotResolveAnotherScopeDuringActivationScan()
        {
            ResolvingPreUninjectExternalTarget.Reset();
            var parentBuilder = new ScopeBuilder();
            parentBuilder.Bind<IAtomicDependency>().FromInstance(new AtomicDependency());
            var parent = parentBuilder.Build(name: "reentrant-scan-parent");
            var otherBuilder = new ScopeBuilder();
            otherBuilder.Bind<ReentrantScanResolvedService>().ToSelf().AsScoped();
            var other = otherBuilder.Build(name: "reentrant-scan-other");
            var target = ScriptableObject.CreateInstance<ResolvingPreUninjectExternalTarget>();
            ResolvingPreUninjectExternalTarget.OtherScope = other;

            try
            {
                target.Inject(parent);
                UnityEngine.Object.DestroyImmediate(target);

                Assert.DoesNotThrow(() => parent.Resolve<IAtomicDependency>());
                Assert.That(ResolvingPreUninjectExternalTarget.PreUninjectCalls, Is.EqualTo(1));
                Assert.That(ResolvingPreUninjectExternalTarget.ResolveWasRejected, Is.True);
                Assert.That(ResolvingPreUninjectExternalTarget.Resolved, Is.Null);
                Assert.That(other.Resolve<ReentrantScanResolvedService>(), Is.Not.Null);
            }
            finally
            {
                ResolvingPreUninjectExternalTarget.OtherScope = null;
                parent.Dispose();
                other.Dispose();
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                ResolvingPreUninjectExternalTarget.Reset();
            }
        }

        [Test]
        public void InjectGameObject_SiblingDestroyedDuringLaterPostInject_RollsBackHierarchy()
        {
            InjectedSiblingVictim.PreUninjectCalls = 0;
            InjectedSiblingDestroyer.PreUninjectCalls = 0;
            var target = new GameObject("transactional-injection-siblings");
            var victim = target.AddComponent<InjectedSiblingVictim>();
            var destroyer = target.AddComponent<InjectedSiblingDestroyer>();
            InjectedSiblingDestroyer.Victim = victim;
            var dependency = new AtomicDependency();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(dependency);
            var scope = builder.Build(name: "transactional-injection-siblings");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.InjectGameObject(target));
                Assert.That(InjectedSiblingVictim.PreUninjectCalls, Is.EqualTo(1));
                Assert.That(InjectedSiblingDestroyer.PreUninjectCalls, Is.EqualTo(1));
                Assert.That(destroyer.Dependency, Is.Null,
                    "The surviving sibling must have its partial injection rolled back.");
                Assert.That(scope.Resolve<IAtomicDependency>(), Is.SameAs(dependency));
            }
            finally
            {
                InjectedSiblingDestroyer.Victim = null;
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                scope.Dispose();
                InjectedSiblingVictim.PreUninjectCalls = 0;
                InjectedSiblingDestroyer.PreUninjectCalls = 0;
            }
        }

        [Test]
        public void HostlessUnityServiceDestroyedInsideItsFirstActivation_RollsBackBeforeReturn()
        {
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterFactory(
                () => new HostlessFirstActivationDestroyer(), Lifetime.Scoped);
            var scope = builder.Build(name: "hostless-first-activation-destroy");

            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => scope.Resolve<HostlessFirstActivationDestroyer>());
                Assert.Throws<ObjectDisposedException>(
                    () => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
            }
        }

        [Test]
        public void CommittedHostlessUnityServiceDestroyedBeforeLaterActivation_FailsEntryAndReleasesTransaction()
        {
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterFactory<HostlessCommittedDestroyer>(
                () => new HostlessCommittedDestroyer(), Lifetime.Scoped);
            var scope = builder.Build(name: "hostless-committed-destroy");

            try
            {
                scope.Resolve<MonitoredScriptableDependency>();
                UnityEngine.Object.DestroyImmediate(dependency);
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<HostlessCommittedDestroyer>());
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());

                var independentScope = new ScopeBuilder().Build(name: "after-hostless-invalidation");
                independentScope.Dispose();
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
            }
        }

        [Test]
        public void EmptyChildBuild_RejectsDestroyedHostlessServiceInParentBeforeAttach()
        {
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var parentBuilder = new ScopeBuilder();
            parentBuilder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            var parent = parentBuilder.Build(name: "destroyed-hostless-parent");

            try
            {
                parent.Resolve<MonitoredScriptableDependency>();
                UnityEngine.Object.DestroyImmediate(dependency);

                var childBuilder = new ScopeBuilder();
                Assert.Throws<InvalidOperationException>(
                    () => childBuilder.Build(parent, "rejected-empty-child"));
                Assert.Throws<ObjectDisposedException>(
                    () => parent.Resolve<MonitoredScriptableDependency>());

                var independent = new ScopeBuilder().Build(name: "after-rejected-empty-child");
                independent.Dispose();
            }
            finally
            {
                parent.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
            }
        }

        [Test]
        public void LifetimeMonitor_RunsBeforeFixedUpdateConsumers()
        {
            MonitoredFixedConsumer.FixedCalls = 0;
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var consumer = new MonitoredFixedConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.Bind<MonitoredFixedConsumer>().FromInstance(consumer);
            var scope = builder.Build(name: "hostless-fixed-service");
            var manager = UpdateLoopManager.Instance;

            try
            {
                UnityEngine.Object.DestroyImmediate(dependency);
                InvokeFixedUpdate(manager);

                Assert.That(MonitoredFixedConsumer.FixedCalls, Is.Zero);
                Assert.That(consumer.Dependency, Is.Null);
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredFixedConsumer>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
                MonitoredFixedConsumer.FixedCalls = 0;
            }
        }

        [Test]
        public void DestroyedHostlessDependency_SkipsLaterConsumerInSameUpdatePhase()
        {
            HostlessPhaseConsumer.UpdateCalls = 0;
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var destroyer = new HostlessPhaseDestroyer(dependency);
            var consumer = new HostlessPhaseConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterInstance(destroyer);
            builder.RegisterInstance(consumer);
            var scope = builder.Build(name: "same-phase-hostless-destruction");
            var manager = UpdateLoopManager.Instance;

            try
            {
                InvokeUpdate(manager);

                Assert.That(HostlessPhaseConsumer.UpdateCalls, Is.Zero);
                Assert.That(consumer.Dependency, Is.Null);
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
                HostlessPhaseConsumer.UpdateCalls = 0;
            }
        }

        [Test]
        public void DestroyedHostlessDependency_SkipsTransitiveConsumerInSameUpdatePhase()
        {
            TransitiveHostlessPhaseConsumer.UpdateCalls = 0;
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var destroyer = new HostlessPhaseDestroyer(dependency);
            var bridge = new HostlessPhaseBridge();
            var consumer = new TransitiveHostlessPhaseConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterInstance(destroyer);
            builder.RegisterInstance(bridge);
            builder.RegisterInstance(consumer);
            var scope = builder.Build(name: "same-phase-transitive-hostless-destruction");
            var manager = UpdateLoopManager.Instance;

            try
            {
                InvokeUpdate(manager);

                Assert.That(TransitiveHostlessPhaseConsumer.UpdateCalls, Is.Zero);
                Assert.That(consumer.Bridge, Is.Null);
                Assert.That(bridge.Dependency, Is.Null);
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
                TransitiveHostlessPhaseConsumer.UpdateCalls = 0;
            }
        }

        [Test]
        public void DestroyedExternalTarget_DoesNotMaskSamePhaseCachedServiceDestruction()
        {
            HostlessPhaseConsumer.UpdateCalls = 0;
            MonitoredExternalScriptableTarget.PreUninjectCalls = 0;
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var externalTarget = ScriptableObject.CreateInstance<MonitoredExternalScriptableTarget>();
            var destroyer = new DualHostlessPhaseDestroyer(dependency, externalTarget);
            var consumer = new HostlessPhaseConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(new AtomicDependency());
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterInstance(destroyer);
            builder.RegisterInstance(consumer);
            var scope = builder.Build(name: "same-phase-dual-hostless-destruction");
            externalTarget.Inject(scope);
            var manager = UpdateLoopManager.Instance;

            try
            {
                InvokeUpdate(manager);

                Assert.That(HostlessPhaseConsumer.UpdateCalls, Is.Zero);
                Assert.That(MonitoredExternalScriptableTarget.PreUninjectCalls, Is.EqualTo(1));
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
                if (externalTarget != null) UnityEngine.Object.DestroyImmediate(externalTarget);
                HostlessPhaseConsumer.UpdateCalls = 0;
                MonitoredExternalScriptableTarget.PreUninjectCalls = 0;
            }
        }

        [Test]
        public void UnexpectedUpdateManagerDestruction_TransfersRegistrationsAndLifetimeMonitoring()
        {
            ReplacementUpdateProbe.UpdateCalls = 0;
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var consumer = new MonitoredScriptableConsumer();
            var updater = new ReplacementUpdateProbe();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterInstance(consumer);
            builder.RegisterInstance(updater);
            var scope = builder.Build(name: "replace-update-manager");
            var original = UpdateLoopManager.Instance;

            try
            {
                UnityEngine.Object.DestroyImmediate(original.gameObject);
                var replacement = UpdateLoopManager.Instance;
                Assert.That(ReferenceEquals(original, replacement), Is.False);
                Assert.Throws<InvalidOperationException>(() => original.GetRegisteredCount());
                Assert.Throws<InvalidOperationException>(() => original.Register(new ReplacementUpdateProbe()));
                Assert.Throws<InvalidOperationException>(() => original.Unregister(updater));

                InvokeUpdate(replacement);
                Assert.That(ReplacementUpdateProbe.UpdateCalls, Is.EqualTo(1));

                UnityEngine.Object.DestroyImmediate(dependency);
                InvokeUpdate(replacement);
                Assert.That(ReplacementUpdateProbe.UpdateCalls, Is.EqualTo(1));
                Assert.That(consumer.Dependency, Is.Null);
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<MonitoredScriptableDependency>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
                ReplacementUpdateProbe.UpdateCalls = 0;
            }
        }

        [Test]
        public void ManagerReplacementDuringLifetimePoll_DoesNotCorruptTheActiveSnapshot()
        {
            ManagerDestroyingExternalTarget.PreUninjectCalls = 0;
            var manager = UpdateLoopManager.Instance;
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(new AtomicDependency());
            var scope = builder.Build(name: "manager-replacement-during-poll");
            var targets = new[]
            {
                ScriptableObject.CreateInstance<ManagerDestroyingExternalTarget>(),
                ScriptableObject.CreateInstance<ManagerDestroyingExternalTarget>(),
                ScriptableObject.CreateInstance<ManagerDestroyingExternalTarget>()
            };

            try
            {
                for (var i = 0; i < targets.Length; i++)
                    targets[i].Inject(scope);

                var lifetimeField = typeof(UpdateLoopManager).GetField(
                    "_unityLifetimes", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(lifetimeField, Is.Not.Null);
                ManagerDestroyingExternalTarget firstPolledTarget = null;
                foreach (var lease in (System.Collections.IEnumerable)lifetimeField.GetValue(manager))
                {
                    var targetProperty = lease.GetType().GetProperty(
                        "Target", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (targetProperty?.GetValue(lease) is ManagerDestroyingExternalTarget candidate)
                        firstPolledTarget = candidate;
                }
                Assert.That(firstPolledTarget, Is.Not.Null);
                firstPolledTarget.DestroyManagerOnCleanup = true;
                ManagerDestroyingExternalTarget.ManagerToDestroy = manager;

                for (var i = 0; i < targets.Length; i++)
                    UnityEngine.Object.DestroyImmediate(targets[i]);

                Assert.DoesNotThrow(() => InvokeUpdate(manager));
                Assert.That(ManagerDestroyingExternalTarget.PreUninjectCalls, Is.EqualTo(targets.Length));
                Assert.That(ReferenceEquals(manager, UpdateLoopManager.Instance), Is.False);
            }
            finally
            {
                ManagerDestroyingExternalTarget.ManagerToDestroy = null;
                scope.Dispose();
                for (var i = 0; i < targets.Length; i++)
                {
                    if (targets[i] != null) UnityEngine.Object.DestroyImmediate(targets[i]);
                }
                ManagerDestroyingExternalTarget.PreUninjectCalls = 0;
            }
        }

        [Test]
        public void ManagerReplacementDuringScopeGuard_SkipsTheStaleManagerCallback()
        {
            ReplacementUpdateProbe.UpdateCalls = 0;
            ManagerDestroyingExternalTarget.PreUninjectCalls = 0;
            var target = ScriptableObject.CreateInstance<ManagerDestroyingExternalTarget>();
            var destroyer = new ExternalTargetPhaseDestroyer(target);
            var updater = new ReplacementUpdateProbe();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(new AtomicDependency());
            builder.RegisterInstance(destroyer);
            builder.RegisterInstance(updater);
            var scope = builder.Build(name: "manager-replacement-during-scope-guard");
            target.Inject(scope);
            var original = UpdateLoopManager.Instance;
            target.DestroyManagerOnCleanup = true;
            ManagerDestroyingExternalTarget.ManagerToDestroy = original;

            try
            {
                InvokeUpdate(original);
                Assert.That(ReplacementUpdateProbe.UpdateCalls, Is.Zero,
                    "The destroyed manager must not invoke a callback after transferring ownership.");

                var replacement = UpdateLoopManager.Instance;
                Assert.That(ReferenceEquals(original, replacement), Is.False);
                InvokeUpdate(replacement);
                Assert.That(ReplacementUpdateProbe.UpdateCalls, Is.EqualTo(1));
            }
            finally
            {
                ManagerDestroyingExternalTarget.ManagerToDestroy = null;
                scope.Dispose();
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                ReplacementUpdateProbe.UpdateCalls = 0;
                ManagerDestroyingExternalTarget.PreUninjectCalls = 0;
            }
        }

        [Test]
        public void ManagerDestroyedByPriorityGetter_RebuildsListsFromRegistrationLedger()
        {
            ManagerDestroyingPriorityProbe.Reset();
            PriorityPeerUpdateProbe.UpdateCalls = 0;
            ReplacementUpdateProbe.UpdateCalls = 0;
            var manuallyRegisteredDuringSort = new ReplacementUpdateProbe();
            var priorityProbe = new ManagerDestroyingPriorityProbe();
            var peer = new PriorityPeerUpdateProbe();
            var builder = new ScopeBuilder();
            builder.RegisterInstance(priorityProbe);
            builder.RegisterInstance(peer);
            var scope = builder.Build(name: "manager-destroyed-during-priority-sort");
            var original = UpdateLoopManager.Instance;
            ManagerDestroyingPriorityProbe.ManagerToDestroy = original;
            ManagerDestroyingPriorityProbe.RegisterBeforeDestroy = manuallyRegisteredDuringSort;

            try
            {
                Assert.DoesNotThrow(() => InvokeUpdate(original));
                Assert.That(ManagerDestroyingPriorityProbe.UpdateCalls, Is.Zero);
                Assert.That(PriorityPeerUpdateProbe.UpdateCalls, Is.Zero);

                var replacement = UpdateLoopManager.Instance;
                Assert.That(ReferenceEquals(original, replacement), Is.False);
                Assert.That(replacement.GetRegisteredCount().update, Is.EqualTo(3));
                InvokeUpdate(replacement);
                Assert.That(ManagerDestroyingPriorityProbe.UpdateCalls, Is.EqualTo(1));
                Assert.That(PriorityPeerUpdateProbe.UpdateCalls, Is.EqualTo(1));
                Assert.That(ReplacementUpdateProbe.UpdateCalls, Is.EqualTo(1));
            }
            finally
            {
                ManagerDestroyingPriorityProbe.ManagerToDestroy = null;
                ManagerDestroyingPriorityProbe.RegisterBeforeDestroy = null;
                UpdateLoopManager.Instance.Unregister(manuallyRegisteredDuringSort);
                scope.Dispose();
                ManagerDestroyingPriorityProbe.Reset();
                PriorityPeerUpdateProbe.UpdateCalls = 0;
                ReplacementUpdateProbe.UpdateCalls = 0;
            }
        }

        [Test]
        public void ManualAndScopeManagedUpdateRegistration_CannotShareAnIdentity()
        {
            ReplacementUpdateProbe.UpdateCalls = 0;
            var manager = UpdateLoopManager.Instance;
            var scopeManaged = new ReplacementUpdateProbe();
            var scopedBuilder = new ScopeBuilder();
            scopedBuilder.RegisterInstance(scopeManaged);
            var scope = scopedBuilder.Build(name: "scope-managed-update");

            try
            {
                Assert.Throws<InvalidOperationException>(() => manager.Register(scopeManaged));
                Assert.Throws<InvalidOperationException>(() => manager.Unregister(scopeManaged));

                var manuallyManaged = new ReplacementUpdateProbe();
                manager.Register(manuallyManaged);
                try
                {
                    var conflictingBuilder = new ScopeBuilder();
                    conflictingBuilder.RegisterInstance(manuallyManaged);
                    Assert.Throws<InvalidOperationException>(() => conflictingBuilder.Build(name: "manual-update-conflict"));
                }
                finally
                {
                    manager.Unregister(manuallyManaged);
                }
            }
            finally
            {
                scope.Dispose();
                ReplacementUpdateProbe.UpdateCalls = 0;
            }
        }

        [Test]
        public void ManualUpdateRegistration_DuringActivationIsRejectedWithoutLeakingRegistration()
        {
            ReplacementUpdateProbe.UpdateCalls = 0;
            var manager = UpdateLoopManager.Instance;
            var updater = new ReplacementUpdateProbe();
            var builder = new ScopeBuilder();
            builder.RegisterFactory<ManualUpdateRegistrationDuringActivation>(() =>
            {
                manager.Register(updater);
                return new ManualUpdateRegistrationDuringActivation();
            }, Lifetime.Scoped);
            var scope = builder.Build(name: "manual-update-during-activation");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<ManualUpdateRegistrationDuringActivation>());
                InvokeUpdate(manager);
                Assert.That(ReplacementUpdateProbe.UpdateCalls, Is.Zero);
            }
            finally
            {
                manager.Unregister(updater);
                scope.Dispose();
                ReplacementUpdateProbe.UpdateCalls = 0;
            }
        }

        [Test]
        public void ManualUpdateUnregistration_DuringActivationIsRejectedAndRolledBackByRefusal()
        {
            ReplacementUpdateProbe.UpdateCalls = 0;
            var manager = UpdateLoopManager.Instance;
            var updater = new ReplacementUpdateProbe();
            manager.Register(updater);
            var builder = new ScopeBuilder();
            builder.RegisterFactory<ManualUpdateRegistrationDuringActivation>(() =>
            {
                manager.Unregister(updater);
                return new ManualUpdateRegistrationDuringActivation();
            }, Lifetime.Scoped);
            var scope = builder.Build(name: "manual-update-unregister-during-activation");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<ManualUpdateRegistrationDuringActivation>());
                InvokeUpdate(manager);
                Assert.That(ReplacementUpdateProbe.UpdateCalls, Is.EqualTo(1));
            }
            finally
            {
                manager.Unregister(updater);
                scope.Dispose();
                ReplacementUpdateProbe.UpdateCalls = 0;
            }
        }

        [Test]
        public void DirectInjectionAndManualUpdateRegistration_CannotShareAnIdentity()
        {
            var manager = UpdateLoopManager.Instance;
            var dependency = new AtomicDependency();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(dependency);
            var scope = builder.Build(name: "direct-injection-update-owner");
            var injectedFirst = new DirectInjectedUpdateProbe();
            var manualFirst = new DirectInjectedUpdateProbe();

            try
            {
                injectedFirst.Inject(scope);
                Assert.Throws<InvalidOperationException>(() => manager.Register(injectedFirst));

                manager.Register(manualFirst);
                Assert.Throws<InvalidOperationException>(() => manualFirst.Inject(scope));
                Assert.That(manualFirst.Dependency, Is.Null);
            }
            finally
            {
                manager.Unregister(manualFirst);
                scope.Dispose();
                Assert.That(injectedFirst.Dependency, Is.Null);
            }
        }

        [Test]
        public void FactoryCannotClaimManuallyRegisteredDisposableUpdater()
        {
            var manager = UpdateLoopManager.Instance;
            var updater = new ManualFactoryDisposableUpdater();
            manager.Register(updater);
            var builder = new ScopeBuilder();
            builder.RegisterFactory<ManualFactoryDisposableUpdater>(() => updater, Lifetime.Scoped);
            var scope = builder.Build(name: "manual-disposable-factory-conflict");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<ManualFactoryDisposableUpdater>());
                Assert.That(updater.DisposeCalls, Is.Zero);
                InvokeUpdate(manager);
                Assert.That(updater.UpdateCalls, Is.EqualTo(1));
            }
            finally
            {
                manager.Unregister(updater);
                scope.Dispose();
            }
        }

        [Test]
        public void FactoryCannotClaimManuallyRegisteredInjectableUpdater()
        {
            var manager = UpdateLoopManager.Instance;
            var updater = new ManualFactoryInjectableUpdater();
            manager.Register(updater);
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(new AtomicDependency());
            builder.RegisterFactory<ManualFactoryInjectableUpdater>(() => updater, Lifetime.Scoped);
            var scope = builder.Build(name: "manual-injectable-factory-conflict");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<ManualFactoryInjectableUpdater>());
                Assert.That(updater.Dependency, Is.Null);
                Assert.That(updater.DisposeCalls, Is.Zero);
                InvokeUpdate(manager);
                Assert.That(updater.UpdateCalls, Is.EqualTo(1));
            }
            finally
            {
                manager.Unregister(updater);
                scope.Dispose();
            }
        }

        [Test]
        public void SubsystemReset_ClearsUpdateManagerCreatedByScopeCleanup()
        {
            var builder = new ScopeBuilder();
            builder.RegisterFactory<ResetUpdateLoopCreator>(
                () => new ResetUpdateLoopCreator(), Lifetime.Scoped);
            var scope = builder.Build(name: "reset-update-loop-order");
            scope.Resolve<ResetUpdateLoopCreator>();

            var reset = typeof(KDI).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(reset, Is.Not.Null);
            reset.Invoke(null, null);

            var instance = typeof(UpdateLoopManager).GetField(
                "_instance", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(instance, Is.Not.Null);
            Assert.That(instance.GetValue(null), Is.Null,
                "The centralized reset must clear a manager created from IDisposable cleanup.");
        }

        [Test]
        public void DestroyCallback_DoesNotInvalidateNestedTransactionSavepoint()
        {
            var serviceObject = new GameObject("transaction-tombstone-service");
            var service = serviceObject.AddComponent<TransactionTombstoneService>();
            var failure = new TransactionDestroyFailure();
            IScope scope = null;
            var builder = new ScopeBuilder();
            builder.Bind<TransactionTombstoneService>().FromFactory(() => service).AsScoped();
            builder.RegisterFactory<TransactionDestroyCoordinator>(() =>
            {
                failure.Inject(scope);
                return new TransactionDestroyCoordinator();
            }, Lifetime.Scoped);
            scope = builder.Build(name: "transaction-tombstone");

            try
            {
                var exception = Assert.Throws<InvalidOperationException>(
                    () => scope.Resolve<TransactionDestroyCoordinator>());
                Assert.That(ContainsException<ArgumentOutOfRangeException>(exception), Is.False,
                    "A destruction callback must not shift a nested rollback checkpoint.");
                Assert.That(ContainsMessage(exception, "expected nested destroy failure"), Is.True);
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(serviceObject);
            }
        }

        [Test]
        public void LaterActivationDestroyingEarlierCachedDependency_RollsBackWholeGraph()
        {
            var dependencyObject = new GameObject("later-activation-destroyed-dependency");
            var dependency = dependencyObject.AddComponent<CachedUnityDependency>();
            TransactionalUnityConsumer consumer = null;
            var builder = new ScopeBuilder();
            builder.Bind<CachedUnityDependency>().FromFactory(() => dependency).AsScoped();
            builder.Bind<TransactionalUnityConsumer>()
                .FromFactory(() => consumer = new TransactionalUnityConsumer())
                .AsScoped();
            builder.RegisterFactory(
                () => new SuccessfulUnityDestroyer(), Lifetime.Scoped);
            builder.RegisterFactory(
                () => new LaterDestroyCoordinator(), Lifetime.Scoped);
            var scope = builder.Build(name: "later-activation-destroyed-dependency");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<LaterDestroyCoordinator>());
                Assert.That(consumer, Is.Not.Null);
                Assert.That(consumer.Dependency, Is.Null);
                Assert.That(consumer.PreUninjectCalls, Is.EqualTo(1));
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<LaterDestroyCoordinator>());
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(dependencyObject);
            }
        }

        [Test]
        public void OwnedPrefab_DeactivatesBeforeItsChildScopeRevokesFields()
        {
            OwnedPrefabDisableProbe.ResetObservation();
            var prefab = new GameObject("owned-child-scope-prefab");
            prefab.SetActive(false);
            var prefabScope = prefab.AddComponent<OwnedPrefabLifetimeScope>();
            SetAutoInitialize(prefabScope, false);
            prefab.AddComponent<OwnedPrefabDisableProbe>();
            var parent = new ScopeBuilder().Build(name: "owned-child-scope-parent");
            GameObject instance = null;

            try
            {
                instance = parent.Instantiate(prefab);
                var child = instance.GetComponent<OwnedPrefabLifetimeScope>();
                child.Initialize();
                instance.SetActive(true);

                parent.Dispose();

                Assert.That(OwnedPrefabDisableProbe.DisableCalls, Is.EqualTo(1));
                Assert.That(OwnedPrefabDisableProbe.LastDisableHadDependency, Is.True,
                    "OnDisable must run before the dynamic child Scope revokes injected fields.");
            }
            finally
            {
                parent.Dispose();
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void DependencyDestroyedDuringPostInject_RollsBackTheConsumer()
        {
            var dependencyObject = new GameObject("destroyed-during-post-inject");
            var dependency = dependencyObject.AddComponent<PostInjectDestroyedDependency>();
            var consumer = new DependencyDestroyingConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<PostInjectDestroyedDependency>().FromFactory(() => dependency).AsScoped();
            builder.Bind<DependencyDestroyingConsumer>().FromInstance(consumer);

            try
            {
                Assert.Throws<InvalidOperationException>(() => builder.Build(name: "destroyed-during-post-inject"));
                Assert.That(consumer.Dependency, Is.Null);
                Assert.That(consumer.PreUninjectCalls, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dependencyObject);
            }
        }

        [Test]
        public void TargetDestroyedByLaterFieldFactory_NeverReceivesPostInject()
        {
            PreAssignmentValidationTarget.PostInjectCalls = 0;
            var targetObject = new GameObject("pre-assignment-validation-target");
            var target = targetObject.AddComponent<PreAssignmentValidationTarget>();
            var dependency = new AtomicDependency();
            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().FromInstance(dependency);
            builder.RegisterFactory<DestroyInjectionTargetDuringResolve>(() =>
            {
                UnityEngine.Object.DestroyImmediate(target);
                return new DestroyInjectionTargetDuringResolve();
            }, Lifetime.Scoped);
            var scope = builder.Build(name: "pre-assignment-validation");

            try
            {
                Assert.Throws<InvalidOperationException>(() => target.Inject(scope));
                Assert.That(target == null, Is.True);
                Assert.That(PreAssignmentValidationTarget.PostInjectCalls, Is.Zero);
                Assert.That(scope.Resolve<IAtomicDependency>(), Is.SameAs(dependency));
            }
            finally
            {
                scope.Dispose();
                if (targetObject != null) UnityEngine.Object.DestroyImmediate(targetObject);
                PreAssignmentValidationTarget.PostInjectCalls = 0;
            }
        }

        [Test]
        public void TransitiveDependencyDestroyedByLaterFieldFactory_NeverReachesPostInject()
        {
            TransitiveActivationValidationTarget.PostInjectCalls = 0;
            var dependency = ScriptableObject.CreateInstance<MonitoredScriptableDependency>();
            var bridge = new TransitiveActivationValidationBridge();
            var target = new TransitiveActivationValidationTarget();
            var builder = new ScopeBuilder();
            builder.Bind<MonitoredScriptableDependency>().FromFactory(() => dependency).AsScoped();
            builder.RegisterInstance(bridge);
            builder.RegisterFactory<DestroyTransitiveDependencyDuringResolve>(() =>
            {
                UnityEngine.Object.DestroyImmediate(dependency);
                return new DestroyTransitiveDependencyDuringResolve();
            }, Lifetime.Scoped);
            var scope = builder.Build(name: "transitive-pre-callback-validation");

            try
            {
                Assert.Throws<InvalidOperationException>(() => target.Inject(scope));
                Assert.That(TransitiveActivationValidationTarget.PostInjectCalls, Is.Zero);
                Assert.That(target.Bridge, Is.Null);
                Assert.That(bridge.Dependency, Is.Null);
                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<TransitiveActivationValidationBridge>());
            }
            finally
            {
                scope.Dispose();
                if (dependency != null) UnityEngine.Object.DestroyImmediate(dependency);
                TransitiveActivationValidationTarget.PostInjectCalls = 0;
            }
        }

        private static bool ContainsException<T>(Exception exception) where T : Exception
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is T) return true;
            }
            return false;
        }

        private static bool ContainsMessage(Exception exception, string expected)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current.Message != null && current.Message.Contains(expected)) return true;
            }
            return false;
        }

        private static void InvokeUpdate(UpdateLoopManager manager)
        {
            var method = typeof(UpdateLoopManager).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            try { method.Invoke(manager, null); }
            catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
        }

        private static void InvokeFixedUpdate(UpdateLoopManager manager)
        {
            var method = typeof(UpdateLoopManager).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            try { method.Invoke(manager, null); }
            catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
        }

        private static void SetAutoInitialize(LifetimeScope scope, bool value)
        {
            typeof(LifetimeScope).GetField("_autoInitialize", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(scope, value);
        }

        private static void SetSerializedParent(LifetimeScope child, LifetimeScope parent)
        {
            typeof(LifetimeScope).GetField("_parent", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(child, parent);
        }
    }

    public sealed class PrefabAwakeProbe : MonoBehaviour, IInjectable
    {
        [Inject] private IAtomicDependency _dependency;
        public static int AwakeCalls { get; private set; }
        public static bool LastAwakeHadDependency { get; private set; }

        private void Awake()
        {
            AwakeCalls++;
            LastAwakeHadDependency = _dependency != null;
        }

        public static void ResetObservations()
        {
            AwakeCalls = 0;
            LastAwakeHadDependency = false;
        }
    }

    public sealed class DestroyCleanupProbe : MonoBehaviour, IInjectable, IPostInjectable, IPreUninjectable
    {
#pragma warning disable CS0169
        [Inject] private IAtomicDependency _dependency;
#pragma warning restore CS0169
        public static int PreUninjectCalls;
        public void PostInject() { }
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class DestinationParentDestroyingProbe : MonoBehaviour, IInjectable, IPostInjectable
    {
        public static GameObject DestinationParent;
        public static GameObject LastInstance;

        public void PostInject()
        {
            LastInstance = gameObject;
            UnityEngine.Object.DestroyImmediate(DestinationParent);
        }
    }

    public sealed class TestLifetimeScope : LifetimeScope
    {
        protected override void Configure(ScopeBuilder builder) { }
    }

    public sealed class ActivationSpawnResult : IInjectable, IPostInjectable
    {
        [Inject] private IInstantiator _instantiator;
        private readonly GameObject _prefab;

        public ActivationSpawnResult(GameObject prefab) => _prefab = prefab;

        public void PostInject() => _instantiator.Instantiate(_prefab);
    }
    public sealed class DisposeDuringActivationResult { }
    public sealed class FailingLifetimeEntryPoint { }

    public sealed class NonInjectableUnityUpdateProbe : MonoBehaviour, IUpdatable
    {
        public static int UpdateCalls;
        public void KDIUpdate(float deltaTime) => UpdateCalls++;
    }

    public sealed class TransactionTombstoneService : MonoBehaviour { }
    public sealed class TransactionDestroyFailure : IInjectable, IPostInjectable
    {
        [Inject] private TransactionTombstoneService _service;

        public void PostInject()
        {
            UnityEngine.Object.DestroyImmediate(_service);
            throw new InvalidOperationException("expected nested destroy failure");
        }
    }

    public sealed class TransactionDestroyCoordinator { }
    public sealed class CrossScopeInvalidationActivation : IInjectable
    {
#pragma warning disable CS0169
        [Inject] private CachedUnityDependency _dependency;
#pragma warning restore CS0169
    }
    public sealed class SuccessfulUnityDestroyer : IInjectable, IPostInjectable
    {
        [Inject] private CachedUnityDependency _dependency;
#pragma warning disable CS0169
        [Inject] private TransactionalUnityConsumer _consumer;
#pragma warning restore CS0169
        public void PostInject() => UnityEngine.Object.DestroyImmediate(_dependency);
    }

    public sealed class LaterDestroyCoordinator : IInjectable
    {
#pragma warning disable CS0169
        [Inject] private SuccessfulUnityDestroyer _destroyer;
#pragma warning restore CS0169
    }

    public sealed class OwnedPrefabDependency : IDependencyObject { }

    public sealed class OwnedPrefabLifetimeScope : LifetimeScope
    {
        protected override void Configure(ScopeBuilder builder) =>
            builder.Bind<OwnedPrefabDependency>().ToSelf().AsScoped();
    }

    public sealed class OwnedPrefabDisableProbe : MonoBehaviour, IInjectable
    {
        [Inject] private OwnedPrefabDependency _dependency;
        public static int DisableCalls { get; private set; }
        public static bool LastDisableHadDependency { get; private set; }

        private void OnDisable()
        {
            DisableCalls++;
            LastDisableHadDependency = _dependency != null;
        }

        public static void ResetObservation()
        {
            DisableCalls = 0;
            LastDisableHadDependency = false;
        }
    }

    public sealed class PostInjectDestroyedDependency : MonoBehaviour { }

    public sealed class DependencyDestroyingConsumer : IDependencyObject, IInjectable, IPostInjectable, IPreUninjectable
    {
        [Inject] private PostInjectDestroyedDependency _dependency;
        public PostInjectDestroyedDependency Dependency => _dependency;
        public int PreUninjectCalls { get; private set; }

        public void PostInject() => UnityEngine.Object.DestroyImmediate(_dependency);
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class DestroyInjectionTargetDuringResolve { }

    public sealed class PreAssignmentValidationTarget : MonoBehaviour, IInjectable, IPostInjectable
    {
#pragma warning disable CS0169
        [Inject] private IAtomicDependency _dependency;
        [Inject] private DestroyInjectionTargetDuringResolve _destroyer;
#pragma warning restore CS0169
        public static int PostInjectCalls;
        public void PostInject() => PostInjectCalls++;
    }

    public sealed class DestroyTransitiveDependencyDuringResolve { }

    public sealed class TransitiveActivationValidationBridge :
        IInjectable,
        IPostInjectable,
        IPreUninjectable
    {
        [Inject] private MonitoredScriptableDependency _dependency;
        public MonitoredScriptableDependency Dependency => _dependency;
        public void PostInject() { }
        public void PreUninject() { }
    }

    public sealed class TransitiveActivationValidationTarget :
        IInjectable,
        IPostInjectable,
        IPreUninjectable
    {
        [Inject] private TransitiveActivationValidationBridge _bridge;
#pragma warning disable CS0169
        [Inject] private DestroyTransitiveDependencyDuringResolve _destroyer;
#pragma warning restore CS0169
        public static int PostInjectCalls;
        public TransitiveActivationValidationBridge Bridge => _bridge;
        public void PostInject() => PostInjectCalls++;
        public void PreUninject() { }
    }

    public sealed class CachedUnityDependency : MonoBehaviour { }

    public sealed class CachedUnityConsumer : IDependencyObject, IInjectable, IPostInjectable, IPreUninjectable
    {
        [Inject] private CachedUnityDependency _dependency;
        public CachedUnityDependency Dependency => _dependency;
        public int PreUninjectCalls { get; private set; }
        public void PostInject() { }
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class TransactionalUnityConsumer : IDependencyObject, IInjectable, IPostInjectable, IPreUninjectable
    {
        [Inject] private CachedUnityDependency _dependency;
        public CachedUnityDependency Dependency => _dependency;
        public int PreUninjectCalls { get; private set; }
        public void PostInject() { }
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class MonitoredScriptableDependency : ScriptableObject { }

    public sealed class MonitoredExternalScriptableTarget :
        ScriptableObject,
        IInjectable,
        IPostInjectable,
        IPreUninjectable
    {
#pragma warning disable CS0169
        [Inject] private IAtomicDependency _dependency;
#pragma warning restore CS0169
        public static int PreUninjectCalls;
        public void PostInject() { }
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class HostlessFirstActivationDestroyer : IInjectable, IPostInjectable
    {
        [Inject] private MonitoredScriptableDependency _dependency;
        public void PostInject() => UnityEngine.Object.DestroyImmediate(_dependency);
    }
    public sealed class HostlessCommittedDestroyer { }
    public sealed class ExternalInjectionDestroyer { }
    public sealed class LifetimeOwnerDestroyer { }

    public sealed class SceneScopeDisableProbe : MonoBehaviour, IInjectable
    {
        [Inject] private OwnedPrefabDependency _dependency;
        public static int DisableCalls { get; private set; }
        public static bool LastDisableHadDependency { get; private set; }
        public OwnedPrefabDependency Dependency => _dependency;

        private void OnDisable()
        {
            DisableCalls++;
            LastDisableHadDependency = _dependency != null;
        }

        public static void ResetObservation()
        {
            DisableCalls = 0;
            LastDisableHadDependency = false;
        }
    }

    public sealed class HostlessPhaseDestroyer : IUpdatable, IUpdatePriority
    {
        private readonly MonitoredScriptableDependency _dependency;
        public int UpdatePriority => -100;
        public HostlessPhaseDestroyer(MonitoredScriptableDependency dependency) => _dependency = dependency;
        public void KDIUpdate(float deltaTime) => UnityEngine.Object.DestroyImmediate(_dependency);
    }

    public sealed class DualHostlessPhaseDestroyer : IUpdatable, IUpdatePriority
    {
        private readonly MonitoredScriptableDependency _dependency;
        private readonly MonitoredExternalScriptableTarget _externalTarget;
        public int UpdatePriority => -100;

        public DualHostlessPhaseDestroyer(
            MonitoredScriptableDependency dependency,
            MonitoredExternalScriptableTarget externalTarget)
        {
            _dependency = dependency;
            _externalTarget = externalTarget;
        }

        public void KDIUpdate(float deltaTime)
        {
            UnityEngine.Object.DestroyImmediate(_dependency);
            UnityEngine.Object.DestroyImmediate(_externalTarget);
        }
    }

    public sealed class ExternalTargetPhaseDestroyer : IUpdatable, IUpdatePriority
    {
        private readonly ManagerDestroyingExternalTarget _target;
        private bool _destroyed;
        public int UpdatePriority => -100;
        public ExternalTargetPhaseDestroyer(ManagerDestroyingExternalTarget target) => _target = target;

        public void KDIUpdate(float deltaTime)
        {
            if (_destroyed) return;
            _destroyed = true;
            UnityEngine.Object.DestroyImmediate(_target);
        }
    }

    public sealed class ManagerDestroyingExternalTarget :
        ScriptableObject,
        IInjectable,
        IPostInjectable,
        IPreUninjectable
    {
#pragma warning disable CS0169
        [Inject] private IAtomicDependency _dependency;
#pragma warning restore CS0169
        public static UpdateLoopManager ManagerToDestroy;
        public static int PreUninjectCalls;
        public bool DestroyManagerOnCleanup;
        public void PostInject() { }

        public void PreUninject()
        {
            PreUninjectCalls++;
            if (!DestroyManagerOnCleanup) return;
            var manager = ManagerToDestroy;
            ManagerToDestroy = null;
            if (manager != null)
                UnityEngine.Object.DestroyImmediate(manager.gameObject);
        }
    }

    public sealed class HostlessPhaseConsumer :
        IInjectable,
        IPostInjectable,
        IPreUninjectable,
        IUpdatable,
        IUpdatePriority
    {
        [Inject] private MonitoredScriptableDependency _dependency;
        public static int UpdateCalls;
        public int UpdatePriority => 100;
        public MonitoredScriptableDependency Dependency => _dependency;
        public void PostInject() { }
        public void PreUninject() { }
        public void KDIUpdate(float deltaTime) => UpdateCalls++;
    }

    public sealed class HostlessPhaseBridge : IInjectable, IPostInjectable, IPreUninjectable
    {
        [Inject] private MonitoredScriptableDependency _dependency;
        public MonitoredScriptableDependency Dependency => _dependency;
        public void PostInject() { }
        public void PreUninject() { }
    }

    public sealed class TransitiveHostlessPhaseConsumer :
        IInjectable,
        IPostInjectable,
        IPreUninjectable,
        IUpdatable,
        IUpdatePriority
    {
        [Inject] private HostlessPhaseBridge _bridge;
        public static int UpdateCalls;
        public int UpdatePriority => 100;
        public HostlessPhaseBridge Bridge => _bridge;
        public void PostInject() { }
        public void PreUninject() { }
        public void KDIUpdate(float deltaTime) => UpdateCalls++;
    }

    public sealed class ReplacementUpdateProbe : IUpdatable
    {
        public static int UpdateCalls;
        public void KDIUpdate(float deltaTime) => UpdateCalls++;
    }

    public sealed class ManagerDestroyingPriorityProbe : IUpdatable, IUpdatePriority
    {
        public static UpdateLoopManager ManagerToDestroy;
        public static ReplacementUpdateProbe RegisterBeforeDestroy;
        public static int UpdateCalls;
        public static int PriorityReads;

        public int UpdatePriority
        {
            get
            {
                PriorityReads++;
                var manager = ManagerToDestroy;
                ManagerToDestroy = null;
                var registration = RegisterBeforeDestroy;
                RegisterBeforeDestroy = null;
                if (manager != null && registration != null)
                    manager.Register(registration);
                if (manager != null)
                    UnityEngine.Object.DestroyImmediate(manager.gameObject);
                return -10;
            }
        }

        public void KDIUpdate(float deltaTime) => UpdateCalls++;

        public static void Reset()
        {
            ManagerToDestroy = null;
            RegisterBeforeDestroy = null;
            UpdateCalls = 0;
            PriorityReads = 0;
        }
    }

    public sealed class PriorityPeerUpdateProbe : IUpdatable, IUpdatePriority
    {
        public static int UpdateCalls;
        public int UpdatePriority => 10;
        public void KDIUpdate(float deltaTime) => UpdateCalls++;
    }

    public sealed class ManualUpdateRegistrationDuringActivation { }

    public sealed class DestroyCommittedHostlessAndThrow { }
    public sealed class RollbackHostlessDestroyCoordinator : IInjectable, IPostInjectable
    {
#pragma warning disable CS0169
        [Inject] private RollbackHostlessDestroyingDisposable _dependency;
#pragma warning restore CS0169

        public void PostInject() =>
            throw new InvalidOperationException("expected coordinator failure");
    }
    public sealed class ParentHostlessDependency : ScriptableObject { }
    public sealed class ChildHostlessDependency : ScriptableObject { }
    public sealed class DestroyParentAndChildHostlessThenThrow { }

    public sealed class RollbackHostlessDestroyingDisposable : IDisposable
    {
        private readonly MonitoredScriptableDependency _dependency;
        public int DisposeCalls { get; private set; }
        public RollbackHostlessDestroyingDisposable(MonitoredScriptableDependency dependency) =>
            _dependency = dependency;

        public void Dispose()
        {
            DisposeCalls++;
            if (_dependency != null)
                UnityEngine.Object.DestroyImmediate(_dependency);
        }
    }

    public sealed class HostlessPostDestroyingConsumer :
        IInjectable,
        IPostInjectable,
        IPreUninjectable
    {
        [Inject] private MonitoredScriptableDependency _dependency;
        public static int PostInjectCalls;
        public static int PreUninjectCalls;
        public MonitoredScriptableDependency Dependency => _dependency;

        public void PostInject()
        {
            PostInjectCalls++;
            UnityEngine.Object.DestroyImmediate(_dependency);
        }

        public void PreUninject() => PreUninjectCalls++;

        public static void Reset()
        {
            PostInjectCalls = 0;
            PreUninjectCalls = 0;
        }
    }

    public sealed class ReentrantScanResolvedService { }

    public sealed class ResolvingPreUninjectExternalTarget :
        ScriptableObject,
        IInjectable,
        IPostInjectable,
        IPreUninjectable
    {
#pragma warning disable CS0169
        [Inject] private IAtomicDependency _dependency;
#pragma warning restore CS0169
        public static IScope OtherScope;
        public static ReentrantScanResolvedService Resolved;
        public static int PreUninjectCalls;
        public static bool ResolveWasRejected;
        public void PostInject() { }

        public void PreUninject()
        {
            PreUninjectCalls++;
            try
            {
                Resolved = OtherScope?.Resolve<ReentrantScanResolvedService>();
            }
            catch (InvalidOperationException)
            {
                ResolveWasRejected = true;
            }
        }

        public static void Reset()
        {
            OtherScope = null;
            Resolved = null;
            PreUninjectCalls = 0;
            ResolveWasRejected = false;
        }
    }

    public sealed class DirectInjectedUpdateProbe :
        IInjectable,
        IPostInjectable,
        IPreUninjectable,
        IUpdatable
    {
        [Inject] private IAtomicDependency _dependency;
        public IAtomicDependency Dependency => _dependency;
        public void PostInject() { }
        public void PreUninject() { }
        public void KDIUpdate(float deltaTime) { }
    }

    public sealed class ManualFactoryDisposableUpdater : IDisposable, IUpdatable
    {
        public int DisposeCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public void Dispose() => DisposeCalls++;
        public void KDIUpdate(float deltaTime) => UpdateCalls++;
    }

    public sealed class ManualFactoryInjectableUpdater :
        IDisposable,
        IInjectable,
        IPostInjectable,
        IPreUninjectable,
        IUpdatable
    {
        [Inject] private IAtomicDependency _dependency;
        public IAtomicDependency Dependency => _dependency;
        public int DisposeCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public void PostInject() { }
        public void PreUninject() { }
        public void Dispose() => DisposeCalls++;
        public void KDIUpdate(float deltaTime) => UpdateCalls++;
    }

    public sealed class InjectedSiblingVictim : MonoBehaviour, IInjectable, IPostInjectable, IPreUninjectable
    {
#pragma warning disable CS0169
        [Inject] private IAtomicDependency _dependency;
#pragma warning restore CS0169
        public static int PreUninjectCalls;
        public void PostInject() { }
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class InjectedSiblingDestroyer : MonoBehaviour, IInjectable, IPostInjectable, IPreUninjectable
    {
        [Inject] private IAtomicDependency _dependency;
        public static InjectedSiblingVictim Victim;
        public static int PreUninjectCalls;
        public IAtomicDependency Dependency => _dependency;
        public void PostInject() => UnityEngine.Object.DestroyImmediate(Victim);
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class MonitoredScriptableConsumer : IDependencyObject, IInjectable, IPostInjectable, IPreUninjectable
    {
        [Inject] private MonitoredScriptableDependency _dependency;
        public MonitoredScriptableDependency Dependency => _dependency;
        public int PreUninjectCalls { get; private set; }
        public void PostInject() { }
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class MonitoredFixedConsumer :
        IDependencyObject,
        IInjectable,
        IPostInjectable,
        IPreUninjectable,
        IFixedUpdatable
    {
        [Inject] private MonitoredScriptableDependency _dependency;
        public static int FixedCalls;
        public MonitoredScriptableDependency Dependency => _dependency;
        public void PostInject() { }
        public void PreUninject() { }
        public void KDIFixedUpdate(float deltaTime) => FixedCalls++;
    }

    public sealed class ResetUpdateLoopCreator : IDisposable
    {
        public void Dispose()
        {
            var ignored = UpdateLoopManager.Instance;
        }
    }

    public sealed class ConfigurableLifetimeScope : LifetimeScope
    {
        public Action<ScopeBuilder> ConfigureAction { get; set; }
        protected override void Configure(ScopeBuilder builder) => ConfigureAction?.Invoke(builder);
    }
}
