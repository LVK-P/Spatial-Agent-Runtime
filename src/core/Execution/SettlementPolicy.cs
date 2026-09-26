using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Typed settlement rules. Blacklists express "user or policy forbids this
    /// call again", never "the last attempt failed for another reason".
    /// </summary>
    public static class SettlementPolicy
    {
        /// <summary>
        /// Whether a blocked step should enter the per-turn tool-name blacklist.
        /// Host races, probes and dependency stops must not poison recovery.
        /// </summary>
        public static bool ShouldBlockToolName(StepExecutionResult result)
        {
            if (result.Status != StepResultStatus.Blocked)
                return false;
            if (result.UserApproved == false)
                return true;
            return result.Cause is
                StepOutcomeCause.UserRejected
                or StepOutcomeCause.BlockedByToolPolicy
                or StepOutcomeCause.BlockedByCallSignature;
        }

        public static StepOutcomeCause NormalizeCause(StepExecutionResult result)
        {
            if (result.Cause != StepOutcomeCause.None)
                return result.Cause;
            if (result.UserApproved == false)
                return StepOutcomeCause.UserRejected;
            return result.Status switch
            {
                StepResultStatus.Failed => StepOutcomeCause.FailedExecution,
                StepResultStatus.AwaitingApproval => StepOutcomeCause.ApprovalRequired,
                StepResultStatus.Blocked => StepOutcomeCause.SafetyPolicy,
                StepResultStatus.Skipped => StepOutcomeCause.BatchStopped,
                _ => StepOutcomeCause.None
            };
        }
    }
}
