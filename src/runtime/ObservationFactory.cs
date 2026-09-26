using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Converts raw ToolResult → structured Observation.
    /// Tries to parse JSON output for typed fields.
    /// </summary>
    public static class ObservationFactory
    {
        public static IObservation Create(string toolName, Dictionary<string, object?> input, ToolResult result, TimeSpan elapsed)
        {
            // Prefer typed StructuredOutput if tool provided it
            if (result.StructuredOutput != null)
                return new Observation { ToolName = toolName, Elapsed = elapsed, Output = result.StructuredOutput, Warnings = result.Success ? new List<string>() : new List<string> { result.Error ?? "" } };

            var output = new ObservationOutput { Raw = result.Output };

            // Fallback: Try parsing JSON output for structured fields
            if (!string.IsNullOrEmpty(result.Output) && result.Success)
            {
                try
                {
                    dynamic? parsed = JsonConvert.DeserializeObject<dynamic>(result.Output);
                    if (parsed != null)
                    {
                        output.ResourceName = (string?)parsed.name ?? (string?)parsed.layer;
                        output.FeatureCount = (long?)parsed.featureCount ?? (long?)parsed.count;
                        output.GeometryType = (string?)parsed.geometryType;
                        output.CRS = (string?)parsed.wkid ?? (string?)parsed.spatialReference?.wkid;
                        output.RowCount = (int?)parsed.rowCount ?? (int?)parsed.count;

                        if (parsed.extent != null)
                            output.Extent = new ExtentInfo
                            {
                                XMin = (double)(parsed.extent.xmin ?? 0),
                                YMin = (double)(parsed.extent.ymin ?? 0),
                                XMax = (double)(parsed.extent.xmax ?? 0),
                                YMax = (double)(parsed.extent.ymax ?? 0)
                            };

                        if (parsed.fields != null)
                            output.Fields = ((JArray)parsed.fields)
                                .Select(f => f?["name"]?.ToString() ?? f?.ToString() ?? "").ToList();
                    }
                }
                catch { /* JSON parse fails → keep Raw only */ }
            }

            return new Observation
            {
                ToolName = toolName,
                ToolCategory = "", // filled by caller if needed
                Elapsed = elapsed,
                Output = output,
                Warnings = result.Success ? new List<string>() : new List<string> { result.Error ?? "" }
            };
        }
    }
}
