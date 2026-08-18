using System;
using System.Collections.Generic;
using System.Reflection;

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
        private readonly List<Type> _aliasTypes = new();
        private Type _implementationType;
        private Lifetime _lifetime = Lifetime.Singleton;
        private object _instance;
        private Func<object> _factory;
        private Func<object> _activator;
        private bool _isEntryPoint;
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
            where TImplementation : IDependencyObject, T, new()
        {
            _implementationType = typeof(TImplementation);
            _activator = () => new TImplementation();
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

            var implType = typeof(T);

            // ToSelf는 클래스 레벨 T(new() 제약 불가)를 쓰므로 컴파일 타임 검증이 불가능하다.
            // 등록 시점에 public 파라미터 없는 생성자 존재를 검사해 fail-fast를 유지한다.
            var ctor = implType.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (ctor == null)
            {
                throw new InvalidOperationException(
                    $"[KDI] {implType.Name}에 public 파라미터 없는 생성자가 없습니다. " +
                    "생성자 인자가 필요한 타입은 FromInstance() 또는 FromFactory()로 등록하세요.");
            }

            _implementationType = implType;
            _activator = () => System.Activator.CreateInstance(implType);
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
            if (instance == null) throw new ArgumentNullException(nameof(instance));
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
        public DependencyBuilder<T> FromFactory(Func<T> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _factory = () => factory();
            return this;
        }

        /// <summary>
        /// 추가 서비스 타입 바인딩 — 같은 단일 인스턴스를 여러 인터페이스로 노출한다.
        /// 예: Bind&lt;IPlayerService&gt;().To&lt;PlayerService&gt;().AlsoBind&lt;IDamageReceiver&gt;().AsScoped()
        /// → IPlayerService, IDamageReceiver 모두 동일한 PlayerService 인스턴스로 resolve.
        /// (각각 To로 등록하면 인스턴스가 2개 생기는 문제를 방지)
        /// </summary>
        public DependencyBuilder<T> AlsoBind<TAlias>() where TAlias : class
        {
            _aliasTypes.Add(typeof(TAlias));
            return this;
        }

        /// <summary>
        /// 엔트리포인트 지정 — 스코프 빌드 시점에 즉시 인스턴스화한다.
        /// lazy resolve로 인해 "아무도 주입하지 않으면 생성되지 않는" 시스템 서비스
        /// (IUpdatable 시뮬레이션 등)를 확실히 기동시킬 때 사용.
        /// 생성 시 [Inject] 주입과 IPostInjectable.PostInject()가 함께 실행된다.
        /// </summary>
        public DependencyBuilder<T> AsEntryPoint()
        {
            _isEntryPoint = true;
            return this;
        }

        private void FinishRegistration()
        {
            var aliases = BuildAndValidateAliases();

            if (_isEntryPoint && _lifetime == Lifetime.Transient && _instance == null)
            {
                throw new InvalidOperationException(
                    $"[KDI] Bind<{_serviceType.Name}>(): 엔트리포인트는 Transient일 수 없습니다. " +
                    "eager 인스턴스화된 Transient는 캐시되지 않아 즉시 유실됩니다. AsScoped()/AsSingleton()을 사용하세요.");
            }

            if (_instance != null)
            {
                _builder.AddRegistration(new Registration
                {
                    ServiceType = _serviceType,
                    Instance = _instance,
                    Lifetime = Lifetime.Scoped,
                    AliasTypes = aliases,
                    IsEntryPoint = _isEntryPoint
                });
            }
            else if (_factory != null)
            {
                _builder.AddRegistration(new Registration
                {
                    ServiceType = _serviceType,
                    Factory = _factory,
                    Lifetime = _lifetime,
                    AliasTypes = aliases,
                    IsEntryPoint = _isEntryPoint
                });
            }
            else if (_implementationType != null)
            {
                _builder.AddRegistration(new Registration
                {
                    ServiceType = _serviceType,
                    ImplementationType = _implementationType,
                    Activator = _activator,
                    Lifetime = _lifetime,
                    AliasTypes = aliases,
                    IsEntryPoint = _isEntryPoint
                });
            }
            else
            {
                throw new InvalidOperationException(
                    $"[KDI] Bind<{_serviceType.Name}>(): To<TImplementation>() / ToSelf() / FromFactory() / FromInstance() 없이 종결할 수 없습니다.");
            }

            _isCompleted = true;
        }

        /// <summary>
        /// AlsoBind 대상 타입들을 검증한다.
        /// 구현타입/인스턴스를 아는 경우 즉시 검사(빌드 타임 fail-fast),
        /// 팩토리는 생성 시점에 Scope가 검사한다.
        /// </summary>
        private Type[] BuildAndValidateAliases()
        {
            if (_aliasTypes.Count == 0) return null;

            foreach (var alias in _aliasTypes)
            {
                if (alias == _serviceType)
                {
                    throw new InvalidOperationException(
                        $"[KDI] Bind<{_serviceType.Name}>(): AlsoBind<{alias.Name}> 대상이 Bind 타입과 동일합니다.");
                }

                if (_implementationType != null && !alias.IsAssignableFrom(_implementationType))
                {
                    throw new InvalidOperationException(
                        $"[KDI] Bind<{_serviceType.Name}>(): 구현타입 {_implementationType.Name}이(가) " +
                        $"AlsoBind 대상 {alias.Name}을(를) 구현하지 않습니다.");
                }

                if (_instance != null && !alias.IsInstanceOfType(_instance))
                {
                    throw new InvalidOperationException(
                        $"[KDI] Bind<{_serviceType.Name}>(): 인스턴스 {_instance.GetType().Name}이(가) " +
                        $"AlsoBind 대상 {alias.Name}을(를) 구현하지 않습니다.");
                }
            }

            return _aliasTypes.ToArray();
        }
    }
}
