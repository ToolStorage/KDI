using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace Kylin.DI
{
    internal static class InstanceFactory
    {
        private static readonly ConcurrentDictionary<Type, Func<object>> _cache = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => _cache.Clear();

        public static object Create(Type type)
        {
            return _cache.GetOrAdd(type, BuildFactory)();
        }

        private static Func<object> BuildFactory(Type type)
        {
            var ctor = type.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

            if (ctor == null)
                throw new InvalidOperationException(
                    $"[KDI] {type.Name}에 public 파라미터 없는 생성자가 없습니다.");

            var newExpr = Expression.New(ctor);
            var lambda = Expression.Lambda<Func<object>>(
                Expression.Convert(newExpr, typeof(object)));
            return lambda.Compile();
        }
    }
}
