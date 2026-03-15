using System;
using System.Collections.Generic;
using MessagePack;
using UnityEngine;

namespace Kylin.SubscribableProperty
{
    public interface IReadOnlySubscribableProperty<T>
    {
        T Value { get; }
        IDisposable Subscribe(Action<T> onNext, bool invokeInitial = false);
    }

    public interface ISubscribableProperty<T> : IReadOnlySubscribableProperty<T>
    {
        new T Value { get; set; }
    }
    [Serializable]
    [MessagePackObject(true)]
    public partial class SubscribableProperty<T> : ISubscribableProperty<T>, ISerializationCallbackReceiver
    {
        [MessagePack.Key(0)]
        [SerializeField]
        private T value;

        public SubscribableProperty()
        {
            OnPropertyCreated();
        }

        public SubscribableProperty(T initValue)
        {
            value = initValue;
            OnPropertyCreated();
        }

        public event Action<T> ValueChanged;

        public T Value
        {
            get => value;
            set
            {
                if (!EqualityComparer<T>.Default.Equals(this.value, value))
                {
                    this.value = value;
                    ValueChanged?.Invoke(this.value);
                }
            }
        }

        public IDisposable Subscribe(Action<T> onNext, bool invokeInitial = false)
        {
            if (onNext == null) throw new ArgumentNullException(nameof(onNext));

            if (invokeInitial)
                onNext(Value);

            ValueChanged += onNext;
            OnSubscribe(onNext);

            return new Disposable(() =>
            {
                ValueChanged -= onNext;
                OnUnsubscribe(onNext);
            });
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize()
            => ValueChanged?.Invoke(value);

        partial void OnPropertyCreated();
        partial void OnSubscribe(Action<T> onNext);
        partial void OnUnsubscribe(Action<T> onNext);

        private class Disposable : IDisposable
        {
            private readonly Action _onDispose;
            public Disposable(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }

    public class ReadOnlySubscribableProperty<T> : IReadOnlySubscribableProperty<T>
    {
        private readonly ISubscribableProperty<T> _source;

        public ReadOnlySubscribableProperty(ISubscribableProperty<T> source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public T Value => _source.Value;

        public IDisposable Subscribe(Action<T> onNext, bool invokeInitial = false)
        {
            return _source.Subscribe(onNext, invokeInitial);
        }
    }

}
