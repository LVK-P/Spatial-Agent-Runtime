using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    public static class ApprovalProtocol
    {
        public static string ComputeCallSignature(string canonicalToolName, IReadOnlyDictionary<string, object?> args)
        {
            var normalized = new JObject
            {
                ["tool"] = canonicalToolName,
                ["args"] = NormalizeObject(args)
            };

            string json = normalized.ToString(Newtonsoft.Json.Formatting.None);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
            return "sig:" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string ComputeBatchSignature(IEnumerable<string> callSignatures)
        {
            string payload = string.Join("|", callSignatures
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OrderBy(s => s, StringComparer.Ordinal));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return "batch:" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string ComputeActionId(
            string goalContractFingerprint,
            IEnumerable<string> capabilities,
            ApprovalTarget target,
            TargetStateExpectation desiredState,
            IEnumerable<GoalParameterConstraint>? parameters = null,
            string fallbackCallSignature = "")
        {
            string capabilityPart = string.Join(",", capabilities
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
            string parameterPart = string.Join(",", (parameters
                    ?? Array.Empty<GoalParameterConstraint>())
                .Where(item => string.IsNullOrWhiteSpace(item.AppliesToCapability)
                    || capabilityPart.Contains(
                        item.AppliesToCapability,
                        StringComparison.OrdinalIgnoreCase))
                .Select(item =>
                    $"{item.AppliesToCapability}:{item.Key}:{item.Value}")
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
            string targetPart = string.IsNullOrWhiteSpace(target.Signature)
                ? string.Join("|", new[]
                {
                    target.Dataset, target.Layer, target.Field,
                    target.WhereClause, target.OutputTarget
                }.Select(item => item?.Trim() ?? ""))
                : target.Signature;
            string payload = string.Join("|", new[]
            {
                goalContractFingerprint?.Trim() ?? "",
                capabilityPart,
                targetPart,
                desiredState.ToString(),
                parameterPart,
                string.IsNullOrWhiteSpace(capabilityPart)
                    ? fallbackCallSignature
                    : ""
            });
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return "action:" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static ApprovalTarget BuildTarget(
            IReadOnlyDictionary<string, object?> args,
            IToolDescriptor? descriptor = null)
        {
            var dataflow = descriptor as IToolDataflowDescriptor;
            var parameters = descriptor?.Parameters;
            string dataset = FirstDeclared(args, dataflow?.InputParameters);
            if (string.IsNullOrWhiteSpace(dataset))
                dataset = ParameterSemanticResolver.FirstValue(
                    args, parameters, ParameterSemanticKind.InputDataset);
            if (string.IsNullOrWhiteSpace(dataset))
                dataset = First(args, "in_table", "in_dataset", "in_features", "input_table", "input_features", "inputs");
            string layer = First(args, "layer_name", "input_layer", "in_layer_or_view", "target_features");
            string field = NormalizeFieldTarget(ParameterSemanticResolver.FirstValue(
                args, parameters, ParameterSemanticKind.TargetField));
            if (string.IsNullOrWhiteSpace(field))
                field = NormalizeFieldTarget(ParameterSemanticResolver.FirstValue(
                    args, parameters, ParameterSemanticKind.Field));
            if (string.IsNullOrWhiteSpace(field))
                field = NormalizeFieldTarget(First(args, "field_name", "field", "fields", "calc_field_name"));
            string where = ParameterSemanticResolver.FirstValue(
                args, parameters, ParameterSemanticKind.Filter);
            if (string.IsNullOrWhiteSpace(where))
                where = First(args, "where_clause", "expression", "sql_expression");
            string output = "";
            if (!string.IsNullOrWhiteSpace(dataflow?.PrimaryOutputParameter))
                output = First(args, dataflow.PrimaryOutputParameter);
            if (string.IsNullOrWhiteSpace(output))
                output = FirstDeclared(args, dataflow?.OutputParameters);
            if (string.IsNullOrWhiteSpace(output))
            {
                var inferredOutput = args.FirstOrDefault(kv =>
                    ParameterNameClassifier.IsOutputParameter(kv.Key)
                    && kv.Value != null
                    && !string.IsNullOrWhiteSpace(kv.Value.ToString()));
                output = inferredOutput.Value?.ToString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(output))
                output = First(args, "out_dataset", "out_table", "out_feature_class", "out_raster",
                    "output", "output_table", "output_features", "output_feature_class");

            return new ApprovalTarget
            {
                Dataset = dataset,
                Layer = layer,
                Field = field,
                WhereClause = where,
                OutputTarget = output,
                Signature = ComputeTargetSignature(dataset, layer, field, where, output)
            };
        }

        public static ApprovalTicket CreateTicket(
            int stepId,
            ApprovalKind kind,
            AgentPermissionLevel? requiredPermissionLevel,
            string toolName,
            string canonicalToolName,
            IReadOnlyDictionary<string, object?> args,
            ApprovalEvidence evidence,
            ApprovalScope scope = ApprovalScope.ExactCall,
            ApprovalTarget? target = null)
        {
            target ??= BuildTarget(args);
            return new ApprovalTicket
            {
                Id = Guid.NewGuid().ToString("N"),
                StepId = stepId,
                Kind = kind,
                Scope = scope,
                RequiredPermissionLevel = requiredPermissionLevel,
                ToolName = toolName,
                CanonicalToolName = canonicalToolName,
                CallSignature = ComputeCallSignature(canonicalToolName, args),
                Target = target,
                Evidence = evidence,
                RequestedAtUtc = DateTime.UtcNow
            };
        }

        private static string ComputeTargetSignature(params string[] parts)
        {
            string payload = string.Join("|", parts.Select(p => p?.Trim() ?? ""));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return "target:" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static JToken NormalizeObject(object? value)
        {
            if (value == null)
                return JValue.CreateNull();

            if (value is IReadOnlyDictionary<string, object?> roDict)
            {
                var obj = new JObject();
                foreach (var kv in roDict.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    obj[kv.Key] = NormalizeObject(kv.Value);
                return obj;
            }

            if (value is IDictionary<string, object?> dict)
            {
                var obj = new JObject();
                foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    obj[kv.Key] = NormalizeObject(kv.Value);
                return obj;
            }

            if (value is System.Collections.IEnumerable seq && value is not string)
            {
                var arr = new JArray();
                foreach (var item in seq)
                    arr.Add(NormalizeObject(item));
                return arr;
            }

            return JToken.FromObject(value);
        }

        private static string First(IReadOnlyDictionary<string, object?> args, params string[] keys)
        {
            foreach (string key in keys)
            {
                var match = args.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Key) && match.Value != null)
                {
                    string text = match.Value.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            return "";
        }

        private static string FirstDeclared(
            IReadOnlyDictionary<string, object?> args,
            IReadOnlyList<string>? keys)
        {
            if (keys == null || keys.Count == 0)
                return "";
            return First(args, keys.ToArray());
        }

        private static string NormalizeFieldTarget(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var fields = raw
                .Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(token =>
                {
                    int whitespaceIndex = token.IndexOf(' ');
                    return whitespaceIndex > 0 ? token[..whitespaceIndex].Trim() : token.Trim();
                })
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return fields.Count == 0 ? raw.Trim() : string.Join(", ", fields);
        }
    }
}
