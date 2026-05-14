using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Kylin.SubscribableProperty
{
    public enum CollectionChangeType
        {
            Add,
            Remove,
            Replace,
            Move,
            Clear,
            Initialize,
        }

        [Serializable]
        public struct CollectionChangeEvent<T>
        {
            public CollectionChangeType Type { get; } // 변경 유형
            public int Index { get; } // 변경이 발생한 인덱스
            public T OldValue { get; } // 제거되거나 교체되서 삭제된 값
            public T NewValue { get; } // 추가되거나 교체되서 추가된 값
            public int OldIndex { get; } // Move 시 이전 인덱스
            public int NewIndex { get; } // Move 시 새로운 인덱스

            private CollectionChangeEvent(CollectionChangeType type, int index, T oldValue, T newValue, int oldIndex = -1, int newIndex = -1)
            {
                Type = type;
                Index = index;
                OldValue = oldValue;
                NewValue = newValue;
                OldIndex = oldIndex;
                NewIndex = newIndex;
            }

            public static CollectionChangeEvent<T> Initialize(int index, T value)
                => new CollectionChangeEvent<T>(CollectionChangeType.Initialize, index, default, value);
            public static CollectionChangeEvent<T> Add(int index, T value)
                => new CollectionChangeEvent<T>(CollectionChangeType.Add, index, default, value);

            public static CollectionChangeEvent<T> Remove(int index, T value)
                => new CollectionChangeEvent<T>(CollectionChangeType.Remove, index, value, default);

            public static CollectionChangeEvent<T> Replace(int index, T oldValue, T newValue)
                => new CollectionChangeEvent<T>(CollectionChangeType.Replace, index, oldValue, newValue);

            public static CollectionChangeEvent<T> Move(T value, int oldIndex, int newIndex)
                => new CollectionChangeEvent<T>(CollectionChangeType.Move, newIndex, value, value, oldIndex, newIndex);

            public static CollectionChangeEvent<T> Clear()
                => new CollectionChangeEvent<T>(CollectionChangeType.Clear, -1, default, default);
        }
    public interface IReadOnlySubscribableCollection<T> : IReadOnlyCollection<T>
    {
        public int IndexOf(T item);
        public bool Contains(T item);
        public void CopyTo(T[] array, int arrayIndex);
        public T Find(Predicate<T> match);
        public bool Exists(Predicate<T> match);
        public IEnumerator<T> GetEnumerator();

        public int Count { get; }

        public bool IsReadOnly{ get; }

        T this[int index] { get; }
        /// <summary>
        /// invo
        /// </summary>
        /// <param name="onChanged"> 각 타입별 지정을 해줄것 </param>
        /// <param name="invokeForExisting"> true로 지정하면 지금 존재하는 collection을 foreach문으로 돌아가면서 실행 (Initialize타입)</param>
        /// <returns></returns>
        IDisposable Subscribe(Action<CollectionChangeEvent<T>> onChanged, bool invokeForExisting = false);

        IDisposable SubscribeCount(Action<int> onCountChanged, bool invokeInitial = false);
        IDisposable SubscribeAdd(Action<int, T> onAdd);
    }

    public interface ISubscribableCollection<T> : IReadOnlySubscribableCollection<T>, ICollection<T>
    {
        new T this[int index] { get; set; }
        void Insert(int index, T item);
        void RemoveAt(int index);
        void Move(int oldIndex, int newIndex);

    }
    [Serializable]
    public class SubscribableCollection<T> : ISubscribableCollection<T>, ISerializationCallbackReceiver, ISubscribablePending
    {
        [SerializeField]
        private List<T> _items = new List<T>();

        private event Action<CollectionChangeEvent<T>> _collectionChanged;
        private event Action<int> _countChanged;

        // 트랜잭션 모드 pending 상태. 변경 이벤트는 순서를 보존해주기 위해 리스트 사용.
        [NonSerialized] private List<CollectionChangeEvent<T>> _pendingChanges;
        [NonSerialized] private bool _hasPendingCountChange;

        public SubscribableCollection()
        {
        }

        public SubscribableCollection(IEnumerable<T> collection)
        {
            if (collection != null)
            {
                _items.AddRange(collection);
            }
        }

        public SubscribableCollection(int capacity)
        {
            _items = new List<T>(capacity);
        }

        public T this[int index]
        {
            get => _items[index];
            set
            {
                var oldValue = _items[index];
                if (!EqualityComparer<T>.Default.Equals(oldValue, value))
                {
                    _items[index] = value;
                    NotifyCollectionChanged(CollectionChangeEvent<T>.Replace(index, oldValue, value));
                }
            }
        }

        public int Count => _items.Count;
        public bool IsReadOnly => false;

        public void Add(T item)
        {
            var index = _items.Count;
            _items.Add(item);
            NotifyCollectionChanged(CollectionChangeEvent<T>.Add(index, item));
            NotifyCountChanged();
        }

        public void Insert(int index, T item)
        {
            _items.Insert(index, item);
            NotifyCollectionChanged(CollectionChangeEvent<T>.Add(index, item));
            NotifyCountChanged();
        }

        public bool Remove(T item)
        {
            var index = _items.IndexOf(item);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }
            return false;
        }

        public void RemoveAt(int index)
        {
            var item = _items[index];
            _items.RemoveAt(index);
            NotifyCollectionChanged(CollectionChangeEvent<T>.Remove(index, item));
            NotifyCountChanged();
        }

        public void Clear()
        {
            if (_items.Count > 0)
            {
                _items.Clear();
                NotifyCollectionChanged(CollectionChangeEvent<T>.Clear());
                NotifyCountChanged();
            }
        }

        public int IndexOf(T item) => _items.IndexOf(item);
        public bool Contains(T item) => _items.Contains(item);

        public T Find(Predicate<T> match) => _items.Find(match);
        public bool Exists(Predicate<T> match) => _items.Exists(match);
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();


        public void AddRange(IEnumerable<T> collection)
        {
            if (collection == null) return;

            foreach (var item in collection)
            {
                Add(item); // 각각 개별 이벤트 발생
            }
        }

        public void Move(int oldIndex, int newIndex)
        {
            if (oldIndex == newIndex) return;

            var item = _items[oldIndex];
            _items.RemoveAt(oldIndex);
            _items.Insert(newIndex, item);
            NotifyCollectionChanged(CollectionChangeEvent<T>.Move(item, oldIndex, newIndex));
        }

        public List<T> ToList() => new List<T>(_items);
        public T[] ToArray() => _items.ToArray();


        public IDisposable Subscribe(Action<CollectionChangeEvent<T>> onChanged, bool invokeForCurrentItems = false)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));

            if (invokeForCurrentItems)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    onChanged(CollectionChangeEvent<T>.Initialize(i, _items[i]));
                }
            }

            _collectionChanged += onChanged;
            return new Disposable(() => _collectionChanged -= onChanged);
        }

        public IDisposable SubscribeCount(Action<int> onCountChanged, bool invokeInitial = false)
        {
            if (onCountChanged == null) throw new ArgumentNullException(nameof(onCountChanged));

            if (invokeInitial)
                onCountChanged(Count);

            _countChanged += onCountChanged;
            return new Disposable(() => _countChanged -= onCountChanged);
        }

        public IDisposable SubscribeAdd(Action<int, T> onAdd)
        {
            return Subscribe(change =>
            {
                if (change.Type == CollectionChangeType.Add || change.Type == CollectionChangeType.Initialize)
                    onAdd(change.Index, change.NewValue);
            });
        }

        public IDisposable SubscribeRemove(Action<int, T> onRemove)
        {
            return Subscribe(change =>
            {
                if (change.Type == CollectionChangeType.Remove)
                    onRemove(change.Index, change.OldValue);
            });
        }


        private void NotifyCollectionChanged(CollectionChangeEvent<T> changeEvent)
        {
            if (Reaction.IsActive)
            {
                _pendingChanges ??= new List<CollectionChangeEvent<T>>();
                _pendingChanges.Add(changeEvent);
                Reaction.RegisterPending(this);
            }
            else
            {
                _collectionChanged?.Invoke(changeEvent);
            }
        }

        private void NotifyCountChanged()
        {
            if (Reaction.IsActive)
            {
                _hasPendingCountChange = true;
                Reaction.RegisterPending(this);
            }
            else
            {
                _countChanged?.Invoke(Count);
            }
        }

        void ISubscribablePending.FlushPendingNotification()
        {
            // 변경 이벤트: 트랜잭션 내 발생 순서대로 invoke (Add/Remove 시퀀스 의미 보존).
            // List 는 영구 보유 + Clear() 재사용 -- 매 트랜잭션 alloc 0.
            if (_pendingChanges != null && _pendingChanges.Count > 0)
            {
                int count = _pendingChanges.Count;
                for (int i = 0; i < count; i++)
                {
                    _collectionChanged?.Invoke(_pendingChanges[i]);
                }
                _pendingChanges.Clear();
            }

            // Count 변경: 트랜잭션 내 다중 발생을 합쳐 *최종 Count* 로 1회만 invoke.
            if (_hasPendingCountChange)
            {
                _hasPendingCountChange = false;
                _countChanged?.Invoke(Count);
            }
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            // 역직렬화 후 이벤트 재설정 (필요시)
        }


        private class Disposable : IDisposable
        {
            private readonly Action _onDispose;
            public Disposable(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose?.Invoke();
        }
    }

}
