using System;
using System.Collections.Generic;
using System.IO;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Locates optional deployment-relative resource files (tools_index.json, Skills\,
    /// system_prompt.md, gis_dict.txt, …) in a host-agnostic way.
    ///
    /// Why this exists: inside an ArcGIS Pro add-in the assemblies are loaded
    /// from the add-in AssemblyCache directory, but AppDomain.CurrentDomain
    /// .BaseDirectory points at the Pro installation folder — so every
    /// BaseDirectory-relative lookup silently misses and the runtime degrades
    /// (no tool index, no skills, no knowledge, built-in prompt fallback).
    /// All SpatialAgent assemblies and their resources deploy to the same
    /// directory, so the directory of THIS assembly is the reliable anchor;
    /// BaseDirectory remains as fallback for ConsoleHost / unit tests.
    ///
    /// Infrastructure utility by design: BCL-only, no state, no behavior
    /// beyond path probing — safe to live in the zero-dependency layer.
    /// </summary>
    /// <remarks>中文：以宿主无关方式定位可选的部署资源（tools_index.json、skills、system_prompt.md、gis_dict.txt 等）。ArcGIS Pro 插件程序集从 AssemblyCache 加载，而 AppDomain.CurrentDomain.BaseDirectory 指向 Pro 安装目录；依赖 BaseDirectory 会静默找不到资源并退化为无工具索引、技能或知识的内置提示词。SpatialAgent 程序集及资源部署在同一目录，因此程序集目录是可靠锚点，BaseDirectory 仅作为 ConsoleHost/单元测试回退。该基础设施工具仅依赖 BCL、无状态且只探测路径，适合放在零依赖层。</remarks>
    public static class ResourcePaths
    {
        /// <summary>
        /// Find a resource file or directory by deployment-relative path.
        /// Returns the first existing match, or null when absent everywhere.
        /// </summary>
        /// <remarks>中文：按部署相对路径查找资源文件或目录，返回第一个存在的匹配；所有候选位置都不存在时返回 null。</remarks>
        public static string? Find(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;

            foreach (string root in CandidateRoots())
            {
                string candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Resolve a deployment-relative path without requiring it to exist:
        /// first existing match wins, otherwise the path is anchored to the
        /// primary root. Use for paths that are about to be created.
        /// </summary>
        /// <remarks>中文：解析不要求已存在的部署相对路径：优先返回第一个现有匹配，否则把路径锚定到主根目录。用于即将创建的路径。</remarks>
        public static string Resolve(string relativePath)
        {
            return Find(relativePath)
                ?? Path.Combine(PrimaryRoot(), relativePath);
        }

        /// <summary>The deployment directory of the SpatialAgent assemblies.</summary>
        /// <remarks>中文：SpatialAgent 装配的部署目录。</remarks>
        public static string PrimaryRoot()
        {
            foreach (string root in CandidateRoots())
                return root;
            return AppContext.BaseDirectory;
        }

        private static IEnumerable<string> CandidateRoots()
        {
            // 1. Directory of this assembly — in the Pro add-in this is the
            //    AssemblyCache deployment folder where all resources live.
            //    (Assembly.Location is empty for single-file publish; guarded.)
            // 中文：1. 该装配的目录——在 Pro 插件中，这是 AssemblyCache 部署文件夹，所有资源都存放在那里。（Assembly.Location 用于单文件发布为空;受保护。）
            string? assemblyDir = null;
            try
            {
                string location = typeof(ResourcePaths).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                    assemblyDir = Path.GetDirectoryName(location);
            }
            catch
            {
                // Fall through to BaseDirectory
                // 中文：通过 BaseDirectory
            }

            if (!string.IsNullOrEmpty(assemblyDir))
                yield return assemblyDir!;

            // 2. Process base directory — correct for ConsoleHost and tests,
            //    wrong inside the Pro add-in (Pro install dir), hence second.
            // 中文：2. 进程库目录——ConsoleHost和测试是正确的，但在Pro插件（Pro安装目录）中错误，因此是第二个。
            string baseDir = AppContext.BaseDirectory;
            if (!string.Equals(baseDir?.TrimEnd(Path.DirectorySeparatorChar),
                    assemblyDir?.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(baseDir))
            {
                yield return baseDir!;
            }
        }
    }
}
