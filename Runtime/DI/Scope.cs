using System;
using System.Collections.Generic;
using System.Text;

namespace Kylin.DI
{
    public interface IScope : IDisposable
    {
        IScope Parent { get; }
        T Resolve<T>() where T : class;
        object Resolve(Type type);
    }

    public class Scope : IScope
    {
        [ThreadStatic]
        private static List<Type> _resolvingChain;

        private readonly Dictionary<Type, Registration> _registrations;
        private readonly Dictionary<Type, object> _instances = new();
        private readonly IScope _parent;
        private readonly List<IScope> _children = new();
        private readonly object _lock = new();
        private readonly string _name;
        private bool _isDisposed;

        public IScope Parent => _parent;

        /// <summary>
        /// 진단용 스코프 이름. LifetimeScope가 자기 타입명을 넘겨준다.
        /// </summary>
        internal string Name => _name;

        /// <summary>
        /// 에디터 인스펙터용 읽기 전용 접근.
        /// </summary>
        internal IReadOnlyDictionary<Type, Registration> Registrations => _registrations;

        internal bool IsResolved(Type type)
        {
            lock (_lock)
            {
                return _instances.ContainsKey(type);
            }
        }

        internal Scope(Dictionary<Type, Registration> registrations, IScope parent, string name)
        {
            _registrations = registrations;
            _parent = parent;
            _name = string.IsNullOrEmpty(name) ? "Scope" : name;

            if (parent is Scope parentScope)
            {
                lock (parentScope._lock)
                {
                    parentScope._children.Add(this);
                }
            }

            // Resolve 권한 없는 생성 전용 인터페이스를 모든 스코프에 기본 제공
            _instances[typeof(IInstantiator)] = new Instantiator(this);

            InjectInstanceRegistrations();
        }

        /// <summary>
        /// FromInstance/RegisterInstance로 등록된 인스턴스는 빌드 시점에 즉시 주입 + Update 루프 등록.
        /// 지연 주입 대신 eager 처리하여 배선 실패가 Build()에서 결정적으로 드러나게 한다.
        /// </summary>
        private void InjectInstanceRegistrations()
        {
            // 1) 먼저 전부 캐시에 올려 인스턴스끼리 서로 주입받을 수 있게 한다
            foreach (var reg in _registrations.Values)
            {
                if (reg.Instance != null)
                    _instances[reg.ServiceType] = reg.Instance;
            }

            // 2) 주입 + Update 루프 등록
            foreach (var reg in _registrations.Values)
            {
                if (reg.Instance == null) continue;

                if (reg.Instance is IInjectable injectable)
                    DependencyInjector.Inject(injectable, this);
                else
                    DependencyInjector.WarnIfHasInjectFieldsWithoutIInjectable(reg.Instance);

                RegisterToUpdateLoop(reg.Instance);
            }
        }

        public T Resolve<T>() where T : class
        {
            return (T)Resolve(typeof(T));
        }

        public object Resolve(Type type)
        {
            if (_isDisposed) throw new ObjectDisposedException(_name);

            _resolvingChain ??= new List<Type>();

            if (_resolvingChain.Contains(type))
            {
                var chain = BuildTypeChain(_resolvingChain, type);
                _resolvingChain.Clear();
                throw new InvalidOperationException($"[KDI] ({_name}) 순환참조 발생: {chain}");
            }

            _resolvingChain.Add(type);
            try
            {
                return ResolveCore(type, this);
            }
            finally
            {
                _resolvingChain.Remove(type);
            }
        }

        /// <summary>
        /// 부모 체인 위임용 내부 Resolve.
        /// 순환참조 추적은 진입점(public Resolve)에서만 수행한다 —
        /// 부모 위임이 같은 타입을 재추적하면 거짓 순환참조가 발생하기 때문.
        /// </summary>
        private object ResolveCore(Type type, Scope origin)
        {
            if (_isDisposed) throw new ObjectDisposedException(_name);

            lock (_lock)
            {
                if (_instances.TryGetValue(type, out var cached))
                    return cached;
            }

            if (_registrations.TryGetValue(type, out var reg))
            {
                var instance = CreateInstance(reg);

                if (reg.Lifetime == Lifetime.Scoped || reg.Lifetime == Lifetime.Singleton)
                {
                    lock (_lock)
                    {
                        _instances[type] = instance;
                    }
                }

                return instance;
            }

            if (_parent is Scope parentScope)
                return parentScope.ResolveCore(type, origin);

            if (_parent != null)
                return _parent.Resolve(type);

            throw new InvalidOperationException(
                $"[KDI] {origin.BuildScopeChain()} 체인에서 {type.Name} 등록을 찾을 수 없습니다.");
        }

        private object CreateInstance(Registration registration)
        {
            object instance;

            if (registration.Factory != null)
            {
                instance = registration.Factory(this);
            }
            else
            {
                instance = InstanceFactory.Create(registration.ImplementationType);
            }

            // 타입 기반 Transient+IUpdatable은 빌드 타임에 차단되지만,
            // 팩토리 생성물은 여기서만 알 수 있으므로 첫 Resolve에서 fail-fast
            if (registration.Lifetime == Lifetime.Transient && IsUpdatable(instance))
            {
                throw new InvalidOperationException(
                    $"[KDI] ({_name}) {registration.ServiceType.Name}: Transient 팩토리가 IUpdatable 계열 인스턴스를 생성했습니다. " +
                    "Transient는 스코프가 수명을 추적하지 않아 Update 루프에서 해제될 수 없습니다. " +
                    "AsScoped()로 등록하거나 Update 등록이 없는 설계로 변경하세요.");
            }

            if (instance is IInjectable injectable)
            {
                DependencyInjector.Inject(injectable, this);
            }
            else
            {
                DependencyInjector.WarnIfHasInjectFieldsWithoutIInjectable(instance);
            }

            // Transient는 수명 추적이 불가능하므로 Update 루프에 올리지 않는다 (위에서 이미 차단됨)
            if (registration.Lifetime != Lifetime.Transient)
            {
                RegisterToUpdateLoop(instance);
            }

            return instance;
        }

        private static bool IsUpdatable(object instance)
        {
            return instance is IUpdatable
                || instance is IFixedUpdatable
                || instance is ILateUpdatable;
        }

        private static void RegisterToUpdateLoop(object instance)
        {
            if (!IsUpdatable(instance)) return;

            var manager = UpdateLoopManager.Instance;
            if (manager != null)
                manager.Register(instance);
        }

        /// <summary>
        /// 진단 메시지용 스코프 체인 문자열 ("BattleScope → AppRootScope").
        /// </summary>
        private string BuildScopeChain()
        {
            var sb = new StringBuilder(_name);
            var parent = _parent;
            while (parent is Scope parentScope)
            {
                sb.Append(" → ").Append(parentScope._name);
                parent = parentScope._parent;
            }
            return sb.ToString();
        }

        private static string BuildTypeChain(List<Type> chain, Type current)
        {
            var sb = new StringBuilder();
            foreach (var t in chain)
            {
                sb.Append(t.Name).Append(" → ");
            }
            sb.Append(current.Name);
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_parent is Scope parentScope)
            {
                lock (parentScope._lock)
                {
                    parentScope._children.Remove(this);
                }
            }

            List<IScope> childrenSnapshot;
            lock (_lock)
            {
                childrenSnapshot = new List<IScope>(_children);
                _children.Clear();
            }

            foreach (var child in childrenSnapshot)
            {
                child.Dispose();
            }

            foreach (var instance in _instances.Values)
            {
                if (instance is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch { /* ignore */ }
                }

                if (IsUpdatable(instance))
                {
                    // Instance getter는 파괴 시점에 새 GameObject를 만들 수 있으므로 non-creating 접근자 사용
                    UpdateLoopManager.TryGetInstance()?.Unregister(instance);
                }
            }
            _instances.Clear();
        }
    }
}
