using System;
using System.Collections.Generic;

namespace Kylin.DI
{
    public class ScopeBuilder
    {
        private readonly Dictionary<Type, Registration> _registrations = new();
        private bool _isBuilt;

        public DependencyBuilder<T> Bind<T>() where T : class
        {
            if (_isBuilt) throw new InvalidOperationException("[KDI] 이미 빌드된 ScopeBuilder에 등록할 수 없습니다.");
            return new DependencyBuilder<T>(this, typeof(T));
        }

        public void RegisterFactory<T>(Func<IScope, T> factory, Lifetime lifetime) where T : class
        {
            if (_isBuilt) throw new InvalidOperationException("[KDI] 이미 빌드된 ScopeBuilder에 등록할 수 없습니다.");

            var serviceType = typeof(T);
            _registrations[serviceType] = new Registration
            {
                ServiceType = serviceType,
                Factory = scope => factory(scope),
                Lifetime = lifetime
            };
        }

        public void RegisterInstance<T>(T instance) where T : class
        {
            if (_isBuilt) throw new InvalidOperationException("[KDI] 이미 빌드된 ScopeBuilder에 등록할 수 없습니다.");

            var serviceType = typeof(T);
            _registrations[serviceType] = new Registration
            {
                ServiceType = serviceType,
                Instance = instance,
                Lifetime = Lifetime.Scoped
            };
        }

        internal void AddRegistration(Registration registration)
        {
            if (_isBuilt) throw new InvalidOperationException("[KDI] 이미 빌드된 ScopeBuilder에 등록할 수 없습니다.");
            _registrations[registration.ServiceType] = registration;
        }

        public IScope Build(IScope parent = null)
        {
            if (_isBuilt) throw new InvalidOperationException("[KDI] ScopeBuilder는 한 번만 Build할 수 있습니다.");

            // child scope에서 Singleton 등록 검증
            if (parent != null)
            {
                foreach (var reg in _registrations.Values)
                {
                    if (reg.Lifetime == Lifetime.Singleton)
                        throw new InvalidOperationException(
                            $"[KDI] Singleton은 RootScope에서만 등록 가능합니다: {reg.ServiceType.Name}");
                }
            }

            _isBuilt = true;
            return new Scope(_registrations, parent);
        }
    }
}
