using Kylin.SubscribableProperty;
using UnityEngine;

namespace Kylin.DI.Samples.UnitSpawn
{
    /// <summary>
    /// 골드 보유/소비 서비스. SubscribableProperty로 상태 노출.
    /// </summary>
    public class GoldService : IDependencyObject
    {
        public SubscribableProperty<int> Gold { get; } = new(500);

        public bool TrySpend(int amount)
        {
            if (Gold.Value < amount) return false;
            Gold.Value -= amount;
            return true;
        }
    }

    /// <summary>
    /// 유닛 프리팹/비용 테이블.
    /// 생성자 인자가 필요한 타입은 FromInstance로 등록한다 (BattleScope 참고).
    /// </summary>
    public class UnitCatalog : IDependencyObject
    {
        private readonly GameObject _knightPrefab;

        public UnitCatalog(GameObject knightPrefab)
        {
            _knightPrefab = knightPrefab;
        }

        public GameObject KnightPrefab => _knightPrefab;
        public int KnightCost => 100;
    }

    /// <summary>
    /// 유닛 소환 서비스.
    /// IInstantiator — Resolve 권한 없는 생성 전용 인터페이스를 주입받아
    /// IScope 전체(서비스 로케이터)를 쥐지 않고도 프리팹 생성 + 주입을 수행한다.
    /// </summary>
    public class SpawnUnitService : IDependencyObject, IInjectable
    {
        [Inject] private GoldService _gold;
        [Inject] private UnitCatalog _catalog;
        [Inject] private IInstantiator _instantiator;

        public void SpawnKnight(Vector3 position)
        {
            if (!_gold.TrySpend(_catalog.KnightCost))
            {
                Debug.Log("[UnitSpawn] 골드가 부족합니다.");
                return;
            }

            // Object.Instantiate + 하위 IInjectable 자동 주입 + Scope 연결을 한 번에
            _instantiator.Instantiate(_catalog.KnightPrefab, position, Quaternion.identity);
        }
    }
}
