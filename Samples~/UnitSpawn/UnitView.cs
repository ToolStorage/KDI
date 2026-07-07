using UnityEngine;

namespace Kylin.DI.Samples.UnitSpawn
{
    /// <summary>
    /// 유닛 프리팹에 부착하는 View.
    /// IInstantiator.Instantiate()로 생성되므로 [Inject] 필드가 자동 주입된다.
    /// </summary>
    public class UnitView : DIBehaviour
    {
        [Inject] private GoldService _gold;

        private void Start()
        {
            // 동적 생성된 오브젝트에도 주입이 완료되었음을 확인
            Debug.Log($"[UnitSpawn] 유닛 생성됨 (남은 골드: {_gold.Gold.Value})");
        }
    }
}
