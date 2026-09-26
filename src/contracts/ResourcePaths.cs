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
    public static class ResourcePaths
    {
        /// <summary>
        /// Find a resource file or directory by deployment-relative path.
        /// Returns the first existing match, or null when absent everywhere.
        /// </summary>
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
        public static string Resolve(string relativePath)
        {
            return Find(relativePath)
                ?? Path.Combine(PrimaryRoot(), relativePath);
        }

        /// <summary>The deployment directory of the SpatialAgent assemblies.</summary>
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
            }

            if (!string.IsNullOrEmpty(assemblyDir))
                yield return assemblyDir!;

            // 2. Process base directory — correct for ConsoleHost and tests,
            //    wrong inside the Pro add-in (Pro install dir), hence second.
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
