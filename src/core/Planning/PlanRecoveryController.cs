using System.Security.Cryptography;
using System.Text;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    public sealed class PlanRecoveryObservation
    {
        public string Signature { get; init; } = "";
        public int RepeatCount { get; init; }
        public bool ShouldDiversify { get; init; }
        public bool ShouldStopThrashing { get; init; }
    }

    /// <summary>
    /// Detects ineffective recovery without knowing any user intent or tool name.
    /// Repeating the same rejected constraint signature is a runtime liveness
    /// failure, not evidence that another identical model retry will help.
    /// </summary>
    public sealed class PlanRecoveryController
    {
        private readonly Dictionary<string, int> _counts =
            new(StringComparer.Ordinal);
        private readonly int _diversifyThreshold;
        private readonly int _stopThreshold;

        public PlanRecoveryController(
            int diversifyThreshold = 2,
            int stopThreshold = 3)
        {
            if (diversifyThreshold < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(diversifyThreshold));
            if (stopThreshold < diversifyThreshold)
                throw new ArgumentOutOfRangeException(nameof(stopThreshold));
            _diversifyThreshold = diversifyThreshold;
            _stopThreshold = stopThreshold;
        }

        public PlanRecoveryObservation Observe(
            PlanAdmissibilityResult? admissibility,
            PlanValidationResult validation)
        {
            ArgumentNullException.ThrowIfNull(validation);
            string signature = BuildSignature(admissibility, validation);
            int count = _counts.TryGetValue(signature, out int current)
                ? current + 1
                : 1;
            _counts[signature] = count;
            return new PlanRecoveryObservation
            {
                Signature = signature,
                RepeatCount = count,
                ShouldDiversify = count >= _diversifyThreshold,
                ShouldStopThrashing = count >= _stopThreshold
            };
        }

        private static string BuildSignature(
            PlanAdmissibilityResult? admissibility,
            PlanValidationResult validation)
        {
            var issues = validation.Issues
                .Where(issue => issue.Severity == IssueSeverity.Error)
                .Select(issue =>
                    $"{issue.ConstraintClass}:{issue.Enforcement}:"
                    + $"{issue.ToolName}:{Normalize(issue.Description)}")
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToList();
            string canonical =
                $"{admissibility?.Decision.ToString() ?? "Unknown"}|"
                + string.Join("|", issues);
            byte[] digest = SHA256.HashData(
                Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(digest)
                .ToLowerInvariant()[..16];
        }

        private static string Normalize(string value) =>
            string.Join(
                " ",
                (value ?? "")
                    .Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries))
                .Trim()
                .ToLowerInvariant();
    }
}
