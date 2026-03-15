namespace Kylin.DI
{
    /// <summary>
    /// DI 기반 FixedUpdate 루프 인터페이스
    /// - MonoBehaviour.FixedUpdate() 대신 사용
    /// - UpdateLoopManager에서 고정 시간 간격으로 호출
    /// </summary>
    public interface IFixedUpdatable
    {
        /// <summary>
        /// 고정 시간 간격으로 호출 (FixedUpdate와 동일한 타이밍)
        /// </summary>
        /// <param name="fixedDeltaTime">Time.fixedDeltaTime</param>
        void KDIFixedUpdate(float fixedDeltaTime);
    }
}
