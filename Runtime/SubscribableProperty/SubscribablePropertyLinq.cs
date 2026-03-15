using System;

namespace Kylin.SubscribableProperty
{
    public static class SubscribablePropertyLinq
    {
        public static IReadOnlySubscribableProperty<TResult> Select<T, TResult>(this IReadOnlySubscribableProperty<T> source, Func<T, TResult> selector)
        {
            return new SelectSubscribableProperty<T, TResult>(source, selector);
        }

        public static IReadOnlySubscribableProperty<T> Where<T>(this IReadOnlySubscribableProperty<T> source, Func<T, bool> predicate)
        {
            return new WhereSubscribableProperty<T>(source, predicate);
        }
    }

    internal class SelectSubscribableProperty<TSource, TResult> : IReadOnlySubscribableProperty<TResult>, IDisposable
    {
        private readonly IReadOnlySubscribableProperty<TSource> _source;
        private readonly Func<TSource, TResult> _selector;
        private readonly SubscribableProperty<TResult> _property;
        private readonly IDisposable _subscription;

        public TResult Value => _property.Value;

        public SelectSubscribableProperty(IReadOnlySubscribableProperty<TSource> source, Func<TSource, TResult> selector)
        {
            _source = source;
            _selector = selector;
            _property = new SubscribableProperty<TResult>(_selector(_source.Value));
            _subscription = _source.Subscribe(OnSourceValueChanged, invokeInitial: false);
        }

        private void OnSourceValueChanged(TSource value)
        {
            _property.Value = _selector(value);
        }

        public IDisposable Subscribe(Action<TResult> onNext, bool invokeInitial = false)
        {
            return _property.Subscribe(onNext, invokeInitial);
        }

        public void Dispose()
        {
            _subscription?.Dispose();
        }
    }

    internal class WhereSubscribableProperty<T> : IReadOnlySubscribableProperty<T>, IDisposable
    {
        private readonly IReadOnlySubscribableProperty<T> _source;
        private readonly Func<T, bool> _predicate;
        private readonly SubscribableProperty<T> _property;
        private readonly IDisposable _subscription;

        public T Value => _property.Value;

        public WhereSubscribableProperty(IReadOnlySubscribableProperty<T> source, Func<T, bool> predicate)
        {
            _source = source;
            _predicate = predicate;
            _property = new SubscribableProperty<T>(source.Value);
            _subscription = _source.Subscribe(OnSourceValueChanged, invokeInitial: true);
        }

        private void OnSourceValueChanged(T value)
        {
            if (_predicate(value))
            {
                _property.Value = value;
            }
        }

        public IDisposable Subscribe(Action<T> onNext, bool invokeInitial = false)
        {
            return _property.Subscribe(onNext, invokeInitial);
        }

        public void Dispose()
        {
            _subscription?.Dispose();
        }
    }
}
