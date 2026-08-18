using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Kylin.DI.Tests
{
    public sealed class InjectionTransactionTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                UnityEngine.Object.DestroyImmediate(_root);
        }

        [Test]
        public void HierarchyInjectionFailure_RollsBackServicesAndEarlierComponents()
        {
            AtomicDependency.Created = 0;
            AtomicDependency.Disposed = 0;
            _root = new GameObject("atomic-hierarchy");
            var first = _root.AddComponent<AtomicFirstBehaviour>();
            _root.AddComponent<AtomicBrokenBehaviour>();

            var builder = new ScopeBuilder();
            builder.Bind<IAtomicDependency>().To<AtomicDependency>().AsScoped();
            var scope = builder.Build(name: "atomic-hierarchy");

            Assert.Throws<InvalidOperationException>(() => scope.InjectGameObject(_root));
            Assert.That(first.Dependency, Is.Null);
            Assert.That(first.PreUninjectCalls, Is.EqualTo(1));
            Assert.That(AtomicDependency.Created, Is.EqualTo(1));
            Assert.That(AtomicDependency.Disposed, Is.EqualTo(1));

            Assert.Throws<InvalidOperationException>(() => scope.InjectGameObject(_root));
            Assert.That(AtomicDependency.Created, Is.EqualTo(2), "The failed dependency must not remain cached.");
            Assert.That(AtomicDependency.Disposed, Is.EqualTo(2));
            scope.Dispose();
        }

        [Test]
        public void Injectable_HasOneActiveScopeOwnerAndSameScopeInjectionIsIdempotent()
        {
            var dependency1 = new AtomicDependency();
            var dependency2 = new AtomicDependency();
            var target = new OwnedInjectionTarget();
            var builder1 = new ScopeBuilder();
            builder1.Bind<IAtomicDependency>().FromInstance(dependency1);
            var builder2 = new ScopeBuilder();
            builder2.Bind<IAtomicDependency>().FromInstance(dependency2);
            var scope1 = builder1.Build(name: "owner-1");
            var scope2 = builder2.Build(name: "owner-2");

            target.Inject(scope1);
            target.Inject(scope1);
            DependencyInjector.ClearCache();
            Assert.That(target.PostInjectCalls, Is.EqualTo(1));
            Assert.That(target.Dependency, Is.SameAs(dependency1));

            Assert.Throws<InvalidOperationException>(() => target.Inject(scope2));
            Assert.That(target.Dependency, Is.SameAs(dependency1));

            scope1.Dispose();
            Assert.That(target.Dependency, Is.Null);
            Assert.That(target.PreUninjectCalls, Is.EqualTo(1));

            target.Inject(scope2);
            Assert.That(target.Dependency, Is.SameAs(dependency2));
            Assert.That(target.PostInjectCalls, Is.EqualTo(2));
            scope2.Dispose();
            Assert.That(target.PreUninjectCalls, Is.EqualTo(2));
        }

        [Test]
        public void SharedFactoryInstance_RevokesInjectionBeforeSingleOwnerDispose()
        {
            var events = new List<string>();
            var shared = new SharedLifecycleService(events);
            var builder = new ScopeBuilder();
            builder.Bind<ISharedServiceA>().FromFactory(() => shared).AsScoped();
            builder.Bind<ISharedServiceB>().FromFactory(() => shared).AsScoped();
            var scope = builder.Build(name: "shared-factory-instance");

            Assert.That(scope.Resolve<ISharedServiceA>(), Is.SameAs(shared));
            Assert.That(scope.Resolve<ISharedServiceB>(), Is.SameAs(shared));
            scope.Dispose();

            Assert.That(events, Is.EqualTo(new[] { "pre-uninject", "dispose" }));
        }

        [Test]
        public void ExternalInjectionBeforeFactoryResolve_RemainsExternallyOwned()
        {
            var events = new List<string>();
            var shared = new SharedLifecycleService(events);
            var builder = new ScopeBuilder();
            builder.Bind<ISharedServiceA>().FromFactory(() => shared).AsScoped();
            var scope = builder.Build(name: "external-before-factory");

            shared.Inject(scope);
            Assert.That(scope.Resolve<ISharedServiceA>(), Is.SameAs(shared));
            scope.Dispose();

            Assert.That(events, Is.EqualTo(new[] { "pre-uninject" }),
                "A factory binding must not take ownership from an already externally injected object.");
        }

        [Test]
        public void SharedDisposableFactoryResult_AcrossSiblingScopesFailsFast()
        {
            var events = new List<string>();
            var shared = new SharedLifecycleService(events);
            var firstBuilder = new ScopeBuilder();
            firstBuilder.Bind<ISharedServiceA>().FromFactory(() => shared).AsScoped();
            var secondBuilder = new ScopeBuilder();
            secondBuilder.Bind<ISharedServiceB>().FromFactory(() => shared).AsScoped();
            var first = firstBuilder.Build(name: "sibling-owner-a");
            var second = secondBuilder.Build(name: "sibling-owner-b");

            try
            {
                Assert.That(first.Resolve<ISharedServiceA>(), Is.SameAs(shared));
                Assert.Throws<InvalidOperationException>(() => second.Resolve<ISharedServiceB>());
                Assert.That(events, Is.EqualTo(Array.Empty<string>()),
                    "The rejected Scope must not dispose an object owned by the first Scope.");
            }
            finally
            {
                second.Dispose();
                first.Dispose();
            }

            Assert.That(events, Is.EqualTo(new[] { "pre-uninject", "dispose" }));
        }

        [Test]
        public void ExternallyInjectedDisposable_CannotBeDisposedBySiblingFactoryFailure()
        {
            var events = new List<string>();
            var shared = new SharedLifecycleService(events);
            var externalScope = new ScopeBuilder().Build(name: "external-injection-owner");
            var factoryBuilder = new ScopeBuilder();
            factoryBuilder.Bind<ISharedServiceA>().FromFactory(() => shared).AsScoped();
            var factoryScope = factoryBuilder.Build(name: "external-injection-sibling");

            try
            {
                shared.Inject(externalScope);
                Assert.Throws<InvalidOperationException>(() => factoryScope.Resolve<ISharedServiceA>());
                Assert.That(events, Is.Empty,
                    "A rejected sibling factory must not dispose an externally injected object.");
            }
            finally
            {
                factoryScope.Dispose();
                externalScope.Dispose();
            }

            Assert.That(events, Is.EqualTo(new[] { "pre-uninject" }));
        }

        [Test]
        public void AmbientSiblingResolve_CannotBorrowContainerOwnershipFromTransaction()
        {
            var shared = new SharedDisposableOnly();
            var siblingBuilder = new ScopeBuilder();
            siblingBuilder.Bind<ISharedDisposableB>().FromFactory(() => shared).AsScoped();
            var sibling = siblingBuilder.Build(name: "ambient-sibling-b");

            var ownerBuilder = new ScopeBuilder();
            ownerBuilder.Bind<ISharedDisposableA>().FromFactory(() => shared).AsScoped();
            ownerBuilder.RegisterFactory(
                () => new AmbientSiblingActivation(sibling), Lifetime.Scoped);
            var owner = ownerBuilder.Build(name: "ambient-sibling-a");

            try
            {
                Assert.Throws<InvalidOperationException>(() => owner.Resolve<AmbientSiblingActivation>());
                Assert.That(shared.DisposeCalls, Is.EqualTo(1),
                    "Only the first owner may dispose the identity while rolling back.");
            }
            finally
            {
                sibling.Dispose();
                owner.Dispose();
            }

            Assert.That(shared.DisposeCalls, Is.EqualTo(1));
        }

        [Test]
        public void FactoryOwnedService_PublicInjectWithSameScopeRemainsContainerOwned()
        {
            var events = new List<string>();
            var service = new SharedLifecycleService(events);
            var builder = new ScopeBuilder();
            builder.Bind<ISharedServiceA>().FromFactory(() => service).AsScoped();
            var scope = builder.Build(name: "factory-owned-idempotent-inject");

            Assert.That(scope.Resolve<ISharedServiceA>(), Is.SameAs(service));
            service.Inject(scope);
            scope.Dispose();

            Assert.That(events, Is.EqualTo(new[] { "pre-uninject", "dispose" }));
        }

        [Test]
        public void DirectInject_MarksExternalBeforePostInjectFactoryReentry()
        {
            var events = new List<string>();
            var builder = new ScopeBuilder();
            ReentrantExternalTarget target = null;
            builder.Bind<IReentrantExternalService>().FromFactory(() => target).AsScoped();
            var scope = builder.Build(name: "reentrant-external-marker");
            target = new ReentrantExternalTarget(scope, events);

            target.Inject(scope);
            Assert.That(target.FactoryResolveWasRejected, Is.True);
            Assert.That(events, Is.Empty, "The failed reentrant factory must not dispose an external target.");

            scope.Dispose();
            Assert.That(events, Is.EqualTo(new[] { "pre-uninject" }));
        }
    }

    public interface IAtomicDependency { }
    public interface IAtomicMissing { }
    public interface ISharedServiceA { }
    public interface ISharedServiceB { }
    public interface ISharedDisposableA { }
    public interface ISharedDisposableB { }
    public interface IReentrantExternalService { }

    public sealed class AtomicDependency : IAtomicDependency, IDependencyObject, IDisposable
    {
        public static int Created;
        public static int Disposed;

        public AtomicDependency() => Created++;
        public void Dispose() => Disposed++;
    }

    public sealed class AtomicFirstBehaviour : MonoBehaviour, IInjectable, IPostInjectable, IPreUninjectable
    {
        [Inject] private IAtomicDependency _dependency;
        public IAtomicDependency Dependency => _dependency;
        public int PreUninjectCalls { get; private set; }
        public void PostInject() { }
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class AtomicBrokenBehaviour : MonoBehaviour, IInjectable
    {
#pragma warning disable CS0169
        [Inject] private IAtomicMissing _missing;
#pragma warning restore CS0169
    }

    public sealed class OwnedInjectionTarget : IInjectable, IPostInjectable, IPreUninjectable
    {
        [Inject] private IAtomicDependency _dependency;
        public IAtomicDependency Dependency => _dependency;
        public int PostInjectCalls { get; private set; }
        public int PreUninjectCalls { get; private set; }
        public void PostInject() => PostInjectCalls++;
        public void PreUninject() => PreUninjectCalls++;
    }

    public sealed class SharedLifecycleService :
        ISharedServiceA,
        ISharedServiceB,
        IInjectable,
        IPostInjectable,
        IPreUninjectable,
        IDisposable
    {
        private readonly List<string> _events;

        public SharedLifecycleService(List<string> events) => _events = events;
        public void PostInject() { }
        public void PreUninject() => _events.Add("pre-uninject");
        public void Dispose() => _events.Add("dispose");
    }

    public sealed class SharedDisposableOnly : ISharedDisposableA, ISharedDisposableB, IDisposable
    {
        public int DisposeCalls { get; private set; }
        public void Dispose() => DisposeCalls++;
    }

    public sealed class AmbientSiblingActivation : IInjectable, IPostInjectable
    {
#pragma warning disable CS0169
        [Inject] private ISharedDisposableA _ownerDependency;
#pragma warning restore CS0169
        private readonly IScope _sibling;
        private readonly AmbientSiblingTarget _target = new();

        public AmbientSiblingActivation(IScope sibling) => _sibling = sibling;

        public void PostInject() => _target.Inject(_sibling);
    }

    public sealed class AmbientSiblingTarget : IInjectable
    {
#pragma warning disable CS0169
        [Inject] private ISharedDisposableB _siblingDependency;
#pragma warning restore CS0169
    }

    public sealed class ReentrantExternalTarget :
        IReentrantExternalService,
        IInjectable,
        IPostInjectable,
        IPreUninjectable,
        IDisposable
    {
        private readonly IScope _scope;
        private readonly List<string> _events;
        public bool FactoryResolveWasRejected { get; private set; }

        public ReentrantExternalTarget(IScope scope, List<string> events)
        {
            _scope = scope;
            _events = events;
        }

        public void PostInject()
        {
            try { _scope.Resolve<IReentrantExternalService>(); }
            catch (InvalidOperationException) { FactoryResolveWasRejected = true; }
        }

        public void PreUninject() => _events.Add("pre-uninject");
        public void Dispose() => _events.Add("dispose");
    }
}
