using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Kylin.SubscribableProperty
{
    // 디버그 정보를 담는 클래스
    public class SPSubscriberInfo
    {
        public WeakReference SubscriberRef { get; }
        public string SubscriberTypeName { get; }
        public string SubscriberInstanceName { get; }
        public string MethodName { get; }
        public DateTime SubscribeTime { get; }
        public string SubscribeCallStack { get; }

        public SPSubscriberInfo(object subscriber, string methodName)
        {
            SubscriberRef = new WeakReference(subscriber);
            SubscriberTypeName = subscriber?.GetType().Name ?? "Unknown";
            SubscriberInstanceName = GetInstanceName(subscriber);
            MethodName = methodName;
            SubscribeTime = DateTime.Now;

#if UNITY_EDITOR
            // 호출 스택 저장 (성능을 위해 간단히)
            var stackTrace = new StackTrace(3, true);
            var frames = stackTrace.GetFrames()?.Take(5).ToArray();
            if (frames != null && frames.Length > 0)
            {
                SubscribeCallStack = string.Join("\n", frames.Select(f =>
                    $"{f.GetMethod()?.DeclaringType?.Name}.{f.GetMethod()?.Name} (Line: {f.GetFileLineNumber()})"));
            }
#endif
        }

        private string GetInstanceName(object subscriber)
        {
            if (subscriber == null) return "null";

            // Unity 컴포넌트인 경우
            if (subscriber is MonoBehaviour mono && mono != null)
                return $"{mono.name} ({mono.GetType().Name})";

            // 일반 객체인 경우 이름 속성 찾기
            try
            {
                var nameProperty = subscriber.GetType().GetProperty("Name") ??
                                  subscriber.GetType().GetProperty("name");
                if (nameProperty != null)
                {
                    var name = nameProperty.GetValue(subscriber)?.ToString();
                    if (!string.IsNullOrEmpty(name))
                        return $"{name} ({subscriber.GetType().Name})";
                }
            }
            catch { /* ignore */ }

            return $"{subscriber.GetType().Name}#{subscriber.GetHashCode()}";
        }

        public bool IsAlive => SubscriberRef.IsAlive;
        public object Subscriber => SubscriberRef.Target;
    }

    // 개별 프로퍼티의 디버그 정보
    public class PropertyDebugInfo
    {
        public WeakReference PropertyRef { get; }
        public string PropertyName { get; }
        public string OwnerTypeName { get; }
        public string OwnerInstanceName { get; }
        public Type PropertyType { get; }
        public DateTime CreatedTime { get; }

        private readonly List<SPSubscriberInfo> _subscribers = new List<SPSubscriberInfo>();
        private readonly object _lock = new object();

        public PropertyDebugInfo(object property, string propertyName, object owner)
        {
            PropertyRef = new WeakReference(property);
            PropertyType = property.GetType();
            PropertyName = propertyName;
            CreatedTime = DateTime.Now;

            if (owner != null)
            {
                OwnerTypeName = owner.GetType().Name;
                OwnerInstanceName = GetOwnerInstanceName(owner);
            }
            else
            {
                OwnerTypeName = "Unknown";
                OwnerInstanceName = "Unknown";
            }
        }

        private string GetOwnerInstanceName(object owner)
        {
            if (owner == null) return "null";

            if (owner is MonoBehaviour mono && mono != null)
                return $"{mono.name} ({mono.GetType().Name})";

            try
            {
                var nameProperty = owner.GetType().GetProperty("Name") ??
                                  owner.GetType().GetProperty("name");
                if (nameProperty != null)
                {
                    var name = nameProperty.GetValue(owner)?.ToString();
                    if (!string.IsNullOrEmpty(name))
                        return $"{name} ({owner.GetType().Name})";
                }
            }
            catch { /* ignore */ }

            return $"{owner.GetType().Name}#{owner.GetHashCode()}";
        }

        public void AddSubscriber(object subscriber, string methodName)
        {
            lock (_lock)
            {
                _subscribers.Add(new SPSubscriberInfo(subscriber, methodName));
            }
        }

        public void RemoveSubscriber(object subscriber)
        {
            lock (_lock)
            {
                _subscribers.RemoveAll(s => ReferenceEquals(s.Subscriber, subscriber));
            }
        }

        public IEnumerable<SPSubscriberInfo> GetAliveSubscribers()
        {
            lock (_lock)
            {
                // 죽은 구독자들 제거
                _subscribers.RemoveAll(s => !s.IsAlive);
                return _subscribers.ToList();
            }
        }

        public int SubscriberCount => GetAliveSubscribers().Count();
        public bool IsAlive => PropertyRef.IsAlive;
        public object Property => PropertyRef.Target;
    }

    // 디버그 정보 관리 클래스
    public static class SubscribablePropertyDebugger
    {
        private static readonly Dictionary<object, PropertyDebugInfo> _debugInfos
            = new Dictionary<object, PropertyDebugInfo>();
        private static readonly object _lock = new object();

        public static bool IsTestModeEnabled =>
#if UNITY_EDITOR
            SubscribablePropertyDebugWindow.IsWindowOpen &&
            SubscribablePropertyDebugWindow.IsTestModeOn;
#else
            false;
#endif

        public static void RegisterProperty(object property, string propertyName, object owner)
        {
            if (!IsTestModeEnabled) return;

            lock (_lock)
            {
                if (!_debugInfos.ContainsKey(property))
                {
                    _debugInfos[property] = new PropertyDebugInfo(property, propertyName, owner);
                }
            }
        }

        public static void UnregisterProperty(object property)
        {
            lock (_lock)
            {
                _debugInfos.Remove(property);
            }
        }

        public static void AddSubscriber(object property, object subscriber, string methodName)
        {
            if (!IsTestModeEnabled) return;

            lock (_lock)
            {
                if (_debugInfos.TryGetValue(property, out var info))
                {
                    info.AddSubscriber(subscriber, methodName);
                }
            }
        }

        public static void RemoveSubscriber(object property, object subscriber)
        {
            lock (_lock)
            {
                if (_debugInfos.TryGetValue(property, out var info))
                {
                    info.RemoveSubscriber(subscriber);
                }
            }
        }

        public static IEnumerable<PropertyDebugInfo> GetAllPropertyInfos()
        {
            lock (_lock)
            {
                // 죽은 프로퍼티들 정리
                var deadProperties = _debugInfos.Keys.Where(p => p == null || !_debugInfos[p].IsAlive).ToList();
                foreach (var dead in deadProperties)
                {
                    _debugInfos.Remove(dead);
                }

                return _debugInfos.Values.ToList();
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _debugInfos.Clear();
            }
        }

        // Owner 찾기 헬퍼 메서드
        public static object FindOwner(object property)
        {
            if (property == null) return null;

            try
            {
                // 스택 트레이스를 통해 호출자 찾기
                var stackTrace = new StackTrace();
                var frames = stackTrace.GetFrames();

                if (frames != null)
                {
                    foreach (var frame in frames.Skip(1).Take(10)) // 첫 번째 프레임은 현재 메서드
                    {
                        var method = frame.GetMethod();
                        if (method?.DeclaringType != null &&
                            !method.DeclaringType.IsSubclassOf(typeof(SubscribableProperty<>)) &&
                            method.DeclaringType != typeof(SubscribablePropertyDebugger))
                        {
                            // 생성자나 필드 초기화인 경우, 해당 타입의 인스턴스를 찾아야 함
                            if (method.IsConstructor || method.Name.Contains("ctor"))
                            {
                                return method.DeclaringType; // 타입 정보만 반환
                            }
                        }
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }
    }
}

// SubscribableProperty에 디버그 기능 추가 (Partial)
namespace Kylin.SubscribableProperty
{
    public partial class SubscribableProperty<T>
    {
        private PropertyDebugInfo _debugInfo;
        private bool _debugInitialized = false;

        partial void OnPropertyCreated()
        {
            InitializeDebugInfo();
        }

        partial void OnSubscribe(Action<T> onNext)
        {
#if UNITY_EDITOR
            if (SubscribablePropertyDebugger.IsTestModeEnabled)
            {
                EnsureDebugInfo();
                SubscribablePropertyDebugger.AddSubscriber(this, onNext.Target, onNext.Method.Name);

                // 클로저 캡처 감지
                if (ClosureProfiler.IsEnabled && onNext.Target != null)
                {
                    var targetType = onNext.Target.GetType();
                    if (targetType.Name.Contains("DisplayClass") || targetType.Name.Contains("<>c"))
                    {
                        var location = $"{PropertyName ?? "Unknown"} in {OwnerTypeName ?? "Unknown"}";
                        ClosureProfiler.RecordCapture(onNext.Target, location);
                    }
                }
            }
#endif
        }

        partial void OnUnsubscribe(Action<T> onNext)
        {
#if UNITY_EDITOR
            if (SubscribablePropertyDebugger.IsTestModeEnabled)
            {
                SubscribablePropertyDebugger.RemoveSubscriber(this, onNext.Target);

                // 클로저 구독 해제 기록
                if (ClosureProfiler.IsEnabled && onNext.Target != null)
                {
                    ClosureProfiler.RecordUnsubscribe(onNext.Target);
                }
            }
#endif
        }

        private string PropertyName => _debugInfo?.PropertyName;
        private string OwnerTypeName => _debugInfo?.OwnerTypeName;

        private void InitializeDebugInfo()
        {
            if (!SubscribablePropertyDebugger.IsTestModeEnabled || _debugInitialized) return;

            _debugInitialized = true;

            // Owner와 프로퍼티 이름 찾기
            var (owner, propertyName) = FindOwnerAndPropertyName();

            SubscribablePropertyDebugger.RegisterProperty(this, propertyName, owner);
        }

        private void EnsureDebugInfo()
        {
            if (!_debugInitialized)
            {
                InitializeDebugInfo();
            }
        }

        private (object owner, string propertyName) FindOwnerAndPropertyName()
        {
            try
            {
                var stackTrace = new StackTrace();
                var frames = stackTrace.GetFrames();

                if (frames != null)
                {
                    foreach (var frame in frames.Skip(1).Take(15))
                    {
                        var method = frame.GetMethod();
                        if (method?.DeclaringType != null &&
                            method.DeclaringType != typeof(SubscribableProperty<T>) &&
                            method.DeclaringType != typeof(SubscribablePropertyDebugger) &&
                            !method.DeclaringType.FullName.Contains("SubscribableProperty"))
                        {
                            var declaringType = method.DeclaringType;

                            // 생성자인 경우 - 필드에서 생성된 것으로 추정
                            if (method.IsConstructor || method.Name.Contains("ctor"))
                            {
                                var propertyName = FindPropertyNameInType(declaringType);
                                return (declaringType, propertyName);
                            }

                            // 일반 메서드인 경우 - 인스턴스를 찾을 수 없으므로 타입만
                            if (!method.IsStatic)
                            {
                                var propertyName = FindPropertyNameInType(declaringType);
                                return (declaringType, propertyName);
                            }
                        }
                    }
                }
            }
            catch { /* ignore */ }

            return (null, $"Unknown_{GetHashCode()}");
        }

        private string FindPropertyNameInType(Type type)
        {
            try
            {
                // 타입의 SubscribableProperty 필드들 찾기
                var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var spFields = fields.Where(f => f.FieldType.IsGenericType &&
                                               f.FieldType.GetGenericTypeDefinition() == typeof(SubscribableProperty<>))
                                   .ToList();

                if (spFields.Count == 1)
                {
                    return CleanFieldName(spFields[0].Name);
                }
                else if (spFields.Count > 1)
                {
                    // 여러 개인 경우, 타입을 기준으로 추정
                    var matchingField = spFields.FirstOrDefault(f => f.FieldType == typeof(SubscribableProperty<T>));
                    if (matchingField != null)
                    {
                        return CleanFieldName(matchingField.Name);
                    }
                    return $"Property_{typeof(T).Name}_{GetHashCode()}";
                }
            }
            catch { /* ignore */ }

            return $"Property_{typeof(T).Name}";
        }

        private string CleanFieldName(string fieldName)
        {
            // _progressSP -> Progress, m_statusProperty -> Status 등
            return fieldName.TrimStart('_', 'm')
                           .Replace("SP", "")
                           .Replace("Property", "")
                           .Replace("Field", "");
        }
    }
}

#if UNITY_EDITOR
namespace Kylin.SubscribableProperty
{
    // 디버그 윈도우
    public class SubscribablePropertyDebugWindow : EditorWindow
    {
        private static SubscribablePropertyDebugWindow _instance;
        private static bool _isTestModeOn = false;

        private Vector2 _scrollPosition;
        private readonly Dictionary<PropertyDebugInfo, bool> _foldoutStates = new Dictionary<PropertyDebugInfo, bool>();
        private string _searchFilter = "";
        private bool _showOnlyWithSubscribers = true;
        private bool _autoRefresh = true;
        private double _lastRefreshTime;

        public static bool IsWindowOpen => _instance != null;
        public static bool IsTestModeOn => _isTestModeOn;

        [MenuItem("Tools/Subscribable Property Debugger")]
        public static void ShowWindow()
        {
            _instance = GetWindow<SubscribablePropertyDebugWindow>("구독 프로퍼티 디버거");
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
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawFilters();
            DrawPropertyList();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical("Box");

            EditorGUILayout.LabelField("구독 프로퍼티 디버그 도구", EditorStyles.boldLabel);

            EditorGUI.BeginDisabledGroup(Application.isPlaying);
            bool newTestMode = EditorGUILayout.Toggle("테스트 모드 활성화", _isTestModeOn);
            if (newTestMode != _isTestModeOn)
            {
                _isTestModeOn = newTestMode;
                if (!_isTestModeOn)
                {
                    SubscribablePropertyDebugger.Clear();
                }
            }
            EditorGUI.EndDisabledGroup();

            _autoRefresh = EditorGUILayout.Toggle("자동 새로고침", _autoRefresh);

            if (Application.isPlaying && _isTestModeOn)
            {
                EditorGUILayout.HelpBox("테스트 모드가 활성화되었습니다. 구독 활동을 모니터링 중입니다.", MessageType.Info);
            }
            else if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox("플레이 모드 중에는 테스트 모드가 비활성화됩니다. 플레이 모드 진입 전에 활성화하세요.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginVertical("Box");
            EditorGUILayout.LabelField("필터", EditorStyles.boldLabel);

            _searchFilter = EditorGUILayout.TextField("검색", _searchFilter);
            _showOnlyWithSubscribers = EditorGUILayout.Toggle("구독자가 있는 항목만 표시", _showOnlyWithSubscribers);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("새로고침"))
            {
                Repaint();
            }
            if (GUILayout.Button("모두 지우기"))
            {
                SubscribablePropertyDebugger.Clear();
                _foldoutStates.Clear();
            }
            if (GUILayout.Button("강제 GC"))
            {
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawPropertyList()
        {
            if (!_isTestModeOn)
            {
                EditorGUILayout.HelpBox("모니터링을 시작하려면 테스트 모드를 활성화하세요.", MessageType.Info);
                return;
            }

            var properties = SubscribablePropertyDebugger.GetAllPropertyInfos()
                .Where(p => p.IsAlive)
                .Where(p => !_showOnlyWithSubscribers || p.SubscriberCount > 0)
                .Where(p => string.IsNullOrEmpty(_searchFilter) ||
                           p.PropertyName.ToLower().Contains(_searchFilter.ToLower()) ||
                           p.OwnerTypeName.ToLower().Contains(_searchFilter.ToLower()))
                .OrderByDescending(p => p.SubscriberCount)
                .ThenBy(p => p.OwnerTypeName)
                .ThenBy(p => p.PropertyName)
                .ToList();

            var totalSubscribers = properties.Sum(p => p.SubscriberCount);
            EditorGUILayout.LabelField($"프로퍼티: {properties.Count} | 전체 구독자: {totalSubscribers}", EditorStyles.boldLabel);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            foreach (var propertyInfo in properties)
            {
                DrawPropertyInfo(propertyInfo);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawPropertyInfo(PropertyDebugInfo propertyInfo)
        {
            if (!_foldoutStates.ContainsKey(propertyInfo))
                _foldoutStates[propertyInfo] = false;

            EditorGUILayout.BeginVertical("Box");

            // 헤더
            EditorGUILayout.BeginHorizontal();

            var subscriberCount = propertyInfo.SubscriberCount;
            var headerText = $"[{subscriberCount}] {propertyInfo.OwnerTypeName}.{propertyInfo.PropertyName}";

            // 구독자 수에 따른 색상
            var prevColor = GUI.color;
            if (subscriberCount > 5)
                GUI.color = Color.red;
            else if (subscriberCount > 2)
                GUI.color = Color.yellow;
            else if (subscriberCount > 0)
                GUI.color = Color.green;

            _foldoutStates[propertyInfo] = EditorGUILayout.Foldout(_foldoutStates[propertyInfo], headerText, true);

            GUI.color = prevColor;

            EditorGUILayout.LabelField($"({propertyInfo.PropertyType.Name})", EditorStyles.miniLabel, GUILayout.Width(100));

            EditorGUILayout.EndHorizontal();

            // 상세 정보
            if (_foldoutStates[propertyInfo])
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("소유자:", propertyInfo.OwnerInstanceName);
                EditorGUILayout.LabelField("생성 시간:", propertyInfo.CreatedTime.ToString("HH:mm:ss"));

                if (subscriberCount > 0)
                {
                    EditorGUILayout.LabelField("구독자:", EditorStyles.boldLabel);
                    EditorGUI.indentLevel++;

                    foreach (var subscriber in propertyInfo.GetAliveSubscribers())
                    {
                        DrawSubscriberInfo(subscriber);
                    }

                    EditorGUI.indentLevel--;
                }
                else
                {
                    EditorGUILayout.LabelField("활성 구독자 없음", EditorStyles.miniLabel);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSubscriberInfo(SPSubscriberInfo subscriberInfo)
        {
            EditorGUILayout.BeginVertical("Box");

            EditorGUILayout.LabelField($"• {subscriberInfo.SubscriberInstanceName}.{subscriberInfo.MethodName}", EditorStyles.label);
            EditorGUILayout.LabelField($"  구독 시간: {subscriberInfo.SubscribeTime:HH:mm:ss}", EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(subscriberInfo.SubscribeCallStack))
            {
                if (GUILayout.Button("호출 스택 표시", EditorStyles.miniButton))
                {
                    Debug.Log($"{subscriberInfo.SubscriberInstanceName}의 구독 호출 스택:\n{subscriberInfo.SubscribeCallStack}");
                }
            }

            EditorGUILayout.EndVertical();
        }
    }
}
#endif
