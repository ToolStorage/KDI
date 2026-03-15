using UnityEngine;

namespace Kylin.DI
{
    /// <summary>
    /// IScope 확장 메서드.
    /// 동적 생성 시 DI 주입을 지원한다.
    /// </summary>
    public static class ScopeExtensions
    {
        /// <summary>
        /// GameObject의 하위 IInjectable MonoBehaviour에 주입.
        /// 동적 생성 후 호출하거나, scope.Instantiate()가 내부적으로 호출.
        /// </summary>
        public static void InjectGameObject(this IScope scope, GameObject gameObject)
        {
            if (scope == null || gameObject == null) return;

            var injectables = gameObject.GetComponentsInChildren<IInjectable>(true);
            foreach (var injectable in injectables)
            {
                DependencyInjector.Inject(injectable, scope);

                if (injectable is DIBehaviour dib)
                    dib.SetInjected(scope);
            }
        }

        /// <summary>
        /// 프리팹 인스턴스화 + 하위 IInjectable 자동 주입.
        /// Object.Instantiate 대신 사용.
        /// </summary>
        public static GameObject Instantiate(this IScope scope, GameObject prefab)
        {
            var instance = Object.Instantiate(prefab);
            scope.InjectGameObject(instance);
            return instance;
        }

        /// <summary>
        /// 프리팹 인스턴스화 (부모 지정) + 하위 IInjectable 자동 주입.
        /// </summary>
        public static GameObject Instantiate(this IScope scope, GameObject prefab, Transform parent)
        {
            var instance = Object.Instantiate(prefab, parent);
            scope.InjectGameObject(instance);
            return instance;
        }

        /// <summary>
        /// 프리팹 인스턴스화 (위치/회전 지정) + 하위 IInjectable 자동 주입.
        /// </summary>
        public static GameObject Instantiate(this IScope scope, GameObject prefab, Vector3 position, Quaternion rotation)
        {
            var instance = Object.Instantiate(prefab, position, rotation);
            scope.InjectGameObject(instance);
            return instance;
        }

        /// <summary>
        /// 프리팹 인스턴스화 (부모 + 위치/회전 지정) + 하위 IInjectable 자동 주입.
        /// </summary>
        public static GameObject Instantiate(this IScope scope, GameObject prefab, Vector3 position, Quaternion rotation, Transform parent)
        {
            var instance = Object.Instantiate(prefab, position, rotation, parent);
            scope.InjectGameObject(instance);
            return instance;
        }
    }
}
