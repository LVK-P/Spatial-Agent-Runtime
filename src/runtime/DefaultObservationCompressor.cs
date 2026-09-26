using System.Text;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    public sealed class DefaultObservationCompressor : IObservationCompressor
    {
        public ObservationDigest Compress(StepExecutionResult step, IObservation? observation = null, int maxRawChars = 240)
        {
            var digest = new ObservationDigest
            {
                StepId = step.StepId,
                ToolName = step.ToolName,
                Status = step.Status.ToString(),
                Success = step.IsSuccessful,
                Warnings = observation?.Warnings?.ToList() ?? new List<string>()
            };

            if (observation != null)
            {
                digest.GeometryType = observation.Output.GeometryType;
                digest.CRS = observation.Output.CRS;
                digest.FeatureCount = observation.Output.FeatureCount;
                digest.RowCount = observation.Output.RowCount;
                digest.Fields = observation.Output.Fields?.ToList() ?? new List<string>();
                digest.Artifacts = observation.Artifacts?.ToList() ?? new List<string>();
            }

            string? raw = observation?.Output.Raw
                ?? step.ToolResult?.Output
                ?? step.ToolResult?.Error
                ?? step.BlockReason
                ?? step.ApprovalReason;
            if (!string.IsNullOrWhiteSpace(raw))
                digest.RawExcerpt = TrimTo(raw.Trim(), maxRawChars);

            if (!string.IsNullOrWhiteSpace(step.BackupPath))
                digest.OutputPath = step.BackupPath;

            digest.Summary = BuildSummary(digest);
            return digest;
        }

        public string Format(ObservationDigest digest)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{digest.ToolName} | {digest.Status} | success={digest.Success}");

            if (!string.IsNullOrWhiteSpace(digest.Summary))
                sb.AppendLine($"summary: {digest.Summary}");

            if (digest.FeatureCount.HasValue)
                sb.AppendLine($"feature_count: {digest.FeatureCount}");

            if (digest.RowCount.HasValue)
                sb.AppendLine($"row_count: {digest.RowCount}");

            if (!string.IsNullOrWhiteSpace(digest.GeometryType))
                sb.AppendLine($"geometry: {digest.GeometryType}");

            if (!string.IsNullOrWhiteSpace(digest.CRS))
                sb.AppendLine($"crs: {digest.CRS}");

            if (digest.Fields.Count > 0)
                sb.AppendLine($"fields: {string.Join(", ", digest.Fields)}");

            if (!string.IsNullOrWhiteSpace(digest.OutputPath))
                sb.AppendLine($"output: {digest.OutputPath}");

            if (digest.Warnings.Count > 0)
                sb.AppendLine($"warnings: {string.Join(" | ", digest.Warnings.Take(4))}");

            if (!string.IsNullOrWhiteSpace(digest.RawExcerpt))
                sb.AppendLine($"raw: {digest.RawExcerpt}");

            if (digest.Artifacts.Count > 0)
                sb.AppendLine($"artifacts: {string.Join(", ", digest.Artifacts.Take(4))}");

            return sb.ToString().TrimEnd();
        }

        private static string BuildSummary(ObservationDigest digest)
        {
            if (digest.Success)
            {
                if (digest.FeatureCount.HasValue)
                    return $"produced {digest.FeatureCount.Value} features";

                if (digest.RowCount.HasValue)
                    return $"produced {digest.RowCount.Value} rows";

                if (!string.IsNullOrWhiteSpace(digest.GeometryType))
                    return $"produced {digest.GeometryType} output";
            }
            else if (!string.IsNullOrWhiteSpace(digest.RawExcerpt))
            {
                return digest.RawExcerpt;
            }

            return digest.Success ? "completed" : "failed";
        }

        private static string TrimTo(string value, int maxChars)
        {
            if (value.Length <= maxChars)
                return value;

            if (maxChars <= 1)
                return value[..1];

            return value[..(maxChars - 1)] + "…";
        }
    }
}
