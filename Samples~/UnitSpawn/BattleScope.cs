using UnityEngine;

namespace Kylin.DI.Samples.UnitSpawn
{
    /// <summary>
    /// 전투 씬 스코프. 씬의 최상위 GameObject에 배치하고
    /// _knightPrefab에 UnitView가 붙은 프리팹을 지정한다.
    ///
    /// 하위 Transform의 모든 IInjectable(SpawnInput 등)에 Awake 시 자동 주입된다.
    /// </summary>
    public class BattleScope : LifetimeScope
    {
        [SerializeField] private GameObject _knightPrefab;

        protected override void Configure(ScopeBuilder builder)
        {
            // ToSelf — 인터페이스 없이 구체 타입 그대로 바인딩
            builder.Bind<GoldService>().ToSelf().AsScoped();
            builder.Bind<SpawnUnitService>().ToSelf().AsScoped();

            // 생성자 인자(프리팹)가 필요한 타입은 FromInstance로 등록.
            // 인스턴스는 Build() 시점에 즉시 주입/등록 처리된다.
            builder.Bind<UnitCatalog>().FromInstance(new UnitCatalog(_knightPrefab));
        }
    }
}
