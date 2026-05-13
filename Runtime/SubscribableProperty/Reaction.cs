using System.Collections.Generic;

namespace Kylin.SubscribableProperty
{
    /// <summary>
    /// Subscribable프로퍼티의 구독자 invoke 를 한 트랜잭션 끝 시점으로 미루기 위한 단위
    /// </summary>
    public static class Reaction
    {
        private static int _depth;

        private static readonly List<ISubscribablePending> _pending = new(16);

        private static readonly List<ISubscribablePending> _flushBuffer = new(16);

        public static bool IsActive => _depth > 0;

        public static Handle Begin()
        {
            _depth++;
            return default;
        }

        /// 동일 인스턴스가 같은 트랜잭션 안에서 여러 번 등록되어도 1회만
        internal static void RegisterPending(ISubscribablePending sp)
        {
            if (sp == null) return;
            if (_depth == 0) return;
            if (!_pending.Contains(sp))
                _pending.Add(sp);
        }

        internal static void End()
        {
            _depth--;
            if (_depth > 0) return;

            if (_pending.Count == 0) return;

            _flushBuffer.Clear();
            for (int i = 0; i < _pending.Count; i++)
                _flushBuffer.Add(_pending[i]);
            _pending.Clear();

            for (int i = 0; i < _flushBuffer.Count; i++)
                _flushBuffer[i].FlushPendingNotification();

            _flushBuffer.Clear();
        }

        public readonly ref struct Handle
        {
            public void Dispose() => Reaction.End();
        }
    }

    internal interface ISubscribablePending
    {
        void FlushPendingNotification();
    }
}
