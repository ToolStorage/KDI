using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Kylin.DI.Tests
{
    public sealed class ResolverAuthorityTests
    {
        [Test]
        public void ZeroArgumentFactory_UsesClosureValuesAndFieldInjection()
        {
            var runtimeValue = new RuntimeValue("runtime");
            var builder = new ScopeBuilder();
            builder.Bind<FactoryDependency>().ToSelf().AsScoped();
            builder.RegisterFactory<ZeroArgumentFactoryConsumer>(
                () => new ZeroArgumentFactoryConsumer(runtimeValue), Lifetime.Scoped);
            var scope = builder.Build(name: "zero-argument-factory");

            try
            {
                var consumer = scope.Resolve<ZeroArgumentFactoryConsumer>();
                Assert.That(consumer.RuntimeValue, Is.SameAs(runtimeValue));
                Assert.That(consumer.Dependency, Is.SameAs(scope.Resolve<FactoryDependency>()));
                Assert.That(consumer.PostInjectCalls, Is.EqualTo(1));
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void FactoryResultFieldInjection_IsRolledBackWithPostInjectFailure()
        {
            RollbackDependency.Reset();
            var target = new FailingInjectedConsumer();
            var builder = new ScopeBuilder();
            builder.Bind<RollbackDependency>().ToSelf().AsScoped();
            builder.RegisterFactory(() => target, Lifetime.Scoped);
            var scope = builder.Build(name: "factory-field-rollback");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<FailingInjectedConsumer>());
                Assert.That(RollbackDependency.Created, Is.EqualTo(1));
                Assert.That(RollbackDependency.Disposed, Is.EqualTo(1),
                    "Factory-result [Inject] dependencies must stay in the outer activation rollback ledger.");
                Assert.That(target.Dependency, Is.Null, "Failed activation must restore the injected field.");
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void FactoryCannotResolveThroughCapturedCompositionScope()
        {
            ForbiddenDependency.Reset();
            IScope scope = null;
            var builder = new ScopeBuilder();
            builder.Bind<ForbiddenDependency>().ToSelf().AsScoped();
            builder.RegisterFactory<FactoryResolveAttempt>(() =>
            {
                scope.Resolve<ForbiddenDependency>();
                return new FactoryResolveAttempt();
            }, Lifetime.Scoped);
            scope = builder.Build(name: "factory-captured-scope");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<FactoryResolveAttempt>());
                Assert.That(ForbiddenDependency.Created, Is.Zero,
                    "Public Resolve must fail before a factory can activate a hidden dependency.");

                Assert.That(scope.Resolve<ForbiddenDependency>(), Is.Not.Null,
                    "The same Resolve remains valid at the outer composition boundary.");
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void PostInjectCannotResolveThroughCapturedCompositionScope()
        {
            ForbiddenDependency.Reset();
            IScope scope = null;
            var builder = new ScopeBuilder();
            builder.Bind<ForbiddenDependency>().ToSelf().AsScoped();
            builder.RegisterFactory(
                () => new PostInjectResolveAttempt(scope), Lifetime.Scoped);
            scope = builder.Build(name: "post-inject-captured-scope");

            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Resolve<PostInjectResolveAttempt>());
                Assert.That(ForbiddenDependency.Created, Is.Zero);
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void StandaloneLifetimeScopeConfigure_CannotResolveThroughCapturedCompositionScope()
        {
            FinalActivationHiddenDependency.Reset();
            var builder = new ScopeBuilder();
            builder.Bind<FinalActivationHiddenDependency>().ToSelf().AsScoped();
            var compositionScope = builder.Build(name: "standalone-configure-authority");
            var gameObject = new GameObject("standalone-configure-scope");
            gameObject.SetActive(false);
            var lifetimeScope = gameObject.AddComponent<StandaloneConfigureResolveLifetimeScope>();
            StandaloneConfigureResolveLifetimeScope.CapturedScope = compositionScope;

            try
            {
                Assert.Throws<InvalidOperationException>(lifetimeScope.Initialize);
                Assert.That(FinalActivationHiddenDependency.Created, Is.Zero,
                    "Configure must fail before it can start a hidden activation in another Scope.");
                Assert.That(compositionScope.Resolve<FinalActivationHiddenDependency>(), Is.Not.Null,
                    "Resolve remains valid after Configure unwinds to the explicit composition boundary.");
            }
            finally
            {
                StandaloneConfigureResolveLifetimeScope.CapturedScope = null;
                compositionScope.Dispose();
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void InternalFieldAliasAndEntryPointResolution_RemainsAvailable()
        {
            EntryPointConsumer.Reset();
            var builder = new ScopeBuilder();
            builder.Bind<AliasedDependency>()
                .ToSelf()
                .AlsoBind<IAliasedDependency>()
                .AsScoped();
            builder.Bind<EntryPointConsumer>()
                .ToSelf()
                .AsEntryPoint()
                .AsScoped();

            var scope = builder.Build(name: "internal-entry-point-resolution");
            try
            {
                Assert.That(EntryPointConsumer.PostInjectCalls, Is.EqualTo(1));
                var consumer = scope.Resolve<EntryPointConsumer>();
                Assert.That(consumer.Dependency, Is.SameAs(scope.Resolve<IAliasedDependency>()));
            }
            finally
            {
                scope.Dispose();
                EntryPointConsumer.Reset();
            }
        }

        [Test]
        public void ResolverAuthority_CannotBeRegisteredByKeyAliasInstanceOrHiddenFactoryResult()
        {
            var keyBuilder = new ScopeBuilder();
            Assert.Throws<InvalidOperationException>(() =>
                keyBuilder.Bind<IScope>().FromFactory(() => null).AsScoped());

            var aliasBuilder = new ScopeBuilder();
            Assert.Throws<InvalidOperationException>(() =>
                aliasBuilder.Bind<object>()
                    .FromFactory(() => new object())
                    .AlsoBind<IScope>()
                    .AsScoped());

            var owner = new ScopeBuilder().Build(name: "raw-scope-owner");
            try
            {
                var instanceBuilder = new ScopeBuilder();
                Assert.Throws<InvalidOperationException>(() =>
                    instanceBuilder.RegisterInstance<object>(owner));

                var resultBuilder = new ScopeBuilder();
                resultBuilder.RegisterFactory<object>(() => owner, Lifetime.Scoped);
                var resultScope = resultBuilder.Build(name: "hidden-scope-result");
                try
                {
                    Assert.Throws<InvalidOperationException>(() => resultScope.Resolve<object>());
                }
                finally
                {
                    resultScope.Dispose();
                }
            }
            finally
            {
                owner.Dispose();
            }
        }

        [Test]
        public void RawResolverFields_AreRejectedBeforeAssignment()
        {
            var interfaceTarget = new ScopeFieldConsumer();
            var interfaceBuilder = new ScopeBuilder();
            interfaceBuilder.RegisterInstance(interfaceTarget);
            Assert.Throws<InvalidOperationException>(() => interfaceBuilder.Build(name: "scope-field"));
            Assert.That(interfaceTarget.Value, Is.Null);

            var concreteTarget = new ConcreteScopeFieldConsumer();
            var concreteBuilder = new ScopeBuilder();
            concreteBuilder.RegisterInstance(concreteTarget);
            Assert.Throws<InvalidOperationException>(() => concreteBuilder.Build(name: "concrete-scope-field"));
            Assert.That(concreteTarget.Value, Is.Null);
        }

        [Test]
        public void DIBehaviour_ExposesOnlyNarrowInstantiatorCapability()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.That(typeof(DIBehaviour).GetProperty("Scope", flags), Is.Null);
            var property = typeof(DIBehaviour).GetProperty("Instantiator", flags);
            Assert.That(property, Is.Not.Null);
            Assert.That(property.PropertyType, Is.EqualTo(typeof(IInstantiator)));

            var gameObject = new GameObject("resolver-authority-behaviour");
            var behaviour = gameObject.AddComponent<InstantiatorBehaviour>();
            var scope = new ScopeBuilder().Build(name: "behaviour-instantiator");
            try
            {
                behaviour.Inject(scope);
                Assert.That(behaviour.CurrentInstantiator, Is.Not.Null);
                Assert.That(behaviour.CurrentInstantiator, Is.Not.InstanceOf<IScope>());

                scope.Dispose();
                Assert.That(behaviour.CurrentInstantiator, Is.Null);
            }
            finally
            {
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void FinalOnInjectedEnable_CannotResolveThroughCapturedCompositionScope()
        {
            FinalActivationHiddenDependency.Reset();
            var sourceHolder = new GameObject("resolver-authority-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("resolver-authority-on-enable-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<FinalActivationResolveBehaviour>();

            var builder = new ScopeBuilder();
            builder.Bind<FinalActivationHiddenDependency>().ToSelf().AsScoped();
            var scope = builder.Build(name: "final-on-enable-resolver-authority");
            FinalActivationResolveBehaviour.CapturedScope = scope;

            try
            {
                var instantiator = scope.Resolve<IInstantiator>();
                LogAssert.Expect(LogType.Exception, new Regex("Public IScope.Resolve cannot be called"));
                Assert.Throws<InvalidOperationException>(() => instantiator.Instantiate(prefab));
                Assert.That(FinalActivationHiddenDependency.Created, Is.Zero,
                    "Final OnInjectedEnable is still activation and must not start a hidden Resolve transaction.");
                Assert.That(scope.Resolve<FinalActivationHiddenDependency>(), Is.Not.Null,
                    "The same Resolve remains valid after compensation returns to the composition boundary.");
            }
            finally
            {
                FinalActivationResolveBehaviour.CapturedScope = null;
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(sourceHolder);
            }
        }

        [Test]
        public void FinalLifetimeScopeConfigure_CannotResolveThroughCapturedCompositionScope()
        {
            FinalActivationHiddenDependency.Reset();
            var sourceHolder = new GameObject("resolver-authority-scope-source-holder");
            sourceHolder.SetActive(false);
            var prefab = new GameObject("resolver-authority-configure-prefab");
            prefab.transform.SetParent(sourceHolder.transform, false);
            prefab.AddComponent<FinalActivationResolveLifetimeScope>();

            var builder = new ScopeBuilder();
            builder.Bind<FinalActivationHiddenDependency>().ToSelf().AsScoped();
            var scope = builder.Build(name: "final-configure-resolver-authority");
            FinalActivationResolveLifetimeScope.CapturedScope = scope;

            try
            {
                var instantiator = scope.Resolve<IInstantiator>();
                LogAssert.Expect(LogType.Exception, new Regex("Public IScope.Resolve cannot be called"));
                Assert.Throws<InvalidOperationException>(() => instantiator.Instantiate(prefab));
                Assert.That(FinalActivationHiddenDependency.Created, Is.Zero,
                    "LifetimeScope.Configure runs inside final prefab activation and cannot resolve hidden services.");
                Assert.That(scope.Resolve<FinalActivationHiddenDependency>(), Is.Not.Null);
            }
            finally
            {
                FinalActivationResolveLifetimeScope.CapturedScope = null;
                scope.Dispose();
                UnityEngine.Object.DestroyImmediate(sourceHolder);
            }
        }

        private sealed class RuntimeValue
        {
            internal string Value { get; }
            internal RuntimeValue(string value) => Value = value;
        }

        private sealed class FactoryDependency : IDependencyObject
        {
            public FactoryDependency() { }
        }

        private sealed class ZeroArgumentFactoryConsumer : IInjectable, IPostInjectable
        {
            [Inject] private FactoryDependency _dependency;
            internal RuntimeValue RuntimeValue { get; }
            internal FactoryDependency Dependency => _dependency;
            internal int PostInjectCalls { get; private set; }

            internal ZeroArgumentFactoryConsumer(RuntimeValue runtimeValue)
            {
                RuntimeValue = runtimeValue;
            }

            public void PostInject()
            {
                PostInjectCalls++;
            }
        }

        private sealed class RollbackDependency : IDependencyObject, IDisposable
        {
            internal static int Created;
            internal static int Disposed;

            public RollbackDependency()
            {
                Created++;
            }

            public void Dispose()
            {
                Disposed++;
            }

            internal static void Reset()
            {
                Created = 0;
                Disposed = 0;
            }
        }

        private sealed class FailingInjectedConsumer : IInjectable, IPostInjectable
        {
            [Inject] private RollbackDependency _dependency;
            internal RollbackDependency Dependency => _dependency;

            public void PostInject()
            {
                throw new InvalidOperationException("expected PostInject failure");
            }
        }

        private sealed class ForbiddenDependency : IDependencyObject
        {
            internal static int Created;

            public ForbiddenDependency()
            {
                Created++;
            }

            internal static void Reset()
            {
                Created = 0;
            }
        }

        private sealed class FactoryResolveAttempt { }

        private sealed class PostInjectResolveAttempt : IInjectable, IPostInjectable
        {
            private readonly IScope _scope;

            internal PostInjectResolveAttempt(IScope scope)
            {
                _scope = scope;
            }

            public void PostInject()
            {
                _scope.Resolve<ForbiddenDependency>();
            }
        }

        private interface IAliasedDependency { }

        private sealed class AliasedDependency : IDependencyObject, IAliasedDependency
        {
            public AliasedDependency() { }
        }

        private sealed class EntryPointConsumer : IDependencyObject, IInjectable, IPostInjectable
        {
            [Inject] private IAliasedDependency _dependency;
            internal static int PostInjectCalls;
            internal IAliasedDependency Dependency => _dependency;

            public EntryPointConsumer() { }

            public void PostInject()
            {
                PostInjectCalls++;
            }

            internal static void Reset()
            {
                PostInjectCalls = 0;
            }
        }

        private sealed class ScopeFieldConsumer : IInjectable
        {
            [Inject] private IScope _value;
            internal IScope Value => _value;
        }

        private sealed class ConcreteScopeFieldConsumer : IInjectable
        {
            [Inject] private Scope _value;
            internal Scope Value => _value;
        }

        private sealed class InstantiatorBehaviour : DIBehaviour
        {
            internal IInstantiator CurrentInstantiator => Instantiator;
        }
    }

    public sealed class FinalActivationHiddenDependency : IDependencyObject
    {
        public static int Created { get; private set; }

        public FinalActivationHiddenDependency()
        {
            Created++;
        }

        public static void Reset()
        {
            Created = 0;
        }
    }

    public sealed class FinalActivationResolveBehaviour : DIBehaviour
    {
        public static IScope CapturedScope;

        protected override void OnInjectedEnable()
        {
            CapturedScope.Resolve<FinalActivationHiddenDependency>();
        }
    }

    public sealed class FinalActivationResolveLifetimeScope : LifetimeScope
    {
        public static IScope CapturedScope;

        protected override void Configure(ScopeBuilder builder)
        {
            CapturedScope.Resolve<FinalActivationHiddenDependency>();
        }
    }

    public sealed class StandaloneConfigureResolveLifetimeScope : LifetimeScope
    {
        public static IScope CapturedScope;

        protected override void Configure(ScopeBuilder builder)
        {
            CapturedScope.Resolve<FinalActivationHiddenDependency>();
        }
    }
}
