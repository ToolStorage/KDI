using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Kylin.SubscribableProperty
{
    public static class SubscribablePropertyExtensions
    {
        public static T AddTo<T>(this T disposable, CompositeDisposable cd) where T : IDisposable
        {
            if (disposable == null || cd == null)
                return disposable;
            cd.Add(disposable);
            return disposable;
        }
    }

    public class CompositeDisposable : IDisposable
    {
        private readonly List<IDisposable> _disposables = new();
        public bool IsDisposed { get; private set; }
        public void Add(IDisposable d) => _disposables.Add(d);
        public void Dispose()
        {
            if (IsDisposed) return;
            foreach (var d in _disposables) d.Dispose();
            _disposables.Clear();
            IsDisposed = true;
        }

        public void Clear()
        {
            if (IsDisposed) return;
            foreach (var d in _disposables.ToArray()) d.Dispose();
            _disposables.Clear();
        }
    }
}
