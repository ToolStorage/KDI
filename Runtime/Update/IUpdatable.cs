namespace Kylin.DI
{
    /// <summary>
    /// DI 기반 Update 루프 인터페이스
    /// - MonoBehaviour.Update() 대신 사용
    /// - UpdateLoopManager에서 매 프레임 호출
    /// </summary>
    public interface IUpdatable
    {
        /// <summary>
        /// 매 프레임 호출 (Update와 동일한 타이밍)
        /// </summary>
        /// <param name="deltaTime">Time.deltaTime</param>
        void KDIUpdate(float deltaTime);
    }
}
