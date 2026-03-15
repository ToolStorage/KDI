using System;

namespace Kylin.SubscribableProperty
{
    public interface ISubscribableCommand : IDisposable
    {
        IReadOnlySubscribableProperty<bool> CanExecute { get; }
        void Execute();
    }

    public class SubscribableCommand : ISubscribableCommand
    {
        private readonly Action _execute;
        private readonly CompositeDisposable _disposables = new CompositeDisposable();
        private bool _disposed;

        public IReadOnlySubscribableProperty<bool> CanExecute { get; }

        public SubscribableCommand(IReadOnlySubscribableProperty<bool> canExecute, Action execute)
        {
            CanExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public SubscribableCommand(Action execute)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            CanExecute = new SubscribableProperty<bool>(true);
        }

        public void Execute()
        {
            if (_disposed)
            {
                return;
            }

            if (CanExecute.Value)
            {
                _execute();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _disposables.Dispose();
        }
    }
}
