using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Kylin.DI.Tests
{
    public sealed class FinalActivationCompensationTests
    {
        [SetUp]
        public void SetUp()
        {
            ActivationCompensationDependency.Reset();
            FailingFinalActivationBehaviour.Reset();
            EmptyScopeCaptureProbe.Reset();
            FailingAwakeLifetimeScope.Reset();
        }

        [Test]
        public void FinalOnEnableFailure_CompensatesCommittedParentRecordsAndConstructedChildScope()
        {
            var sourceHolder = new GameObject("inactive-prefab-source");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("failing-active-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<FailingFinalActivationBehaviour>();
            var child = new GameObject("empty-child-scope");
            child.transform.SetParent(prefab.transform, false);
            child.AddComponent<EmptyActivationLifetimeScope>();
            child.AddComponent<EmptyScopeCaptureProbe>();

            var builder = new ScopeBuilder();
            builder.Bind<IActivationCompensationDependency>()
                .FromFactory(() => new ActivationCompensationDependency())
                .AsScoped();
            var scope = builder.Build(name: "final-activation-parent");

            try
            {
                LogAssert.Expect(LogType.Exception, new Regex("expected final activation failure"));
                var exception = Assert.Throws<InvalidOperationException>(() => scope.Instantiate(prefab));

                StringAssert.Contains("Final prefab activation failed", exception.Message);
                Assert.That(FailingFinalActivationBehaviour.LastInstance == null, Is.True,
                    "The failed clone must be destroyed rather than returned in a disabled/partial state.");
                Assert.That(ActivationCompensationDependency.Created, Is.EqualTo(1));
                Assert.That(ActivationCompensationDependency.Disposed, Is.EqualTo(1),
                    "The dependency crossed the parent transaction commit and must be reverse-compensated.");
                Assert.That(EmptyScopeCaptureProbe.CapturedScope, Is.Not.Null);
                Assert.Throws<ObjectDisposedException>(() =>
                    EmptyScopeCaptureProbe.CapturedScope.Resolve<IActivationCompensationDependency>(),
                    "Even an empty child Scope has to be disposed; it may have no activation record receipt of its own.");

                var recovered = scope.Resolve<IActivationCompensationDependency>();
                Assert.That(recovered, Is.Not.Null);
                Assert.That(ActivationCompensationDependency.Created, Is.EqualTo(2),
                    "The compensated dependency must not remain cached in the parent Scope.");
            }
            finally
            {
                scope.Dispose();
                if (sourceHolder != null)
                    UnityEngine.Object.DestroyImmediate(sourceHolder);
            }
        }

        [Test]
        public void LifetimeScopeAwakeFailure_IsLatchedEvenWhenUnityConsumesTheCallbackException()
        {
            var sourceHolder = new GameObject("inactive-lifetime-source");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("failing-lifetime-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<FailingAwakeLifetimeScope>();

            var parent = new ScopeBuilder().Build(name: "lifetime-activation-parent");
            try
            {
                LogAssert.Expect(LogType.Exception, new Regex("expected lifetime activation failure"));
                var exception = Assert.Throws<InvalidOperationException>(() => parent.Instantiate(prefab));

                StringAssert.Contains("Final prefab activation failed", exception.Message);
                Assert.That(FailingAwakeLifetimeScope.LastConfiguredInstance == null, Is.True,
                    "The LifetimeScope that failed in Awake must not survive as a quarantined owned clone.");
            }
            finally
            {
                parent.Dispose();
                if (sourceHolder != null)
                    UnityEngine.Object.DestroyImmediate(sourceHolder);
            }
        }

        [Test]
        public void FinalActivation_RejectsManualUpdateRegistrationBeforeItCanEscapeCompensation()
        {
            var sourceHolder = new GameObject("manual-update-registration-source");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("manual-update-registration-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<FinalActivationManualUpdateRegistrationBehaviour>();

            var probe = new ManualUpdateProbe();
            FinalActivationManualUpdateRegistrationBehaviour.Probe = probe;
            var scope = new ScopeBuilder().Build(name: "manual-update-final-activation");
            var manager = UpdateLoopManager.Instance;

            try
            {
                LogAssert.Expect(LogType.Exception, new Regex("Manual registration during KDI activation"));
                Assert.Throws<InvalidOperationException>(() => scope.Instantiate(prefab));
                Assert.That(IsManuallyRegistered(probe), Is.False,
                    "A final-activation callback must fail before it mutates the manual update ledger.");

                manager.Register(probe);
                Assert.That(IsManuallyRegistered(probe), Is.True,
                    "The same operation remains valid after control returns to the composition boundary.");
                manager.Unregister(probe);
                Assert.That(IsManuallyRegistered(probe), Is.False);
            }
            finally
            {
                FinalActivationManualUpdateRegistrationBehaviour.Probe = null;
                if (IsManuallyRegistered(probe))
                    manager.Unregister(probe);
                scope.Dispose();
                if (sourceHolder != null)
                    UnityEngine.Object.DestroyImmediate(sourceHolder);
            }
        }

        [Test]
        public void RevokeInjection_OnBeforeUninjectReactivation_CannotReenterInjectedEnableOrLeakBucket()
        {
            var target = new GameObject("reentrant-revoke-target");
            var behaviour = target.AddComponent<ReentrantRevokeBehaviour>();
            var builder = new ScopeBuilder();
            builder.Bind<IActivationCompensationDependency>()
                .FromFactory(() => new ActivationCompensationDependency())
                .AsScoped();
            var scope = builder.Build(name: "reentrant-revoke");

            try
            {
                scope.InjectGameObject(target);
                Assert.That(behaviour.EnableCalls, Is.EqualTo(1));

                scope.Dispose();

                Assert.That(behaviour.EnableCalls, Is.EqualTo(1),
                    "OnEnable raised by OnBeforeUninject must not start another injected-active interval.");
                Assert.That(behaviour.DisableCalls, Is.EqualTo(1));
                Assert.That(behaviour.BeforeUninjectCalls, Is.EqualTo(1));
                Assert.That(behaviour.BeforeUninjectHadDependency, Is.True);
                Assert.That(behaviour.Dependency, Is.Null);
                Assert.That(behaviour.ExposesInjectedState, Is.False);
                Assert.That(behaviour.ExposesInstantiator, Is.False);
                Assert.That(IsCompositeDisposed(behaviour.LastActiveBucket), Is.True,
                    "The active bucket visible from the last OnInjectedEnable must always be closed.");
                Assert.That(behaviour.LateAddedDisposals, Is.EqualTo(1),
                    "A cleanup callback that adds to _cd must hit a disposed terminal bucket, not the next interval.");

                target.SetActive(false);
                Assert.That(behaviour.DisableCalls, Is.EqualTo(1),
                    "A revoked component must not retain a phantom injected-active state.");
            }
            finally
            {
                scope.Dispose();
                if (target != null)
                    UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void AbortInjection_PartialCleanupReactivation_CannotReenterInjectedEnableOrLeakBucket()
        {
            var target = new GameObject("reentrant-abort-target");
            var behaviour = target.AddComponent<ReentrantAbortBehaviour>();
            var builder = new ScopeBuilder();
            builder.Bind<IActivationCompensationDependency>()
                .FromFactory(() => new ActivationCompensationDependency())
                .AsScoped();
            var scope = builder.Build(name: "reentrant-abort");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.InjectGameObject(target));

                Assert.That(behaviour.EnableCalls, Is.EqualTo(1));
                Assert.That(behaviour.BeforeUninjectCalls, Is.EqualTo(1));
                Assert.That(behaviour.BeforeUninjectHadDependency, Is.True);
                Assert.That(behaviour.Dependency, Is.Null);
                Assert.That(behaviour.ExposesInjectedState, Is.False);
                Assert.That(behaviour.ExposesInstantiator, Is.False);
                Assert.That(IsCompositeDisposed(behaviour.LastActiveBucket), Is.True);
                Assert.That(behaviour.LateAddedDisposals, Is.EqualTo(1));
                Assert.That(ActivationCompensationDependency.Disposed, Is.EqualTo(1));
            }
            finally
            {
                scope.Dispose();
                if (target != null)
                    UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static bool IsCompositeDisposed(object value)
        {
            Assert.That(value, Is.Not.Null);
            var property = value.GetType().GetProperty("IsDisposed", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null);
            return (bool)property.GetValue(value);
        }

        private static bool IsManuallyRegistered(object service)
        {
            var method = typeof(UpdateLoopManager).GetMethod(
                "IsManuallyRegistered",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new[] { service });
        }
    }

    public interface IActivationCompensationDependency { }

    public sealed class ActivationCompensationDependency :
        IActivationCompensationDependency,
        IDependencyObject,
        IDisposable
    {
        public static int Created { get; private set; }
        public static int Disposed { get; private set; }

        public ActivationCompensationDependency() => Created++;
        public void Dispose() => Disposed++;

        public static void Reset()
        {
            Created = 0;
            Disposed = 0;
        }
    }

    public sealed class FailingFinalActivationBehaviour : DIBehaviour
    {
        [Inject] private IActivationCompensationDependency _dependency;
        public static FailingFinalActivationBehaviour LastInstance { get; private set; }

        protected override void OnInjectedEnable()
        {
            LastInstance = this;
            if (_dependency == null)
                throw new InvalidOperationException("dependency was not injected before final activation");
            throw new InvalidOperationException("expected final activation failure");
        }

        public static void Reset() => LastInstance = null;
    }

    public sealed class EmptyActivationLifetimeScope : LifetimeScope
    {
        protected override void Configure(ScopeBuilder builder) { }
    }

    [DefaultExecutionOrder(-5000)]
    public sealed class EmptyScopeCaptureProbe : MonoBehaviour
    {
        public static IScope CapturedScope { get; private set; }

        private void OnEnable()
        {
            CapturedScope = GetComponent<EmptyActivationLifetimeScope>().Scope;
        }

        public static void Reset() => CapturedScope = null;
    }

    public sealed class FailingAwakeLifetimeScope : LifetimeScope
    {
        public static FailingAwakeLifetimeScope LastConfiguredInstance { get; private set; }

        protected override void Configure(ScopeBuilder builder)
        {
            LastConfiguredInstance = this;
            builder.Bind<FailingAwakeEntryPoint>()
                .FromFactory(() => throw new InvalidOperationException("expected lifetime activation failure"))
                .AsEntryPoint()
                .AsScoped();
        }

        public static void Reset() => LastConfiguredInstance = null;
    }

    public sealed class FailingAwakeEntryPoint : IDependencyObject { }

    public sealed class FinalActivationManualUpdateRegistrationBehaviour : DIBehaviour
    {
        public static object Probe;

        protected override void OnInjectedEnable()
        {
            UpdateLoopManager.Instance.Register(Probe);
        }
    }

    public sealed class ManualUpdateProbe : IUpdatable
    {
        public void KDIUpdate(float deltaTime) { }
    }

    public sealed class ReentrantRevokeBehaviour : DIBehaviour
    {
        [Inject] private IActivationCompensationDependency _dependency;

        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public int BeforeUninjectCalls { get; private set; }
        public bool BeforeUninjectHadDependency { get; private set; }
        public IActivationCompensationDependency Dependency => _dependency;
        public bool ExposesInjectedState => IsInjected;
        public bool ExposesInstantiator => Instantiator != null;
        public object LastActiveBucket { get; private set; }
        public int LateAddedDisposals { get; private set; }

        protected override void OnInjectedEnable()
        {
            EnableCalls++;
            LastActiveBucket = ReadActiveBucket(this);
        }

        protected override void OnInjectedDisable() => DisableCalls++;

        protected override void OnBeforeUninject()
        {
            BeforeUninjectCalls++;
            BeforeUninjectHadDependency = _dependency != null;
            gameObject.SetActive(false);
            gameObject.SetActive(true);
            AddToActiveBucket(this, new LateAddingDisposable(this));
        }

        internal static object ReadActiveBucket(DIBehaviour behaviour)
        {
            return typeof(DIBehaviour)
                .GetField("_cd", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(behaviour);
        }

        internal static void AddToActiveBucket(DIBehaviour behaviour, IDisposable disposable)
        {
            var bucket = ReadActiveBucket(behaviour);
            var add = bucket?.GetType().GetMethod("Add", new[] { typeof(IDisposable) });
            if (add == null)
                throw new InvalidOperationException("CompositeDisposable.Add was not found.");
            add.Invoke(bucket, new object[] { disposable });
        }

        private sealed class LateAddingDisposable : IDisposable
        {
            private ReentrantRevokeBehaviour _owner;

            internal LateAddingDisposable(ReentrantRevokeBehaviour owner) => _owner = owner;

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                if (owner == null) return;
                AddToActiveBucket(owner, new CountingDisposable(() => owner.LateAddedDisposals++));
            }
        }

        internal sealed class CountingDisposable : IDisposable
        {
            private Action _onDispose;

            internal CountingDisposable(Action onDispose) => _onDispose = onDispose;

            public void Dispose()
            {
                var callback = _onDispose;
                _onDispose = null;
                callback?.Invoke();
            }
        }
    }

    public sealed class ReentrantAbortBehaviour : DIBehaviour, IPostInjectable
    {
        [Inject] private IActivationCompensationDependency _dependency;

        public int EnableCalls { get; private set; }
        public int BeforeUninjectCalls { get; private set; }
        public bool BeforeUninjectHadDependency { get; private set; }
        public IActivationCompensationDependency Dependency => _dependency;
        public bool ExposesInjectedState => IsInjected;
        public bool ExposesInstantiator => Instantiator != null;
        public object LastActiveBucket { get; private set; }
        public int LateAddedDisposals { get; private set; }

        public void PostInject() { }

        protected override void OnInjectedEnable()
        {
            EnableCalls++;
            LastActiveBucket = ReentrantRevokeBehaviour.ReadActiveBucket(this);
            if (EnableCalls == 1)
                throw new InvalidOperationException("expected injected enable failure");
        }

        protected override void OnBeforeUninject()
        {
            BeforeUninjectCalls++;
            BeforeUninjectHadDependency = _dependency != null;
            gameObject.SetActive(false);
            gameObject.SetActive(true);
            ReentrantRevokeBehaviour.AddToActiveBucket(
                this,
                new ReentrantRevokeBehaviour.CountingDisposable(() => LateAddedDisposals++));
        }
    }
}
