using Kylin.SubscribableProperty;
using UnityEngine;

namespace Kylin.DI
{
    /// <summary>
    /// DI 지원 MonoBehaviour 기본 클래스.
    /// LifetimeScope.Initialize() 시 Push 방식으로 주입됨.
    /// 동적 생성 시 scope.Instantiate() 또는 scope.InjectGameObject() 사용.
    /// OnDisable 시 구독 정리.
    ///
    /// 사용 예시:
    /// <code>
    /// public class PlayerController : DIBehaviour
    /// {
    ///     [Inject] private IPlayerService _playerService;
    ///     [Inject] private IInputService _inputService;
    ///
    ///     void Start()
    ///     {
    ///         _playerService.Health.Subscribe(OnHealthChanged).AddTo(_cd);
    ///     }
    /// }
    /// </code>
    /// </summary>
    public abstract class DIBehaviour : MonoBehaviour, IInjectable
    {
        /// <summary>
        /// 구독 정리용 CompositeDisposable.
        /// Subscribe().AddTo(_cd) 패턴으로 사용.
        /// </summary>
        protected CompositeDisposable _cd = new();

        /// <summary>
        /// 캐싱된 스코프.
        /// </summary>
        private IScope _cachedScope;

        /// <summary>
        /// 현재 사용 중인 스코프.
        /// </summary>
        protected IScope Scope => _cachedScope;

        protected virtual void OnEnable() { }

        protected virtual void OnDisable()
        {
            Dispose();
        }

        /// <summary>
        /// 구독 정리 및 리소스 해제.
        /// OnDisable에서 자동 호출됨.
        /// </summary>
        public virtual void Dispose()
        {
            _cd?.Dispose();
            _cd = new CompositeDisposable();
        }

        /// <summary>
        /// LifetimeScope.InjectChildren() 또는 scope.InjectGameObject()에서 호출.
        /// Push 주입 시 스코프 참조를 캐싱.
        /// </summary>
        internal void SetInjected(IScope scope)
        {
            _cachedScope = scope;
        }
    }
}
