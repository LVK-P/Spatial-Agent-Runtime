using System.IO;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Adapters.ArcGIS
{
    /// <summary>
    /// Reads ArcGIS Pro project structure: path, GDB, file listing.
    /// </summary>
    public class ProjectReader : IProjectReader
    {
        public async Task<ProjectContext> GetProjectContextAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var project = Project.Current;
                if (project == null)
                    return new ProjectContext { IsAvailable = false };

                // Pro 3.4 对未保存项目会在 %TEMP% 下创建临时 .aprx，URI 不为空。
                // 不能用 URI 判空。改用 HomeFolderPath 是否在 OS 临时目录下。
                // Path.GetTempPath() 是运行时 API 调用，不是路径硬编码。
                string tempRoot = System.IO.Path.GetTempPath().TrimEnd('\\');
                bool isInTemp = (project.HomeFolderPath ?? "").StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);

                return new ProjectContext
                {
                    IsAvailable = true,
                    IsSaved = !isInTemp,
                    ProjectName = project.Name ?? "",
                    HomeFolder = isInTemp ? null : project.HomeFolderPath,
                    TempFolder = isInTemp ? project.HomeFolderPath : null,
                    DefaultGdbName = Path.GetFileNameWithoutExtension(project.DefaultGeodatabasePath ?? ""),
                    DefaultGdbPath = project.DefaultGeodatabasePath
                };
            });
        }

        public async Task<string> GetContextForAIAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var project = Project.Current;
                if (project == null) return "（无当前项目）";

                var lines = new List<string>();
                lines.Add($"项目：{project.Name}");
                lines.Add($"家目录：{project.HomeFolderPath}");
                lines.Add($"默认数据库名：{Path.GetFileNameWithoutExtension(project.DefaultGeodatabasePath ?? "")}");
                lines.Add($"默认数据库路径：{project.DefaultGeodatabasePath}");

                return string.Join("\n", lines);
            });
        }

        public async Task<string> GetProjectInfoAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var project = Project.Current;
                if (project == null) return JsonConvert.SerializeObject(new { error = "无当前项目" });

                string tempRoot = System.IO.Path.GetTempPath().TrimEnd('\\');
                bool isInTemp = (project.HomeFolderPath ?? "").StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
                return JsonConvert.SerializeObject(new
                {
                    name = project.Name,
                    path = project.URI,
                    homeFolder = isInTemp ? null : project.HomeFolderPath,
                    tempFolder = isInTemp ? project.HomeFolderPath : null,
                    defaultGdb = project.DefaultGeodatabasePath,
                    isSaved = !isInTemp
                }, Formatting.Indented);
            });
        }

        public async Task<string> ScanFolderAsync(string path)
        {
            return await Task.Run(() =>
            {
                if (!Directory.Exists(path)) return JsonConvert.SerializeObject(new { error = $"文件夹不存在：{path}" });
                var entries = Directory.GetFileSystemEntries(path).Take(100).Select(e => new
                {
                    type = Directory.Exists(e) ? "dir" : "file",
                    name = Path.GetFileName(e),
                    path = e
                }).ToList();
                return JsonConvert.SerializeObject(new { path, count = entries.Count, entries }, Formatting.Indented);
            });
        }
    }
}
