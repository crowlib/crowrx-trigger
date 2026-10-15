using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace CrowRx.Trigger.Unity.Editor
{
    internal readonly struct SharedTargetPreview
    {
        internal readonly bool Inherited;
        internal readonly GameObject Target;
        internal SharedTargetPreview(GameObject target) { Inherited = true; Target = target; }
    }

    internal sealed class InspectorContext
    {
        internal readonly SerializedObject Serialized;
        private readonly VisualElement _root;
        private readonly Action _rebuild;
        private bool _queued;
        internal InspectorContext(SerializedObject serialized, VisualElement root, Action rebuild)
        { Serialized = serialized; _root = root; _rebuild = rebuild; }
        internal void Rebuild()
        {
            if (_queued) return;
            _queued = true;
            _root.schedule.Execute(() => { _queued = false; if (Serialized.targetObject) _rebuild(); });
        }
        internal void Edit(Action action)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Serialized.Update();
            action();
            Serialized.ApplyModifiedProperties();
            Rebuild();
        }
        internal void Add(string path, ConditionDefinition definition)
        {
            Edit(() =>
            {
                var array = Serialized.FindProperty(path);
                var index = array.arraySize;
                array.arraySize++;
                array.GetArrayElementAtIndex(index).managedReferenceValue = definition;
            });
        }
        internal void WatchReference(ObjectField field, string path)
        {
            var previous = Serialized.FindProperty(path).objectReferenceValue;
            field.RegisterValueChangedCallback(_ => _root.schedule.Execute(() =>
            {
                if (!Serialized.targetObject) return;
                var property = Serialized.FindProperty(path);
                if (property == null || property.objectReferenceValue == previous) return;
                previous = property.objectReferenceValue;
                Rebuild();
            }));
        }
    }

    internal static class ConditionEditorGUI
    {
        internal static string Title(ConditionDefinition definition)
        {
            if (definition == null) return "설정 누락";
            if (definition is ComponentConditionReference reference && reference.Component && reference.Component.Definition != null)
                return reference.Component.Definition.GetType().GetCustomAttribute<ConditionMenuAttribute>()?.Name ?? reference.Component.GetType().Name;
            return definition.GetType().GetCustomAttribute<ConditionMenuAttribute>()?.Name ?? definition.GetType().Name;
        }

        internal static void AddList(VisualElement host, SerializedProperty array, InspectorContext context, SharedTargetPreview shared = default)
        {
            var path = array.propertyPath;
            var list = new ListView
            {
                bindingPath = path,
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                reorderable = true,
                reorderMode = ListViewReorderMode.Animated,
                showBoundCollectionSize = false,
                showAddRemoveFooter = false,
                selectionType = SelectionType.Single,
                makeItem = () => new VisualElement()
            };
            list.AddToClassList("condition-list");
            list.bindItem = (row, index) =>
            {
                row.Unbind();
                row.Clear();
                var current = context.Serialized.FindProperty(path);
                if (current == null || index >= current.arraySize) return;
                var property = current.GetArrayElementAtIndex(index);
                var definition = property.managedReferenceValue as ConditionDefinition;
                var itemPath = property.propertyPath;
                var header = new VisualElement();
                header.AddToClassList("condition-row");
                var foldout = new Foldout { text = $"{index + 1}. {Title(definition)} · {(definition is ComponentConditionReference ? "참조" : "내부")}", value = property.isExpanded };
                foldout.AddToClassList("condition-foldout");
                foldout.RegisterValueChangedCallback(e =>
                {
                    if (e.target != foldout) return;
                    var found = context.Serialized.FindProperty(itemPath);
                    if (found != null) found.isExpanded = e.newValue;
                });
                var remove = new Button(() => context.Edit(() =>
                {
                    var currentArray = context.Serialized.FindProperty(path);
                    currentArray.DeleteArrayElementAtIndex(index);
                })) { text = "−", tooltip = "목록에서 제거한다. 참조한 컴포넌트는 삭제하지 않는다." };
                remove.AddToClassList("condition-remove");
                header.Add(foldout);
                header.Add(remove);
                row.Add(header);
                if (definition == null) foldout.Add(new HelpBox("조건 설정이나 타입이 누락됐다. 항목을 제거하고 다시 추가해.", HelpBoxMessageType.Error));
                else AddDefinition(foldout, context.Serialized.FindProperty(itemPath), context, shared);
            };
            list.itemIndexChanged += (_, __) => context.Rebuild();
            host.Add(list);
            list.BindProperty(array);
            if (array.arraySize == 0) host.Add(new HelpBox("조건을 하나 이상 추가해.", HelpBoxMessageType.Warning));
            var add = new Button { text = "+ 조건 추가" };
            add.AddToClassList("condition-add");
            add.clicked += () => UnityEditor.PopupWindow.Show(add.worldBound, new ConditionPicker(definition => context.Add(path, definition)));
            host.Add(add);
            host.AddManipulator(new ConditionDropManipulator(components =>
            {
                foreach (var component in components) context.Add(path, new ComponentConditionReference { Component = component });
            }));
        }

        internal static void AddDefinition(VisualElement host, SerializedProperty property, InspectorContext context, SharedTargetPreview inherited = default)
        {
            var definition = property.propertyType == SerializedPropertyType.ManagedReference
                ? property.managedReferenceValue as ConditionDefinition
                : (property.serializedObject.targetObject as ConditionBehaviour)?.Definition;
            if (definition is ConditionGroupDefinition group)
            {
                AddField(host, property.FindPropertyRelative("combineType"), "결합 방식");
                AddField(host, property.FindPropertyRelative("isNot"), "NOT · 결과 반전");
                var childrenTarget = default(SharedTargetPreview);
                if (group is SharedTargetGroupDefinition shared)
                {
                    AddTarget(host, property.FindPropertyRelative("target"), inherited, context);
                    childrenTarget = new SharedTargetPreview(inherited.Inherited ? inherited.Target : shared.Target);
                }
                var children = new VisualElement();
                children.AddToClassList("condition-children");
                host.Add(children);
                AddList(children, property.FindPropertyRelative("conditions"), context, childrenTarget);
                return;
            }
            if (definition is ComponentConditionReference)
            {
                var reference = property.FindPropertyRelative("component");
                var field = new ObjectField("조건 컴포넌트") { objectType = typeof(ConditionBehaviour), allowSceneObjects = true };
                field.SetValueWithoutNotify(reference.objectReferenceValue);
                field.BindProperty(reference);
                context.WatchReference(field, reference.propertyPath);
                host.Add(field);
                if (!reference.objectReferenceValue) host.Add(new HelpBox("조건 컴포넌트를 지정해.", HelpBoxMessageType.Warning));
                else if (inherited.Inherited)
                {
                    var source = (ConditionBehaviour)reference.objectReferenceValue;
                    if (source.Definition is ActiveStateConditionDefinition || source.Definition is DistanceConditionDefinition || source.Definition is SharedTargetGroupDefinition)
                        AddInheritedTarget(host, inherited.Target);
                }
                return;
            }
            var child = property.Copy();
            var end = property.GetEndProperty();
            if (!child.NextVisible(true)) return;
            do
            {
                if (SerializedProperty.EqualContents(child, end)) break;
                if (child.depth != property.depth + 1) continue;
                if (child.name == "target" && (definition is ActiveStateConditionDefinition || definition is DistanceConditionDefinition))
                    AddTarget(host, child.Copy(), inherited, context);
                else AddField(host, child.Copy(), Label(child.name));
            } while (child.NextVisible(false));
            if (property.FindPropertyRelative("origin") is SerializedProperty origin && !origin.objectReferenceValue)
                host.Add(new HelpBox("거리의 기준점을 직접 지정해. 리스너 위치로 자동 대체하지 않아.", HelpBoxMessageType.Warning));
        }

        private static string Label(string name)
        {
            switch (name)
            {
                case "duration": return "대기 시간 (초)";
                case "timeSource": return "시간 기준";
                case "origin": return "기준점";
                case "maxDistance": return "최대 거리 (m)";
                case "isNot": return "NOT · 결과 반전";
                default: return ObjectNames.NicifyVariableName(name);
            }
        }
        private static void AddField(VisualElement host, SerializedProperty property, string label)
        {
            var field = new PropertyField(property, label);
            host.Add(field);
            field.BindProperty(property);
        }
        private static void AddTarget(VisualElement host, SerializedProperty property, SharedTargetPreview inherited, InspectorContext context)
        {
            if (inherited.Inherited) { AddInheritedTarget(host, inherited.Target); return; }
            var field = new ObjectField("타겟") { objectType = typeof(GameObject), allowSceneObjects = true };
            field.SetValueWithoutNotify(property.objectReferenceValue);
            field.BindProperty(property);
            context.WatchReference(field, property.propertyPath);
            host.Add(field);
            if (!property.objectReferenceValue) host.Add(new HelpBox("타겟을 지정해. 상위 공유 그룹에 연결하면 그 그룹의 타겟을 사용해.", HelpBoxMessageType.Warning));
        }
        private static void AddInheritedTarget(VisualElement host, GameObject target)
        {
            var field = new ObjectField("부모 타겟") { objectType = typeof(GameObject), value = target };
            field.SetEnabled(false);
            host.Add(field);
        }
    }

    internal sealed class ConditionPicker : PopupWindowContent
    {
        private readonly Action<ConditionDefinition> _select;
        internal ConditionPicker(Action<ConditionDefinition> select) { _select = select; }
        public override Vector2 GetWindowSize() => new Vector2(300, 350);
        public override void OnGUI(Rect rect) { }
        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            var search = new ToolbarSearchField();
            var scroll = new ScrollView();
            root.Add(search);
            root.Add(scroll);
            var types = TypeCache.GetTypesDerivedFrom<ConditionDefinition>()
                .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => (type: t, menu: t.GetCustomAttribute<ConditionMenuAttribute>()))
                .Where(x => x.menu != null).OrderBy(x => x.menu.Category).ThenBy(x => x.menu.Name).ToArray();
            void Refresh(string text)
            {
                scroll.Clear();
                string category = null;
                foreach (var item in types)
                {
                    if (item.menu.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0 && item.menu.Category.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (category != item.menu.Category) { category = item.menu.Category; scroll.Add(new Label(category)); }
                    var type = item.type;
                    scroll.Add(new Button(() =>
                    {
                        _select((ConditionDefinition)Activator.CreateInstance(type));
                        editorWindow.Close();
                    }) { text = item.menu.Name });
                }
            }
            search.RegisterValueChangedCallback(e => Refresh(e.newValue));
            Refresh("");
            search.Focus();
        }
    }

    internal sealed class ConditionDropManipulator : PointerManipulator
    {
        private readonly Action<List<ConditionBehaviour>> _drop;
        internal ConditionDropManipulator(Action<List<ConditionBehaviour>> drop) { _drop = drop; }
        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<DragUpdatedEvent>(OnUpdate);
            target.RegisterCallback<DragPerformEvent>(OnPerform);
        }
        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<DragUpdatedEvent>(OnUpdate);
            target.UnregisterCallback<DragPerformEvent>(OnPerform);
        }
        private static List<ConditionBehaviour> Resolve()
        {
            var result = new List<ConditionBehaviour>();
            foreach (var item in DragAndDrop.objectReferences)
            {
                if (item is ConditionBehaviour component) result.Add(component);
                else if (item is GameObject gameObject)
                {
                    var components = gameObject.GetComponents<ConditionBehaviour>();
                    if (components.Length != 1) return null;
                    result.Add(components[0]);
                }
                else return null;
            }
            return result.Count > 0 ? result : null;
        }
        private void OnUpdate(DragUpdatedEvent e)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Resolve() == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            e.StopPropagation();
        }
        private void OnPerform(DragPerformEvent e)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var components = Resolve();
            if (components == null) return;
            DragAndDrop.AcceptDrag();
            _drop(components);
            e.StopPropagation();
        }
    }
}
