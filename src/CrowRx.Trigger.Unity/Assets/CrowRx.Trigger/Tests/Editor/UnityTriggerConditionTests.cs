using System;
using System.Threading.Tasks;
using CrowRx.Trigger;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using Object = UnityEngine.Object;

namespace CrowRx.Trigger.Unity.Tests
{
    public sealed class UnityTriggerConditionTests
    {
        private Scene _scene;
        private Scene _previousScene;
        private GameObject _root;
        private GameObject _target;
        private UnityTriggerSensor _sensor;
        private Collider _body;

        [SetUp]
        public void SetUp()
        {
            _previousScene = SceneManager.GetActiveScene();
            _scene = EditorSceneManager.NewPreviewScene();
            _root = new GameObject("TriggerTestRoot");
            SceneManager.MoveGameObjectToScene(_root, _scene);
            _root.transform.position = Vector3.right * 100000f;
            GameObject detector = Child("Detector");
            Rigidbody rigidbody = detector.AddComponent<Rigidbody>();
            rigidbody.isKinematic = true;
            rigidbody.useGravity = false;
            SphereCollider volume = detector.AddComponent<SphereCollider>();
            volume.radius = 3f;
            volume.isTrigger = true;
            _sensor = detector.AddComponent<UnityTriggerSensor>();
            _sensor.Configure(volume, ~0);
            _target = Child("Target");
            _body = _target.AddComponent<BoxCollider>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            if (_previousScene.IsValid() && _previousScene.isLoaded)
            {
                SceneManager.SetActiveScene(_previousScene);
            }

            if (_scene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_scene);
            }
        }

        private GameObject Child(string name)
        {
            GameObject child = new(name);
            child.transform.SetParent(_root.transform, false);
            return child;
        }

        private ICondition Condition(bool isNot = false) => new TriggerOverlapConditionDefinition
        {
            Sensor = _sensor, Target = _target, IsNot = isNot
        }.CreateCondition();

        [Test]
        public void ActivationFindsAlreadyInsideTarget_AndDeactivationTurnsOffOnlySensor()
        {
            ICondition condition = Condition();
            condition.Initialize();
            Assert.That(_sensor.Volume.enabled, Is.False);
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.True);
            condition.Deactivate();
            Assert.That(_sensor.Volume.enabled, Is.False);
            Assert.That(_body.enabled, Is.True);
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.True);
            condition.Deactivate();
        }

        [Test]
        public void SharedSensorRemainsActiveUntilLastConditionStops()
        {
            ICondition first = Condition();
            ICondition second = Condition();
            first.Activate();
            second.Activate();
            first.Deactivate();
            Assert.That(_sensor.ActiveUsers, Is.EqualTo(1));
            Assert.That(_sensor.Volume.enabled, Is.True);
            Assert.That(second.IsSatisfied, Is.True);
            second.Deactivate();
            Assert.That(_sensor.Volume.enabled, Is.False);
        }

        [Test]
        public void MultipleColliders_PartialDisableDoesNotRemoveTarget()
        {
            GameObject extra = new("ExtraCollider");
            extra.transform.SetParent(_target.transform, false);
            Collider second = extra.AddComponent<SphereCollider>();
            ICondition condition = Condition();
            condition.Activate();
            _body.enabled = false;
            Assert.That(condition.IsSatisfied, Is.True);
            second.enabled = false;
            Assert.That(condition.IsSatisfied, Is.False);
            condition.Deactivate();
        }

        [Test]
        public void DestroyedOrDisabledColliderDoesNotLeaveStaleOccupancy()
        {
            ICondition condition = Condition();
            condition.Activate();
            Object.DestroyImmediate(_body);
            Assert.That(condition.IsSatisfied, Is.False);
            condition.Deactivate();
        }

        [Test]
        public void SharedTargetCanChangeWithoutRestartingCondition()
        {
            GameObject outside = Child("Outside");
            outside.transform.localPosition = Vector3.right * 10f;
            outside.AddComponent<BoxCollider>();
            ICondition condition = Condition();
            condition.Activate();
            IConditionTargetSharable<GameObject> shared = (IConditionTargetSharable<GameObject>)condition;
            shared.Target = outside;
            Assert.That(condition.IsSatisfied, Is.False);
            shared.Target = _target;
            Assert.That(condition.IsSatisfied, Is.True);
            condition.Deactivate();
        }

        [Test]
        public void MissingTargetIsInvalid_NotDoesNotTurnItIntoSuccess()
        {
            ICondition condition = new TriggerOverlapConditionDefinition { Sensor = _sensor, IsNot = true }.CreateCondition();
            Assert.That(condition.IsValid, Is.False);
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.False);
            condition.Deactivate();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DynamicProviderWaitsForTargetAndOverridesSharedTarget(bool isNot)
        {
            var provider = _root.AddComponent<TestTriggerTargetProvider>();
            ICondition condition = new TriggerOverlapConditionDefinition
            {
                Sensor = _sensor, Target = _target, TargetProvider = provider, IsNot = isNot
            }.CreateCondition();
            condition.Activate();
            Assert.That(condition.IsValid, Is.True);
            Assert.That(condition.IsSatisfied, Is.False);
            ((IConditionTargetSharable<GameObject>)condition).Target = null;
            provider.Current = _target;
            Assert.That(condition.IsSatisfied, Is.EqualTo(!isNot));
            _target.transform.localPosition = Vector3.right * 10f;
            Assert.That(condition.IsSatisfied, Is.EqualTo(isNot));
            provider.Current = null;
            Assert.That(condition.IsSatisfied, Is.False);
            condition.Deactivate();
            Assert.That(_sensor.ActiveUsers, Is.Zero);
        }

        [Test]
        public void SharedGroupSuppliesTargetWithoutChangingDefinition()
        {
            var definition = new TriggerOverlapConditionDefinition { Sensor = _sensor };
            var group = new SharedTargetGroupDefinition { Target = _target };
            group.Conditions.Add(definition);
            ICondition condition = group.CreateCondition();
            condition.Initialize();
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.True);
            Assert.That(definition.Target, Is.Null);
            condition.Deactivate();
            Assert.That(_sensor.ActiveUsers, Is.Zero);
        }

        [Test]
        public void NegatedConditionIsTrueForValidOutsideTarget()
        {
            _target.transform.localPosition = Vector3.right * 10f;
            ICondition condition = Condition(true);
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.True);
            condition.Deactivate();
        }

        [Test]
        public void ReconciliationGrowsBufferWithoutDroppingContacts()
        {
            for (int i = 0; i < 50; i++)
            {
                Child("OtherCollider").AddComponent<BoxCollider>();
            }

            ICondition condition = Condition();
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.True);
            condition.Deactivate();
        }

        [TestCase(0)]
        [TestCase(1)]
        public void PrimitiveVolumesRejectBoundsOnlyFalsePositives(int shape)
        {
            GameObject detector = _sensor.gameObject;
            Object.DestroyImmediate(_sensor.Volume);
            Collider volume;
            if (shape == 0)
            {
                BoxCollider box = detector.AddComponent<BoxCollider>();
                box.size = new Vector3(1f, 2f, 6f);
                detector.transform.rotation = Quaternion.Euler(0, 45, 0);
                volume = box;
                _target.transform.localPosition = new Vector3(2f, 0, -2f);
            }
            else
            {
                CapsuleCollider capsule = detector.AddComponent<CapsuleCollider>();
                capsule.radius = 1f;
                capsule.height = 6f;
                volume = capsule;
                _target.transform.localPosition = new Vector3(1.4f, 2.8f, 1.4f);
            }

            volume.isTrigger = true;
            _sensor.Configure(volume, ~0);
            ICondition condition = Condition();
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.False);
            condition.Deactivate();
        }

        [Test]
        public void LayerFilterRejectsUnrelatedContacts()
        {
            _sensor.Configure(_sensor.Volume, 0);
            ICondition condition = Condition();
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.False);
            condition.Deactivate();
        }

        [Test]
        public void CharacterControllerIsDetectedOnActivation()
        {
            Object.DestroyImmediate(_body);
            _target.AddComponent<CharacterController>();
            ICondition condition = Condition();
            condition.Activate();
            Assert.That(condition.IsSatisfied, Is.True);
            condition.Deactivate();
        }

        [Test]
        public void DisablingSensorClearsContacts_LeaseDisposalIsIdempotent()
        {
            IDisposable lease = _sensor.Acquire();
            _sensor.enabled = false;
            Assert.That(_sensor.Contains(_target), Is.False);
            lease.Dispose();
            lease.Dispose();
            Assert.That(_sensor.Volume.enabled, Is.False);
            Assert.That(_sensor.ActiveUsers, Is.Zero);
            _sensor.enabled = true;
            Assert.That(_sensor.Volume.enabled, Is.False);
        }

        [Test]
        public async Task OneShotCompletionAndExplicitRestartReleaseSensor()
        {
            Listener listener = new(Condition()) { IsOneShot = true };
            ListenerResult first = await listener.StartExecution().Completion;
            Assert.That(first.Kind, Is.EqualTo(ListenerResultKind.Satisfied));
            Assert.That(_sensor.ActiveUsers, Is.Zero);
            Assert.That(_sensor.Volume.enabled, Is.False);
            ListenerResult second = await listener.StartExecution().Completion;
            Assert.That(second.Kind, Is.EqualTo(ListenerResultKind.Satisfied));
            Assert.That(_sensor.Volume.enabled, Is.False);
        }

        [Test]
        public async Task CancellationWhileWaitingReleasesSensor()
        {
            _target.transform.localPosition = Vector3.right * 10f;
            Listener listener = new(Condition()) { IsOneShot = false };
            listener.StartListen();
            Assert.That(_sensor.Volume.enabled, Is.True);
            await listener.StopListenAsync();
            Assert.That(_sensor.Volume.enabled, Is.False);
            Assert.That(_sensor.ActiveUsers, Is.Zero);
        }
    }

    public sealed class TestTriggerTargetProvider : TriggerTargetProvider
    {
        public GameObject Current;
        public override GameObject Target => Current;
    }
}
