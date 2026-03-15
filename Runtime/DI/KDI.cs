using UnityEngine;

namespace Kylin.DI
{
    /// <summary>
    /// KDI (Kylin Dependency Injection) - Static Facade
    ///
    /// 사용 패턴:
    /// 1. 서비스 등록: LifetimeScope 상속 클래스에서 Configure(ScopeBuilder builder)에서 등록
    /// 2. 서비스 해결: this.Inject(scope) 또는 scope.Resolve() 사용
    /// 3. 자동 주입 (권장): [Inject] 어트리뷰트 + DIBehaviour
    /// </summary>
    public static class KDI
    {
        private static IScope _rootScope;

        /// <summary>
        /// RootScope 접근점.
        /// parent가 없는 LifetimeScope가 Initialize()할 때 자동 설정됨.
        /// </summary>
        public static IScope RootScope
        {
            get
            {
                if (_rootScope == null)
                {
                    Debug.LogWarning("[KDI] RootScope가 설정되지 않았습니다. 빈 RootScope를 자동 생성합니다.");
                    _rootScope = new ScopeBuilder().Build(parent: null);
                }
                return _rootScope;
            }
        }

        /// <summary>
        /// RootScope 설정. parent가 없는 LifetimeScope에서 호출.
        /// </summary>
        internal static void SetRootScope(IScope scope)
        {
            if (_rootScope != null)
            {
                Debug.LogWarning("[KDI] RootScope가 이미 설정되어 있습니다. 기존 RootScope를 교체합니다.");
            }
            _rootScope = scope;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _rootScope?.Dispose();
            _rootScope = null;
            DependencyInjector.ClearCache();
        }
    }
}
