using UnityEngine;

namespace Kylin.DI
{
    /// <summary>
    /// Resolve 권한 없는 동적 생성 전용 인터페이스.
    /// 모든 Scope에 자동 등록되어 [Inject]로 주입받을 수 있다.
    ///
    /// IScope 전체를 주입하면 임의 타입을 Resolve할 수 있어 서비스 로케이터가 되지만,
    /// IInstantiator는 "프리팹 생성 + 하위 주입" 능력만 제공하므로
    /// 팩토리 클래스가 컨테이너에 커플링되지 않는다.
    ///
    /// 사용 예시:
    /// <code>
    /// public class SpawnUnitApp : IApplicationServiceLayer
    /// {
    ///     [Inject] private IUnitCatalog _catalog;
    ///     [Inject] private IInstantiator _instantiator;
    ///
    ///     public void Spawn(UnitType type, Vector3 pos)
    ///     {
    ///         _instantiator.Instantiate(_catalog.PrefabOf(type), pos, Quaternion.identity);
    ///     }
    /// }
    /// </code>
    /// </summary>
    public interface IInstantiator
    {
        /// <summary>프리팹 인스턴스화 + 하위 IInjectable 자동 주입.</summary>
        GameObject Instantiate(GameObject prefab);

        /// <summary>프리팹 인스턴스화 (부모 지정) + 하위 IInjectable 자동 주입.</summary>
        GameObject Instantiate(GameObject prefab, Transform parent);

        /// <summary>프리팹 인스턴스화 (위치/회전 지정) + 하위 IInjectable 자동 주입.</summary>
        GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation);

        /// <summary>프리팹 인스턴스화 (부모 + 위치/회전 지정) + 하위 IInjectable 자동 주입.</summary>
        GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent);

        /// <summary>이미 존재하는 GameObject의 하위 IInjectable에 주입.</summary>
        void InjectGameObject(GameObject gameObject);
    }

    /// <summary>
    /// IInstantiator 구현체. Scope 생성 시 자동으로 해당 스코프에 바인딩된다.
    /// </summary>
    internal sealed class Instantiator : IInstantiator
    {
        private readonly IScope _scope;

        internal Instantiator(IScope scope)
        {
            _scope = scope;
        }

        public GameObject Instantiate(GameObject prefab)
            => _scope.Instantiate(prefab);

        public GameObject Instantiate(GameObject prefab, Transform parent)
            => _scope.Instantiate(prefab, parent);

        public GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation)
            => _scope.Instantiate(prefab, position, rotation);

        public GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent)
            => _scope.Instantiate(prefab, position, rotation, parent);

        public void InjectGameObject(GameObject gameObject)
            => _scope.InjectGameObject(gameObject);
    }
}
