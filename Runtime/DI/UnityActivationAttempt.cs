using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kylin.DI
{
    /// <summary>
    /// Extends a prepared prefab's activation boundary past the Scope transaction
    /// that committed its ownership. Unity can log and swallow exceptions raised by
    /// Awake/OnEnable, so KDI lifecycle callbacks explicitly signal this attempt.
    /// Every Scope record committed while the attempt is current is retained as a
    /// compensation receipt until final activation succeeds.
    /// </summary>
    internal sealed class UnityActivationAttempt : IDisposable
    {
        [ThreadStatic]
        private static UnityActivationAttempt _current;

        private readonly UnityActivationAttempt _parent;
        private readonly List<Scope.ActivationRecord> _committedRecords = new();
        private readonly HashSet<Scope.ActivationRecord> _committedRecordSet = new();
        private readonly List<Scope> _constructedScopes = new();
        private readonly HashSet<Scope> _constructedScopeSet = new();
        private GameObject _root;
        private Exception _failure;
        private string _failureSource;
        private bool _isRollingBack;
        private bool _isCompleted;
        private bool _isRolledBack;
        private bool _isDisposed;

        private UnityActivationAttempt()
        {
            _parent = _current;
            _current = this;
        }

        internal static UnityActivationAttempt Begin()
        {
            KDI.EnsureMainThread();
            ThrowIfRollingBack();
            return new UnityActivationAttempt();
        }

        internal static bool HasActiveAttempt => _current != null;

        internal static bool IsRollingBack
        {
            get
            {
                for (var attempt = _current; attempt != null; attempt = attempt._parent)
                {
                    if (attempt._isRollingBack) return true;
                }
                return false;
            }
        }

        internal static void ResetStatic()
        {
            _current = null;
        }

        internal static void ThrowIfRollingBack()
        {
            for (var attempt = _current; attempt != null; attempt = attempt._parent)
            {
                if (!attempt._isRollingBack) continue;
                throw new InvalidOperationException(
                    "[KDI] Resolving, building a Scope, or starting another prefab activation from " +
                    "activation compensation cleanup is not allowed.");
            }
        }

        internal static void ReportFailure(UnityEngine.Object source, Exception exception)
        {
            if (exception == null) return;
            var attempt = _current;
            if (attempt == null || attempt._isRollingBack || attempt._isCompleted) return;
            if (attempt._failure != null) return;

            attempt._failure = exception;
            attempt._failureSource = Describe(source);
        }

        internal static void ReportUnexpectedRelease(Scope.ActivationRecord record, string releasedKind)
        {
            if (record == null || IsRollingBack) return;

            for (var attempt = _current; attempt != null; attempt = attempt._parent)
            {
                if (attempt._isCompleted || attempt._isRolledBack ||
                    !attempt._committedRecordSet.Contains(record)) continue;

                var typeName = record.Instance?.GetType().Name ?? "Unity object";
                attempt.LatchInvariantFailure(
                    releasedKind,
                    $"[KDI] Tracked {releasedKind} {typeName} was destroyed or released before final activation completed.");
            }
        }

        internal static void ReportUnexpectedScopeDisposal(Scope scope)
        {
            if (scope == null || IsRollingBack) return;

            for (var attempt = _current; attempt != null; attempt = attempt._parent)
            {
                if (attempt._isCompleted || attempt._isRolledBack ||
                    !attempt.TracksScopeOrOwnedRecord(scope)) continue;

                attempt.LatchInvariantFailure(
                    $"Scope '{scope.Name}'",
                    $"[KDI] Scope '{scope.Name}' was disposed before final activation completed.");
            }
        }

        internal static void TrackCommitted(Scope.ActivationRecord record)
        {
            if (record == null || record.IsReleased) return;
            for (var attempt = _current; attempt != null; attempt = attempt._parent)
            {
                if (attempt._isCompleted || attempt._isRolledBack ||
                    !attempt._committedRecordSet.Add(record)) continue;
                attempt._committedRecords.Add(record);
            }
        }

        internal static void TrackConstructedScope(Scope scope)
        {
            if (scope == null) return;
            for (var attempt = _current; attempt != null; attempt = attempt._parent)
            {
                if (attempt._isCompleted || attempt._isRolledBack ||
                    !attempt._constructedScopeSet.Add(scope)) continue;
                attempt._constructedScopes.Add(scope);
            }
        }

        internal void BindRoot(GameObject root)
        {
            if (!ReferenceEquals(_root, null) && !ReferenceEquals(_root, root))
                throw new InvalidOperationException("[KDI] An activation attempt cannot own more than one root clone.");
            _root = root;
        }

        internal void ThrowIfFailed()
        {
            ValidateExpectedState();
            if (_failure == null) return;
            var source = string.IsNullOrEmpty(_failureSource) ? "a KDI lifecycle callback" : _failureSource;
            throw new InvalidOperationException(
                $"[KDI] Final prefab activation failed in {source}. " +
                "The clone and every Scope side effect committed by this activation will be compensated.",
                _failure);
        }

        internal void Complete()
        {
            ThrowIfFailed();
            _isCompleted = true;
            ClearReceipts();
        }

        internal void Rollback()
        {
            if (_isCompleted || _isRolledBack) return;

            _isRollingBack = true;
            try
            {
                // OnDisable must run while injected fields and child Scopes are still
                // valid. The committed owned-GameObject record destroys the clone only
                // after the remaining records have been compensated in reverse order.
                if (_root != null && _root.activeSelf)
                {
                    try { _root.SetActive(false); }
                    catch (Exception exception) { Debug.LogException(exception); }
                }

                // A Scope can commit with no activation records at all. Track Scope
                // construction separately so a successful LifetimeScope awakened before
                // a later callback failure cannot survive the failed clone.
                for (var i = _constructedScopes.Count - 1; i >= 0; i--)
                {
                    var scope = _constructedScopes[i];
                    if (scope == null || scope.IsDisposed) continue;
                    try { scope.Dispose(); }
                    catch (Exception exception) { Debug.LogException(exception); }
                }

                Scope.RollbackCommittedActivationRecords(_committedRecords);
            }
            finally
            {
                _isRollingBack = false;
                _isRolledBack = true;
                ClearReceipts();
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try
            {
                if (!_isCompleted && !_isRolledBack)
                    Rollback();
            }
            finally
            {
                if (ReferenceEquals(_current, this))
                    _current = _parent;
            }
        }

        private void ClearReceipts()
        {
            _committedRecords.Clear();
            _committedRecordSet.Clear();
            _constructedScopes.Clear();
            _constructedScopeSet.Clear();
        }

        private void ValidateExpectedState()
        {
            if (_isRollingBack || _isCompleted || _isRolledBack || _failure != null) return;

            if (!ReferenceEquals(_root, null) && _root == null)
            {
                LatchInvariantFailure(
                    "the prefab root",
                    "[KDI] The prefab root was destroyed before final activation completed.");
                return;
            }

            for (var i = 0; i < _constructedScopes.Count; i++)
            {
                var scope = _constructedScopes[i];
                if (scope != null && !scope.IsDisposed) continue;
                LatchInvariantFailure(
                    scope == null ? "a constructed Scope" : $"Scope '{scope.Name}'",
                    "[KDI] A Scope constructed by final activation was disposed before the attempt completed.");
                return;
            }

            for (var i = 0; i < _committedRecords.Count; i++)
            {
                var record = _committedRecords[i];
                if (record == null) continue;
                if (!record.IsReleased && record.Owner.ContainsCommittedRecord(record) &&
                    (!(record.Instance is UnityEngine.Object unityObject) || unityObject != null)) continue;

                var typeName = record.Instance?.GetType().Name ?? "Unity object";
                LatchInvariantFailure(
                    typeName,
                    $"[KDI] Tracked activation record for {typeName} was released or destroyed before final activation completed.");
                return;
            }
        }

        private bool TracksScopeOrOwnedRecord(Scope scope)
        {
            if (_constructedScopeSet.Contains(scope)) return true;
            for (var i = 0; i < _committedRecords.Count; i++)
            {
                var record = _committedRecords[i];
                if (record != null && ReferenceEquals(record.Owner, scope)) return true;
            }
            return false;
        }

        private void LatchInvariantFailure(string source, string message)
        {
            if (_failure != null || _isRollingBack || _isCompleted || _isRolledBack) return;
            _failure = new InvalidOperationException(message);
            _failureSource = source;
        }

        private static string Describe(UnityEngine.Object source)
        {
            if (ReferenceEquals(source, null)) return null;
            try
            {
                return $"{source.GetType().Name} '{source.name}'";
            }
            catch
            {
                return source.GetType().Name;
            }
        }
    }
}
