using Kylin.SubscribableProperty;
using System;
using System.Reflection;
using UnityEngine;

namespace Kylin.DI
{
    /// <summary>
    /// DI 지원 MonoBehaviour 기본 클래스.
    /// LifetimeScope.Initialize() 시 Push 방식으로 주입됨.
    /// 동적 생성 시 주입된 IInstantiator 또는 Instantiator 프로퍼티를 사용.
    /// OnDisable 시 활성 구간 구독을 정리하고, 다시 활성화되면
    /// OnInjectedEnable에서 새 구독 구간을 시작한다.
    ///
    /// 사용 예시:
    /// <code>
    /// public class PlayerController : DIBehaviour
    /// {
    ///     [Inject] private IPlayerService _playerService;
    ///     [Inject] private IInputService _inputService;
    ///
    ///     protected override void OnInjectedEnable()
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

        private CompositeDisposable _injectionDisposables = new();

        /// <summary>
        /// 캐싱된 스코프.
        /// </summary>
        private IScope _cachedScope;

        private IInstantiator _instantiator;

        /// <summary>
        /// Resolve 권한 없이 동적 Unity 객체 생성/주입만 제공하는 현재 Scope의 도구.
        /// </summary>
        protected IInstantiator Instantiator => _instantiator;

        /// <summary>
        /// 주입 성공부터 주입 해제까지 유지할 자원용 컨테이너.
        /// 활성/비활성 구간에만 필요한 구독은 _cd를 사용한다.
        /// </summary>
        protected CompositeDisposable InjectionDisposables => _injectionDisposables;

        protected bool IsInjected => _isInjected;

        private bool _isInjected;
        private bool _isPreparingInjection;
        private bool _isInjectedActive;
        private bool _isEndingInjection;
        private bool _isDisposingActiveBucket;
        private bool _isReconcilingActiveState;
        private bool _activeStateChangedDuringCallback;
        private int _lifecycleCallbackDepth;
        private bool _publishFreshBucketAfterCallback;

        protected void OnEnable()
        {
            ReconcileInjectedActiveState();
        }

        protected void OnDisable()
        {
            ReconcileInjectedActiveState();
        }

        /// <summary>
        /// 주입이 완료된 활성 구간마다 호출된다. 비활성화 후 재활성화되면 다시 호출된다.
        /// </summary>
        protected virtual void OnInjectedEnable() { }

        /// <summary>
        /// OnInjectedEnable과 짝을 이루며 dependency가 아직 유효한 동안 호출된다.
        /// </summary>
        protected virtual void OnInjectedDisable() { }

        /// <summary>
        /// Scope 폐기 또는 activation rollback으로 주입을 해제하기 직전에 호출된다.
        /// </summary>
        protected virtual void OnBeforeUninject() { }

        /// <summary>
        /// 구독 정리 및 리소스 해제.
        /// OnDisable에서 자동 호출됨.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposingActiveBucket)
                return;

            // Publish an already-disposed terminal bucket while user disposal
            // callbacks run. A callback that adds another subscription through _cd
            // transfers it to that terminal bucket and it is disposed immediately,
            // rather than leaking into the next active interval.
            var subscriptions = _cd;
            _cd = CreateTerminalActiveBucket();
            _isDisposingActiveBucket = true;
            try
            {
                subscriptions?.Dispose();
            }
            finally
            {
                _isDisposingActiveBucket = false;
                // During Revoke/Abort the terminal bucket remains installed for the
                // complete cleanup interval. A fresh bucket is published only after
                // every user/injection cleanup callback has finished.
                if (!_isEndingInjection)
                    _cd = new CompositeDisposable();
            }
        }

        /// <summary>
        /// LifetimeScope.InjectChildren() 또는 scope.InjectGameObject()에서 호출.
        /// Push 주입 시 스코프 참조를 캐싱.
        /// </summary>
        internal bool IsInjectedWith(IScope scope)
        {
            return _isInjected && ReferenceEquals(_cachedScope, scope);
        }

        internal void PrepareInjection(IScope scope)
        {
            ValidateLifecycleMessages();

            if (_isInjected)
            {
                if (ReferenceEquals(_cachedScope, scope))
                    return;

                throw new System.InvalidOperationException(
                    $"[KDI] {GetType().Name} is already injected by another Scope. Re-injection without revocation is not allowed.");
            }

            if (_isPreparingInjection)
                throw new System.InvalidOperationException($"[KDI] Re-entrant injection detected: {GetType().Name}");

            _isPreparingInjection = true;
            _cachedScope = scope;
            _instantiator = ResolverAuthorityGuard.RequireConcreteScope(scope, "DIBehaviour injection")
                .GetInstantiator();
        }

        internal void CompleteInjection()
        {
            if (!_isPreparingInjection)
                return;

            _isPreparingInjection = false;
            _isInjected = true;
            EnterInjectedActive();
        }

        internal void AbortInjection(IScope scope, bool invokePartialCleanup)
        {
            if (!ReferenceEquals(_cachedScope, scope) || _isEndingInjection)
                return;

            _isEndingInjection = true;
            try
            {
                try { ExitInjectedActive(); }
                catch (System.Exception ex) { Debug.LogException(ex); }
                try { SealActiveBucketForInjectionEnd(); }
                catch (System.Exception ex) { Debug.LogException(ex); }
                if (invokePartialCleanup)
                {
                    try
                    {
                        using (ActivationCallbackGuard.EnterLifecycle())
                            InvokeLifecycleCallback(OnBeforeUninject);
                    }
                    catch (System.Exception ex) { Debug.LogException(ex); }
                }
                try
                {
                    using (ActivationCallbackGuard.EnterLifecycle())
                        _injectionDisposables?.Dispose();
                }
                catch (System.Exception ex) { Debug.LogException(ex); }
            }
            finally
            {
                _injectionDisposables = new CompositeDisposable();
                _isInjectedActive = false;
                try { SealActiveBucketForInjectionEnd(); }
                catch (System.Exception ex) { Debug.LogException(ex); }
                _instantiator = null;
                _cachedScope = null;
                _isPreparingInjection = false;
                _isInjected = false;
                _isEndingInjection = false;
                PublishFreshBucketAfterInjectionEnd();
            }
        }

        internal void RevokeInjection(IScope scope)
        {
            if (!ReferenceEquals(_cachedScope, scope) || _isEndingInjection)
                return;

            System.Exception firstError = null;
            _isEndingInjection = true;
            try
            {
                try { ExitInjectedActive(); }
                catch (System.Exception ex) { firstError = ex; }
                try { SealActiveBucketForInjectionEnd(); }
                catch (System.Exception ex) when (firstError == null) { firstError = ex; }
                catch (System.Exception ex) { Debug.LogException(ex); }
                try
                {
                    using (ActivationCallbackGuard.EnterLifecycle())
                        InvokeLifecycleCallback(OnBeforeUninject);
                }
                catch (System.Exception ex) when (firstError == null)
                {
                    firstError = ex;
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                }

                try
                {
                    using (ActivationCallbackGuard.EnterLifecycle())
                        _injectionDisposables?.Dispose();
                }
                catch (System.Exception ex) when (firstError == null) { firstError = ex; }
                catch (System.Exception ex) { Debug.LogException(ex); }
            }
            finally
            {
                _injectionDisposables = new CompositeDisposable();
                _isInjectedActive = false;
                try { SealActiveBucketForInjectionEnd(); }
                catch (System.Exception ex) when (firstError == null) { firstError = ex; }
                catch (System.Exception ex) { Debug.LogException(ex); }
                _instantiator = null;
                _cachedScope = null;
                _isPreparingInjection = false;
                _isInjected = false;
                _isEndingInjection = false;
                PublishFreshBucketAfterInjectionEnd();
            }

            if (firstError != null) throw firstError;
        }

        private void EnterInjectedActive()
        {
            ReconcileInjectedActiveState();
        }

        private void ReconcileInjectedActiveState()
        {
            if (!_isInjected || _isEndingInjection)
                return;

            if (_isReconcilingActiveState)
            {
                _activeStateChangedDuringCallback = true;
                return;
            }

            _isReconcilingActiveState = true;
            var transitions = 0;
            try
            {
                while (true)
                {
                    _activeStateChangedDuringCallback = false;
                    var shouldBeActive = this != null && _isInjected && !_isEndingInjection && isActiveAndEnabled;
                    if (shouldBeActive && !_isInjectedActive)
                        EnterInjectedActiveInterval();
                    else if (!shouldBeActive && _isInjectedActive)
                        ExitInjectedActive();

                    shouldBeActive = this != null && _isInjected && !_isEndingInjection && isActiveAndEnabled;
                    if (!_activeStateChangedDuringCallback && shouldBeActive == _isInjectedActive)
                        break;
                    if (++transitions < 32) continue;

                    throw new InvalidOperationException(
                        $"[KDI] {GetType().Name} changed its active state repeatedly from injected lifecycle callbacks. " +
                        "The active subscription interval could not reach a stable state.");
                }
            }
            catch (Exception exception)
            {
                UnityActivationAttempt.ReportFailure(this, exception);
                throw;
            }
            finally
            {
                _activeStateChangedDuringCallback = false;
                _isReconcilingActiveState = false;
            }
        }

        private void EnterInjectedActiveInterval()
        {
            if (this == null || !_isInjected || _isInjectedActive || _isEndingInjection || !isActiveAndEnabled)
                return;

            try
            {
                using (ActivationCallbackGuard.EnterLifecycle())
                {
                    Dispose();
                    _isInjectedActive = true;
                    InvokeLifecycleCallback(OnInjectedEnable);
                }
            }
            catch (System.Exception activationException)
            {
                _isInjectedActive = false;
                UnityActivationAttempt.ReportFailure(this, activationException);
                try { Dispose(); }
                catch (System.Exception cleanupException) { Debug.LogException(cleanupException); }
                throw;
            }
        }

        private void ExitInjectedActive()
        {
            if (!_isInjectedActive)
                return;

            _isInjectedActive = false;
            Exception firstError = null;
            try
            {
                using (ActivationCallbackGuard.EnterLifecycle())
                    InvokeLifecycleCallback(OnInjectedDisable);
            }
            catch (Exception exception)
            {
                firstError = exception;
                UnityActivationAttempt.ReportFailure(this, exception);
            }

            try
            {
                using (ActivationCallbackGuard.EnterLifecycle())
                    Dispose();
            }
            catch (Exception exception)
            {
                UnityActivationAttempt.ReportFailure(this, exception);
                if (firstError == null)
                    firstError = exception;
                else
                    Debug.LogException(exception);
            }

            if (firstError != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
        }

        private void SealActiveBucketForInjectionEnd()
        {
            // Dispose() deliberately leaves the terminal bucket installed while
            // _isEndingInjection is true.
            using (ActivationCallbackGuard.EnterLifecycle())
                Dispose();
        }

        private static CompositeDisposable CreateTerminalActiveBucket()
        {
            var terminal = new CompositeDisposable();
            terminal.Dispose();
            return terminal;
        }

        private void InvokeLifecycleCallback(Action callback)
        {
            checked { _lifecycleCallbackDepth++; }
            try
            {
                callback();
            }
            finally
            {
                _lifecycleCallbackDepth--;
                if (_lifecycleCallbackDepth == 0 && _publishFreshBucketAfterCallback && !_isEndingInjection)
                {
                    _publishFreshBucketAfterCallback = false;
                    _cd = new CompositeDisposable();
                }
            }
        }

        private void PublishFreshBucketAfterInjectionEnd()
        {
            if (_lifecycleCallbackDepth > 0)
            {
                // Revoke can run synchronously from DestroyImmediate inside a lifecycle
                // callback. Keep the disposed terminal bucket published until that
                // original callback frame returns, so code after DestroyImmediate cannot
                // leak subscriptions into a fresh interval that no longer exists.
                _publishFreshBucketAfterCallback = true;
                return;
            }

            _publishFreshBucketAfterCallback = false;
            _cd = new CompositeDisposable();
        }

        private void ValidateLifecycleMessages()
        {
            for (var type = GetType(); type != null && type != typeof(DIBehaviour); type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                if (DeclaresParameterless(type, nameof(OnEnable), flags) ||
                    DeclaresParameterless(type, nameof(OnDisable), flags))
                {
                    throw new InvalidOperationException(
                        $"[KDI] {type.FullName} declares OnEnable/OnDisable and bypasses DIBehaviour's lifecycle. " +
                        "Move subscription logic to OnInjectedEnable/OnInjectedDisable instead.");
                }
            }
        }

        private static bool DeclaresParameterless(Type type, string name, BindingFlags flags)
        {
            var methods = type.GetMember(name, MemberTypes.Method, flags);
            for (var i = 0; i < methods.Length; i++)
            {
                if (methods[i] is MethodInfo method && method.GetParameters().Length == 0)
                    return true;
            }
            return false;
        }
    }
}
