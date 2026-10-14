using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Services
{
    /// <summary>
    /// Post-condition verifier. Rule logic lives here; GIS state probing is injected.
    /// </summary>
    /// <remarks>中文：后条件验证器。规则逻辑存在于此;GIS状态探测被注入。</remarks>
    public class PostConditionVerifier
    {
        private readonly IPostConditionProbe _probe;

        public PostConditionVerifier(IPostConditionProbe probe)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        public async Task<(bool passed, string detail)> VerifyAsync(VerificationRule rule, string toolOutput)
        {
            _ = toolOutput;

            try
            {
                (bool passed, string detail) = rule.Type switch
                {
                    "verify_exists" => await VerifyExistsAsync(rule.Target),
                    "verify_count_min" => await VerifyCountMinAsync(rule.Target, rule.Expected),
                    "verify_count_zero" => await VerifyCountZeroAsync(rule.Target),
                    "verify_empty" => await VerifyCountZeroAsync(rule.Target),
                    "verify_field" => await VerifyFieldAsync(rule.Target, rule.Expected),
                    "verify_field_populated" => await VerifyFieldPopulatedAsync(rule.Target),
                    "verify_spatial_ref" => await VerifySpatialRefAsync(rule.Target, rule.Expected),
                    "verify_no_overlap" => await VerifyNoOverlapAsync(rule.Target, rule.Expected),
                    _ => (true, "无后置验证规则，跳过")
                };

                if (rule.Invert)
                {
                    passed = !passed;
                    detail = passed
                        ? detail.Replace("验证失败", "验证通过（反转）").Replace("不存在", "已删除")
                        : detail.Replace("验证通过", "验证失败（反转）").Replace("存在", "仍存在");
                }

                return (passed, detail);
            }
            catch (Exception ex)
            {
                return (false, $"后置验证异常：{ex.Message}");
            }
        }

        public Task<ParameterScopedProofResult> VerifyParameterScopedProofAsync(
            ParameterScopedProofRequirement requirement) =>
            _probe.VerifyParameterScopedProofAsync(requirement);

        private async Task<(bool, string)> VerifyExistsAsync(string target)
        {
            var result = await _probe.VerifyExistsAsync(target);
            return (result.Exists, result.Detail);
        }

        private async Task<(bool, string)> VerifyCountMinAsync(string target, string expected)
        {
            if (!int.TryParse(expected, out int minCount))
                return (false, $"期望值「{expected}」不是有效整数");

            var result = await _probe.GetCountAsync(target);
            if (!result.Found)
                return (false, result.Detail);

            long count = result.Count ?? 0;
            return count >= minCount
                ? (true, $"要素数 {count} >= {minCount}")
                : (false, $"要素数 {count} < 期望 {minCount}");
        }

        private async Task<(bool, string)> VerifyCountZeroAsync(string target)
        {
            var result = await _probe.GetCountAsync(target);
            if (!result.Found)
                return (false, result.Detail);

            long count = result.Count ?? 0;
            return count == 0
                ? (true, "要素数为 0")
                : (false, $"要素数 {count} > 0");
        }

        private async Task<(bool, string)> VerifyFieldAsync(string target, string expected)
        {
            int sep = target.IndexOf('.');
            if (sep <= 0 || sep >= target.Length - 1)
                return (false, $"verify_field 的 target 格式应为 '图层名.字段名'，收到：{target}");

            string layerName = target[..sep];
            string fieldName = target[(sep + 1)..];
            var result = await _probe.VerifyFieldAsync(layerName, fieldName);
            if (!result.Exists)
                return (false, result.Detail);

            string type = result.FieldType ?? "";
            if (!string.IsNullOrWhiteSpace(expected) &&
                !type.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"字段「{fieldName}」存在但类型为 {type}，期望 {expected}");
            }

            return (true, $"字段「{fieldName}」存在，类型 {type}");
        }

        /// <summary>
        /// Field-value verification for CalculateField-class tools.
        /// Field existence alone is a blind spot: an expression that evaluates to
        /// None on every row "succeeds" at GP level, keeps the field in schema,
        /// and silently wipes the data. Sampling values closes that gap.
        /// </summary>
        /// <remarks>中文：CalculateField 类工具的字段值验证。单纯场存在本身就是一个盲点：一个在每行都评估为无的表达式在GP级别“成功”，保持场处于模式状态，并悄无声息地清除数据。抽样值可以弥补这个差距。</remarks>
        private async Task<(bool, string)> VerifyFieldPopulatedAsync(string target)
        {
            int sep = target.IndexOf('.');
            if (sep <= 0 || sep >= target.Length - 1)
                return (false, $"verify_field_populated 的 target 格式应为 '图层名.字段名'，收到：{target}");

            string layerName = target[..sep];
            string fieldName = target[(sep + 1)..];

            var field = await _probe.VerifyFieldAsync(layerName, fieldName);
            if (!field.Exists)
                return (false, field.Detail);

            var population = await _probe.GetFieldPopulationAsync(layerName, fieldName);
            if (!population.Found)
                return (false, population.Detail);

            long total = population.Total ?? 0;
            long populated = population.Populated ?? 0;

            if (total == 0)
                return (true, $"图层「{layerName}」无要素记录，字段「{fieldName}」跳过值检查");

            if (populated == 0)
                return (false,
                    $"字段「{fieldName}」存在，但抽样 {total} 条记录值全部为空——" +
                    "计算表达式可能对每行返回了空值，原有数据或已被覆盖清空");

            return (true, $"字段「{fieldName}」有值：抽样 {total} 条中 {populated} 条非空");
        }

        private async Task<(bool, string)> VerifySpatialRefAsync(string target, string expected)
        {
            var result = await _probe.GetSpatialReferenceAsync(target);
            if (!result.Found)
                return (false, result.Detail);

            int wkid = result.Wkid ?? 0;
            return int.TryParse(expected, out int expectedWkid) && wkid == expectedWkid
                ? (true, $"坐标系 WKID={wkid}，符合期望 {expectedWkid}")
                : (false, $"坐标系 WKID={wkid}，期望 {expected}");
        }

        private async Task<(bool, string)> VerifyNoOverlapAsync(string target, string expected)
        {
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(expected))
                return (false, "verify_no_overlap 需要同时指定目标图层和对照图层");

            return await _probe.VerifyNoOverlapAsync(target, expected);
        }
    }
}
