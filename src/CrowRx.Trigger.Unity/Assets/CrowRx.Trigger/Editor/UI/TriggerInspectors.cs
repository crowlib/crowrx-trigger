using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace CrowRx.Trigger.Unity.Editor
{
    internal static class InspectorAssets
    {
        internal static string Directory
        {
            get
            {
                foreach (string guid in AssetDatabase.FindAssets("CrowRxTriggerInspector t:StyleSheet"))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith("/Editor/UI/CrowRxTriggerInspector.uss", System.StringComparison.Ordinal))
                    {
                        return Path.GetDirectoryName(path)?.Replace('\\', '/');
                    }
                }

                return string.Empty;
            }
        }
    }

    [CustomEditor(typeof(TriggerListenerBehaviour))]
    internal sealed class TriggerListenerEditor : UnityEditor.Editor
    {
        private VisualElement _root;
        private ListenerRuntimePanel _runtime;

        public override VisualElement CreateInspectorGUI()
        {
            _root = new VisualElement();
            Build();
            _root.TrackSerializedObjectValue(serializedObject, _ => UpdateValidation());
            return _root;
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += Rebuild;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }

        private void OnDisable()
        {
            _runtime?.Dispose();
            Undo.undoRedoPerformed -= Rebuild;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
        }

        private void PlayModeChanged(PlayModeStateChange state) => Rebuild();

        private void Rebuild()
        {
            if (_root?.panel != null && target)
            {
                Build();
            }
        }

        private void Build()
        {
            bool runtimeExpanded = _runtime?.Expanded ?? false;
            _runtime?.Dispose();
            _root.Unbind();
            _root.Clear();

            serializedObject.Update();

            string directory = InspectorAssets.Directory;
            VisualTreeAsset layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(directory + "/TriggerListenerInspector.uxml");
            if (!layout)
            {
                _root.Add(new HelpBox("인스펙터 레이아웃을 불러올 수 없어.", HelpBoxMessageType.Error));
                return;
            }

            VisualElement settings = new() { name = "listener-settings" };
            _root.Add(settings);
            layout.CloneTree(settings);

            InspectorContext context = new(serializedObject, _root, Build);
            SerializedProperty conditions = serializedObject.FindProperty("conditions");
            PropertyField combine = _root.Q<PropertyField>("combineType");
            combine.EnableInClassList("condition-hidden", conditions.arraySize < 2);
            ConditionEditorGUI.AddList(_root.Q("conditions"), conditions, context);

            UpdateValidation();

            settings.Bind(serializedObject);
            settings.SetEnabled(!EditorApplication.isPlayingOrWillChangePlaymode);
            _runtime = new ListenerRuntimePanel((TriggerListenerBehaviour)target) { Expanded = runtimeExpanded };
            _root.Add(_runtime);
        }

        private void UpdateValidation()
        {
            VisualElement host = _root?.Q("validation");
            if (host == null || !target)
            {
                return;
            }

            host.Clear();
            foreach (ConditionDefinition definition in ((TriggerListenerBehaviour)target).Conditions)
            {
                string message = ConditionAuthoringValidation.Message(definition);
                if (message is not null)
                {
                    host.Add(new HelpBox(message, HelpBoxMessageType.Warning));
                    break;
                }
            }
        }
    }

    [CustomEditor(typeof(ConditionBehaviour), true)]
    internal sealed class ConditionComponentEditor : UnityEditor.Editor
    {
        private VisualElement _root;

        private void OnEnable()
        {
            Undo.undoRedoPerformed += Rebuild;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Rebuild;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
        }

        private void PlayModeChanged(PlayModeStateChange state) => Rebuild();

        private void Rebuild()
        {
            if (_root?.panel != null && target)
            {
                Build();
            }
        }

        public override VisualElement CreateInspectorGUI()
        {
            _root = new VisualElement();
            Build();
            return _root;
        }

        private void Build()
        {
            _root.Unbind();
            _root.Clear();
            serializedObject.Update();
            string directory = InspectorAssets.Directory;
            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(directory + "/CrowRxTriggerInspector.uss");
            if (sheet)
            {
                _root.styleSheets.Add(sheet);
            }

            SerializedProperty settings = serializedObject.FindProperty("settings");
            if (settings != null)
            {
                ConditionEditorGUI.AddDefinition(_root, settings, new InspectorContext(serializedObject, _root, Build));
            }
            else
            {
                InspectorElement.FillDefaultInspector(_root, serializedObject, this);
            }

            _root.Bind(serializedObject);
            _root.SetEnabled(!EditorApplication.isPlayingOrWillChangePlaymode);
        }
    }

    [CustomPropertyDrawer(typeof(ConditionDefinition), true)]
    internal sealed class ConditionDefinitionDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            VisualElement root = new();
            string path = property.propertyPath;
            SerializedObject serialized = property.serializedObject;

            Build();

            return root;

            void Build()
            {
                root.Unbind();
                root.Clear();
                SerializedProperty current = serialized.FindProperty(path);
                if (current != null)
                {
                    ConditionEditorGUI.AddDefinition(root, current, new InspectorContext(serialized, root, Build));
                }
            }
        }
    }
}
