using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kylin.SubscribableProperty
{
    [Serializable]
    public struct DictionaryChangeEvent<TKey, TValue>
    {
        public CollectionChangeType Type { get; }
        public TKey Key { get; }
        public TValue OldValue { get; }
        public TValue NewValue { get; }

        private DictionaryChangeEvent(CollectionChangeType type, TKey key, TValue oldValue, TValue newValue)
        {
            Type = type;
            Key = key;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public static DictionaryChangeEvent<TKey, TValue> Add(TKey key, TValue value)
            => new DictionaryChangeEvent<TKey, TValue>(CollectionChangeType.Add, key, default, value);

        public static DictionaryChangeEvent<TKey, TValue> Remove(TKey key, TValue value)
            => new DictionaryChangeEvent<TKey, TValue>(CollectionChangeType.Remove, key, value, default);

        public static DictionaryChangeEvent<TKey, TValue> Replace(TKey key, TValue oldValue, TValue newValue)
            => new DictionaryChangeEvent<TKey, TValue>(CollectionChangeType.Replace, key, oldValue, newValue);

        public static DictionaryChangeEvent<TKey, TValue> Clear()
            => new DictionaryChangeEvent<TKey, TValue>(CollectionChangeType.Clear, default, default, default);
    }
    public interface IReadOnlySubscribableDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    {
        // 구독 메서드들
        IDisposable Subscribe(Action<DictionaryChangeEvent<TKey, TValue>> onChanged, bool invokeForCurrentItems = false);
        IDisposable SubscribeCount(Action<int> onCountChanged, bool invokeInitial = false);
        IDisposable SubscribeAdd(Action<TKey, TValue> onAdd);
        IDisposable SubscribeRemove(Action<TKey, TValue> onRemove);
        IDisposable SubscribeReplace(Action<TKey, TValue, TValue> onReplace);
    }
    public interface ISubscribableDictionary<TKey, TValue> : IReadOnlySubscribableDictionary<TKey, TValue>, IDictionary<TKey, TValue>
    {
    }
    [Serializable]
    public class SubscribableDictionary<TKey, TValue> : ISubscribableDictionary<TKey, TValue>, ISerializationCallbackReceiver, ISubscribablePending
    {
        [SerializeField]
        private List<TKey> _keys = new List<TKey>();

        [SerializeField]
        private List<TValue> _values = new List<TValue>();

        private Dictionary<TKey, TValue> _dictionary = new Dictionary<TKey, TValue>();

        private event Action<DictionaryChangeEvent<TKey, TValue>> _dictionaryChanged;
        private event Action<int> _countChanged;

        [NonSerialized] private List<DictionaryChangeEvent<TKey, TValue>> _pendingChanges;
        [NonSerialized] private bool _hasPendingCountChange;

        public SubscribableDictionary()
        {
        }

        public SubscribableDictionary(IDictionary<TKey, TValue> dictionary)
        {
            if (dictionary != null)
            {
                foreach (var kvp in dictionary)
                {
                    _dictionary[kvp.Key] = kvp.Value;
                }
                FullSyncToSerializedLists();
            }
        }

        public SubscribableDictionary(int capacity)
        {
            _dictionary = new Dictionary<TKey, TValue>(capacity);
        }

        #region IDictionary<TKey, TValue> Implementation

        public TValue this[TKey key]
        {
            get => _dictionary[key];
            set
            {
                if (_dictionary.TryGetValue(key, out var oldValue))
                {
                    if (!EqualityComparer<TValue>.Default.Equals(oldValue, value))
                    {
                        _dictionary[key] = value;
                        UpdateSerializedValue(key, value);
                        NotifyDictionaryChanged(DictionaryChangeEvent<TKey, TValue>.Replace(key, oldValue, value));
                    }
                }
                else
                {
                    Add(key, value);
                }
            }
        }

        public ICollection<TKey> Keys => _dictionary.Keys;
        public ICollection<TValue> Values => _dictionary.Values;
        public int Count => _dictionary.Count;
        public bool IsReadOnly => false;

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        public void Add(TKey key, TValue value)
        {
            _dictionary.Add(key, value);
            AddToSerializedLists(key, value);
            NotifyDictionaryChanged(DictionaryChangeEvent<TKey, TValue>.Add(key, value));
            NotifyCountChanged();
        }

        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

        public bool Remove(TKey key)
        {
            if (_dictionary.TryGetValue(key, out var value))
            {
                _dictionary.Remove(key);
                RemoveFromSerializedLists(key);
                NotifyDictionaryChanged(DictionaryChangeEvent<TKey, TValue>.Remove(key, value));
                NotifyCountChanged();
                return true;
            }
            return false;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            if (_dictionary.TryGetValue(item.Key, out var value) &&
                EqualityComparer<TValue>.Default.Equals(value, item.Value))
            {
                return Remove(item.Key);
            }
            return false;
        }

        public void Clear()
        {
            if (_dictionary.Count > 0)
            {
                _dictionary.Clear();
                ClearSerializedLists();
                NotifyDictionaryChanged(DictionaryChangeEvent<TKey, TValue>.Clear());
                NotifyCountChanged();
            }
        }

        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);
        public bool Contains(KeyValuePair<TKey, TValue> item) => _dictionary.Contains(item);
        public bool TryGetValue(TKey key, out TValue value) => _dictionary.TryGetValue(key, out value);

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ((IDictionary<TKey, TValue>)_dictionary).CopyTo(array, arrayIndex);
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion

        #region Subscription Methods

        public IDisposable Subscribe(Action<DictionaryChangeEvent<TKey, TValue>> onChanged, bool invokeForCurrentItems = false)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));

            if (invokeForCurrentItems)
            {
                foreach (var kvp in _dictionary)
                {
                    onChanged(DictionaryChangeEvent<TKey, TValue>.Add(kvp.Key, kvp.Value));
                }
            }

            _dictionaryChanged += onChanged;
            return new Disposable(() => _dictionaryChanged -= onChanged);
        }

        public IDisposable SubscribeCount(Action<int> onCountChanged, bool invokeInitial = false)
        {
            if (onCountChanged == null) throw new ArgumentNullException(nameof(onCountChanged));

            if (invokeInitial)
                onCountChanged(Count);

            _countChanged += onCountChanged;
            return new Disposable(() => _countChanged -= onCountChanged);
        }

        public IDisposable SubscribeAdd(Action<TKey, TValue> onAdd)
        {
            return Subscribe(change =>
            {
                if (change.Type == CollectionChangeType.Add)
                    onAdd(change.Key, change.NewValue);
            });
        }

        public IDisposable SubscribeRemove(Action<TKey, TValue> onRemove)
        {
            return Subscribe(change =>
            {
                if (change.Type == CollectionChangeType.Remove)
                    onRemove(change.Key, change.OldValue);
            });
        }
        public IDisposable SubscribeReplace(Action<TKey, TValue, TValue> onReplace)
        {
            return Subscribe(change =>
            {
                if (change.Type == CollectionChangeType.Replace)
                    onReplace(change.Key, change.OldValue, change.NewValue);
            });
        }
        #endregion

        private void NotifyDictionaryChanged(DictionaryChangeEvent<TKey, TValue> changeEvent)
        {
            if (Reaction.IsActive)
            {
                _pendingChanges ??= new List<DictionaryChangeEvent<TKey, TValue>>();
                _pendingChanges.Add(changeEvent);
                Reaction.RegisterPending(this);
            }
            else
            {
                _dictionaryChanged?.Invoke(changeEvent);
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
            if (_pendingChanges != null && _pendingChanges.Count > 0)
            {
                int count = _pendingChanges.Count;
                for (int i = 0; i < count; i++)
                {
                    _dictionaryChanged?.Invoke(_pendingChanges[i]);
                }
                _pendingChanges.Clear();
            }

            if (_hasPendingCountChange)
            {
                _hasPendingCountChange = false;
                _countChanged?.Invoke(Count);
            }
        }

        #region Optimized Serialization Support

        private void AddToSerializedLists(TKey key, TValue value)
        {
            _keys.Add(key);
            _values.Add(value);
        }

        private void UpdateSerializedValue(TKey key, TValue newValue)
        {
            var index = _keys.IndexOf(key);
            if (index >= 0)
            {
                _values[index] = newValue;
            }
            else
            {
                AddToSerializedLists(key, newValue);
            }
        }

        private void RemoveFromSerializedLists(TKey key)
        {
            var index = _keys.IndexOf(key);
            if (index >= 0)
            {
                _keys.RemoveAt(index);
                _values.RemoveAt(index);
            }
        }

        private void ClearSerializedLists()
        {
            _keys.Clear();
            _values.Clear();
        }

        private void FullSyncToSerializedLists()
        {
            _keys.Clear();
            _values.Clear();

            foreach (var kvp in _dictionary)
            {
                _keys.Add(kvp.Key);
                _values.Add(kvp.Value);
            }
        }

        public void OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            _dictionary.Clear();

            for (int i = 0; i < _keys.Count && i < _values.Count; i++)
            {
                _dictionary[_keys[i]] = _values[i];
            }
        }

        #endregion

        private class Disposable : IDisposable
        {
            private readonly Action _onDispose;
            public Disposable(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose?.Invoke();
        }
    }
}
