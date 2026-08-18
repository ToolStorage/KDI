using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Kylin.DI.Tests
{
    public sealed class UpdateLoopManagerTests
    {
        private static readonly MethodInfo UpdateMethod = typeof(UpdateLoopManager).GetMethod(
            "Update",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ResetStaticMethod = typeof(UpdateLoopManager).GetMethod(
            "ResetStatic",
            BindingFlags.Static | BindingFlags.NonPublic);

        private GameObject _gameObject;
        private UpdateLoopManager _manager;

        [SetUp]
        public void SetUp()
        {
            Assert.That(UpdateMethod, Is.Not.Null);
            Assert.That(ResetStaticMethod, Is.Not.Null);
            ResetStaticMethod.Invoke(null, null);
            _manager = UpdateLoopManager.Instance;
            _gameObject = _manager.gameObject;
        }

        [TearDown]
        public void TearDown()
        {
            ResetStaticMethod.Invoke(null, null);
            _manager = null;
            _gameObject = null;
        }

        [Test]
        public void DuplicateRegistration_RemainsActiveUntilMatchingFinalUnregister()
        {
            var target = new CountingUpdatable();

            _manager.Register(target);
            _manager.Register(target);
            Tick();

            Assert.That(_manager.GetRegisteredCount().update, Is.EqualTo(1));
            Assert.That(target.UpdateCalls, Is.EqualTo(1));

            _manager.Unregister(target);
            Tick();

            Assert.That(_manager.GetRegisteredCount().update, Is.EqualTo(1));
            Assert.That(target.UpdateCalls, Is.EqualTo(2));

            _manager.Unregister(target);
            Tick();

            Assert.That(_manager.GetRegisteredCount().update, Is.Zero);
            Assert.That(target.UpdateCalls, Is.EqualTo(2));

            _manager.Unregister(target);
            Tick();

            Assert.That(_manager.GetRegisteredCount().update, Is.Zero);
            Assert.That(target.UpdateCalls, Is.EqualTo(2));
        }

        [Test]
        public void QueuedZeroOneTransitions_AreAppliedInCallOrder()
        {
            var target = new CountingUpdatable();

            _manager.Register(target);
            _manager.Unregister(target);
            _manager.Register(target);
            Tick();

            Assert.That(_manager.GetRegisteredCount().update, Is.EqualTo(1));
            Assert.That(target.UpdateCalls, Is.EqualTo(1));

            _manager.Unregister(target);
            _manager.Register(target);
            _manager.Unregister(target);
            _manager.Unregister(target);
            Tick();

            Assert.That(_manager.GetRegisteredCount().update, Is.Zero);
            Assert.That(target.UpdateCalls, Is.EqualTo(1));
        }

        [Test]
        public void FinalUnregisterDuringUpdate_RetiresBeforeQueuedRemoval()
        {
            var target = new CountingUpdatable();
            var unregistering = new CallbackUpdatable(call =>
            {
                if (call <= 2)
                    _manager.Unregister(target);
            });

            _manager.Register(unregistering);
            _manager.Register(target);
            _manager.Register(target);

            Tick();
            Assert.That(target.UpdateCalls, Is.EqualTo(1),
                "The first unregister only decrements 2 -> 1 and must not retire the target.");

            Tick();
            Assert.That(target.UpdateCalls, Is.EqualTo(1),
                "The final unregister must suppress callbacks before its queued list removal runs.");
            Assert.That(_manager.GetRegisteredCount().update, Is.EqualTo(2),
                "The target is retired synchronously but remains structurally queued until the next phase boundary.");

            Tick();
            Assert.That(_manager.GetRegisteredCount().update, Is.EqualTo(1));
            Assert.That(target.UpdateCalls, Is.EqualTo(1));
        }

        [Test]
        public void ManualRegistrationWithoutUpdateInterface_FailsWithoutStructuralEntry()
        {
            Assert.Throws<ArgumentException>(() => _manager.Register(new object()));

            Tick();
            Assert.That(_manager.GetRegisteredCount().update, Is.Zero);
            Assert.That(_manager.GetRegisteredCount().fixedUpdate, Is.Zero);
            Assert.That(_manager.GetRegisteredCount().lateUpdate, Is.Zero);
        }

        [Test]
        public void ValueTypeUpdateService_IsRejectedBecauseBoxedIdentityCannotBeUnregistered()
        {
            Assert.Throws<ArgumentException>(() => _manager.Register(new ValueTypeUpdatable()));
            Tick();
            Assert.That(_manager.GetRegisteredCount().update, Is.Zero);
        }

        [Test]
        public void SceneAddedManager_CannotBecomeASecondOwnershipAuthority()
        {
            var duplicateObject = new GameObject("DuplicateUpdateLoopManager");
            var duplicate = duplicateObject.AddComponent<UpdateLoopManager>();
            var target = new CountingUpdatable();

            try
            {
                Assert.Throws<InvalidOperationException>(() => duplicate.Register(target));
                Assert.Throws<InvalidOperationException>(() => duplicate.Unregister(target));
                Assert.Throws<InvalidOperationException>(() => duplicate.GetRegisteredCount());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(duplicateObject);
            }
        }

        private void Tick()
        {
            try
            {
                UpdateMethod.Invoke(_manager, null);
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
        }

        private sealed class CountingUpdatable : IUpdatable
        {
            public int UpdateCalls { get; private set; }
            public void KDIUpdate(float deltaTime) => UpdateCalls++;
        }

        private sealed class CallbackUpdatable : IUpdatable, IUpdatePriority
        {
            private readonly Action<int> _callback;
            private int _updateCalls;

            public CallbackUpdatable(Action<int> callback) => _callback = callback;
            public int UpdatePriority => -100;

            public void KDIUpdate(float deltaTime)
            {
                _updateCalls++;
                _callback(_updateCalls);
            }
        }

        private struct ValueTypeUpdatable : IUpdatable
        {
            public void KDIUpdate(float deltaTime) { }
        }
    }
}
