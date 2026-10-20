using System;
using System.Collections;
using System.Linq;
using CrowRx.Pool.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CrowRx.Trigger.Unity.Tests
{
    public class ConditionAuthoringTests
    {
        private Scene _scene;
        private Scene _previous;
        private string _folder;
        private UnityEditor.Editor _editor;
        private EditorWindow _window;

        [SetUp]
        public void SetUp()
        {
            _previous = SceneManager.GetActiveScene();
            _scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            if (_window) _window.Close();
            if (_editor) Object.DestroyImmediate(_editor);
            if (_previous.IsValid() && _previous.isLoaded) SceneManager.SetActiveScene(_previous);
            if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.ClosePreviewScene(_scene);
            if (_folder != null) AssetDatabase.DeleteAsset(_folder);
        }

        [Test]
        public void SettingsAreCopiedAndEveryCreationHasIndependentState()
        {
            var definition = new TimeConditionDefinition { Duration = 1, IsNot = false };
            var first = definition.CreateCondition();
            var second = definition.CreateCondition();
            first.Initialize();
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(second.IsInitialized, Is.False);
            definition.Duration = -1;
            definition.IsNot = true;
            Assert.That(first.IsValid, Is.True);
            Assert.That(first.IsNot, Is.False);
            Assert.That(definition.CreateCondition().IsValid, Is.False);
        }

        [Test]
        public void SharedParentOverridesNestedSharedTargetWithoutChangingSettings()
        {
            var parentTarget = MakeObject("Parent Target");
            var localTarget = MakeObject("Local Target");
            var leaf = new ActiveStateConditionDefinition { Target = localTarget };
            var child = new SharedTargetGroupDefinition { Target = localTarget };
            child.Conditions.Add(leaf);
            var parent = new SharedTargetGroupDefinition { Target = parentTarget };
            parent.Conditions.Add(child);
            var runtime = (ConditionGroupShareTarget<GameObject>)parent.CreateCondition();
            var nested = (ConditionGroupShareTarget<GameObject>)runtime.Conditions[0];
            Assert.That(nested.Target, Is.SameAs(parentTarget));
            Assert.That(((IConditionTargetSharable<GameObject>)nested.Conditions[0]).Target, Is.SameAs(parentTarget));
            Assert.That(child.Target, Is.SameAs(localTarget));
            Assert.That(leaf.Target, Is.SameAs(localTarget));
        }

        [Test]
        public void NormalGroupStopsTargetPropagation()
        {
            var parentTarget = MakeObject("Parent Target");
            var localTarget = MakeObject("Local Target");
            var normal = new ConditionGroupDefinition();
            normal.Conditions.Add(new ActiveStateConditionDefinition { Target = localTarget });
            var parent = new SharedTargetGroupDefinition { Target = parentTarget };
            parent.Conditions.Add(normal);
            var runtime = (ConditionGroup)parent.CreateCondition();
            var nested = (ConditionGroup)runtime.Conditions[0];
            Assert.That(((IConditionTargetSharable<GameObject>)nested.Conditions[0]).Target, Is.SameAs(localTarget));
        }

        [Test]
        public void DuplicateComponentReferencesCreateDifferentExecutionConditions()
        {
            var target = MakeObject("Target");
            var source = MakeObject("Source").AddComponent<ActiveStateConditionBehaviour>();
            source.Settings.Target = target;
            var definition = new ConditionGroupDefinition();
            definition.Conditions.Add(new ComponentConditionReference { Component = source });
            definition.Conditions.Add(new ComponentConditionReference { Component = source });
            var runtime = (ConditionGroup)definition.CreateCondition();
            Assert.That(runtime.Conditions[0], Is.Not.SameAs(runtime.Conditions[1]));
            Object.DestroyImmediate(source);
            Assert.That(runtime.IsValid, Is.True);
        }

        [Test]
        public void EmptyMissingAndCyclicSettingsAreRejected()
        {
            Assert.Throws<InvalidOperationException>(() => new ConditionGroupDefinition().CreateCondition());
            Assert.Throws<InvalidOperationException>(() => new ComponentConditionReference().CreateCondition());
            var group = new ConditionGroupDefinition();
            group.Conditions.Add(group);
            Assert.Throws<InvalidOperationException>(() => group.CreateCondition());
            group.Conditions.Clear(); group.Conditions.Add(null);
            Assert.Throws<InvalidOperationException>(() => group.CreateCondition());
            group.Conditions.Clear(); group.Conditions.Add(new TimeConditionDefinition());
            var recovered = (ConditionGroup)group.CreateCondition();
            Assert.That(recovered.Conditions.Count, Is.EqualTo(1));
            Assert.That(recovered.IsValid, Is.True);
            Assert.That(group.CreateCondition(), Is.Not.SameAs(recovered));
        }

        [Test]
        public void DragResolutionPreservesMixedInputOrder()
        {
            var first = MakeObject("First").AddComponent<TimeConditionBehaviour>();
            var second = MakeObject("Second").AddComponent<ActiveStateConditionBehaviour>();
            var multiple = MakeObject("Multiple");
            var explicitComponent = multiple.AddComponent<DistanceConditionBehaviour>();
            multiple.AddComponent<TimeConditionBehaviour>();

            Assert.That(ResolveDragged(first.gameObject, second.gameObject, explicitComponent),
                Is.EqualTo(new ConditionBehaviour[] { first, second, explicitComponent }));
        }

        [Test]
        public void DragResolutionRejectsInvalidInputAndRecoversForNextDrag()
        {
            var valid = MakeObject("Valid").AddComponent<TimeConditionBehaviour>();
            var missing = MakeObject("No Condition");
            var multiple = MakeObject("Multiple");
            multiple.AddComponent<TimeConditionBehaviour>();
            multiple.AddComponent<ActiveStateConditionBehaviour>();

            Assert.That(ResolveDragged(), Is.Null);
            Assert.That(ResolveDragged(valid, missing), Is.Null);
            Assert.That(ResolveDragged(valid, valid.transform), Is.Null);
            Assert.That(ResolveDragged(valid, multiple), Is.Null);
            Assert.That(ResolveDragged(valid.gameObject), Is.EqualTo(new[] { valid }));
        }

        private static ConditionBehaviour[] ResolveDragged(params Object[] items)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .Single(x => x.GetName().Name == "CrowRx.Trigger.Unity.Editor");
            var type = assembly.GetType("CrowRx.Trigger.Unity.Editor.ConditionDropManipulator", true);
            var resolve = type.GetMethod("TryResolve", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            using PooledList<ConditionBehaviour> result = ListPool<ConditionBehaviour>.Get();
            return (bool)resolve.Invoke(null, new object[] { items, result }) ? result.ToArray() : null;
        }

        [Test]
        public void SingleRootUsesLeafAndMultipleRootsPreserveOrder()
        {
            var listener = MakeObject("Listener").AddComponent<TriggerListenerBehaviour>();
            listener.Conditions.Add(new TimeConditionDefinition { IsNot = true });
            listener.Initialize();
            Assert.That(listener.Condition, Is.Not.InstanceOf<ConditionGroup>());
            Assert.That(listener.Condition.IsNot, Is.True);
            Assert.Throws<InvalidOperationException>(() => listener.CombineType = ConditionCombineType.Or);
            var multi = MakeObject("Multiple").AddComponent<TriggerListenerBehaviour>();
            multi.CombineType = ConditionCombineType.Or;
            multi.Conditions.Add(new TimeConditionDefinition { IsNot = true });
            multi.Conditions.Add(new TimeConditionDefinition { IsNot = false });
            multi.Initialize();
            var group = (ConditionGroup)multi.Condition;
            Assert.That(group.CombineType, Is.EqualTo(ConditionCombineType.Or));
            Assert.That(group.Conditions.Select(c => c.IsNot), Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void DistanceUsesExplicitOriginAndIncludesBoundary()
        {
            var origin = MakeObject("Origin");
            var target = MakeObject("Target");
            target.transform.position = new Vector3(3, 0, 0);
            var definition = new DistanceConditionDefinition { Origin = origin.transform, Target = target, MaxDistance = 3 };
            var runtime = definition.CreateCondition();
            Assert.That(runtime.IsSatisfied, Is.True);
            target.transform.position = new Vector3(3.01f, 0, 0);
            Assert.That(runtime.IsSatisfied, Is.False);
            definition.IsNot = true;
            Assert.That(definition.CreateCondition().IsSatisfied, Is.True);
            definition.Origin = null;
            Assert.That(definition.CreateCondition().IsValid, Is.False);
            Assert.That(definition.CreateCondition().IsSatisfied, Is.False);
        }

        [Test]
        public void ActiveStateUsesHierarchyAndDestroyedTargetsStayInvalidWithNot()
        {
            var parent = MakeObject("Parent");
            var target = MakeObject("Target"); target.transform.SetParent(parent.transform);
            var normal = new ActiveStateConditionDefinition { Target = target }.CreateCondition();
            var inverted = new ActiveStateConditionDefinition { Target = target, IsNot = true }.CreateCondition();
            parent.SetActive(false);
            Assert.That(normal.IsSatisfied, Is.False);
            Assert.That(inverted.IsSatisfied, Is.True);
            Object.DestroyImmediate(target);
            Assert.That(inverted.IsValid, Is.False);
            Assert.That(inverted.IsSatisfied, Is.False);
        }

        [Test]
        public void SceneRoundTripPreservesMixedListReferencesAndNestedOrder()
        {
            var root = MakeMixedListener();
            var path = MakeFolder() + "/Mixed.unity";
            var fixture = AssetDatabase.FindAssets("CrowRxTriggerEmpty t:Scene").Select(AssetDatabase.GUIDToAssetPath).Single();
            Assert.That(AssetDatabase.CopyAsset(fixture, path), Is.True);
            var saved = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                SceneManager.MoveGameObjectToScene(root.gameObject, saved);
                Assert.That(EditorSceneManager.SaveScene(saved), Is.True);
            }
            finally { EditorSceneManager.CloseScene(saved, true); }
            EditorSceneManager.ClosePreviewScene(_scene);
            _scene = EditorSceneManager.OpenPreviewScene(path);
            var restored = _scene.GetRootGameObjects().Single(x => x.name == "Mixed").GetComponent<TriggerListenerBehaviour>();
            AssertMixed(restored);
        }

        [Test]
        public void PrefabRoundTripAndOverridesPreserveManagedReferences()
        {
            var root = MakeMixedListener();
            var path = MakeFolder() + "/Mixed.prefab";
            PrefabUtility.SaveAsPrefabAsset(root.gameObject, path);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), _scene);
            var listener = instance.GetComponent<TriggerListenerBehaviour>();
            AssertMixed(listener);
            var serialized = new SerializedObject(listener);
            serialized.FindProperty("conditions").GetArrayElementAtIndex(0).FindPropertyRelative("duration").floatValue = 9;
            serialized.ApplyModifiedProperties();
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.True);
            PrefabUtility.RevertPrefabInstance(instance, InteractionMode.AutomatedAction);
            Assert.That(((TimeConditionDefinition)listener.Conditions[0]).Duration, Is.EqualTo(2));
            serialized.Update();
            serialized.FindProperty("conditions").MoveArrayElement(0, 1);
            serialized.ApplyModifiedProperties();
            PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<TriggerListenerBehaviour>();
            Assert.That(asset.Conditions[0], Is.InstanceOf<ComponentConditionReference>());
            Assert.That(asset.Conditions[1], Is.InstanceOf<TimeConditionDefinition>());
        }

        [Test]
        public void SerializedAddReorderRemoveSupportUndoAndRedo()
        {
            var listener = MakeMixedListener();
            var group = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            try
            {
                var serialized = new SerializedObject(listener);
                var array = serialized.FindProperty("conditions");
                array.MoveArrayElement(0, 1);
                serialized.ApplyModifiedProperties();
                Undo.FlushUndoRecordObjects();
                Assert.That(listener.Conditions[0], Is.InstanceOf<ComponentConditionReference>());
                Undo.PerformUndo();
                Assert.That(listener.Conditions[0], Is.InstanceOf<TimeConditionDefinition>());
                Undo.PerformRedo();
                Assert.That(listener.Conditions[0], Is.InstanceOf<ComponentConditionReference>());
                serialized.Update(); array = serialized.FindProperty("conditions");
                array.arraySize++;
                array.GetArrayElementAtIndex(3).managedReferenceValue = new TimeConditionDefinition();
                serialized.ApplyModifiedProperties(); Undo.FlushUndoRecordObjects();
                Assert.That(listener.Conditions.Count, Is.EqualTo(4));
                Undo.PerformUndo(); Assert.That(listener.Conditions.Count, Is.EqualTo(3));
                serialized.Update(); array = serialized.FindProperty("conditions");
                var source = ((ComponentConditionReference)listener.Conditions[0]).Component;
                array.DeleteArrayElementAtIndex(0); serialized.ApplyModifiedProperties();
                Assert.That(source, Is.Not.Null);
            }
            finally { Undo.RevertAllDownToGroup(group + 1); }
        }

        [UnityTest]
        public IEnumerator InspectorBindsMixedNestedRowsAndInheritedTarget()
        {
            var listener = MakeMixedListener();
            var shared = (SharedTargetGroupDefinition)listener.Conditions[2];
            var nested = new SharedTargetGroupDefinition { Target = MakeObject("Other") };
            nested.Conditions.Add(new ActiveStateConditionDefinition());
            shared.Conditions.Add(nested);
            var serialized = new SerializedObject(listener);
            var array = serialized.FindProperty("conditions");
            for (int i = 0; i < array.arraySize; i++) array.GetArrayElementAtIndex(i).isExpanded = true;
            var children = array.GetArrayElementAtIndex(2).FindPropertyRelative("conditions");
            for (int i = 0; i < children.arraySize; i++) children.GetArrayElementAtIndex(i).isExpanded = true;
            _editor = UnityEditor.Editor.CreateEditor(listener);
            Assert.That(_editor.GetType().Name, Is.EqualTo("TriggerListenerEditor"));
            _window = ScriptableObject.CreateInstance<InspectorTestWindow>();
            _window.position = new Rect(80, 80, 650, 900);
            _window.Show();
            var ui = _editor.CreateInspectorGUI();
            _window.rootVisualElement.Add(ui);
            for (int i = 0; i < 8; i++) yield return null;
            Assert.That(ui.Q<ListView>(), Is.Not.Null);
            Assert.That(ui.Query<Foldout>().ToList().Any(x => x.text.Contains("타겟 공유 그룹")), Is.True);
            var inherited = ui.Query<UnityEditor.UIElements.ObjectField>().ToList().Where(x => x.label == "부모 타겟").ToArray();
            Assert.That(inherited.Length, Is.GreaterThan(0));
            Assert.That(inherited.All(x => !x.enabledSelf && x.value == shared.Target), Is.True);
            Assert.That(ui.Query<Button>().ToList().Any(x => x.text == "+ 조건 추가"), Is.True);
        }

        [UnityTest]
        public IEnumerator PopupSearchSelectionAddsInlineConditionAndKeepsLayoutStable()
        {
            var listener = MakeMixedListener();
            _editor = UnityEditor.Editor.CreateEditor(listener);
            _window = ScriptableObject.CreateInstance<InspectorTestWindow>();
            _window.Show(); _window.position = new Rect(80, 80, 600, 850); _window.Focus();
            var ui = _editor.CreateInspectorGUI(); _window.rootVisualElement.Add(ui);
            for (int i = 0; i < 12; i++) yield return null;
            var add = ui.Query<Button>().ToList().Last(x => x.text == "+ 조건 추가");
            var stable = add;
            for (int i = 0; i < 6; i++) yield return null;
            Assert.That(ui.Query<Button>().ToList().Last(x => x.text == "+ 조건 추가"), Is.SameAs(stable), "Initial binding must not repeatedly rebuild the inspector.");
            Assert.That(float.IsNaN(add.worldBound.y), Is.False);
            Assert.That(add.worldBound.height, Is.GreaterThan(0));
            SimulateClick(add);
            // Test Runner가 다음 틱에 포커스를 가져가기 전에 팝업의 동기 검색·선택 경로를 검증한다.
            var popup = Resources.FindObjectsOfTypeAll<EditorWindow>().Single(x => x is UnityEditor.PopupWindow);
            try
            {
                var search = popup.rootVisualElement.Q<UnityEditor.UIElements.ToolbarSearchField>();
                Assert.That(search, Is.Not.Null);
                search.value = "거리";
                var choices = popup.rootVisualElement.Q<ScrollView>().Query<Button>().ToList();
                Assert.That(choices.Count, Is.EqualTo(1));
                Assert.That(choices[0].text, Is.EqualTo("거리"));
                SimulateClick(choices[0]);
                yield return null; yield return null;
                Assert.That(listener.Conditions.Count, Is.EqualTo(4));
                Assert.That(listener.Conditions[3], Is.InstanceOf<DistanceConditionDefinition>());
                Assert.That(listener.GetComponents<ConditionBehaviour>().Length, Is.EqualTo(1));
            }
            finally { if (popup) popup.Close(); }
        }

        private static void SimulateClick(Button button)
        {
            var method = typeof(Clickable).GetMethod("SimulateSingleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            using (var evt = ClickEvent.GetPooled())
            { evt.target = button; method.Invoke(button.clickable, new object[] { evt, 0 }); }
        }

        [UnityTest]
        public IEnumerator IndividualConditionInspectorsUseTheSharedSettingsEditor()
        {
            _window = ScriptableObject.CreateInstance<InspectorTestWindow>(); _window.Show();
            var components = new ConditionBehaviour[]
            {
                MakeObject("Time").AddComponent<TimeConditionBehaviour>(),
                MakeObject("Active").AddComponent<ActiveStateConditionBehaviour>(),
                MakeObject("Distance").AddComponent<DistanceConditionBehaviour>()
            };
            foreach (var component in components)
            {
                _editor = UnityEditor.Editor.CreateEditor(component);
                var ui = _editor.CreateInspectorGUI(); _window.rootVisualElement.Add(ui);
                yield return null;
                Assert.That(ui.childCount, Is.GreaterThan(0));
                _window.rootVisualElement.Clear(); Object.DestroyImmediate(_editor); _editor = null;
            }
        }

        [UnityTest]
        public IEnumerator RuntimePanelsArePassiveAndReleaseIndependentRequests()
        {
            var listener = MakeMixedListener();
            _editor = UnityEditor.Editor.CreateEditor(listener);
            var otherEditor = UnityEditor.Editor.CreateEditor(listener);
            _window = ScriptableObject.CreateInstance<InspectorTestWindow>(); _window.Show();
            VisualElement first = _editor.CreateInspectorGUI(), second = otherEditor.CreateInspectorGUI();
            _window.rootVisualElement.Add(first); _window.rootVisualElement.Add(second);
            try
            {
                yield return null;
                var a = first.Q<Foldout>("runtime-foldout");
                var b = second.Q<Foldout>("runtime-foldout");
                Assert.That(a, Is.Not.Null); Assert.That(b, Is.Not.Null);
                Assert.That(first.Q<Label>("runtime-execution").text, Is.EqualTo("Idle / Not initialized"));
                Assert.That(first.Q("listener-runtime").Query<Button>().ToList(), Is.Empty);
                a.value = true; b.value = true;
                yield return null;
                var requests = typeof(TriggerListenerBehaviour).GetField("_diagnosticsRequests", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(requests.GetValue(listener), Is.EqualTo(2));
                Assert.That(listener.IsInitialized, Is.False);
                Assert.That(listener.Condition, Is.Null);
                a.value = false;
                Assert.That(requests.GetValue(listener), Is.EqualTo(1));
                second.RemoveFromHierarchy();
                Assert.That(requests.GetValue(listener), Is.Zero);
                _window.rootVisualElement.Add(second);
                Assert.That(requests.GetValue(listener), Is.EqualTo(1));
                Object.DestroyImmediate(otherEditor); otherEditor = null;
                Assert.That(requests.GetValue(listener), Is.Zero);
            }
            finally { if (otherEditor) Object.DestroyImmediate(otherEditor); }
        }

        private TriggerListenerBehaviour MakeMixedListener()
        {
            var root = MakeObject("Mixed");
            var listener = root.AddComponent<TriggerListenerBehaviour>();
            var source = root.AddComponent<ActiveStateConditionBehaviour>(); source.Settings.Target = root;
            listener.Conditions.Add(new TimeConditionDefinition { Duration = 2 });
            listener.Conditions.Add(new ComponentConditionReference { Component = source });
            var group = new SharedTargetGroupDefinition { Target = root, CombineType = ConditionCombineType.Or };
            group.Conditions.Add(new DistanceConditionDefinition { Origin = root.transform, MaxDistance = 3 });
            listener.Conditions.Add(group);
            return listener;
        }
        private static void AssertMixed(TriggerListenerBehaviour listener)
        {
            Assert.That(listener.Conditions.Count, Is.EqualTo(3));
            Assert.That(((TimeConditionDefinition)listener.Conditions[0]).Duration, Is.EqualTo(2));
            Assert.That(((ComponentConditionReference)listener.Conditions[1]).Component, Is.EqualTo(listener.GetComponent<ActiveStateConditionBehaviour>()));
            var group = (SharedTargetGroupDefinition)listener.Conditions[2];
            Assert.That(group.Target, Is.EqualTo(listener.gameObject));
            Assert.That(group.CombineType, Is.EqualTo(ConditionCombineType.Or));
            Assert.That(((DistanceConditionDefinition)group.Conditions[0]).Origin, Is.EqualTo(listener.transform));
        }
        private GameObject MakeObject(string name)
        {
            var gameObject = new GameObject(name);
            SceneManager.MoveGameObjectToScene(gameObject, _scene);
            return gameObject;
        }
        private string MakeFolder()
        {
            _folder = "Assets/CrowRxTriggerVerification_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _folder.Substring("Assets/".Length));
            return _folder;
        }
    }

    internal sealed class InspectorTestWindow : EditorWindow { }
}
