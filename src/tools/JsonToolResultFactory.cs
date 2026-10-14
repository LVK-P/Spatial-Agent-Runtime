using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Tools.Core
{
    /// <summary>
    /// Normalizes ArcGIS reader envelopes. Some host APIs report a logical
    /// failure as JSON instead of throwing; that payload must not become a
    /// successful tool observation.
    /// </summary>
    /// <remarks>中文：规范化ArcGIS读卡机信封。一些主机API将逻辑失败报告为JSON，而非抛出;该有效载荷绝不能成为成功的工具观测。</remarks>
    public static class JsonToolResultFactory
    {
        public static ToolResult FromJson(string toolName, string json)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JObject.Parse(json);
                    string error = parsed["error"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        return new ToolResult
                        {
                            ToolName = toolName,
                            Success = false,
                            Output = json,
                            Error = error
                        };
                    }
                }
                catch
                {
                    // Reader adapters may legitimately return plain text.
                    // Transport success remains success when no error
                    // envelope can be identified.
                    // 中文：阅读器适配器可以合法返回纯文本。当无法识别出错误包络时，传输成功依然是成功。
                }
            }

            return new ToolResult
            {
                ToolName = toolName,
                Success = true,
                Output = json
            };
        }
    }
}
