namespace Kylin.DI
{
    /// <summary>
    /// Update 실행 순서 우선순위 인터페이스 (선택적)
    /// - 낮은 값이 먼저 실행됨
    /// - 기본값: 0
    /// </summary>
    public interface IUpdatePriority
    {
        /// <summary>
        /// 실행 우선순위 (낮을수록 먼저 실행)
        /// </summary>
        int UpdatePriority { get; }
    }
}
