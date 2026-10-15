using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using CrowRx.Pool.Collections;
using Object = UnityEngine.Object;

namespace CrowRx.Trigger.Unity.Editor
{
    internal readonly struct SharedTargetPreview
    {
        internal readonly bool Inherited;
        internal readonly GameObject Target;

        internal SharedTargetPreview(GameObject target)
        {
            Inherited = true;
            Target = target;
        }
    }

    internal sealed class InspectorContext
    {
        internal readonly SerializedObject Serialized;
        private readonly VisualElement _root;
        private readonly Action _rebuild;
        private bool _queued;

        internal InspectorContext(SerializedObject serialized, VisualElement root, Action rebuild)
        {
            Serialized = serialized;
            _root = root;
            _rebuild = rebuild;
        }

        internal void Rebuild()
        {
            if (_queued)
            {
                return;
            }

            _queued = true;

            _root.schedule.Execute(() =>
            {
                _queued = false;
                if (Serialized.targetObject)
                {
                    _rebuild();
                }
            });
        }

        internal void Edit(Action action)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            Serialized.Update();
            action();
            Serialized.ApplyModifiedProperties();
            Rebuild();
        }

        internal void Add(string path, ConditionDefinition definition)
        {
            Edit(() =>
            {
                SerializedProperty array = Serialized.FindProperty(path);
                int index = array.arraySize;
                array.arraySize++;
                array.GetArrayElementAtIndex(index).managedReferenceValue = definition;
            });
        }

        internal void WatchReference(ObjectField field, string path)
        {
            Object previous = Serialized.FindProperty(path).objectReferenceValue;
            field.RegisterValueChangedCallback(_ => _root.schedule.Execute(() =>
            {
                if (!Serialized.targetObject)
                {
                    return;
                }

                SerializedProperty property = Serialized.FindProperty(path);
                if (property == null || property.objectReferenceValue == previous)
                {
                    return;
                }

                previous = property.objectReferenceValue;
                Rebuild();
            }));
        }
    }

    internal static class ConditionEditorGUI
    {
        internal static string Title(ConditionDefinition definition) =>
            definition switch
            {
                null => "설정 누락",
                ComponentConditionReference reference when reference.Component && reference.Component.Definition != null => reference.Component.Definition.GetType().GetCustomAttribute<ConditionMenuAttribute>()?.Name ?? reference.Component.GetType().Name,
                _ => definition.GetType().GetCustomAttribute<ConditionMenuAttribute>()?.Name ?? definition.GetType().Name
            };

        internal static void AddList(VisualElement host, SerializedProperty array, InspectorContext context, SharedTargetPreview shared = default)
        {
            string path = array.propertyPath;
            ListView list = new()
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
                SerializedProperty current = context.Serialized.FindProperty(path);
                if (current == null || index >= current.arraySize)
                {
                    return;
                }

                SerializedProperty property = current.GetArrayElementAtIndex(index);
                ConditionDefinition definition = property.managedReferenceValue as ConditionDefinition;
                string itemPath = property.propertyPath;

                VisualElement header = new();
                header.AddToClassList("condition-row");

                Foldout foldout = new()
                {
                    text = $"{index + 1}. {Title(definition)} · {(definition is ComponentConditionReference ? "참조" : "내부")}",
                    value = property.isExpanded
                };
                foldout.AddToClassList("condition-foldout");
                foldout.RegisterValueChangedCallback(e =>
                {
                    if (e.target != foldout)
                    {
                        return;
                    }

                    SerializedProperty found = context.Serialized.FindProperty(itemPath);
                    if (found != null)
                    {
                        found.isExpanded = e.newValue;
                    }
                });

                Button remove = new(() => context.Edit(() =>
                {
                    SerializedProperty currentArray = context.Serialized.FindProperty(path);
                    currentArray.DeleteArrayElementAtIndex(index);
                })) { text = "−", tooltip = "목록에서 제거한다. 참조한 컴포넌트는 삭제하지 않는다." };
                remove.AddToClassList("condition-remove");

                header.Add(foldout);
                header.Add(remove);
                row.Add(header);

                if (definition == null)
                {
                    foldout.Add(new HelpBox("조건 설정이나 타입이 누락됐다. 항목을 제거하고 다시 추가해.", HelpBoxMessageType.Error));
                }
                else
                {
                    AddDefinition(foldout, context.Serialized.FindProperty(itemPath), context, shared);
                }
            };

            list.itemIndexChanged += (_, __) => context.Rebuild();
            host.Add(list);

            list.BindProperty(array);
            if (array.arraySize == 0)
            {
                host.Add(new HelpBox("조건을 하나 이상 추가해.", HelpBoxMessageType.Warning));
            }

            Button add = new() { text = "+ 조건 추가" };
            add.AddToClassList("condition-add");
            add.clicked += () => UnityEditor.PopupWindow.Show(add.worldBound, new ConditionPicker(definition => context.Add(path, definition)));

            host.Add(add);
            host.AddManipulator(new ConditionDropManipulator(components =>
            {
                foreach (ConditionBehaviour component in components)
                {
                    context.Add(path, new ComponentConditionReference { Component = component });
                }
            }));
        }

        internal static void AddDefinition(VisualElement host, SerializedProperty property, InspectorContext context, SharedTargetPreview inherited = default)
        {
            ConditionDefinition definition =
                property.propertyType == SerializedPropertyType.ManagedReference
                    ? property.managedReferenceValue as ConditionDefinition
                    : (property.serializedObject.targetObject as ConditionBehaviour)?.Definition;
            switch (definition)
            {
                case ConditionGroupDefinition group:
                {
                    AddField(host, property.FindPropertyRelative("combineType"), "결합 방식");
                    AddField(host, property.FindPropertyRelative("isNot"), "NOT · 결과 반전");
                    SharedTargetPreview childrenTarget = default(SharedTargetPreview);
                    if (group is SharedTargetGroupDefinition shared)
                    {
                        AddTarget(host, property.FindPropertyRelative("target"), inherited, context);
                        childrenTarget = new SharedTargetPreview(inherited.Inherited ? inherited.Target : shared.Target);
                    }

                    VisualElement children = new();
                    children.AddToClassList("condition-children");
                    host.Add(children);
                    AddList(children, property.FindPropertyRelative("conditions"), context, childrenTarget);
                    return;
                }
                case ComponentConditionReference:
                {
                    SerializedProperty reference = property.FindPropertyRelative("component");
                    ObjectField field = new("조건 컴포넌트") { objectType = typeof(ConditionBehaviour), allowSceneObjects = true };
                    field.SetValueWithoutNotify(reference.objectReferenceValue);
                    field.BindProperty(reference);
                    context.WatchReference(field, reference.propertyPath);
                    host.Add(field);
                    if (!reference.objectReferenceValue)
                    {
                        host.Add(new HelpBox("조건 컴포넌트를 지정해.", HelpBoxMessageType.Warning));
                    }
                    else if (inherited.Inherited)
                    {
                        ConditionBehaviour source = (ConditionBehaviour)reference.objectReferenceValue;
                        if (source.Definition is ActiveStateConditionDefinition or DistanceConditionDefinition or SharedTargetGroupDefinition)
                        {
                            AddInheritedTarget(host, inherited.Target);
                        }
                    }

                    return;
                }
            }

            SerializedProperty child = property.Copy();
            SerializedProperty end = property.GetEndProperty();
            if (!child.NextVisible(true))
            {
                return;
            }

            do
            {
                if (SerializedProperty.EqualContents(child, end))
                {
                    break;
                }

                if (child.depth != property.depth + 1)
                {
                    continue;
                }

                if (child.name == "target" && definition is ActiveStateConditionDefinition or DistanceConditionDefinition)
                {
                    AddTarget(host, child.Copy(), inherited, context);
                }
                else
                {
                    AddField(host, child.Copy(), Label(child.name));
                }
            } while (child.NextVisible(false));

            if (property.FindPropertyRelative("origin") is { } origin && !origin.objectReferenceValue)
            {
                host.Add(new HelpBox("거리의 기준점을 직접 지정해. 리스너 위치로 자동 대체하지 않아.", HelpBoxMessageType.Warning));
            }
        }

        private static string Label(string name) =>
            name switch
            {
                "duration" => "대기 시간 (초)",
                "timeSource" => "시간 기준",
                "origin" => "기준점",
                "maxDistance" => "최대 거리 (m)",
                "isNot" => "NOT · 결과 반전",
                _ => ObjectNames.NicifyVariableName(name)
            };

        private static void AddField(VisualElement host, SerializedProperty property, string label)
        {
            PropertyField field = new(property, label);
            host.Add(field);
            field.BindProperty(property);
        }

        private static void AddTarget(VisualElement host, SerializedProperty property, SharedTargetPreview inherited, InspectorContext context)
        {
            if (inherited.Inherited)
            {
                AddInheritedTarget(host, inherited.Target);
                return;
            }

            ObjectField field = new("타겟")
            {
                objectType = typeof(GameObject),
                allowSceneObjects = true
            };
            field.SetValueWithoutNotify(property.objectReferenceValue);
            field.BindProperty(property);

            context.WatchReference(field, property.propertyPath);

            host.Add(field);

            if (!property.objectReferenceValue)
            {
                host.Add(new HelpBox("타겟을 지정해. 상위 공유 그룹에 연결하면 그 그룹의 타겟을 사용해.", HelpBoxMessageType.Warning));
            }
        }

        private static void AddInheritedTarget(VisualElement host, GameObject target)
        {
            ObjectField field = new("부모 타겟")
            {
                objectType = typeof(GameObject),
                value = target
            };
            field.SetEnabled(false);
            host.Add(field);
        }
    }

    internal sealed class ConditionPicker : PopupWindowContent
    {
        private readonly Action<ConditionDefinition> _select;

        internal ConditionPicker(Action<ConditionDefinition> select)
        {
            _select = select;
        }

        public override Vector2 GetWindowSize() => new(300, 350);

        public override void OnGUI(Rect rect)
        {
        }

        public override void OnOpen()
        {
            VisualElement root = editorWindow.rootVisualElement;
            ToolbarSearchField search = new();
            ScrollView scroll = new();

            root.Add(search);
            root.Add(scroll);

            (Type type, ConditionMenuAttribute menu)[] types =
                TypeCache.GetTypesDerivedFrom<ConditionDefinition>()
                    .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && t.GetConstructor(Type.EmptyTypes) != null)
                    .Select(t => (type: t, menu: t.GetCustomAttribute<ConditionMenuAttribute>()))
                    .Where(x => x.menu != null)
                    .OrderBy(x => x.menu.Category)
                    .ThenBy(x => x.menu.Name)
                    .ToArray();

            search.RegisterValueChangedCallback(e => Refresh(e.newValue));
            Refresh("");
            search.Focus();

            return;

            void Refresh(string text)
            {
                scroll.Clear();
                string category = null;
                foreach ((Type type, ConditionMenuAttribute menu) in types)
                {
                    if (menu.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0 && menu.Category.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (category != menu.Category)
                    {
                        category = menu.Category;
                        scroll.Add(new Label(category));
                    }

                    scroll.Add(new Button(() =>
                    {
                        _select((ConditionDefinition)Activator.CreateInstance(type));
                        editorWindow.Close();
                    })
                    {
                        text = menu.Name
                    });
                }
            }
        }
    }

    internal sealed class ConditionDropManipulator : PointerManipulator
    {
        #region static

        private static bool TryResolve(Object[] items, List<ConditionBehaviour> result)
        {
            using PooledList<ConditionBehaviour> buffer = ListPool<ConditionBehaviour>.Get();
            foreach (Object item in items)
            {
                switch (item)
                {
                    case ConditionBehaviour component:
                        result.Add(component);
                        break;
                    case GameObject gameObject:
                    {
                        gameObject.GetComponents(buffer);
                        if (buffer.Count != 1)
                        {
                            return false;
                        }

                        result.Add(buffer[0]);
                        break;
                    }
                    default:
                        return false;
                }
            }

            return result.Count > 0;
        }

        private static void OnUpdate(DragUpdatedEvent e)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            using PooledList<ConditionBehaviour> components = ListPool<ConditionBehaviour>.Get();
            if (!TryResolve(DragAndDrop.objectReferences, components))
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            e.StopPropagation();
        }

        #endregion

        // 콜백에 전달한 대여 목록은 호출이 끝나기 전에만 사용한다.
        private readonly Action<List<ConditionBehaviour>> _drop;

        internal ConditionDropManipulator(Action<List<ConditionBehaviour>> drop)
        {
            _drop = drop;
        }

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

        private void OnPerform(DragPerformEvent e)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            using PooledList<ConditionBehaviour> components = ListPool<ConditionBehaviour>.Get();
            if (!TryResolve(DragAndDrop.objectReferences, components))
            {
                return;
            }

            DragAndDrop.AcceptDrag();
            _drop(components);
            e.StopPropagation();
        }
    }
}