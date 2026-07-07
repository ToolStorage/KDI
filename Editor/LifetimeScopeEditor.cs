using UnityEditor;
using UnityEngine;

namespace Kylin.DI.Editor
{
    /// <summary>
    /// LifetimeScope 커스텀 인스펙터.
    /// Play 모드에서 등록 목록과 Resolve 상태를 읽기 전용으로 표시한다.
    /// </summary>
    [CustomEditor(typeof(LifetimeScope), true)]
    public class LifetimeScopeEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (!Application.isPlaying)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("Play 모드에서 등록 목록과 Resolve 상태가 표시됩니다.", MessageType.None);
                return;
            }

            var lifetimeScope = (LifetimeScope)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);

            if (!lifetimeScope.IsInitialized)
            {
                EditorGUILayout.LabelField("Initialized", "✘ (not yet)");
                return;
            }

            EditorGUILayout.LabelField("Initialized", "✔");

            if (lifetimeScope.Scope is not Scope scope)
                return;

            var parentName = scope.Parent is Scope parentScope
                ? parentScope.Name
                : (scope.Parent != null ? scope.Parent.GetType().Name : "(none — RootScope)");
            EditorGUILayout.LabelField("Parent", parentName);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Registrations ({scope.Registrations.Count})", EditorStyles.boldLabel);

            using (new EditorGUI.IndentLevelScope())
            {
                foreach (var reg in scope.Registrations.Values)
                {
                    var implName = reg.ImplementationType != null
                        ? reg.ImplementationType.Name
                        : (reg.Instance != null ? reg.Instance.GetType().Name : "(factory)");

                    var resolved = scope.IsResolved(reg.ServiceType) ? "● resolved" : "○ not yet";

                    EditorGUILayout.LabelField(
                        $"{reg.ServiceType.Name} → {implName}",
                        $"{reg.Lifetime}   {resolved}");
                }
            }
        }
    }
}
