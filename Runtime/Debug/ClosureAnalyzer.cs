using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Kylin.SubscribableProperty
{
    /// <summary>
    /// 클로저 캡처 정보
    /// </summary>
    public class ClosureCaptureInfo
    {
        public WeakReference ClosureRef { get; }
        public Type ClosureType { get; }
        public string ClosureTypeName { get; }
        public DateTime CaptureTime { get; }
        public string CaptureLocation { get; }

        // 캡처된 변수들
        public List<CapturedVariableInfo> CapturedVariables { get; }

        // 메모리 정보
        public long EstimatedMemoryBytes { get; private set; }
        public bool IsAlive => ClosureRef.IsAlive;
        public object Closure => ClosureRef.Target;

        // 구독 해제 정보
        public bool IsUnsubscribed { get; set; }
        public DateTime? UnsubscribeTime { get; set; }
        public TimeSpan? Lifetime => UnsubscribeTime.HasValue
            ? UnsubscribeTime.Value - CaptureTime
            : (DateTime.Now - CaptureTime);

        public ClosureCaptureInfo(object closure, string captureLocation)
        {
            ClosureRef = new WeakReference(closure);
            ClosureType = closure.GetType();
            ClosureTypeName = GetReadableTypeName(ClosureType);
            CaptureTime = DateTime.Now;
            CaptureLocation = captureLocation;
            CapturedVariables = new List<CapturedVariableInfo>();

            AnalyzeCapturedVariables();
        }

        private string GetReadableTypeName(Type type)
        {
            // <>c__DisplayClass1_0 → Closure_1_0
            if (type.Name.Contains("DisplayClass"))
            {
                var parts = type.Name.Split('_');
                if (parts.Length >= 3)
                {
                    return $"Closure_{parts[^2]}_{parts[^1]}";
                }
            }
            return type.Name;
        }

        private void AnalyzeCapturedVariables()
        {
            if (!IsAlive) return;

            var closure = Closure;
            var fields = ClosureType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(closure);
                    var varInfo = new CapturedVariableInfo(field, value);
                    CapturedVariables.Add(varInfo);
                    EstimatedMemoryBytes += varInfo.EstimatedMemoryBytes;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClosureAnalyzer] Failed to analyze field {field.Name}: {ex.Message}");
                }
            }
        }

        public void MarkUnsubscribed()
        {
            IsUnsubscribed = true;
            UnsubscribeTime = DateTime.Now;
        }

        public void RefreshMemoryInfo()
        {
            EstimatedMemoryBytes = 0;
            foreach (var varInfo in CapturedVariables)
            {
                varInfo.RefreshMemoryInfo();
                EstimatedMemoryBytes += varInfo.EstimatedMemoryBytes;
            }
        }
    }

    /// <summary>
    /// 캡처된 개별 변수 정보
    /// </summary>
    public class CapturedVariableInfo
    {
        public string FieldName { get; }
        public Type FieldType { get; }
        public string FieldTypeName { get; }
        public WeakReference ValueRef { get; }
        public bool IsReferenceType { get; }
        public long EstimatedMemoryBytes { get; private set; }

        // 위험도 평가
        public ClosureCaptureRisk RiskLevel { get; private set; }
        public string RiskDescription { get; private set; }

        public bool IsAlive => ValueRef?.IsAlive ?? false;
        public object Value => ValueRef?.Target;

        public CapturedVariableInfo(FieldInfo field, object value)
        {
            FieldName = CleanFieldName(field.Name);
            FieldType = field.FieldType;
            FieldTypeName = GetReadableTypeName(field.FieldType);
            IsReferenceType = !field.FieldType.IsValueType;

            if (value != null && IsReferenceType)
            {
                ValueRef = new WeakReference(value);
            }

            EstimateMemory(value);
            EvaluateRisk(field, value);
        }

        private string CleanFieldName(string fieldName)
        {
            // <>4__this → this
            if (fieldName.Contains("4__this"))
                return "this";

            // CS$<>8__locals1 → locals_1
            if (fieldName.Contains("CS$"))
                return "locals";

            return fieldName.TrimStart('<', '>');
        }

        private string GetReadableTypeName(Type type)
        {
            if (type.IsGenericType)
            {
                var name = type.Name.Split('`')[0];
                var args = string.Join(", ", type.GetGenericArguments().Select(t => t.Name));
                return $"{name}<{args}>";
            }
            return type.Name;
        }

        private void EstimateMemory(object value)
        {
            if (value == null)
            {
                EstimatedMemoryBytes = 0;
                return;
            }

            if (!IsReferenceType)
            {
                EstimatedMemoryBytes = System.Runtime.InteropServices.Marshal.SizeOf(FieldType);
                return;
            }

            // 참조 타입 메모리 추정
            EstimatedMemoryBytes = 8; // 참조 자체

            try
            {
                if (value is string str)
                {
                    EstimatedMemoryBytes += str.Length * 2 + 24; // UTF-16 + 오버헤드
                }
                else if (value is Array array)
                {
                    EstimatedMemoryBytes += array.Length * 8 + 24;
                }
                else if (value is System.Collections.ICollection collection)
                {
                    EstimatedMemoryBytes += collection.Count * 8 + 40;
                }
                else if (value is MonoBehaviour || value is ScriptableObject)
                {
                    EstimatedMemoryBytes += 1024; // Unity 객체 추정치
                }
                else
                {
                    EstimatedMemoryBytes += 64; // 일반 객체 추정치
                }
            }
            catch
            {
                EstimatedMemoryBytes += 64;
            }
        }

        private void EvaluateRisk(FieldInfo field, object value)
        {
            RiskLevel = ClosureCaptureRisk.Low;
            RiskDescription = "안전";

            if (value == null) return;

            // this 캡처 (가장 위험)
            if (field.Name.Contains("4__this"))
            {
                RiskLevel = ClosureCaptureRisk.Critical;
                RiskDescription = "인스턴스 전체 캡처 - 모든 필드가 메모리에 유지됨";
                return;
            }

            // 큰 컬렉션
            if (value is System.Collections.ICollection collection && collection.Count > 100)
            {
                RiskLevel = ClosureCaptureRisk.High;
                RiskDescription = $"대용량 컬렉션 캡처 ({collection.Count}개 요소)";
                return;
            }

            // Unity 객체
            if (value is MonoBehaviour || value is ScriptableObject)
            {
                RiskLevel = ClosureCaptureRisk.High;
                RiskDescription = "Unity 컴포넌트 캡처 - 게임 오브젝트와 연결된 모든 데이터 유지";
                return;
            }

            // 큰 배열/버퍼
            if (value is Array array && array.Length > 1000)
            {
                RiskLevel = ClosureCaptureRisk.High;
                RiskDescription = $"대용량 배열 캡처 ({array.Length}개 요소, ~{EstimatedMemoryBytes / 1024}KB)";
                return;
            }

            // 중간 위험
            if (EstimatedMemoryBytes > 1024) // 1KB 이상
            {
                RiskLevel = ClosureCaptureRisk.Medium;
                RiskDescription = $"메모리 사용량 높음 (~{EstimatedMemoryBytes / 1024}KB)";
                return;
            }

            // 참조 타입이지만 작은 객체
            if (IsReferenceType)
            {
                RiskLevel = ClosureCaptureRisk.Low;
                RiskDescription = "작은 참조 타입 캡처";
            }
        }

        public void RefreshMemoryInfo()
        {
            if (IsAlive)
            {
                EstimateMemory(Value);
            }
        }
    }

    public enum ClosureCaptureRisk
    {
        Low,      // 안전 (값 타입, 작은 참조)
        Medium,   // 주의 (중간 크기 객체)
        High,     // 위험 (큰 객체, Unity 컴포넌트)
        Critical  // 매우 위험 (this 캡처)
    }

    /// <summary>
    /// 클로저 캡처 프로파일러
    /// </summary>
    public static class ClosureProfiler
    {
        private static readonly List<ClosureCaptureInfo> _captures = new List<ClosureCaptureInfo>();
        private static readonly object _lock = new object();

        public static bool IsEnabled =>
#if UNITY_EDITOR
            ClosureProfilerWindow.IsWindowOpen && ClosureProfilerWindow.IsProfilingEnabled;
#else
            false;
#endif

        public static void RecordCapture(object closure, string location)
        {
            if (!IsEnabled) return;
            if (closure == null) return;

            // 컴파일러 생성 클로저인지 확인
            var typeName = closure.GetType().Name;
            if (!typeName.Contains("DisplayClass") && !typeName.Contains("<>c"))
                return;

            lock (_lock)
            {
                var info = new ClosureCaptureInfo(closure, location);
                _captures.Add(info);
            }
        }

        public static void RecordUnsubscribe(object closure)
        {
            if (closure == null) return;

            lock (_lock)
            {
                var info = _captures.FirstOrDefault(c =>
                    c.IsAlive && ReferenceEquals(c.Closure, closure));

                if (info != null)
                {
                    info.MarkUnsubscribed();
                }
            }
        }

        public static IEnumerable<ClosureCaptureInfo> GetAllCaptures()
        {
            lock (_lock)
            {
                // 죽은 클로저는 제거하지 않음 (히스토리 유지)
                return _captures.ToList();
            }
        }

        public static IEnumerable<ClosureCaptureInfo> GetActiveCaptures()
        {
            lock (_lock)
            {
                return _captures.Where(c => c.IsAlive && !c.IsUnsubscribed).ToList();
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _captures.Clear();
            }
        }

        public static (int total, int active, int unsubscribed, long totalMemory) GetStatistics()
        {
            lock (_lock)
            {
                var total = _captures.Count;
                var active = _captures.Count(c => c.IsAlive && !c.IsUnsubscribed);
                var unsubscribed = _captures.Count(c => c.IsUnsubscribed);
                var totalMemory = _captures
                    .Where(c => c.IsAlive && !c.IsUnsubscribed)
                    .Sum(c => c.EstimatedMemoryBytes);

                return (total, active, unsubscribed, totalMemory);
            }
        }

        public static void RefreshMemoryInfo()
        {
            lock (_lock)
            {
                foreach (var capture in _captures.Where(c => c.IsAlive))
                {
                    capture.RefreshMemoryInfo();
                }
            }
        }
    }
}
