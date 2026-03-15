using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Kylin.DI
{
    public static class DependencyInjector
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> _fieldCache
            = new ConcurrentDictionary<Type, FieldInfo[]>();

        public static FieldInfo[] GetCachedInjectableFields(Type type)
        {
            return _fieldCache.GetOrAdd(type, t =>
            {
                var fieldList = new List<FieldInfo>();
                var currentType = t;
                while (currentType != null && currentType != typeof(MonoBehaviour) && currentType != typeof(object))
                {
                    var fields = currentType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                        .Where(f => f.GetCustomAttribute<InjectAttribute>() != null);
                    fieldList.AddRange(fields);
                    currentType = currentType.BaseType;
                }
                return fieldList.ToArray();
            });
        }

        /// <summary>
        /// scope 없이 호출 시 KDI.RootScope에서 Resolve
        /// </summary>
        public static void Inject(this IInjectable target)
        {
            Inject(target, KDI.RootScope);
        }

        public static void Inject(this IInjectable target, IScope scope)
        {
            if (target == null || scope == null) return;

            var fields = GetCachedInjectableFields(target.GetType());
            if (fields.Length == 0)
            {
                if (target is IPostInjectable post)
                    post.PostInject();
                return;
            }

            // Phase 1: 모든 의존성 Resolve 시도 — 하나라도 실패하면 전체 주입 중단
            var resolved = new object[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                try
                {
                    resolved[i] = scope.Resolve(fields[i].FieldType);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[KDI] {target.GetType().Name}.{fields[i].Name} ({fields[i].FieldType.Name}) resolve 실패: {ex.Message}\n" +
                        $"  → 주입 중단: {target.GetType().Name}의 모든 [Inject] 필드가 주입되지 않았습니다.");
                    return;
                }
            }

            // Phase 2: 전부 성공 — 일괄 주입
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i].SetValue(target, resolved[i]);
            }

            if (target is IPostInjectable postInjectable)
            {
                postInjectable.PostInject();
            }
        }

        /// <summary>
        /// [Inject] 필드가 있지만 IInjectable을 구현하지 않은 타입 경고.
        /// Scope.CreateInstance 및 LifetimeScope.InjectHierarchy에서 호출.
        /// </summary>
        public static void WarnIfHasInjectFieldsWithoutIInjectable(object target)
        {
            if (target == null || target is IInjectable) return;

            var fields = GetCachedInjectableFields(target.GetType());
            if (fields.Length > 0)
            {
                var fieldNames = string.Join(", ", fields.Select(f => f.Name));
                Debug.LogWarning(
                    $"[KDI] {target.GetType().Name}에 [Inject] 필드({fieldNames})가 있지만 " +
                    $"IInjectable을 구현하지 않았습니다. 주입이 수행되지 않습니다.");
            }
        }

        public static void ClearCache()
        {
            _fieldCache.Clear();
        }
    }
}
