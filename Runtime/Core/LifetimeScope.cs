using System.Collections.Generic;
using UnityEngine;

namespace Kylin.DI
{
    /// <summary>
    /// 스코프 기반 의존성 관리를 위한 추상 MonoBehaviour.
    /// Initialize() 시 하위 계층의 IInjectable MonoBehaviour에 일괄 주입 (Push).
    /// child LifetimeScope 경계에서 탐색 중단 (하위 스코프의 주입 영역 침범 방지).
    ///
    /// 사용 방법:
    /// 1. 이 클래스를 상속받은 클래스 생성
    /// 2. Configure(ScopeBuilder builder)에서 builder.Bind 등으로 서비스 등록
    /// 3. 씬에 해당 컴포넌트를 하나 배치
    ///
    /// 예시:
    /// <code>
    /// public class BattleSceneScope : LifetimeScope
    /// {
    ///     protected override void Configure(ScopeBuilder builder)
    ///     {
    ///         builder.Bind&lt;IBattleService&gt;().To&lt;BattleService&gt;().AsScoped();
    ///     }
    /// }
    /// </code>
    /// </summary>
    public abstract class LifetimeScope : MonoBehaviour
    {
        // === Static Registry ===
        private static readonly List<LifetimeScope> _activeScopes = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            _activeScopes.Clear();
        }

        // === Inspector Fields ===

        [Header("Scope Hierarchy")]
        [SerializeField]
        [Tooltip("부모 LifetimeScope. 없으면 RootScope로 생성됨.")]
        private LifetimeScope _parent;

        [SerializeField]
        [Tooltip("true면 Awake에서 자동 초기화, false면 수동으로 Initialize() 호출 필요")]
        private bool _autoInitialize = true;

        // === Instance State ===

        private IScope _scope;
        private bool _isInitialized;
        private int _hierarchyDepth;

        /// <summary>
        /// 이 LifetimeScope의 IScope.
        /// </summary>
        public IScope Scope => _scope;

        /// <summary>
        /// 초기화 완료 여부
        /// </summary>
        public bool IsInitialized => _isInitialized;

        #region Unity Lifecycle

        protected virtual void Awake()
        {
            if (_autoInitialize)
            {
                Initialize();
            }
        }

        protected virtual void OnDestroy()
        {
            Dispose();
        }

        #endregion

        #region Public API

        /// <summary>
        /// 수동 초기화.
        /// _autoInitialize가 false일 때 사용.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
            {
                Debug.LogWarning($"[LifetimeScope] {GetType().Name} is already initialized.");
                return;
            }

            var builder = new ScopeBuilder();
            Configure(builder);

            if (_parent != null)
            {
                if (!_parent.IsInitialized)
                {
                    _parent.Initialize();
                }
                _scope = builder.Build(_parent.Scope, GetType().Name);
            }
            else
            {
                // parent가 없으면 RootScope로 빌드
                _scope = builder.Build(parent: null, name: GetType().Name);
                KDI.SetRootScope(_scope);
            }

            // static registry 등록
            _hierarchyDepth = ComputeDepth(transform);
            _activeScopes.Add(this);

            _isInitialized = true;

            // 자기 GameObject의 IInjectable 주입 — 부모 스코프는 이 GO를 경계로 보고 건너뛰므로
            // 여기서 주입하지 않으면 아무도 주입해주지 않는다
            InjectSelf();

            // 하위 계층 IInjectable 일괄 주입 (Push)
            InjectChildren();

            Debug.Log($"[LifetimeScope] {GetType().Name} initialized.");
        }

        /// <summary>
        /// 스코프 정리.
        /// OnDestroy에서 자동 호출됨.
        /// </summary>
        public void Dispose()
        {
            if (!_isInitialized)
                return;

            _activeScopes.Remove(this);
            _scope?.Dispose();
            _scope = null;
            _isInitialized = false;

            Debug.Log($"[LifetimeScope] {GetType().Name} disposed.");
        }

        #endregion

        #region Push Injection

        /// <summary>
        /// 하위 계층의 IInjectable MonoBehaviour를 찾아 일괄 주입.
        /// child LifetimeScope 경계에서 탐색을 중단하여 하위 스코프 영역을 침범하지 않는다.
        /// </summary>
        private void InjectChildren()
        {
            InjectHierarchy(transform);
        }

        /// <summary>
        /// LifetimeScope 자신의 GameObject에 붙은 IInjectable 컴포넌트 주입.
        /// </summary>
        private void InjectSelf()
        {
            var injectables = GetComponents<IInjectable>();
            foreach (var injectable in injectables)
            {
                if (injectable is LifetimeScope) continue;

                DependencyInjector.Inject(injectable, _scope);

                if (injectable is DIBehaviour dib)
                    dib.SetInjected(_scope);
            }

            var behaviours = GetComponents<MonoBehaviour>();
            foreach (var mb in behaviours)
            {
                if (mb == null || mb is IInjectable || mb is LifetimeScope) continue;
                DependencyInjector.WarnIfHasInjectFieldsWithoutIInjectable(mb);
            }
        }

        private void InjectHierarchy(Transform current)
        {
            for (int i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);

                // child LifetimeScope가 있으면 탐색 중단 (그 scope의 영역)
                if (child.TryGetComponent<LifetimeScope>(out _))
                    continue;

                // 이 GameObject의 IInjectable 컴포넌트 주입
                var injectables = child.GetComponents<IInjectable>();
                foreach (var injectable in injectables)
                {
                    DependencyInjector.Inject(injectable, _scope);

                    if (injectable is DIBehaviour dib)
                        dib.SetInjected(_scope);
                }

                // [Inject] 필드가 있지만 IInjectable 미구현인 MonoBehaviour 경고
                var behaviours = child.GetComponents<MonoBehaviour>();
                foreach (var mb in behaviours)
                {
                    if (mb == null || mb is IInjectable) continue;
                    DependencyInjector.WarnIfHasInjectFieldsWithoutIInjectable(mb);
                }

                // 재귀
                InjectHierarchy(child);
            }
        }

        #endregion

        #region Static Helpers (Registry 기반)

        /// <summary>
        /// static registry에서 가장 가까운 LifetimeScope 찾기.
        /// GetComponentInParent 대신 Transform.IsChildOf (네이티브 호출) 사용.
        /// </summary>
        public static LifetimeScope Find(Transform from) => FindInternal(from);

        public static LifetimeScope Find(GameObject from) => FindInternal(from?.transform);

        public static LifetimeScope Find(Component from) => FindInternal(from?.transform);

        private static LifetimeScope FindInternal(Transform from)
        {
            if (from == null) return null;

            LifetimeScope best = null;
            int bestDepth = -1;

            for (int i = 0; i < _activeScopes.Count; i++)
            {
                var scope = _activeScopes[i];
                if (scope == null || !scope._isInitialized) continue;

                if (from.IsChildOf(scope.transform))
                {
                    if (scope._hierarchyDepth > bestDepth)
                    {
                        bestDepth = scope._hierarchyDepth;
                        best = scope;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// 씬에서 루트 LifetimeScope 찾기.
        /// </summary>
        public static LifetimeScope FindRoot()
        {
            for (int i = 0; i < _activeScopes.Count; i++)
            {
                if (_activeScopes[i]._parent == null)
                    return _activeScopes[i];
            }
            return null;
        }

        private static int ComputeDepth(Transform t)
        {
            int depth = 0;
            while (t.parent != null)
            {
                depth++;
                t = t.parent;
            }
            return depth;
        }

        #endregion

        #region Abstract

        /// <summary>
        /// 서비스 등록.
        /// 하위 클래스에서 구현하여 builder.Bind 등으로 서비스 등록.
        /// </summary>
        protected abstract void Configure(ScopeBuilder builder);

        #endregion
    }
}
