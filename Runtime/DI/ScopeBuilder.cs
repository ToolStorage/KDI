using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kylin.DI
{
    public class ScopeBuilder
    {
        private readonly Dictionary<Type, Registration> _registrations = new();
        private readonly List<IPendingBinding> _pendingBindings = new();
        private bool _isBuilt;

        public DependencyBuilder<T> Bind<T>() where T : class
        {
            ThrowIfBuilt();
            var binding = new DependencyBuilder<T>(this, typeof(T));
            _pendingBindings.Add(binding);
            return binding;
        }

        public void RegisterFactory<T>(Func<T> factory, Lifetime lifetime) where T : class
        {
            ThrowIfBuilt();
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            AddRegistration(new Registration
            {
                ServiceType = typeof(T),
                Factory = () => factory(),
                Lifetime = lifetime
            });
        }

        public void RegisterInstance<T>(T instance) where T : class
        {
            ThrowIfBuilt();
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            AddRegistration(new Registration
            {
                ServiceType = typeof(T),
                Instance = instance,
                Lifetime = Lifetime.Scoped
            });
        }

        internal void AddRegistration(Registration registration)
        {
            ThrowIfBuilt();

            if (registration == null) throw new ArgumentNullException(nameof(registration));
            ResolverAuthorityGuard.ThrowIfRegistrationType(registration.ImplementationType, "Implementation type");
            if (registration.Instance != null)
                ResolverAuthorityGuard.ThrowIfResolverInstance(registration.Instance, "FromInstance/RegisterInstance");

            var keys = new List<Type> { registration.ServiceType };
            if (registration.AliasTypes != null)
                keys.AddRange(registration.AliasTypes);

            // Validate the complete registration before mutating the builder. A bad alias
            // must not leave the primary key behind as a half-completed binding.
            var uniqueKeys = new HashSet<Type>();
            foreach (var key in keys)
            {
                if (!uniqueKeys.Add(key))
                    throw new InvalidOperationException($"[KDI] {key.Name} is listed more than once in the same binding.");
                ValidateRegistrationKey(key);
            }

            foreach (var key in keys)
                _registrations[key] = registration;
        }

        private void ValidateRegistrationKey(Type serviceType)
        {
            ResolverAuthorityGuard.ThrowIfRegistrationType(serviceType, "Service type or alias");

            if (serviceType == typeof(IInstantiator))
            {
                throw new InvalidOperationException(
                    "[KDI] IInstantiator is a reserved per-Scope service and cannot be overridden or used as an alias.");
            }

            // 같은 스코프 내 재등록은 무음 덮어쓰기 대신 즉시 실패 — 오버라이드는 자식 스코프에서
            if (_registrations.ContainsKey(serviceType))
            {
                throw new InvalidOperationException(
                    $"[KDI] {serviceType.Name}이(가) 이미 등록되어 있습니다. " +
                    "같은 스코프 내 재등록은 허용되지 않습니다 — 오버라이드가 필요하면 자식 스코프에서 등록하세요.");
            }
        }

        public IScope Build(IScope parent = null, string name = null)
        {
            KDI.EnsureMainThread();
            ActivationCallbackGuard.ThrowIfConfigureMutation("ScopeBuilder.Build");
            if (_isBuilt) throw new InvalidOperationException("[KDI] ScopeBuilder는 한 번만 Build할 수 있습니다.");

            if (parent != null && !(parent is Scope))
                throw new NotSupportedException(
                    "[KDI] ScopeBuilder parents must be concrete KDI Scopes. A custom IScope has no shared " +
                    "activation/lifetime ledger, so child resolution cannot provide atomic rollback.");

            ValidatePendingBindings(name);
            ValidateLifetimes(parent, name);

            _isBuilt = true;
            return new Scope(_registrations, parent, name);
        }

        /// <summary>
        /// 종결 메서드(AsSingleton/AsScoped/AsTransient/FromInstance) 없이 끝난
        /// fluent 체인은 조용히 사라지는 대신 Build()에서 실패한다.
        /// </summary>
        private void ValidatePendingBindings(string name)
        {
            foreach (var binding in _pendingBindings)
            {
                if (!binding.IsCompleted)
                {
                    throw new InvalidOperationException(
                        $"[KDI] Build 실패 ({DisplayName(name)}): Bind<{binding.ServiceType.Name}>() 체인이 종결되지 않았습니다. " +
                        ".AsSingleton() / .AsScoped() / .AsTransient() / .FromInstance() 중 하나로 완결하세요.");
                }
            }
        }

        private void ValidateLifetimes(IScope parent, string name)
        {
            foreach (var reg in _registrations.Values)
            {
                // Singleton은 RootScope에서만 — child scope 파괴 시 인스턴스가 사라져 의미와 모순
                if (parent != null && reg.Lifetime == Lifetime.Singleton)
                {
                    throw new InvalidOperationException(
                        $"[KDI] Build 실패 ({DisplayName(name)}): Singleton은 RootScope에서만 등록 가능합니다: {reg.ServiceType.Name}");
                }

                if (reg.Lifetime == Lifetime.Transient && reg.ImplementationType != null)
                {
                    // A transient update service has no stable registration identity between resolves.
                    if (typeof(IUpdatable).IsAssignableFrom(reg.ImplementationType) ||
                        typeof(IFixedUpdatable).IsAssignableFrom(reg.ImplementationType) ||
                        typeof(ILateUpdatable).IsAssignableFrom(reg.ImplementationType))
                    {
                        throw new InvalidOperationException(
                            $"[KDI] Build 실패 ({DisplayName(name)}): {reg.ImplementationType.Name}(Transient)이 IUpdatable 계열을 구현합니다. " +
                            "Transient update services have no stable player-loop registration identity. " +
                            "AsScoped()로 등록하거나, 다수 인스턴스가 필요하면 Scoped 매니저 하나가 목록을 순회하는 구조를 권장합니다.");
                    }

                    if (typeof(IDisposable).IsAssignableFrom(reg.ImplementationType) ||
                        typeof(IInjectable).IsAssignableFrom(reg.ImplementationType))
                    {
                        Debug.LogWarning(
                            $"[KDI] ({DisplayName(name)}) {reg.ImplementationType.Name}(Transient)은 Scope lifetime tracking 대상입니다. " +
                            "The Scope retains it for injection revocation and/or Dispose; use a shorter child Scope for high-volume resolves.");
                    }
                }
            }
        }

        private void ThrowIfBuilt()
        {
            if (_isBuilt) throw new InvalidOperationException("[KDI] 이미 빌드된 ScopeBuilder에 등록할 수 없습니다.");
        }

        private static string DisplayName(string name) => string.IsNullOrEmpty(name) ? "Scope" : name;
    }
}
