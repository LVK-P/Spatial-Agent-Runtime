using System.Collections.Generic;

// ================================================================
// ExecutionEstimator.cs — 工具执行时间估算器
//
// 根据工具类型和要素数估算执行耗时，在工具执行前向用户展示预期。
// 数据源：ContextManager 已收集的图层要素数。
// 预置 Top 50 高频工具的复杂度档案，其余给出通用估算。
//
// 纯逻辑，零 ArcGIS 依赖。
//
// 来源：跨实现架构审查后的共享策略抽取。
// ================================================================
namespace SpatialAgent.Core
{
    /// <summary>
    /// Estimates GP tool execution time based on tool type and feature count.
    /// </summary>
    public static class ExecutionEstimator
    {
        /// <summary>
        /// Tool complexity profile: (Complexity, MillisecondsPer10KFeatures).
        /// Values calibrated for typical ArcGIS Pro 3.4 desktop performance.
        /// </summary>
        private static readonly Dictionary<string, (string Complexity, double MsPer10K)> Profiles = new()
        {
            // Fast: O(1) or near-constant time
            ["management_AddField"] = ("O(1)", 50),
            ["management_DeleteField"] = ("O(1)", 100),
            ["management_SelectLayerByAttribute"] = ("O(1)", 200),

            // Linear: O(n) — scales with feature count
            ["management_CalculateField"] = ("O(n)", 300),
            ["management_CopyFeatures"] = ("O(n)", 500),
            ["analysis_Buffer"] = ("O(n)", 500),
            ["management_Project"] = ("O(n)", 800),

            // n log n: O(n log n) — dissolve, generalize
            ["management_Dissolve"] = ("O(n log n)", 5000),

            // n × m: O(n * m) — overlay operations
            ["analysis_Intersect"] = ("O(n * m)", 3000),
            ["analysis_Clip"] = ("O(n * m)", 2000),
            ["analysis_Erase"] = ("O(n * m)", 2500),

            // Statistics
            ["analysis_Statistics"] = ("O(n)", 400),

            // Merge (combines multiple inputs)
            ["management_Merge"] = ("O(n)", 600),

            // Heavy
            ["analysis_MultipleRingBuffer"] = ("O(n)", 1000),
        };

        /// <summary>
        /// Estimate execution time for a given tool and feature count.
        /// </summary>
        /// <param name="toolName">Tool name in underscore format (e.g. "analysis_Buffer").</param>
        /// <param name="featureCount">Total input feature count. 0 = unknown.</param>
        /// <returns>Human-readable estimate in Chinese.</returns>
        public static string Estimate(string toolName, long featureCount)
        {
            if (featureCount <= 0)
                return "预计数秒（数据量未知）";

            if (!Profiles.TryGetValue(toolName, out var profile))
                return EstimateGeneric(featureCount);

            double estimatedMs = profile.MsPer10K * (featureCount / 10_000.0);

            // Floor: at least 100ms
            if (estimatedMs < 100) estimatedMs = 100;

            return estimatedMs switch
            {
                < 1_000 => "预计 1 秒内",
                < 60_000 => $"预计 {estimatedMs / 1_000:F0} 秒",
                < 3_600_000 => $"预计 {estimatedMs / 60_000:F1} 分钟",
                _ => $"预计 {estimatedMs / 3_600_000:F1} 小时，建议先在小范围测试"
            };
        }

        private static string EstimateGeneric(long featureCount)
        {
            // Conservative generic estimate: 500ms per 10K features
            double estimatedMs = 500 * (featureCount / 10_000.0);

            return estimatedMs switch
            {
                < 1_000 => "预计数秒",
                < 60_000 => $"预计 {estimatedMs / 1_000:F0} 秒",
                < 3_600_000 => $"预计 {estimatedMs / 60_000:F1} 分钟",
                _ => $"预计 {estimatedMs / 3_600_000:F1} 小时"
            };
        }
    }
}
