using System;
using System.Collections.Generic;

namespace Kylin.SubscribableProperty
{
    public static class SubscribableCollectionExtensions
    {
        // /// <summary>
        // /// 컬렉션의 변경 사항을 다른 CompositeDisposable에 추가합니다.
        // /// </summary>
        // public static IDisposable AddTo<T>(this IReadOnlySubscribableCollection<T> collection,
        //     CompositeDisposable compositeDisposable,
        //     Action<CollectionChangeEvent<T>> onChanged,
        //     bool invokeForExisting = false)
        // {
        //     if (collection == null || compositeDisposable == null || onChanged == null)
        //         return null;
        //
        //     var subscription = collection.Subscribe(onChanged, invokeForExisting);
        //     compositeDisposable.Add(subscription);
        //     return subscription;
        // }
    }
}
