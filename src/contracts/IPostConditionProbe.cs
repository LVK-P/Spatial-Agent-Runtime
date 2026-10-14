namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Reads actual GIS state for post-condition verification.
    /// Implementations live in adapter projects; services only consume this contract.
    /// </summary>
    /// <remarks>中文：读取实际GIS状态以进行后期验证。实现存在适配器项目中;服务只消耗这份合同。</remarks>
    public interface IPostConditionProbe
    {
        Task<(bool Exists, string Detail)> VerifyExistsAsync(string target);
        Task<(bool Found, long? Count, string Detail)> GetCountAsync(string target);
        Task<(bool Exists, string? FieldType, string Detail)> VerifyFieldAsync(string layerName, string fieldName);
        Task<(bool Found, int? Wkid, string Detail)> GetSpatialReferenceAsync(string target);
        Task<(bool Passed, string Detail)> VerifyNoOverlapAsync(string target, string expected);

        /// <summary>
        /// Sample field values to detect wipe-outs: a CalculateField that writes
        /// NULL/empty to every row still "succeeds" at the GP level and still
        /// passes a field-exists check — only value sampling catches it.
        /// Total/Populated are sampled counts (implementation may cap the scan).
        /// </summary>
        /// <remarks>中文：用于检测清除的样本字段值：计算字段在每行写入 NULL/empty 时，在 GP 层面仍然“成功”，并且仍然通过字段存在检查——只有值采样能捕捉到。总数/填充计数是采样计数（实现可能会限制扫描次数）。</remarks>
        Task<(bool Found, long? Total, long? Populated, string Detail)> GetFieldPopulationAsync(string layerName, string fieldName);

        Task<ParameterScopedProofResult> VerifyParameterScopedProofAsync(
            ParameterScopedProofRequirement requirement) =>
            Task.FromResult(new ParameterScopedProofResult
            {
                Supported = false,
                Passed = false,
                Detail = "当前 GIS Adapter 不支持参数语义数值证明"
            });
    }
}
