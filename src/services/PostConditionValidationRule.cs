using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using SpatialAgent.Tools.Core;

// PostConditionValidationRule.cs — IValidationRule adapter for post-condition checks.
// After every tool execution:
//   1. Look up the tool's GPToolContract → get default verification rules.
//   2. Extract verification targets (layer names, field names) from input params.
//   3. Call PostConditionVerifier to run the actual GIS-state probe.
//   4. Publish detailed verification event to EventBus (expected/actual/sample).
//   5. Record to VerificationLedger (independent audit file).
//   6. Return PASS / WARN / FAIL verdict.
// 中文：PostConditionValidationRule.cs — 用于后置条件检查的 IValidationRule 适配器。每次工具执行后：1. 查找工具的 GPToolContract 并获取默认验证规则；2. 从输入参数提取验证目标（图层名、字段名）；3. 调用 PostConditionVerifier 运行实际 GIS 状态探测；4. 向事件总线发布详细验证事件（预期值/实际值/样本）；5. 写入 VerificationLedger（独立审计文件）；6. 返回通过/警告/失败结论。
namespace SpatialAgent.Services
{
    public class PostConditionValidationRule : IValidationRule
    {
        private readonly PostConditionVerifier _verifier;
        private readonly IEventBus? _eventBus;
        private readonly ITranscriptToolWriter? _transcript;
        private readonly VerificationLedger? _ledger;

        public string Name => "post_condition";
        public string Description => "后置条件验证：检查字段存在、要素数量、坐标系等";

        /// <summary>Current turn number (set by AgentRuntime before each turn).</summary>
        /// <remarks>中文：当前回合数（由AgentRuntime在每回合前设定）。</remarks>
        public int CurrentTurn { get; set; }

        public PostConditionValidationRule(
            IPostConditionProbe probe,
            IEventBus? eventBus = null,
            ITranscriptToolWriter? transcript = null,
            VerificationLedger? ledger = null)
        {
            _verifier = new PostConditionVerifier(probe);
            _eventBus = eventBus;
            _transcript = transcript;
            _ledger = ledger;
        }

        public bool AppliesTo(string toolName, IObservation observation)
        {
            // 1. Check GPToolContract (core tools with fine-grained verification)
            // 中文：1. 查看GPToolContract（带有细粒度验证的核心工具）
            var contract = GPToolContract.Get(toolName);
            if (contract != null && contract.DefaultVerifications.Count > 0)
                return true;

            // 2. Fallback: check tool's own Verification rule (dynamic tools from manifest)
            // 中文：2. 备用：检查工具自身的验证规则（动态工具来自清单）
            if (observation is Observation concrete && concrete.ToolVerification != null)
                return true;

            if (observation is Observation proofObservation
                && proofObservation.ProofRequirements.Count > 0)
                return true;

            return false;
        }

        public async Task<ValidationResult> ValidateAsync(IObservation observation)
        {
            var contract = GPToolContract.Get(observation.ToolName);
            bool hasContractRules = contract != null && contract.DefaultVerifications.Count > 0;

            // Fallback: use tool verification from ToolDefinition (dynamic tools)
            // 中文：备选：使用ToolDefinition（动态工具）中的工具验证
            var toolVerification = (observation is Observation obsConcrete) ? obsConcrete.ToolVerification : null;
            bool hasToolRule = toolVerification != null;
            var proofRequirements = (observation as Observation)?
                .ProofRequirements
                ?? Array.Empty<ParameterScopedProofRequirement>();

            if (!hasContractRules && !hasToolRule
                && proofRequirements.Count == 0)
                return new ValidationResult
                {
                    RuleName = Name,
                    Verdict = ValidationVerdict.PASS,
                    Detail = "无验证规则"
                };

            var concreteObservation = observation as Observation;
            var inputParams = concreteObservation != null
                ? concreteObservation.InputParameters
                : new Dictionary<string, object?>();

            var failures = new List<string>();
            var warnings = new List<string>();
            int passed = 0;
            var satisfiedProofIds = new List<string>();
            int stepNumber = concreteObservation?.StepId ?? 0;

            // Build rule list: contract rules first, fall back to tool own rule
            // 中文：构建规则列表：先用合同规则，回归工具自有规则
            var rules = hasContractRules
                ? contract!.DefaultVerifications
                : (hasToolRule ? new List<VerificationRule> { toolVerification! } : new List<VerificationRule>());

            foreach (var rule in rules)
            {
                // Pass contract when available so ResolveOutputTarget can use OutputIndex
                // 中文：有合约时请传递，这样ResolveOutputTarget可以使用OutputIndex
                string target = ResolveTarget(rule.Type, rule.Target, contract, inputParams, rule.Params);
                string verifyExpected = ResolveExpected(rule.Type, rule.Expected, contract, inputParams, rule.Params);
                string humanExpected = DescribeExpected(
                    rule.Type, rule.Expected, inputParams, contract, rule.Params, rule.Invert);

                if (string.IsNullOrEmpty(target))
                {
                    warnings.Add($"无法确定 {rule.Type} 的验证目标");
                    continue;
                }

                var filledRule = new VerificationRule
                {
                    Type = rule.Type,
                    Target = target,
                    Expected = verifyExpected,  // ← 实际验证参数。 / The actual verification parameter.
                    FixHint = rule.FixHint,
                    Invert = rule.Invert
                };

                var sw = Stopwatch.StartNew();
                try
                {
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "tool.progress",
                        Turn = CurrentTurn,
                        Metadata = new()
                        {
                            ["tool"] = observation.ToolName,
                            ["step"] = stepNumber.ToString(),
                            ["phase"] = "verification"
                        },
                        Data = new()
                        {
                            ["message"] = $"正在验证：{rule.Type}"
                        }
                    });

                    var (passed_check, detail) = await _verifier.VerifyAsync(filledRule, observation.Output.Raw ?? "");
                    sw.Stop();

                    // Describe actual state
                    // 中文：描述实际状态
                    string actual = passed_check ? detail : detail.Replace("验证通过", "").Replace("验证失败", "").Trim();
                    object? sample = ExtractSample(rule.Type, target, observation);

                    // ── Publish rich verification event ──
                    // 中文：发布富验证事件
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = passed_check ? "verification.passed" : "verification.failed",
                        Turn = CurrentTurn,
                        Metadata = new()
                        {
                            ["tool"] = observation.ToolName,
                            ["rule"] = rule.Type,
                            ["target"] = target,
                            ["verdict"] = passed_check ? "PASS" : "FAIL"
                        },
                        Data = new()
                        {
                            ["expected"] = humanExpected,
                            ["actual"] = actual,
                            ["sample"] = sample,
                            ["elapsed_ms"] = sw.ElapsedMilliseconds,
                            ["detail"] = detail
                        }
                    });

                    // ── Record to Verification Ledger ──
                    // 中文：记录至验证账本
                    _ledger?.Record(new VerificationEntry
                    {
                        Turn = CurrentTurn,
                        Step = stepNumber,
                        ToolName = observation.ToolName,
                        RuleType = rule.Type,
                        Target = target,
                        Expected = humanExpected,
                        Actual = actual,
                        Sample = sample,
                        QueryMethod = $"PostConditionVerifier.{rule.Type}({target})",
                        Verdict = passed_check ? "PASS" : "FAIL",
                        Detail = detail,
                        ElapsedMs = sw.ElapsedMilliseconds
                    });

                    // ── Record to Transcript ──
                    // 中文：记录到文字记录
                    _transcript?.WriteVerification(
                        CurrentTurn, stepNumber, observation.ToolName,
                        rule.Type, target, humanExpected, actual,
                        passed_check ? "PASS" : "FAIL", sample, detail);

                    if (!passed_check)
                        failures.Add($"{rule.Type}: {detail}");
                    else
                        passed++;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    warnings.Add($"{rule.Type} 异常: {ex.Message}");

                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "verification.error",
                        Turn = CurrentTurn,
                        Metadata = new()
                        {
                            ["tool"] = observation.ToolName,
                            ["rule"] = rule.Type,
                            ["target"] = target
                        },
                        Data = new() { ["error"] = ex.Message }
                    });
                }
            }

            foreach (ParameterScopedProofRequirement requirement in proofRequirements)
            {
                var sw = Stopwatch.StartNew();
                ParameterScopedProofResult proofResult;
                try
                {
                    proofResult = await _verifier
                        .VerifyParameterScopedProofAsync(requirement);
                }
                catch (Exception ex)
                {
                    proofResult = new ParameterScopedProofResult
                    {
                        Supported = false,
                        Passed = false,
                        Detail = $"参数语义证明异常：{ex.Message}"
                    };
                }
                sw.Stop();
                bool proofPassed = proofResult.Supported && proofResult.Passed;
                string target = $"{requirement.Layer}.{requirement.Field}";
                string expected =
                    $"capability={requirement.CapabilityId};"
                    + $"method={requirement.MeasurementMethod};"
                    + $"unit={requirement.Unit};"
                    + $"decimal_places={requirement.DecimalPlaces};"
                    + $"rounding={requirement.RoundingMode}";
                var sample = new
                {
                    sampled = proofResult.SampledCount,
                    mismatches = proofResult.MismatchCount,
                    max_absolute_error = proofResult.MaximumAbsoluteError,
                    max_allowed_absolute_error =
                        proofResult.MaximumAllowedAbsoluteError
                };
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = proofPassed
                        ? "verification.passed"
                        : "verification.failed",
                    Turn = CurrentTurn,
                    Metadata = new()
                    {
                        ["tool"] = observation.ToolName,
                        ["rule"] = "verify_parameter_semantics",
                        ["target"] = target,
                        ["proof_id"] = requirement.ProofObligationId,
                        ["verdict"] = proofPassed ? "PASS" : "FAIL"
                    },
                    Data = new()
                    {
                        ["expected"] = expected,
                        ["actual"] = proofResult.Detail,
                        ["sample"] = sample,
                        ["elapsed_ms"] = sw.ElapsedMilliseconds
                    }
                });
                _ledger?.Record(new VerificationEntry
                {
                    Turn = CurrentTurn,
                    Step = stepNumber,
                    ToolName = observation.ToolName,
                    RuleType = "verify_parameter_semantics",
                    Target = target,
                    Expected = expected,
                    Actual = proofResult.Detail,
                    Sample = sample,
                    QueryMethod =
                        $"PostConditionVerifier.VerifyParameterScopedProof({target})",
                    Verdict = proofPassed ? "PASS" : "FAIL",
                    Detail = proofResult.Detail,
                    ElapsedMs = sw.ElapsedMilliseconds
                });
                _transcript?.WriteVerification(
                    CurrentTurn,
                    stepNumber,
                    observation.ToolName,
                    "verify_parameter_semantics",
                    target,
                    expected,
                    proofResult.Detail,
                    proofPassed ? "PASS" : "FAIL",
                    sample,
                    proofResult.Detail);

                if (proofPassed)
                {
                    passed++;
                    satisfiedProofIds.Add(requirement.ProofObligationId);
                }
                else
                {
                    failures.Add(
                        $"verify_parameter_semantics[{requirement.ProofObligationId}]: "
                        + proofResult.Detail);
                }
            }

            if (failures.Count > 0)
                return new ValidationResult
                {
                    RuleName = Name,
                    Verdict = ValidationVerdict.FAIL,
                    Detail = string.Join("; ", failures),
                    RepairSuggestion = rules.FirstOrDefault()?.FixHint,
                    SatisfiedProofObligationIds = satisfiedProofIds
                };

            if (warnings.Count > 0)
                return new ValidationResult
                {
                    RuleName = Name,
                    Verdict = ValidationVerdict.WARN,
                    Detail = $"{passed} 项通过, {warnings.Count} 项跳过",
                    SatisfiedProofObligationIds = satisfiedProofIds
                };

            return new ValidationResult
            {
                RuleName = Name,
                Verdict = ValidationVerdict.PASS,
                Detail = $"{passed} 项全部通过",
                SatisfiedProofObligationIds = satisfiedProofIds
            };
        }

        // ────────────────────────────────────────────────────────────────
        // Target resolution
        // ────────────────────────────────────────────────────────────────
        // 中文：目标分辨率

        private static string ResolveTarget(string verifyType, string existingTarget,
            GPToolContract? contract, Dictionary<string, object?> inputParams, string? ruleParams)
        {
            if (!string.IsNullOrEmpty(existingTarget))
            {
                if (verifyType.Equals("verify_no_overlap", StringComparison.OrdinalIgnoreCase))
                    return ResolveTargetReference(inputParams, existingTarget);

                return existingTarget;
            }

            return verifyType switch
            {
                "verify_field" => ResolveFieldTarget(contract, inputParams, ruleParams),
                "verify_field_populated" => ResolveFieldTarget(contract, inputParams, ruleParams),
                "verify_exists" => ResolveOutputTarget(contract, inputParams),
                "verify_count_min" => ResolveInputTarget(contract, inputParams),
                "verify_count_zero" => ResolveInputTarget(contract, inputParams),
                "verify_empty" => ResolveInputTarget(contract, inputParams),
                "verify_spatial_ref" => ResolveOutputTarget(contract, inputParams),
                "verify_no_overlap" => ResolveNoOverlapSourceTarget(contract, inputParams, ruleParams),
                _ => existingTarget
            };
        }

        /// <summary>
        /// Describe what we expect to find — used in verification ledger for expected/actual diff.
        /// </summary>
        /// <remarks>中文：描述我们期望发现的内容——用于验证账本中用于预期/实际差值。</remarks>
        private static string DescribeExpected(string verifyType, string existingExpected,
            Dictionary<string, object?> inputParams, GPToolContract? contract,
            string? ruleParams, bool invert)
        {
            if (!string.IsNullOrEmpty(existingExpected) &&
                !verifyType.Equals("verify_no_overlap", StringComparison.OrdinalIgnoreCase))
                return existingExpected;

            return verifyType switch
            {
                "verify_field" =>
                    invert ? "字段不存在于图层中" : "字段存在于图层中，类型匹配",
                "verify_field_populated" =>
                    $"字段存在且至少一条记录有非空值",
                "verify_exists" =>
                    $"输出数据集/图层存在",
                "verify_count_min" =>
                    $"至少 1 条要素记录",
                "verify_count_zero" =>
                    $"要素数量为 0",
                "verify_empty" =>
                    $"要素数量为 0",
                "verify_spatial_ref" =>
                    $"坐标系 WKID 匹配期望值",
                "verify_no_overlap" =>
                    DescribeNoOverlapExpected(contract, inputParams, ruleParams, existingExpected),
                _ => "操作成功"
            };
        }

        /// <summary>
        /// Resolve the actual verification parameter (not human-readable).
        /// Returns empty string for verify_field (skip type check),
        /// WKID for verify_spatial_ref, "1" for verify_count_min.
        /// </summary>
        /// <remarks>中文：解决实际验证参数（非人类可读）。返回空字符串表示verify_field（跳过类型检查），返回 WKID 表示verify_spatial_ref，“1” 返回verify_count_min。</remarks>
        private static string ResolveExpected(string verifyType, string existingExpected,
            GPToolContract? contract, Dictionary<string, object?> inputParams, string? ruleParams)
        {
            if (!string.IsNullOrEmpty(existingExpected))
            {
                if (verifyType.Equals("verify_no_overlap", StringComparison.OrdinalIgnoreCase))
                    return ResolveTargetReference(inputParams, existingExpected);

                return existingExpected;
            }

            return verifyType switch
            {
                "verify_field" => "",           // 空值表示跳过类型检查，仅验证字段存在。 / Empty means skip the type check and only verify existence.
                "verify_field_populated" => "", // 值采样不需要期望参数。 / Value sampling needs no expected parameter.
                "verify_count_min" => "1",      // 至少一个要素。 / At least one feature.
                "verify_count_zero" => "0",
                "verify_empty" => "0",
                "verify_spatial_ref" =>
                    inputParams.TryGetValue("out_coor_system", out var cs)
                        ? cs?.ToString() ?? ""
                        : "",
                "verify_no_overlap" =>
                    ResolveNoOverlapComparisonTarget(
                        contract,
                        inputParams,
                        ruleParams,
                        ResolveNoOverlapSourceTarget(contract, inputParams, ruleParams)),
                _ => ""
            };
        }

        /// <summary>
        /// Extract sample data from the observation output for evidence.
        /// </summary>
        /// <remarks>中文：从观测输出中提取样本数据以获取证据。</remarks>
        private static object? ExtractSample(string verifyType, string target, IObservation observation)
        {
            try
            {
                if (string.IsNullOrEmpty(observation.Output.Raw)) return null;

                var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(observation.Output.Raw);
                if (parsed == null) return null;

                return verifyType switch
                {
                    "verify_count_min" => (long?)parsed.featureCount ?? (long?)parsed.count,
                    "verify_count_zero" => (long?)parsed.featureCount ?? (long?)parsed.count,
                    "verify_empty" => (long?)parsed.featureCount ?? (long?)parsed.count,
                    "verify_field" => new
                    {
                        fields = parsed.fields != null
                            ? ((IEnumerable<dynamic>)parsed.fields).Take(5).Select(f => f?.name?.ToString() ?? "").ToList()
                            : null
                    },
                    "verify_field_populated" => new
                    {
                        fields = parsed.fields != null
                            ? ((IEnumerable<dynamic>)parsed.fields).Take(5).Select(f => f?.name?.ToString() ?? "").ToList()
                            : null
                    },
                    "verify_spatial_ref" => (string?)parsed.spatialReference?.wkid ?? (string?)parsed.wkid,
                    _ => null
                };
            }
            catch { return null; }
        }

        // ── Helpers ──
        // 中文：助手

        private static string ResolveFieldTarget(GPToolContract? contract, Dictionary<string, object?> inputParams, string? ruleParams = null)
        {
            string? layerName = ExtractParam(inputParams, "in_table", "in_dataset", "in_features", "in_layer_or_view", "layer_name");
            string? fieldName = ExtractParam(inputParams, "field_name", "field", "drop_field");

            // Tool-declared default (rule Params "field_default=XXX") — for composite
            // tools like cc_calculate_area whose written field is implicit when the
            // caller omits field_name.
            // 中文：工具声明默认（规则参数“field_default=XXX”）——适用于复合工具，如cc_calculate_area，其写入字段在调用者省略时隐含field_name。
            if (string.IsNullOrEmpty(fieldName))
            {
                var parsed = ParseRuleParams(ruleParams);
                if (parsed.TryGetValue("field_default", out string? defaultField) &&
                    !string.IsNullOrWhiteSpace(defaultField))
                    fieldName = defaultField;
            }

            if (!string.IsNullOrEmpty(layerName) && !string.IsNullOrEmpty(fieldName))
                return $"{layerName}.{fieldName}";

            // Target unknown → return empty so the rule is SKIPPED with a warning.
            // Never fabricate "first-two-args" targets: that produced false FAILs
            // (area_type value treated as a field name) — a wrong verdict is worse
            // than an honest skip.
            // 中文：目标未知时返回空值，以便跳过该规则并发出警告。切勿根据“前两个参数”臆造目标，否则会产生错误失败（例如把 area_type 值当成字段名）；错误结论比诚实跳过更糟。
            return "";
        }

        private static string ResolveOutputTarget(GPToolContract? contract, Dictionary<string, object?> inputParams)
        {
            if (contract != null && contract.OutputIndex >= 0 && contract.OutputIndex < contract.ParameterOrder.Length)
            {
                string outputKey = contract.ParameterOrder[contract.OutputIndex];
                if (inputParams.TryGetValue(outputKey, out var val) && val != null)
                    return val.ToString() ?? "";
            }
            return ExtractParam(inputParams,
                "out_table",
                "out_feature_class",
                "Output_Feature_class",
                "out_dataset",
                "out_raster",
                "out_feature_dataset",
                "out_workspace",
                "output",
                "Output") ?? "";
        }

        private static string ResolveInputTarget(GPToolContract? contract, Dictionary<string, object?> inputParams)
        {
            return ExtractParam(inputParams,
                "in_table",
                "in_dataset",
                "in_features",
                "input_features",
                "in_layer_or_view",
                "input_layer",
                "target_features") ?? "";
        }

        private static string ResolveNoOverlapSourceTarget(
            GPToolContract? contract,
            Dictionary<string, object?> inputParams,
            string? ruleParams)
        {
            string? explicitTarget = ResolveRuleParamReference(inputParams, ruleParams,
                "source", "target", "output", "result", "primary", "source_param", "target_param", "output_param");
            if (!string.IsNullOrWhiteSpace(explicitTarget))
                return explicitTarget;

            string output = ResolveOutputTarget(contract, inputParams);
            if (!string.IsNullOrWhiteSpace(output))
                return output;

            return ResolveInputTarget(contract, inputParams);
        }

        private static string ResolveNoOverlapComparisonTarget(
            GPToolContract? contract,
            Dictionary<string, object?> inputParams,
            string? ruleParams,
            string sourceTarget)
        {
            string? explicitTarget = ResolveRuleParamReference(inputParams, ruleParams,
                "compare", "against", "secondary", "reference", "reference_param",
                "compare_param", "against_param", "other", "other_param");
            if (!string.IsNullOrWhiteSpace(explicitTarget))
                return explicitTarget!;

            foreach (string key in NoOverlapComparisonKeys)
            {
                string? candidate = NormalizeLayerCandidate(ExtractParam(inputParams, key));
                if (IsUsableComparisonTarget(candidate, sourceTarget))
                    return candidate!;
            }

            if (contract != null)
            {
                for (int i = 0; i < contract.ParameterOrder.Length; i++)
                {
                    if (i == contract.OutputIndex)
                        continue;

                    string key = contract.ParameterOrder[i];
                    if (!ParameterNameClassifier.LooksLikeInputDataSource(key))
                        continue;

                    string? candidate = NormalizeLayerCandidate(ExtractParam(inputParams, key));
                    if (IsUsableComparisonTarget(candidate, sourceTarget))
                        return candidate!;
                }
            }

            foreach (var kv in inputParams)
            {
                if (!ParameterNameClassifier.LooksLikeInputDataSource(kv.Key))
                    continue;

                string? candidate = NormalizeLayerCandidate(kv.Value);
                if (IsUsableComparisonTarget(candidate, sourceTarget))
                    return candidate!;
            }

            return "";
        }

        private static string DescribeNoOverlapExpected(
            GPToolContract? contract,
            Dictionary<string, object?> inputParams,
            string? ruleParams,
            string existingExpected)
        {
            string source = ResolveNoOverlapSourceTarget(contract, inputParams, ruleParams);
            string compare = !string.IsNullOrWhiteSpace(existingExpected)
                ? ResolveTargetReference(inputParams, existingExpected)
                : ResolveNoOverlapComparisonTarget(contract, inputParams, ruleParams, source);

            if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(compare))
                return $"「{source}」与「{compare}」无实质几何重叠";

            return "输出/目标图层与对照图层无实质几何重叠";
        }

        private static string? ResolveRuleParamReference(
            Dictionary<string, object?> inputParams,
            string? ruleParams,
            params string[] keys)
        {
            var parsed = ParseRuleParams(ruleParams);
            foreach (string key in keys)
            {
                if (!parsed.TryGetValue(key, out string? reference) || string.IsNullOrWhiteSpace(reference))
                    continue;

                string? value = ExtractParam(inputParams, reference);
                return string.IsNullOrWhiteSpace(value)
                    ? reference.Trim()
                    : NormalizeLayerCandidate(value) ?? value;
            }

            return null;
        }

        private static string ResolveTargetReference(Dictionary<string, object?> inputParams, string reference)
        {
            string? value = ExtractParam(inputParams, reference);
            return string.IsNullOrWhiteSpace(value)
                ? reference
                : NormalizeLayerCandidate(value) ?? value;
        }

        private static Dictionary<string, string> ParseRuleParams(string? ruleParams)
        {
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(ruleParams))
                return parsed;

            string text = ruleParams.Trim();
            if (text.StartsWith("{") && text.EndsWith("}"))
            {
                try
                {
                    var obj = JObject.Parse(text);
                    foreach (var property in obj.Properties())
                        parsed[property.Name] = property.Value.Type == JTokenType.Null
                            ? ""
                            : property.Value.ToString().Trim();
                    return parsed;
                }
                catch
                {
                    // Fall through to key=value parsing for permissive legacy params.
                    // 中文：对于允许的遗留参数，切换到 key=value 解析。
                }
            }

            var positional = new List<string>();
            foreach (string segment in text.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = segment.Trim();
                if (part.Length == 0)
                    continue;

                int sep = part.IndexOf('=');
                if (sep < 0)
                {
                    positional.Add(part);
                    continue;
                }

                string key = part[..sep].Trim();
                string value = part[(sep + 1)..].Trim();
                if (key.Length > 0)
                    parsed[key] = value;
            }

            if (positional.Count > 0)
                parsed.TryAdd("source", positional[0]);
            if (positional.Count > 1)
                parsed.TryAdd("compare", positional[1]);

            return parsed;
        }

        private static bool IsUsableComparisonTarget(string? candidate, string sourceTarget)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            if (string.IsNullOrWhiteSpace(sourceTarget))
                return true;

            return !candidate.Trim().Equals(sourceTarget.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeLayerCandidate(object? value)
        {
            if (value == null)
                return null;

            string? text = value.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (text.Contains(';'))
                text = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string? ExtractParam(Dictionary<string, object?> inputParams, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (inputParams.TryGetValue(key, out var val) && val != null)
                {
                    string str = val.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(str)) return str;
                }

                var kv = inputParams.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                {
                    string str = kv.Value.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(str)) return str;
                }
            }
            return null;
        }

        private static readonly string[] NoOverlapComparisonKeys =
        {
            "erase_features",
            "clip_features",
            "mask_features",
            "overlay_features",
            "join_features",
            "compare_features",
            "comparison_features",
            "against_features",
            "reference_features",
            "barrier_features",
            "boundary_features",
            "target_features",
            "erase_layer",
            "clip_layer",
            "mask_layer",
            "overlay_layer",
            "join_layer",
            "compare_layer",
            "comparison_layer",
            "against_layer",
            "reference_layer",
            "barrier_layer",
            "boundary_layer"
        };
    }
}
