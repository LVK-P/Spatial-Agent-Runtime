using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Tools.Core
{
    /// <summary>
    /// Normalizes ArcGIS reader envelopes. Some host APIs report a logical
    /// failure as JSON instead of throwing; that payload must not become a
    /// successful tool observation.
    /// </summary>
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
