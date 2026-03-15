using System;

namespace Kylin.DI
{
    public class DependencyBuilder<T> where T : class
    {
        private readonly ScopeBuilder _builder;
        private readonly Type _serviceType;
        private Type _implementationType;
        private Lifetime _lifetime = Lifetime.Singleton;
        private object _instance;
        private Func<IScope, object> _factory;

        internal DependencyBuilder(ScopeBuilder builder, Type serviceType)
        {
            _builder = builder;
            _serviceType = serviceType;
        }

        /// <summary>
        /// 구현타입 지정
        /// </summary>
        public DependencyBuilder<T> To<TImplementation>()
            where TImplementation : IDependencyObject, T
        {
            _implementationType = typeof(TImplementation);
            return this;
        }

        /// <summary>
        /// 싱글톤 (RootScope에서만 허용)
        /// </summary>
        public void AsSingleton()
        {
            _lifetime = Lifetime.Singleton;
            FinishRegistration();
        }

        /// <summary>
        /// 매번 새 인스턴스
        /// </summary>
        public void AsTransient()
        {
            _lifetime = Lifetime.Transient;
            FinishRegistration();
        }

        /// <summary>
        /// 스코프 내 단일 인스턴스
        /// </summary>
        public void AsScoped()
        {
            _lifetime = Lifetime.Scoped;
            FinishRegistration();
        }

        public void FromInstance(T instance)
        {
            if (instance is IDependencyObject)
            {
                _instance = instance;
                FinishRegistration();
            }
            else
            {
                throw new ArgumentException($"Instance must implement IDependencyObject");
            }
        }

        /// <summary>
        /// 팩토리 등록
        /// </summary>
        public DependencyBuilder<T> FromFactory(Func<IScope, T> factory)
        {
            _factory = scope => factory(scope);
            return this;
        }

        private void FinishRegistration()
        {
            if (_instance != null)
            {
                _builder.AddRegistration(new Registration
                {
                    ServiceType = _serviceType,
                    Instance = _instance,
                    Lifetime = Lifetime.Scoped
                });
            }
            else if (_factory != null)
            {
                _builder.AddRegistration(new Registration
                {
                    ServiceType = _serviceType,
                    Factory = _factory,
                    Lifetime = _lifetime
                });
            }
            else if (_implementationType != null)
            {
                _builder.AddRegistration(new Registration
                {
                    ServiceType = _serviceType,
                    ImplementationType = _implementationType,
                    Lifetime = _lifetime
                });
            }
            else
            {
                throw new InvalidOperationException("[KDI] Registration is incomplete");
            }
        }
    }
}
