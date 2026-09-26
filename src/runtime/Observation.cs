using System;
using System.Collections.Generic;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Simple Observation implementation.
    /// </summary>
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
        public int StepId { get; set; }

        /// <summary>Input parameters from the tool call — needed by validators to reconstruct targets.</summary>
        public Dictionary<string, object?> InputParameters { get; set; } = new();

        /// <summary>Tool verification rule (from ToolDefinition, for dynamic tools without GPToolContract).</summary>
        public VerificationRule? ToolVerification { get; set; }

        public IReadOnlyList<ParameterScopedProofRequirement> ProofRequirements
        { get; set; } = Array.Empty<ParameterScopedProofRequirement>();

        /// <summary>Add a warning after validation.</summary>
        public void AddWarning(string warning)
        {
            _warnings.Add(warning);
        }
    }
}
