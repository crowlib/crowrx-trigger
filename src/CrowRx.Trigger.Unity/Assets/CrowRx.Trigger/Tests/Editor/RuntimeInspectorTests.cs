using System;
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CrowRx.Trigger.Unity.Tests
{
    public sealed class RuntimeInspectorTests
    {
        private GameObject _object;
        private UnityEditor.Editor _editor;
        private EditorWindow _window;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_window) _window.Close();
            if (_editor) Object.DestroyImmediate(_editor);
            if (_object) Object.Destroy(_object);
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator PanelDisplaysStoredExecutionAndPreparationErrorsWithoutEditingSettings()
        {
            yield return new EnterPlayMode();
            _object = new GameObject("Runtime Inspector verification");
            _object.SetActive(false);
            var listener = _object.AddComponent<TriggerListenerBehaviour>();
            listener.StartMode = ListenerStartMode.Manual;
            listener.IsOneShot = false;
            _object.SetActive(true);
            _editor = UnityEditor.Editor.CreateEditor(listener);
            _window = ScriptableObject.CreateInstance<InspectorTestWindow>(); _window.Show();
            VisualElement ui = _editor.CreateInspectorGUI(); _window.rootVisualElement.Add(ui);
            ui.Q<Foldout>("runtime-foldout").value = true;
            Assert.That(ui.Q("listener-settings").enabledSelf, Is.False);
            Assert.That(ui.Q("listener-runtime").enabledInHierarchy, Is.True);
            Assert.That(listener.IsInitialized, Is.False);
            Assert.Throws<InvalidOperationException>(() => listener.StartExecution());
            for (int i = 0; i < 40 && !ui.Q<Label>("runtime-preparation").text.Contains("Preparation error"); i++) yield return null;
            Assert.That(ui.Q<Label>("runtime-preparation").text, Does.Contain("Before Execution"));
            listener.Conditions.Add(new ActiveStateConditionDefinition { Target = _object });
            listener.OnSuccess.AddListener(() =>
            {
                LogAssert.Expect(LogType.Exception, new Regex("Runtime panel callback test"));
                throw new InvalidOperationException("Runtime panel callback test");
            });
            ListenerExecution execution = listener.StartExecution();
            for (int i = 0; i < 100 && !execution.Completion.IsCompleted; i++) yield return null;
            Assert.That(execution.Completion.IsCompleted, Is.True);
            for (int i = 0; i < 40 && !ui.Q<Label>("runtime-result").text.Contains("Errors present"); i++) yield return null;
            Assert.That(ui.Q<Label>("runtime-result").text, Does.Contain($"#{execution.Id}: Satisfied · Errors present"));
            Assert.That(ui.Q<Label>("runtime-errors").text, Does.Contain("SuccessCallback"));
            Assert.That(ui.Q<Label>("runtime-conditions").text, Does.Contain("Root"));
            Assert.That(ui.Q<Label>("runtime-preparation").text, Is.Empty);

            listener.OnSuccess.RemoveAllListeners();
            listener.Conditions.Clear(); // 이미 생성한 실행 트리를 변경하지 않는다.
            ListenerExecution next = listener.StartExecution();
            Assert.That(listener.LastResult.Value.ExecutionId, Is.EqualTo(execution.Id));
            listener.gameObject.SetActive(false);
            for (int i = 0; i < 100 && !next.Completion.IsCompleted; i++) yield return null;
            Assert.That(next.Completion.IsCompleted, Is.True);
            Assert.That(next.Completion.Result.Kind, Is.EqualTo(ListenerResultKind.Canceled));
            Assert.That(execution.Completion.Result.Kind, Is.EqualTo(ListenerResultKind.Satisfied));
        }
    }
}
