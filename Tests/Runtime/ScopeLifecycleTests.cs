using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Kylin.DI.Tests
{
    public sealed class ScopeLifecycleTests
    {
        private static readonly List<string> DisposeOrder = new();

        [Test]
        public void FailedGraph_IsRolledBackAndNotCached()
        {
            RollbackDependency.Created = 0;
            RollbackDependency.Disposed = 0;
            var builder = new ScopeBuilder();
            builder.Bind<IRollbackDependency>().To<RollbackDependency>().AsScoped();
            builder.Bind<BrokenConsumer>().ToSelf().AsScoped();
            var scope = builder.Build(name: "rollback");

            Assert.Throws<InvalidOperationException>(() => scope.Resolve<BrokenConsumer>());
            Assert.That(RollbackDependency.Created, Is.EqualTo(1));
            Assert.That(RollbackDependency.Disposed, Is.EqualTo(1));

            Assert.Throws<InvalidOperationException>(() => scope.Resolve<BrokenConsumer>());
            Assert.That(RollbackDependency.Created, Is.EqualTo(2));
            Assert.That(RollbackDependency.Disposed, Is.EqualTo(2));
            scope.Dispose();
        }

        [Test]
        public void Dispose_UsesReverseActivationOrder()
        {
            DisposeOrder.Clear();
            var builder = new ScopeBuilder();
            builder.Bind<IOrderedDependency>().To<OrderedDependency>().AsScoped();
            builder.Bind<OrderedConsumer>().ToSelf().AsScoped();
            var scope = builder.Build(name: "dispose-order");
            scope.Resolve<OrderedConsumer>();

            scope.Dispose();

            CollectionAssert.AreEqual(new[] { "consumer", "dependency" }, DisposeOrder);
        }

        [Test]
        public void FromInstance_CycleFailsWithoutMutatingTargets()
        {
            var left = new Left();
            var right = new Right();
            var builder = new ScopeBuilder();
            builder.Bind<ILeft>().FromInstance(left);
            builder.Bind<IRight>().FromInstance(right);

            Assert.Throws<InvalidOperationException>(() => builder.Build(name: "instance-cycle"));
            Assert.That(left.Right, Is.Null);
            Assert.That(right.Left, Is.Null);
        }

        [Test]
        public void FromInstance_RemainsExternallyOwned()
        {
            var instance = new ExternalInstance();
            var builder = new ScopeBuilder();
            builder.Bind<IExternalInstance>().FromInstance(instance);
            var scope = builder.Build(name: "external-instance");

            scope.Dispose();

            Assert.That(instance.WasDisposed, Is.False);
        }

        [Test]
        public void PostInjectFailure_RestoresPreviouslyAssignedFields()
        {
            var previous = new PostDependency();
            var injected = new PostDependency();
            var target = new PostFailTarget(previous);
            var builder = new ScopeBuilder();
            builder.Bind<IPostDependency>().FromInstance(injected);
            builder.Bind<PostFailTarget>().FromInstance(target);

            Assert.Throws<InvalidOperationException>(() => builder.Build(name: "post-failure"));

            Assert.That(target.Dependency, Is.SameAs(previous));
            Assert.That(target.PreUninjectCalls, Is.EqualTo(1));
        }

        [Test]
        public void BuiltInInstantiator_CannotBeOverridden()
        {
            var builder = new ScopeBuilder();
            Assert.Throws<InvalidOperationException>(() =>
                builder.RegisterFactory<IInstantiator>(() => null, Lifetime.Scoped));
        }

        [Test]
        public void ExternalInjectionInsideFailedActivation_IsRolledBackWithGraph()
        {
            var previous = new ExternalDependency();
            var injected = new ExternalDependency();
            var target = new ExternalInjectionTarget(previous);
            IScope scope = null;
            var builder = new ScopeBuilder();
            builder.Bind<IExternalDependency>().FromInstance(injected);
            builder.RegisterFactory(
                () => new ExternalActivation(target, scope), Lifetime.Scoped);
            scope = builder.Build(name: "external-rollback");

            Assert.Throws<InvalidOperationException>(() => scope.Resolve<ExternalActivation>());
            Assert.That(target.Dependency, Is.SameAs(previous));
            Assert.That(target.PreUninjectCalls, Is.EqualTo(1));

            scope.Dispose();
            Assert.That(target.PreUninjectCalls, Is.EqualTo(1));
        }

        [Test]
        public void TransientCleanup_IsTrackedAndRunsBeforeOwnerDispose()
        {
            TransientResource.Events.Clear();
            var builder = new ScopeBuilder();
            builder.Bind<TransientResource>().ToSelf().AsTransient();
            var scope = builder.Build(name: "transient-cleanup");
            scope.Resolve<TransientResource>();

            scope.Dispose();

            CollectionAssert.AreEqual(new[] { "post", "pre", "dispose" }, TransientResource.Events);
        }

        [Test]
        public void RollbackCleanup_CannotResolveAndLeakANewService()
        {
            RollbackResolveProbe.Reset();
            IScope scope = null;
            var builder = new ScopeBuilder();
            builder.RegisterFactory(
                () => new RollbackResolveProbe(scope), Lifetime.Scoped);
            builder.RegisterFactory<RollbackLeak>(() => new RollbackLeak(), Lifetime.Scoped);
            builder.Bind<RollbackReentryFailure>().ToSelf().AsScoped();
            scope = builder.Build(name: "rollback-reentry");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<RollbackReentryFailure>());
                Assert.That(RollbackResolveProbe.DisposeCalls, Is.EqualTo(1));
                Assert.That(RollbackResolveProbe.ResolveWasRejected, Is.True);
                Assert.That(RollbackLeak.Created, Is.EqualTo(0),
                    "Rollback cleanup must not append an uncommitted activation to the transaction.");
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void SubsystemReset_DisposesManuallyBuiltScopesBeforeClearingOwnershipState()
        {
            var resource = new ResetTrackedResource();
            var builder = new ScopeBuilder();
            builder.RegisterInstance(resource);
            var scope = builder.Build(name: "manual-reset-scope");

            var reset = typeof(Scope).GetMethod("ResetState", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(reset, Is.Not.Null);
            reset.Invoke(null, null);

            Assert.That(resource.PreUninjectCalls, Is.EqualTo(1));
            Assert.That(resource.DisposeCalls, Is.EqualTo(0), "FromInstance remains externally owned during reset.");
            Assert.Throws<ObjectDisposedException>(() => scope.Resolve<ResetTrackedResource>());
        }

        [Test]
        public void CustomParentScope_IsRejectedBeforeChildCanResolveFromAnUntrackedLedger()
        {
            var builder = new ScopeBuilder();
            Assert.Throws<NotSupportedException>(() => builder.Build(new CustomParentScope(), "custom-parent"));
        }

        [Test]
        public void FromInstanceOwnership_IsObservedBeforeAnyFactoryActivation()
        {
            var shared = new ExplicitExternalResource();
            var consumer = new ExternalFactoryConsumer();
            var builder = new ScopeBuilder();
            builder.RegisterInstance(consumer);
            builder.RegisterFactory<IExternalFactoryView>(() => shared, Lifetime.Scoped);
            builder.RegisterInstance<IExplicitExternalView>(shared);

            var scope = builder.Build(name: "external-identity-prepass");
            try
            {
                Assert.That(consumer.Dependency, Is.SameAs(shared));
            }
            finally
            {
                scope.Dispose();
            }

            Assert.That(shared.DisposeCalls, Is.Zero,
                "An identity explicitly declared through FromInstance must remain externally owned regardless of registration order.");
        }

        [Test]
        public void SubsystemReset_ContinuesAfterCustomRootDisposeThrows()
        {
            var reset = typeof(KDI).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic);
            var setRoot = typeof(KDI).GetMethod("SetRootScope", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(reset, Is.Not.Null);
            Assert.That(setRoot, Is.Not.Null);
            reset.Invoke(null, null);

            var resource = new ResetTrackedResource();
            var builder = new ScopeBuilder();
            builder.RegisterInstance(resource);
            var scope = builder.Build(name: "reset-after-custom-root-failure");
            var customRoot = new ThrowingCustomRootScope();
            setRoot.Invoke(null, new object[] { customRoot });

            LogAssert.Expect(LogType.Error, new Regex("Failed to reset custom root Scope"));
            reset.Invoke(null, null);

            Assert.That(customRoot.DisposeCalls, Is.EqualTo(1));
            Assert.That(resource.PreUninjectCalls, Is.EqualTo(1),
                "A custom root failure must not skip concrete Scope cleanup.");
            Assert.Throws<ObjectDisposedException>(() => scope.Resolve<ResetTrackedResource>());
        }

        public interface IMissing { }
        public interface IRollbackDependency { }
        public interface IExternalFactoryView { }
        public interface IExplicitExternalView { }

        public sealed class RollbackDependency : IRollbackDependency, IDependencyObject, IDisposable
        {
            public static int Created;
            public static int Disposed;
            public RollbackDependency() => Created++;
            public void Dispose() => Disposed++;
        }

        public sealed class BrokenConsumer : IDependencyObject, IInjectable
        {
#pragma warning disable CS0169
            [Inject] private IRollbackDependency _dependency;
            [Inject] private IMissing _missing;
#pragma warning restore CS0169
        }

        public interface IOrderedDependency { }

        public sealed class OrderedDependency : IOrderedDependency, IDependencyObject, IDisposable
        {
            public void Dispose() => DisposeOrder.Add("dependency");
        }

        public sealed class OrderedConsumer : IDependencyObject, IInjectable, IDisposable
        {
#pragma warning disable CS0169
            [Inject] private IOrderedDependency _dependency;
#pragma warning restore CS0169
            public void Dispose() => DisposeOrder.Add("consumer");
        }

        public interface ILeft { }
        public interface IRight { }

        public sealed class Left : ILeft, IDependencyObject, IInjectable
        {
            [Inject] private IRight _right;
            public IRight Right => _right;
        }

        public sealed class Right : IRight, IDependencyObject, IInjectable
        {
            [Inject] private ILeft _left;
            public ILeft Left => _left;
        }

        public interface IExternalInstance { }

        public sealed class ExternalInstance : IExternalInstance, IDependencyObject, IDisposable
        {
            public bool WasDisposed { get; private set; }
            public void Dispose() => WasDisposed = true;
        }

        public interface IPostDependency { }

        public sealed class PostDependency : IPostDependency, IDependencyObject { }

        public sealed class PostFailTarget : IDependencyObject, IInjectable, IPostInjectable, IPreUninjectable
        {
            [Inject] private IPostDependency _dependency;
            public IPostDependency Dependency => _dependency;
            public int PreUninjectCalls { get; private set; }

            public PostFailTarget(IPostDependency previous) => _dependency = previous;

            public void PostInject() => throw new InvalidOperationException("post failure");
            public void PreUninject() => PreUninjectCalls++;
        }

        public interface IExternalDependency { }

        public sealed class ExternalDependency : IExternalDependency, IDependencyObject { }

        public sealed class ExternalInjectionTarget : IInjectable, IPostInjectable, IPreUninjectable
        {
            [Inject] private IExternalDependency _dependency;
            public IExternalDependency Dependency => _dependency;
            public int PreUninjectCalls { get; private set; }

            public ExternalInjectionTarget(IExternalDependency previous) => _dependency = previous;
            public void PostInject() { }
            public void PreUninject() => PreUninjectCalls++;
        }

        public sealed class ExternalActivation : IInjectable, IPostInjectable
        {
            private readonly ExternalInjectionTarget _target;
            private readonly IScope _scope;

            public ExternalActivation(ExternalInjectionTarget target, IScope scope)
            {
                _target = target;
                _scope = scope;
            }

            public void PostInject()
            {
                _target.Inject(_scope);
                throw new InvalidOperationException("activation failure");
            }
        }

        public sealed class TransientResource : IDependencyObject, IInjectable, IPostInjectable, IPreUninjectable, IDisposable
        {
            public static readonly List<string> Events = new();
            public void PostInject() => Events.Add("post");
            public void PreUninject() => Events.Add("pre");
            public void Dispose() => Events.Add("dispose");
        }

        public sealed class RollbackResolveProbe : IDisposable
        {
            private readonly IScope _scope;
            public static int DisposeCalls { get; private set; }
            public static bool ResolveWasRejected { get; private set; }

            public RollbackResolveProbe(IScope scope) => _scope = scope;

            public void Dispose()
            {
                DisposeCalls++;
                try { _scope.Resolve<RollbackLeak>(); }
                catch (InvalidOperationException) { ResolveWasRejected = true; }
            }

            public static void Reset()
            {
                DisposeCalls = 0;
                ResolveWasRejected = false;
                RollbackLeak.Created = 0;
            }
        }

        public sealed class RollbackLeak
        {
            public static int Created;
            public RollbackLeak() => Created++;
        }

        public sealed class RollbackReentryFailure : IDependencyObject, IInjectable
        {
#pragma warning disable CS0169
            [Inject] private RollbackResolveProbe _probe;
            [Inject] private IMissing _missing;
#pragma warning restore CS0169
        }

        public sealed class ResetTrackedResource : IInjectable, IPostInjectable, IPreUninjectable, IDisposable
        {
            public int PreUninjectCalls { get; private set; }
            public int DisposeCalls { get; private set; }
            public void PostInject() { }
            public void PreUninject() => PreUninjectCalls++;
            public void Dispose() => DisposeCalls++;
        }

        public sealed class ExplicitExternalResource : IExternalFactoryView, IExplicitExternalView, IDisposable
        {
            public int DisposeCalls { get; private set; }
            public void Dispose() => DisposeCalls++;
        }

        public sealed class ExternalFactoryConsumer : IInjectable
        {
            [Inject] private IExternalFactoryView _dependency;
            public IExternalFactoryView Dependency => _dependency;
        }

        private sealed class CustomParentScope : IScope
        {
            public IScope Parent => null;
            public T Resolve<T>() where T : class => throw new InvalidOperationException();
            public object Resolve(Type type) => throw new InvalidOperationException();
            public void Dispose() { }
        }

        private sealed class ThrowingCustomRootScope : IScope
        {
            public int DisposeCalls { get; private set; }
            public IScope Parent => null;
            public T Resolve<T>() where T : class => throw new InvalidOperationException();
            public object Resolve(Type type) => throw new InvalidOperationException();

            public void Dispose()
            {
                DisposeCalls++;
                throw new InvalidOperationException("expected custom root cleanup failure");
            }
        }
    }
}
