using System.Threading;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Tools.Core
{
    /// <summary>
    /// Get project metadata: name, path, default GDB.
    /// </summary>
    public class GetProjectInfoTool : ITool
    {
        private readonly ProjectReader _reader = new();
        public string Name => "agent_get_project_info";
        public string Description => "获取当前 ArcGIS Pro 项目的基本信息：项目名、路径、默认数据库。";
        public string Category => "project";
        public ToolAnnotation Annotation => ToolAnnotation.ReadOnly;
        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>();
        public VerificationRule? Verification => null;
        public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        public IReadOnlyList<string> RequiredParameters => Array.Empty<string>();

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            try
            {
                string json = await _reader.GetProjectInfoAsync();
                return JsonToolResultFactory.FromJson(Name, json);
            }
            catch (Exception ex) { return new ToolResult { ToolName = Name, Success = false, Error = ex.Message }; }
        }
    }
}
