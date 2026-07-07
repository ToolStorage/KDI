using Kylin.SubscribableProperty;
using UnityEngine;

namespace Kylin.DI.Samples.UnitSpawn
{
    /// <summary>
    /// 스페이스 키로 유닛 소환. BattleScope 하위에 배치하면 자동 주입된다.
    /// </summary>
    public class SpawnInput : DIBehaviour
    {
        [Inject] private SpawnUnitService _spawner;
        [Inject] private GoldService _gold;

        private void Start()
        {
            _gold.Gold
                .Subscribe(gold => Debug.Log($"[UnitSpawn] Gold: {gold}"), invokeInitial: true)
                .AddTo(_cd);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                _spawner.SpawnKnight(new Vector3(Random.Range(-3f, 3f), 0f, 0f));
            }
        }
    }
}
