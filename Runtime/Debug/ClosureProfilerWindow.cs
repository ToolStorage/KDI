#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Kylin.SubscribableProperty
{
    public class ClosureProfilerWindow : EditorWindow
    {
        private static ClosureProfilerWindow _instance;
        private static bool _isProfilingEnabled = false;

        private Vector2 _scrollPosition;
        private readonly Dictionary<ClosureCaptureInfo, bool> _foldoutStates = new Dictionary<ClosureCaptureInfo, bool>();

        private string _searchFilter = "";
        private bool _showOnlyActive = true;
        private bool _showUnsubscribed = true;
        private ClosureCaptureRisk _minRiskFilter = ClosureCaptureRisk.Low;
        private bool _autoRefresh = true;
        private double _lastRefreshTime;

        private enum SortMode { Time, Memory, Risk, Lifetime }
        private SortMode _sortMode = SortMode.Risk;
        private bool _sortDescending = true;

        public static bool IsWindowOpen => _instance != null;
        public static bool IsProfilingEnabled => _isProfilingEnabled;

        [MenuItem("Tools/Closure Profiler")]
        public static void ShowWindow()
        {
            _instance = GetWindow<ClosureProfilerWindow>("클로저 프로파일러");
            _instance.minSize = new Vector2(800, 600);
            _instance.Show();
        }

        private void OnEnable()
        {
            _instance = this;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            _instance = null;
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (_autoRefresh && EditorApplication.timeSinceStartup - _lastRefreshTime > 1.0)
            {
                _lastRefreshTime = EditorApplication.timeSinceStartup;
                ClosureProfiler.RefreshMemoryInfo();
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawStatistics();
            DrawFilters();
            DrawClosureList();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical("Box");

            EditorGUILayout.LabelField("클로저 캡처 프로파일러", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("람다식의 외부 변수 캡처를 감지하고 메모리 누수를 분석합니다.", EditorStyles.miniLabel);

            EditorGUILayout.Space(5);

            EditorGUI.BeginDisabledGroup(Application.isPlaying);
            bool newProfilingEnabled = EditorGUILayout.Toggle("프로파일링 활성화", _isProfilingEnabled);
            if (newProfilingEnabled != _isProfilingEnabled)
            {
                _isProfilingEnabled = newProfilingEnabled;
                if (!_isProfilingEnabled)
                {
                    ClosureProfiler.Clear();
                    _foldoutStates.Clear();
                }
            }
            EditorGUI.EndDisabledGroup();

            _autoRefresh = EditorGUILayout.Toggle("자동 새로고침", _autoRefresh);

            if (Application.isPlaying && _isProfilingEnabled)
            {
                EditorGUILayout.HelpBox("프로파일링 활성화됨 - 클로저 캡처를 모니터링 중입니다.", MessageType.Info);
            }
            else if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox("플레이 모드 진입 전에 프로파일링을 활성화하세요.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawStatistics()
        {
            EditorGUILayout.BeginVertical("Box");
            EditorGUILayout.LabelField("통계", EditorStyles.boldLabel);

            var (total, active, unsubscribed, totalMemory) = ClosureProfiler.GetStatistics();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"전체 클로저: {total}", GUILayout.Width(150));
            EditorGUILayout.LabelField($"활성: {active}", GUILayout.Width(100));
            EditorGUILayout.LabelField($"구독 해제됨: {unsubscribed}", GUILayout.Width(150));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            var memoryKB = totalMemory / 1024f;
            var memoryMB = memoryKB / 1024f;
            var memoryText = memoryMB >= 1f ? $"{memoryMB:F2} MB" : $"{memoryKB:F2} KB";

            var prevColor = GUI.contentColor;
            if (memoryMB >= 10f)
                GUI.contentColor = Color.red;
            else if (memoryMB >= 1f)
                GUI.contentColor = Color.yellow;
            else
                GUI.contentColor = Color.green;

            EditorGUILayout.LabelField($"추정 메모리 사용량: {memoryText}", EditorStyles.boldLabel);
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginVertical("Box");
            EditorGUILayout.LabelField("필터 및 정렬", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _searchFilter = EditorGUILayout.TextField("검색", _searchFilter);
            _showOnlyActive = EditorGUILayout.Toggle("활성만", _showOnlyActive, GUILayout.Width(100));
            _showUnsubscribed = EditorGUILayout.Toggle("구독 해제 포함", _showUnsubscribed, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("최소 위험도", GUILayout.Width(100));
            _minRiskFilter = (ClosureCaptureRisk)EditorGUILayout.EnumPopup(_minRiskFilter, GUILayout.Width(150));

            EditorGUILayout.LabelField("정렬", GUILayout.Width(50));
            _sortMode = (SortMode)EditorGUILayout.EnumPopup(_sortMode, GUILayout.Width(100));
            _sortDescending = EditorGUILayout.Toggle("↓", _sortDescending, GUILayout.Width(30));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("새로고침"))
            {
                ClosureProfiler.RefreshMemoryInfo();
                Repaint();
            }
            if (GUILayout.Button("모두 지우기"))
            {
                ClosureProfiler.Clear();
                _foldoutStates.Clear();
            }
            if (GUILayout.Button("강제 GC"))
            {
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
                ClosureProfiler.RefreshMemoryInfo();
            }
            if (GUILayout.Button("CSV 내보내기"))
            {
                ExportToCSV();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawClosureList()
        {
            if (!_isProfilingEnabled)
            {
                EditorGUILayout.HelpBox("프로파일링을 시작하려면 위에서 활성화하세요.", MessageType.Info);
                return;
            }

            var captures = ClosureProfiler.GetAllCaptures()
                .Where(c => FilterCapture(c))
                .OrderBy(c => GetSortKey(c), _sortDescending)
                .ToList();

            EditorGUILayout.LabelField($"클로저 캡처: {captures.Count}개", EditorStyles.boldLabel);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            foreach (var capture in captures)
            {
                DrawCaptureInfo(capture);
            }

            EditorGUILayout.EndScrollView();
        }

        private bool FilterCapture(ClosureCaptureInfo capture)
        {
            if (_showOnlyActive && !capture.IsAlive)
                return false;

            if (!_showUnsubscribed && capture.IsUnsubscribed)
                return false;

            var maxRisk = capture.CapturedVariables.Max(v => v.RiskLevel);
            if (maxRisk < _minRiskFilter)
                return false;

            if (!string.IsNullOrEmpty(_searchFilter))
            {
                var filter = _searchFilter.ToLower();
                if (!capture.ClosureTypeName.ToLower().Contains(filter) &&
                    !capture.CaptureLocation.ToLower().Contains(filter))
                    return false;
            }

            return true;
        }

        private IComparable GetSortKey(ClosureCaptureInfo capture)
        {
            return _sortMode switch
            {
                SortMode.Time => _sortDescending ? -capture.CaptureTime.Ticks : capture.CaptureTime.Ticks,
                SortMode.Memory => _sortDescending ? -capture.EstimatedMemoryBytes : capture.EstimatedMemoryBytes,
                SortMode.Risk => _sortDescending
                    ? -(int)capture.CapturedVariables.Max(v => v.RiskLevel)
                    : (int)capture.CapturedVariables.Max(v => v.RiskLevel),
                SortMode.Lifetime => _sortDescending
                    ? -(capture.Lifetime?.TotalSeconds ?? 0)
                    : (capture.Lifetime?.TotalSeconds ?? 0),
                _ => 0
            };
        }

        private void DrawCaptureInfo(ClosureCaptureInfo capture)
        {
            if (!_foldoutStates.ContainsKey(capture))
                _foldoutStates[capture] = false;

            EditorGUILayout.BeginVertical("Box");

            // 헤더
            EditorGUILayout.BeginHorizontal();

            var maxRisk = capture.CapturedVariables.Any()
                ? capture.CapturedVariables.Max(v => v.RiskLevel)
                : ClosureCaptureRisk.Low;

            var riskIcon = GetRiskIcon(maxRisk);
            var memoryKB = capture.EstimatedMemoryBytes / 1024f;
            var headerText = $"{riskIcon} {capture.ClosureTypeName} ({memoryKB:F2}KB)";

            if (capture.IsUnsubscribed)
            {
                headerText += " [구독 해제됨]";
            }

            var prevColor = GUI.color;
            GUI.color = GetRiskColor(maxRisk);

            _foldoutStates[capture] = EditorGUILayout.Foldout(_foldoutStates[capture], headerText, true);

            GUI.color = prevColor;

            if (!capture.IsAlive)
            {
                EditorGUILayout.LabelField("[GC됨]", EditorStyles.miniLabel, GUILayout.Width(60));
            }

            EditorGUILayout.EndHorizontal();

            // 상세 정보
            if (_foldoutStates[capture])
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("위치:", capture.CaptureLocation);
                EditorGUILayout.LabelField("캡처 시간:", capture.CaptureTime.ToString("HH:mm:ss"));

                if (capture.IsUnsubscribed)
                {
                    EditorGUILayout.LabelField("구독 해제 시간:", capture.UnsubscribeTime?.ToString("HH:mm:ss") ?? "Unknown");
                    EditorGUILayout.LabelField("생존 시간:", $"{capture.Lifetime?.TotalSeconds:F2}초");
                }
                else
                {
                    EditorGUILayout.LabelField("현재 생존 시간:", $"{capture.Lifetime?.TotalSeconds:F2}초");
                }

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("캡처된 변수:", EditorStyles.boldLabel);

                EditorGUI.indentLevel++;
                foreach (var variable in capture.CapturedVariables.OrderByDescending(v => v.RiskLevel))
                {
                    DrawVariableInfo(variable);
                }
                EditorGUI.indentLevel--;

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawVariableInfo(CapturedVariableInfo variable)
        {
            EditorGUILayout.BeginHorizontal("Box");

            var riskIcon = GetRiskIcon(variable.RiskLevel);
            var memoryBytes = variable.EstimatedMemoryBytes;
            var memoryText = memoryBytes >= 1024 ? $"{memoryBytes / 1024f:F2}KB" : $"{memoryBytes}B";

            var prevColor = GUI.contentColor;
            GUI.contentColor = GetRiskColor(variable.RiskLevel);

            EditorGUILayout.LabelField($"{riskIcon} {variable.FieldName}", GUILayout.Width(150));
            EditorGUILayout.LabelField(variable.FieldTypeName, GUILayout.Width(150));
            EditorGUILayout.LabelField(memoryText, GUILayout.Width(80));

            if (!variable.IsAlive && variable.IsReferenceType)
            {
                EditorGUILayout.LabelField("[GC됨]", EditorStyles.miniLabel, GUILayout.Width(60));
            }

            GUI.contentColor = prevColor;

            EditorGUILayout.EndHorizontal();

            if (variable.RiskLevel >= ClosureCaptureRisk.Medium)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"⚠ {variable.RiskDescription}", EditorStyles.helpBox);
                EditorGUI.indentLevel--;
            }
        }

        private string GetRiskIcon(ClosureCaptureRisk risk)
        {
            return risk switch
            {
                ClosureCaptureRisk.Critical => "🔴",
                ClosureCaptureRisk.High => "🟠",
                ClosureCaptureRisk.Medium => "🟡",
                ClosureCaptureRisk.Low => "🟢",
                _ => "⚪"
            };
        }

        private Color GetRiskColor(ClosureCaptureRisk risk)
        {
            return risk switch
            {
                ClosureCaptureRisk.Critical => new Color(1f, 0.3f, 0.3f),
                ClosureCaptureRisk.High => new Color(1f, 0.6f, 0.2f),
                ClosureCaptureRisk.Medium => new Color(1f, 1f, 0.4f),
                ClosureCaptureRisk.Low => new Color(0.6f, 1f, 0.6f),
                _ => Color.white
            };
        }

        /// <summary>
        /// 분석용..
        /// </summary>
        private void ExportToCSV()
        {
            var path = EditorUtility.SaveFilePanel("CSV 내보내기", "", "closure_profile.csv", "csv");
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var lines = new List<string>
                {
                    "Capture Time,Location,Closure Type,Is Active,Memory (KB),Max Risk,Variables,Lifetime (sec)"
                };

                foreach (var capture in ClosureProfiler.GetAllCaptures())
                {
                    var maxRisk = capture.CapturedVariables.Any()
                        ? capture.CapturedVariables.Max(v => v.RiskLevel)
                        : ClosureCaptureRisk.Low;

                    var line = $"{capture.CaptureTime:yyyy-MM-dd HH:mm:ss}," +
                               $"\"{capture.CaptureLocation}\"," +
                               $"{capture.ClosureTypeName}," +
                               $"{(!capture.IsUnsubscribed && capture.IsAlive)}," +
                               $"{capture.EstimatedMemoryBytes / 1024f:F2}," +
                               $"{maxRisk}," +
                               $"{capture.CapturedVariables.Count}," +
                               $"{capture.Lifetime?.TotalSeconds:F2}";

                    lines.Add(line);
                }

                System.IO.File.WriteAllLines(path, lines);
                EditorUtility.DisplayDialog("내보내기 완료", $"CSV 파일이 저장되었습니다:\n{path}", "확인");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("오류", $"CSV 내보내기 실패:\n{ex.Message}", "확인");
            }
        }
    }

    // OrderBy 확장 메서드 (정렬 방향 지원)
    internal static class ClosureProfilerExtensions
    {
        public static IOrderedEnumerable<T> OrderBy<T, TKey>(
            this IEnumerable<T> source,
            Func<T, TKey> keySelector,
            bool descending)
        {
            return descending
                ? source.OrderByDescending(keySelector)
                : source.OrderBy(keySelector);
        }
    }
}
#endif
