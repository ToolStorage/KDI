using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kylin.DI
{
    public class UpdateLoopManager : MonoBehaviour
    {
        private static UpdateLoopManager _instance;
        private static bool _applicationQuitting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            _instance = null;
            _applicationQuitting = false;
        }

        public static UpdateLoopManager Instance
        {
            get
            {
                if (_applicationQuitting) return null;

                if (_instance == null)
                {
                    var go = new GameObject("[KDI] UpdateLoopManager");
                    _instance = go.AddComponent<UpdateLoopManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        /// <summary>
        /// 존재하는 인스턴스만 반환 (없으면 생성하지 않고 null).
        /// 씬 종료/Scope Dispose 경로에서 사용 — Instance getter는
        /// 파괴 시점에 새 GameObject를 만들어버릴 수 있다.
        /// </summary>
        internal static UpdateLoopManager TryGetInstance()
        {
            return _applicationQuitting ? null : _instance;
        }

        private List<IUpdatable> _updatables = new List<IUpdatable>();
        private List<IFixedUpdatable> _fixedUpdatables = new List<IFixedUpdatable>();
        private List<ILateUpdatable> _lateUpdatables = new List<ILateUpdatable>();

        private bool _updatablesDirty = false;
        private bool _fixedUpdatablesDirty = false;
        private bool _lateUpdatablesDirty = false;

        private Queue<Action> _pendingOperations = new Queue<Action>();
        private readonly object _lock = new object();

        public void Register(object service)
        {
            if (service == null) return;

            lock (_lock)
            {
                _pendingOperations.Enqueue(() =>
                {
                    if (service is IUpdatable updatable)
                    {
                        if (!_updatables.Contains(updatable))
                        {
                            _updatables.Add(updatable);
                            _updatablesDirty = true;
                        }
                    }

                    if (service is IFixedUpdatable fixedUpdatable)
                    {
                        if (!_fixedUpdatables.Contains(fixedUpdatable))
                        {
                            _fixedUpdatables.Add(fixedUpdatable);
                            _fixedUpdatablesDirty = true;
                        }
                    }

                    if (service is ILateUpdatable lateUpdatable)
                    {
                        if (!_lateUpdatables.Contains(lateUpdatable))
                        {
                            _lateUpdatables.Add(lateUpdatable);
                            _lateUpdatablesDirty = true;
                        }
                    }
                });
            }
        }

        public void Unregister(object service)
        {
            if (service == null) return;

            lock (_lock)
            {
                _pendingOperations.Enqueue(() =>
                {
                    if (service is IUpdatable updatable)
                    {
                        _updatables.Remove(updatable);
                    }

                    if (service is IFixedUpdatable fixedUpdatable)
                    {
                        _fixedUpdatables.Remove(fixedUpdatable);
                    }

                    if (service is ILateUpdatable lateUpdatable)
                    {
                        _lateUpdatables.Remove(lateUpdatable);
                    }
                });
            }
        }


        private void Update()
        {
            ProcessPendingOperations();

            if (_updatablesDirty)
            {
                SortByPriority(_updatables);
                _updatablesDirty = false;
            }

            float deltaTime = Time.deltaTime;
            for (int i = 0; i < _updatables.Count; i++)
            {
                try
                {
                    _updatables[i]?.KDIUpdate(deltaTime);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UpdateLoopManager] Error in KDIUpdate: {ex}");
                }
            }
        }

        private void FixedUpdate()
        {
            if (_fixedUpdatablesDirty)
            {
                SortByPriority(_fixedUpdatables);
                _fixedUpdatablesDirty = false;
            }

            float fixedDeltaTime = Time.fixedDeltaTime;
            for (int i = 0; i < _fixedUpdatables.Count; i++)
            {
                try
                {
                    _fixedUpdatables[i]?.KDIFixedUpdate(fixedDeltaTime);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UpdateLoopManager] Error in KDIFixedUpdate: {ex}");
                }
            }
        }

        private void LateUpdate()
        {
            if (_lateUpdatablesDirty)
            {
                SortByPriority(_lateUpdatables);
                _lateUpdatablesDirty = false;
            }

            float deltaTime = Time.deltaTime;
            for (int i = 0; i < _lateUpdatables.Count; i++)
            {
                try
                {
                    _lateUpdatables[i]?.KDILateUpdate(deltaTime);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UpdateLoopManager] Error in KDILateUpdate: {ex}");
                }
            }
        }

        private void ProcessPendingOperations()
        {
            lock (_lock)
            {
                while (_pendingOperations.Count > 0)
                {
                    var operation = _pendingOperations.Dequeue();
                    operation?.Invoke();
                }
            }
        }

        /// <summary>
        /// 우선순위에 따라 정렬
        /// </summary>
        private void SortByPriority<T>(List<T> list)
        {
            list.Sort((a, b) =>
            {
                int priorityA = (a is IUpdatePriority pa) ? pa.UpdatePriority : 0;
                int priorityB = (b is IUpdatePriority pb) ? pb.UpdatePriority : 0;
                return priorityA.CompareTo(priorityB);
            });
        }

        /// <summary>
        /// 등록된 서비스 수 확인
        /// </summary>
        public (int update, int fixedUpdate, int lateUpdate) GetRegisteredCount()
        {
            return (_updatables.Count, _fixedUpdatables.Count, _lateUpdatables.Count);
        }

        [ContextMenu("Print Registered Services")]
        private void PrintRegisteredServices()
        {
            Debug.Log($"[UpdateLoopManager] Registered Services:");
            Debug.Log($"  - KDIUpdate: {_updatables.Count}");
            Debug.Log($"  - KDIFixedUpdate: {_fixedUpdatables.Count}");
            Debug.Log($"  - KDILateUpdate: {_lateUpdatables.Count}");

            foreach (var updatable in _updatables)
            {
                var priority = (updatable is IUpdatePriority p) ? p.UpdatePriority : 0;
                Debug.Log($"    • {updatable.GetType().Name} (Priority: {priority})");
            }
        }

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
