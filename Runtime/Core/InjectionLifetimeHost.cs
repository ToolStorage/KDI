using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Kylin.DI
{
    internal interface IUnityLifetimeMonitorLease
    {
        bool IsTargetDestroyed { get; }
        void HandleMonitoredTargetDestroyed();
        void RebindMonitor(UpdateLoopManager previous, UpdateLoopManager replacement);
    }

    /// <summary>
    /// Connects injection leases to a Component's actual Unity lifetime. Scope remains
    /// the fallback owner, while destruction can release the lease without waiting for
    /// a long-lived parent Scope to shut down.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    internal sealed class InjectionLifetimeHost : MonoBehaviour
    {
        private readonly List<Entry> _entries = new();
        private readonly List<UnityServiceEntry> _serviceEntries = new();

        internal void Track(Scope owner, InjectionLease lease)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (ReferenceEquals(_entries[i].Lease, lease)) return;
            }

            hideFlags |= HideFlags.HideInInspector;
            _entries.Add(new Entry(owner, lease));
        }

        internal void Detach(InjectionLease lease)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_entries[i].Lease, lease))
                    _entries.RemoveAt(i);
            }
        }

        internal void Track(Scope owner, UnityServiceLifetimeLease lease)
        {
            for (var i = 0; i < _serviceEntries.Count; i++)
            {
                if (ReferenceEquals(_serviceEntries[i].Lease, lease)) return;
            }

            hideFlags |= HideFlags.HideInInspector;
            _serviceEntries.Add(new UnityServiceEntry(owner, lease));
        }

        internal void Detach(UnityServiceLifetimeLease lease)
        {
            for (var i = _serviceEntries.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_serviceEntries[i].Lease, lease))
                    _serviceEntries.RemoveAt(i);
            }
        }

        private void OnDestroy()
        {
            var injectionSnapshot = _entries.ToArray();
            var serviceSnapshot = _serviceEntries.ToArray();
            _entries.Clear();
            _serviceEntries.Clear();
            for (var i = injectionSnapshot.Length - 1; i >= 0; i--)
                injectionSnapshot[i].Owner?.ReleaseInjectionLease(injectionSnapshot[i].Lease);
            for (var i = serviceSnapshot.Length - 1; i >= 0; i--)
                serviceSnapshot[i].Owner?.ReleaseUnityServiceLifetime(serviceSnapshot[i].Lease);
        }

        private readonly struct Entry
        {
            internal readonly Scope Owner;
            internal readonly InjectionLease Lease;

            internal Entry(Scope owner, InjectionLease lease)
            {
                Owner = owner;
                Lease = lease;
            }
        }

        private readonly struct UnityServiceEntry
        {
            internal readonly Scope Owner;
            internal readonly UnityServiceLifetimeLease Lease;

            internal UnityServiceEntry(Scope owner, UnityServiceLifetimeLease lease)
            {
                Owner = owner;
                Lease = lease;
            }
        }
    }

    /// <summary>
    /// Evicts a cached Unity component when that component is destroyed independently
    /// of its owning Scope. This also covers services that do not participate in
    /// IInjectable and therefore have no InjectionLease of their own.
    /// </summary>
    internal sealed class UnityServiceLifetimeLease : IDisposable, IUnityLifetimeMonitorLease
    {
        private Scope _owner;
        private UnityEngine.Object _target;
        private InjectionLifetimeHost _host;
        private UpdateLoopManager _monitor;
        private CancellationTokenRegistration _destroyRegistration;
        private bool _hasDestroyRegistration;
        internal bool RequiresPolling { get; }

        private UnityServiceLifetimeLease(Scope owner, UnityEngine.Object target)
        {
            _owner = owner;
            _target = target;
            RequiresPolling = !(target is MonoBehaviour) && !(target is GameObject);
        }

        public bool IsTargetDestroyed => !ReferenceEquals(_target, null) && _target == null;

        internal static UnityServiceLifetimeLease Attach(Scope owner, object instance)
        {
            Component component = null;
            GameObject gameObject = null;
            UnityEngine.Object target;
            if (instance is Component candidate)
            {
                if (candidate == null) return null;
                component = candidate;
                gameObject = candidate.gameObject;
                target = candidate;
            }
            else if (instance is GameObject candidateGameObject)
            {
                if (candidateGameObject == null) return null;
                gameObject = candidateGameObject;
                target = candidateGameObject;
            }
            else if (instance is UnityEngine.Object candidateObject)
            {
                if (candidateObject == null) return null;
                target = candidateObject;
            }
            else
            {
                return null;
            }

            var lease = new UnityServiceLifetimeLease(owner, target);
            try
            {
                if (component is MonoBehaviour monoBehaviour)
                {
                    lease._hasDestroyRegistration = true;
                    lease._destroyRegistration = monoBehaviour.destroyCancellationToken.Register(
                        lease.HandleUnityDestroyed);
                }

                if (gameObject != null)
                {
                    var host = gameObject.GetComponent<InjectionLifetimeHost>();
                    if (host == null)
                        host = gameObject.AddComponent<InjectionLifetimeHost>();
                    lease._host = host;
                    host.Track(owner, lease);
                }

                // MonoBehaviours have a destruction token and GameObjects are covered
                // by the host. Poll only targets whose individual destruction has no
                // callback so ordinary prefab-heavy scenes do not enlarge the hot path.
                var monitor = lease.RequiresPolling ? UpdateLoopManager.GetOrCreateForKDI() : null;
                if (monitor != null)
                {
                    lease._monitor = monitor;
                    monitor.RegisterUnityLifetime(lease);
                }
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _owner = null;
            _target = null;
            var monitor = _monitor;
            _monitor = null;
            if (monitor != null)
                monitor.UnregisterUnityLifetime(this);
            if (_hasDestroyRegistration)
            {
                _hasDestroyRegistration = false;
                _destroyRegistration.Dispose();
                _destroyRegistration = default;
            }

            var host = _host;
            _host = null;
            if (host != null)
                host.Detach(this);
        }

        private void HandleUnityDestroyed()
        {
            // Dispose on a CancellationTokenRegistration may wait for its own callback.
            _hasDestroyRegistration = false;
            _destroyRegistration = default;
            var owner = _owner;
            owner?.ReleaseUnityServiceLifetime(this);
        }

        public void HandleMonitoredTargetDestroyed()
        {
            var owner = _owner;
            owner?.ReleaseUnityServiceLifetime(this);
        }

        public void RebindMonitor(UpdateLoopManager previous, UpdateLoopManager replacement)
        {
            if (ReferenceEquals(_monitor, previous))
                _monitor = replacement;
        }
    }
}
