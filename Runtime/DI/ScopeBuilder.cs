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

        public void RegisterFactory<T>(Func<IScope, T> factory, Lifetime lifetime) where T : class
        {
            ThrowIfBuilt();

            AddRegistration(new Registration
            {
                ServiceType = typeof(T),
                Factory = scope => factory(scope),
                Lifetime = lifetime
            });
        }

        public void RegisterInstance<T>(T instance) where T : class
        {
            ThrowIfBuilt();

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

            // 같은 스코프 내 재등록은 무음 덮어쓰기 대신 즉시 실패 — 오버라이드는 자식 스코프에서
            if (_registrations.ContainsKey(registration.ServiceType))
            {
                throw new InvalidOperationException(
                    $"[KDI] {registration.ServiceType.Name}이(가) 이미 등록되어 있습니다. " +
                    "같은 스코프 내 재등록은 허용되지 않습니다 — 오버라이드가 필요하면 자식 스코프에서 등록하세요.");
            }

            _registrations[registration.ServiceType] = registration;
        }

        public IScope Build(IScope parent = null, string name = null)
        {
            if (_isBuilt) throw new InvalidOperationException("[KDI] ScopeBuilder는 한 번만 Build할 수 있습니다.");

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
                    // Transient는 스코프가 수명을 추적하지 않아 Update 루프에서 해제 불가 → 조합 자체를 차단
                    if (typeof(IUpdatable).IsAssignableFrom(reg.ImplementationType) ||
                        typeof(IFixedUpdatable).IsAssignableFrom(reg.ImplementationType) ||
                        typeof(ILateUpdatable).IsAssignableFrom(reg.ImplementationType))
                    {
                        throw new InvalidOperationException(
                            $"[KDI] Build 실패 ({DisplayName(name)}): {reg.ImplementationType.Name}(Transient)이 IUpdatable 계열을 구현합니다. " +
                            "Transient는 스코프가 수명을 추적하지 않아 Update 루프에서 해제될 수 없습니다. " +
                            "AsScoped()로 등록하거나, 다수 인스턴스가 필요하면 Scoped 매니저 하나가 목록을 순회하는 구조를 권장합니다.");
                    }

                    if (typeof(IDisposable).IsAssignableFrom(reg.ImplementationType))
                    {
                        Debug.LogWarning(
                            $"[KDI] ({DisplayName(name)}) {reg.ImplementationType.Name}(Transient)이 IDisposable을 구현합니다. " +
                            "스코프가 Transient의 Dispose를 호출하지 않으므로 생성한 쪽에서 직접 Dispose해야 합니다.");
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
