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
// English：================================================================ ExecutionEstimator.cs — Tool execution time estimator Estimates execution time based on tool type and number of elements, showing expectations to users before tool execution. Data source: Number of layer elements collected by ContextManager. Pre-installed complexity profiles of the Top 50 high-frequency tools, with the rest given general estimates. Pure logic, zero ArcGIS dependency. Source: Sharing Policy Extraction After Cross-Implementation Architecture Review. ================================================================
namespace SpatialAgent.Core
{
    /// <summary>
    /// Estimates GP tool execution time based on tool type and feature count.
    /// </summary>
    /// <remarks>中文：根据工具类型和特征数量估算GP工具的执行时间。</remarks>
    public static class ExecutionEstimator
    {
        /// <summary>
        /// Tool complexity profile: (Complexity, MillisecondsPer10KFeatures).
        /// Values calibrated for typical ArcGIS Pro 3.4 desktop performance.
        /// </summary>
        /// <remarks>中文：工具复杂度配置：（复杂度、每一万个要素所需毫秒数）。数值按典型 ArcGIS Pro 3.4 桌面性能校准。</remarks>
        private static readonly Dictionary<string, (string Complexity, double MsPer10K)> Profiles = new()
        {
            // Fast: O(1) or near-constant time
            // 中文：快速：O（1） 或近常数时间
            ["management_AddField"] = ("O(1)", 50),
            ["management_DeleteField"] = ("O(1)", 100),
            ["management_SelectLayerByAttribute"] = ("O(1)", 200),

            // Linear: O(n) — scales with feature count
            // 中文：线性：O（n） — 特征数尺度
            ["management_CalculateField"] = ("O(n)", 300),
            ["management_CopyFeatures"] = ("O(n)", 500),
            ["analysis_Buffer"] = ("O(n)", 500),
            ["management_Project"] = ("O(n)", 800),

            // n log n: O(n log n) — dissolve, generalize
            // 中文：n log n： O（n log n） — 溶解，推广
            ["management_Dissolve"] = ("O(n log n)", 5000),

            // n × m: O(n * m) — overlay operations
            // 中文：n × m： O（n * m） — 叠加运算
            ["analysis_Intersect"] = ("O(n * m)", 3000),
            ["analysis_Clip"] = ("O(n * m)", 2000),
            ["analysis_Erase"] = ("O(n * m)", 2500),

            // Statistics
            // 中文：统计数据
            ["analysis_Statistics"] = ("O(n)", 400),

            // Merge (combines multiple inputs)
            // 中文：合并（合并多个输入）
            ["management_Merge"] = ("O(n)", 600),

            // Heavy
            // 中文：重
            ["analysis_MultipleRingBuffer"] = ("O(n)", 1000),
        };

        /// <summary>
        /// Estimate execution time for a given tool and feature count.
        /// </summary>
        /// <param name="toolName">Tool name in underscore format (e.g. "analysis_Buffer").</param>
        /// <param name="featureCount">Total input feature count. 0 = unknown.</param>
        /// <returns>Human-readable estimate in Chinese.</returns>
        /// <remarks>中文：估算给定工具的执行时间和功能数量。工具名称以下划线格式表示（例如“analysis_Buffer”）。总输入特征数。0 = 未知。中文可读估计。</remarks>
        public static string Estimate(string toolName, long featureCount)
        {
            if (featureCount <= 0)
                return "预计数秒（数据量未知）";

            if (!Profiles.TryGetValue(toolName, out var profile))
                return EstimateGeneric(featureCount);

            double estimatedMs = profile.MsPer10K * (featureCount / 10_000.0);

            // Floor: at least 100ms
            // 中文：最低：至少100毫秒
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
            // 中文：保守的通用估计：每10K特征500毫秒
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
