namespace Kylin.DI
{
    /// <summary>
    /// DI 기반 LateUpdate 루프 인터페이스
    /// - MonoBehaviour.LateUpdate() 대신 사용
    /// - UpdateLoopManager에서 Update 이후 호출
    /// </summary>
    public interface ILateUpdatable
    {
        /// <summary>
        /// Update 이후 호출 (LateUpdate와 동일한 타이밍)
        /// </summary>
        /// <param name="deltaTime">Time.deltaTime</param>
        void KDILateUpdate(float deltaTime);
    }
}
