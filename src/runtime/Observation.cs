using System;
using System.Collections.Generic;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Simple Observation implementation.
    /// </summary>
    /// <remarks>中文：简单观察实现。</remarks>
    public class Observation : IObservation
    {
        public string ToolName { get; set; } = "";
        public string ToolCategory { get; set; } = "";
        public TimeSpan Elapsed { get; set; }
        public ObservationOutput Output { get; set; } = new();
        public IReadOnlyList<string> Warnings
        {
            get => _warnings.AsReadOnly();
            set { _warnings = new List<string>(value ?? new List<string>()); }
        }
        public IReadOnlyList<string> Artifacts { get; set; } = new List<string>();

        private List<string> _warnings = new();

        /// <summary>Plan step index, set by Scheduler for transcript and validation evidence.</summary>
        /// <remarks>中文：计划步骤索引，由调度器设置用于转录记录和验证证据。</remarks>
        public int StepId { get; set; }

        /// <summary>Input parameters from the tool call — needed by validators to reconstruct targets.</summary>
        /// <remarks>中文：工具调用的输入参数——验证者重建目标所需的参数。</remarks>
        public Dictionary<string, object?> InputParameters { get; set; } = new();

        /// <summary>Tool verification rule (from ToolDefinition, for dynamic tools without GPToolContract).</summary>
        /// <remarks>中文：工具验证规则（来自 ToolDefinition，适用于没有 GPToolContract 的动态工具）。</remarks>
        public VerificationRule? ToolVerification { get; set; }

        public IReadOnlyList<ParameterScopedProofRequirement> ProofRequirements
        { get; set; } = Array.Empty<ParameterScopedProofRequirement>();

        /// <summary>Add a warning after validation.</summary>
        /// <remarks>中文：验证后添加警告。</remarks>
        public void AddWarning(string warning)
        {
            _warnings.Add(warning);
        }
    }
}
