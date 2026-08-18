using System;

namespace Kylin.DI
{
    /// <summary>
    /// Marks framework-owned user callbacks that run outside an ordinary Scope transaction.
    /// All callback modes reject service location and manual player-loop mutation. Configure
    /// additionally rejects operations that could commit or destroy an independent graph.
    /// </summary>
    internal static class ActivationCallbackGuard
    {
        [ThreadStatic]
        private static int _depth;

        [ThreadStatic]
        private static int _configureDepth;

        internal static bool IsActive => _depth > 0;
        internal static bool IsConfiguring => _configureDepth > 0;

        internal static IDisposable EnterLifecycle()
        {
            checked { _depth++; }
            return new Lease(false);
        }

        internal static IDisposable EnterConfigure()
        {
            checked
            {
                _depth++;
                _configureDepth++;
            }
            return new Lease(true);
        }

        internal static void ThrowIfConfigureMutation(string operation)
        {
            if (!IsConfiguring) return;
            throw new InvalidOperationException(
                $"[KDI] {operation} cannot run from LifetimeScope.Configure. Configure may only describe " +
                "registrations on the supplied ScopeBuilder; committing, injecting, instantiating, initializing, " +
                "or disposing another graph would escape Configure failure rollback.");
        }

        internal static void ResetStatic()
        {
            _depth = 0;
            _configureDepth = 0;
        }

        private sealed class Lease : IDisposable
        {
            private readonly bool _isConfigure;
            private bool _isDisposed;

            internal Lease(bool isConfigure)
            {
                _isConfigure = isConfigure;
            }

            public void Dispose()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                if (_isConfigure && _configureDepth > 0)
                    _configureDepth--;
                if (_depth > 0)
                    _depth--;
            }
        }
    }
}
