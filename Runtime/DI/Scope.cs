using System;
using System.Collections.Generic;
using UnityEngine;

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
        private static HashSet<Type> _resolvingTypes;

        private readonly Dictionary<Type, Registration> _registrations;
        private readonly Dictionary<Type, object> _instances = new();
        private readonly IScope _parent;
        private readonly List<IScope> _children = new();
        private readonly object _lock = new();
        private bool _isDisposed;

        public IScope Parent => _parent;

        internal Scope(Dictionary<Type, Registration> registrations, IScope parent)
        {
            _registrations = registrations;
            _parent = parent;

            if (parent is Scope parentScope)
            {
                lock (parentScope._lock)
                {
                    parentScope._children.Add(this);
                }
            }
        }

        public T Resolve<T>() where T : class
        {
            return (T)Resolve(typeof(T));
        }

        public object Resolve(Type type)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(Scope));

            _resolvingTypes ??= new HashSet<Type>();

            if (!_resolvingTypes.Add(type))
            {
                var chain = string.Join(" → ", _resolvingTypes);
                _resolvingTypes.Clear();
                throw new InvalidOperationException($"[KDI] 순환참조 발생: {chain} → {type.Name}");
            }

            try
            {
                return ResolveInternal(type);
            }
            finally
            {
                _resolvingTypes.Remove(type);
            }
        }

        private object ResolveInternal(Type type)
        {
            lock (_lock)
            {
                if (_instances.TryGetValue(type, out var cached))
                    return cached;
            }

            if (_registrations.TryGetValue(type, out var reg))
            {
                if (reg.Instance != null)
                {
                    lock (_lock)
                    {
                        _instances[type] = reg.Instance;
                    }
                    return reg.Instance;
                }

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

            if (_parent != null)
                return _parent.Resolve(type);

            throw new InvalidOperationException($"[KDI] {type.Name}에 대한 등록을 찾을 수 없습니다.");
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

            if (instance is IInjectable injectable)
            {
                DependencyInjector.Inject(injectable, this);
            }
            else
            {
                DependencyInjector.WarnIfHasInjectFieldsWithoutIInjectable(instance);
            }

            if (instance is IUpdatable ||
                instance is IFixedUpdatable ||
                instance is ILateUpdatable)
            {
                UpdateLoopManager.Instance.Register(instance);
            }

            return instance;
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

                if (instance is IUpdatable ||
                    instance is IFixedUpdatable ||
                    instance is ILateUpdatable)
                {
                    UpdateLoopManager.Instance?.Unregister(instance);
                }
            }
            _instances.Clear();
        }
    }
}
