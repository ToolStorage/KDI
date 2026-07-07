using System;

namespace Kylin.DI
{
    /// <summary>
    /// ScopeBuilder가 미완결 fluent 체인을 Build()에서 검출하기 위한 내부 추적 인터페이스.
    /// </summary>
    internal interface IPendingBinding
    {
        Type ServiceType { get; }
        bool IsCompleted { get; }
    }

    public class DependencyBuilder<T> : IPendingBinding where T : class
    {
        private readonly ScopeBuilder _builder;
        private readonly Type _serviceType;
        private Type _implementationType;
        private Lifetime _lifetime = Lifetime.Singleton;
        private object _instance;
        private Func<IScope, object> _factory;
        private bool _isCompleted;

        Type IPendingBinding.ServiceType => _serviceType;
        bool IPendingBinding.IsCompleted => _isCompleted;

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
        /// 자기 바인딩 — 서비스 타입 자신을 구현타입으로 사용.
        /// 구체 타입 주입이 필요할 때(예: KDILayered의 [OwnerOnly] 패턴) 사용.
        /// </summary>
        public DependencyBuilder<T> ToSelf()
        {
            if (typeof(T).IsInterface || typeof(T).IsAbstract)
            {
                throw new InvalidOperationException(
                    $"[KDI] ToSelf()는 구체 타입에만 사용할 수 있습니다: {typeof(T).Name}. 인터페이스는 To<TImplementation>()을 사용하세요.");
            }

            if (!typeof(IDependencyObject).IsAssignableFrom(typeof(T)))
            {
                throw new InvalidOperationException(
                    $"[KDI] {typeof(T).Name}은(는) IDependencyObject를 구현해야 합니다.");
            }

            _implementationType = typeof(T);
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
                throw new ArgumentException(
                    $"[KDI] FromInstance 인스턴스는 IDependencyObject를 구현해야 합니다: {typeof(T).Name}");
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
                throw new InvalidOperationException(
                    $"[KDI] Bind<{_serviceType.Name}>(): To<TImplementation>() / ToSelf() / FromFactory() / FromInstance() 없이 종결할 수 없습니다.");
            }

            _isCompleted = true;
        }
    }
}
