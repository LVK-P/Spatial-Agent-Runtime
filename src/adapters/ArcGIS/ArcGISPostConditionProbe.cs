using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using System.Globalization;

namespace SpatialAgent.Adapters.ArcGIS
{
    /// <summary>
    /// ArcGIS Pro SDK-backed probe for post-condition checks.
    /// </summary>
    public class ArcGISPostConditionProbe : IPostConditionProbe
    {
        private readonly MapReader _mapReader = new();

        public async Task<(bool Exists, string Detail)> VerifyExistsAsync(string target)
        {
            return await QueuedTask.Run<(bool Exists, string Detail)>(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return (false, "无活动地图");

                foreach (var layer in map.Layers)
                {
                    if (layer.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                        return (true, $"图层「{target}」存在于当前地图");

                    if (layer is FeatureLayer fl && fl.GetFeatureClass()?.GetName() == target)
                        return (true, $"要素类「{target}」存在");
                }

                foreach (var table in map.StandaloneTables)
                {
                    if (table.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                        return (true, $"属性表「{target}」存在");
                }

                return (false, $"在地图中找不到「{target}」");
            });
        }

        public async Task<(bool Found, long? Count, string Detail)> GetCountAsync(string target)
        {
            return await QueuedTask.Run<(bool Found, long? Count, string Detail)>(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return (false, (long?)null, "无活动地图");

                foreach (var layer in map.Layers)
                {
                    if (!layer.Name.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;

                    if (layer is FeatureLayer fl)
                    {
                        var fc = fl.GetFeatureClass();
                        if (fc == null) return (false, (long?)null, $"图层「{target}」无法获取要素类");

                        long count = fc.GetCount();
                        return (true, count, $"图层「{target}」要素数 {count}");
                    }
                }

                return (false, (long?)null, $"找不到图层「{target}」");
            });
        }

        public async Task<(bool Exists, string? FieldType, string Detail)> VerifyFieldAsync(string layerName, string fieldName)
        {
            try
            {
                var layerJson = await _mapReader.DescribeLayerAsync(layerName);
                var desc = JsonConvert.DeserializeObject<dynamic>(layerJson);
                var fields = desc?.fields;
                if (fields == null)
                    return (false, null, $"图层「{layerName}」无字段信息");

                foreach (var field in fields)
                {
                    string? name = field?.name?.ToString() ?? field?.Name?.ToString();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    if (name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        string? type = field?.type?.ToString() ?? field?.Type?.ToString() ?? "";
                        return (true, type, $"字段「{fieldName}」存在，类型 {type}");
                    }
                }

                return (false, null, $"图层「{layerName}」中无字段「{fieldName}」");
            }
            catch (Exception ex)
            {
                return (false, null, $"验证字段异常：{ex.Message}");
            }
        }

        public async Task<(bool Found, int? Wkid, string Detail)> GetSpatialReferenceAsync(string target)
        {
            return await QueuedTask.Run<(bool Found, int? Wkid, string Detail)>(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return (false, (int?)null, "无活动地图");

                foreach (var layer in map.Layers)
                {
                    if (!layer.Name.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;

                    if (layer is FeatureLayer fl)
                    {
                        var fc = fl.GetFeatureClass();
                        var sr = fc?.GetDefinition()?.GetSpatialReference();
                        if (sr == null) return (false, (int?)null, $"图层「{target}」无坐标系信息");

                        return (true, sr.Wkid, $"坐标系 WKID={sr.Wkid}");
                    }
                }

                return (false, (int?)null, $"找不到图层「{target}」");
            });
        }

        public async Task<(bool Passed, string Detail)> VerifyNoOverlapAsync(string target, string expected)
        {
            try
            {
                return await QueuedTask.Run<(bool Passed, string Detail)>(() =>
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return (false, "无活动地图");

                    var sourceLayer = FindFeatureLayer(map, target);
                    var compareLayer = FindFeatureLayer(map, expected);

                    if (sourceLayer == null)
                        return (false, $"找不到图层「{target}」");
                    if (compareLayer == null)
                        return (false, $"找不到图层「{expected}」");
                    var sourceFc = sourceLayer.GetFeatureClass();
                    var compareFc = compareLayer.GetFeatureClass();
                    if (sourceFc == null)
                        return (false, $"图层「{sourceLayer.Name}」无法获取要素类");
                    if (compareFc == null)
                        return (false, $"图层「{compareLayer.Name}」无法获取要素类");

                    if (ReferenceEquals(sourceLayer, compareLayer) ||
                        sourceLayer.Name.Equals(compareLayer.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return VerifyInternalNoOverlap(sourceLayer, sourceFc);
                    }

                    long sourceCount = sourceFc.GetCount();
                    long compareCount = compareFc.GetCount();
                    if (sourceCount == 0 || compareCount == 0)
                        return (true, $"「{sourceLayer.Name}」或「{compareLayer.Name}」为空，未发现重叠");

                    if (sourceCount > compareCount)
                    {
                        (sourceLayer, compareLayer) = (compareLayer, sourceLayer);
                        (sourceFc, compareFc) = (compareFc, sourceFc);
                    }

                    var spatialFilter = new SpatialQueryFilter
                    {
                        SpatialRelationship = SpatialRelationship.Intersects
                    };

                    long inspected = 0;
                    using (var cursor = sourceFc.Search(null, false))
                    {
                        while (cursor.MoveNext())
                        {
                            if (cursor.Current is not Feature feature)
                                continue;

                            var shape = feature.GetShape();
                            if (shape == null || shape.IsEmpty)
                            {
                                inspected++;
                                continue;
                            }

                            spatialFilter.FilterGeometry = shape;
                            using var hits = compareFc.Search(spatialFilter, false);
                            while (hits.MoveNext())
                            {
                                if (hits.Current is not Feature hitFeature)
                                    continue;

                                var hitShape = hitFeature.GetShape();
                                if (hitShape == null || hitShape.IsEmpty)
                                    continue;

                                if (HasMaterialOverlap(shape, hitShape))
                                    return (false, $"检测到「{sourceLayer.Name}」与「{compareLayer.Name}」存在实质重叠");
                            }

                            inspected++;
                        }
                    }

                    return (true, $"「{sourceLayer.Name}」与「{compareLayer.Name}」无重叠，已检查 {inspected} 个要素");
                });
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn(nameof(ArcGISPostConditionProbe), "拓扑检查异常", ex);
                return (false, "拓扑检查异常");
            }
        }

        public async Task<(bool Found, long? Total, long? Populated, string Detail)> GetFieldPopulationAsync(string layerName, string fieldName)
        {
            try
            {
                return await QueuedTask.Run<(bool Found, long? Total, long? Populated, string Detail)>(() =>
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return (false, (long?)null, (long?)null, "无活动地图");

                    var layer = FindFeatureLayer(map, layerName);
                    if (layer == null)
                        return (false, (long?)null, (long?)null, $"找不到图层「{layerName}」");

                    var fc = layer.GetFeatureClass();
                    if (fc == null)
                        return (false, (long?)null, (long?)null, $"图层「{layerName}」无法获取要素类");

                    int fieldIndex = fc.GetDefinition().FindField(fieldName);
                    if (fieldIndex < 0)
                        return (false, (long?)null, (long?)null, $"图层「{layerName}」中无字段「{fieldName}」");

                    // Sample cap keeps the probe cheap on large layers; a full-table
                    // wipe is still caught because every sampled row is empty.
                    const int SampleCap = 2000;
                    long total = 0, populated = 0;
                    using (var cursor = fc.Search(null, false))
                    {
                        while (total < SampleCap && cursor.MoveNext())
                        {
                            total++;
                            object? value = cursor.Current[fieldIndex];
                            if (value != null && value != DBNull.Value &&
                                !string.IsNullOrWhiteSpace(value.ToString()))
                                populated++;
                        }
                    }

                    return (true, (long?)total, (long?)populated,
                        $"抽样 {total} 条记录，字段「{fieldName}」非空 {populated} 条");
                });
            }
            catch (Exception ex)
            {
                return (false, null, null, $"字段值抽样异常：{ex.Message}");
            }
        }

        public async Task<ParameterScopedProofResult>
            VerifyParameterScopedProofAsync(
                ParameterScopedProofRequirement requirement)
        {
            try
            {
                return await QueuedTask.Run(() =>
                {
                    if (string.IsNullOrWhiteSpace(requirement.Layer)
                        || string.IsNullOrWhiteSpace(requirement.Field)
                        || requirement.DecimalPlaces is < 0 or > 15)
                    {
                        return Unsupported(
                            "参数证明缺少图层、字段或有效 decimal_places");
                    }
                    if (!TryUnitFactor(
                            requirement.MeasurementKind,
                            requirement.Unit,
                            out double unitFactor))
                        return Unsupported($"不支持的证明单位：{requirement.Unit}");
                    string method = requirement.MeasurementMethod
                        .Trim()
                        .ToUpperInvariant();
                    if (method is not "GEODESIC" and not "PLANAR")
                        return Unsupported($"不支持的量算方法：{requirement.MeasurementMethod}");
                    MidpointRounding rounding = requirement.RoundingMode
                        .Equals("HALF_UP", StringComparison.OrdinalIgnoreCase)
                        ? MidpointRounding.AwayFromZero
                        : requirement.RoundingMode.Equals(
                            "TO_EVEN",
                            StringComparison.OrdinalIgnoreCase)
                            ? MidpointRounding.ToEven
                            : (MidpointRounding)(-1);
                    if ((int)rounding < 0)
                        return Unsupported($"不支持的舍入规则：{requirement.RoundingMode}");

                    var map = MapView.Active?.Map;
                    if (map == null)
                        return Unsupported("无活动地图");
                    FeatureLayer? layer = FindFeatureLayer(
                        map,
                        requirement.Layer,
                        requirement.DatasetId);
                    if (layer == null)
                        return Unsupported($"找不到图层「{requirement.Layer}」");
                    FeatureClass? featureClass = layer.GetFeatureClass();
                    if (featureClass == null)
                        return Unsupported($"图层「{requirement.Layer}」无法获取要素类");
                    int fieldIndex = featureClass.GetDefinition()
                        .FindField(requirement.Field);
                    if (fieldIndex < 0)
                        return Unsupported($"图层中无字段「{requirement.Field}」");

                    int cap = Math.Clamp(requirement.SampleSize, 1, 256);
                    int sampled = 0;
                    int mismatches = 0;
                    double maximumError = 0;
                    double maximumAllowedError = 0;
                    using var cursor = featureClass.Search(null, false);
                    while (sampled < cap && cursor.MoveNext())
                    {
                        if (cursor.Current is not Feature feature)
                            continue;
                        Geometry shape = feature.GetShape();
                        if (shape == null || shape.IsEmpty)
                            continue;
                        object? stored = feature[fieldIndex];
                        if (stored == null || stored == DBNull.Value
                            || !double.TryParse(
                                Convert.ToString(stored, CultureInfo.InvariantCulture),
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out double actual))
                        {
                            sampled++;
                            mismatches++;
                            continue;
                        }

                        if (!TryMeasure(
                                shape,
                                requirement.MeasurementKind,
                                method,
                                out double baseValue))
                            return Unsupported(
                                $"几何类型 {shape.GeometryType} 与 {requirement.MeasurementKind} 证明不匹配");
                        double expected = Math.Round(
                            baseValue / unitFactor,
                            requirement.DecimalPlaces,
                            rounding);
                        double error = Math.Abs(actual - expected);
                        double allowedError =
                            ParameterScopedProofTolerance
                                .CalculateAllowedAbsoluteError(
                                    requirement.MeasurementKind,
                                    method,
                                    requirement.DecimalPlaces,
                                    expected);
                        maximumError = Math.Max(maximumError, error);
                        maximumAllowedError = Math.Max(
                            maximumAllowedError,
                            allowedError);
                        if (!double.IsFinite(actual)
                            || !double.IsFinite(expected)
                            || error > allowedError)
                            mismatches++;
                        sampled++;
                    }

                    if (sampled == 0)
                    {
                        return new ParameterScopedProofResult
                        {
                            Supported = true,
                            Passed = false,
                            Detail = "没有可用于数值重算的非空几何样本"
                        };
                    }
                    return new ParameterScopedProofResult
                    {
                        Supported = true,
                        Passed = mismatches == 0,
                        SampledCount = sampled,
                        MismatchCount = mismatches,
                        MaximumAbsoluteError = maximumError,
                        MaximumAllowedAbsoluteError = maximumAllowedError,
                        Detail = mismatches == 0
                            ? $"抽样 {sampled} 条，单位/方法/精度/舍入重算全部匹配（最大误差 {maximumError:G6}，允许 {maximumAllowedError:G6}）"
                            : $"抽样 {sampled} 条，{mismatches} 条与合同重算值不匹配（最大误差 {maximumError:G6}，允许 {maximumAllowedError:G6}）"
                    };
                });
            }
            catch (Exception ex)
            {
                return new ParameterScopedProofResult
                {
                    Supported = true,
                    Passed = false,
                    Detail = $"参数语义数值证明异常：{ex.Message}"
                };
            }
        }

        private static ParameterScopedProofResult Unsupported(string detail) =>
            new()
            {
                Supported = false,
                Passed = false,
                Detail = detail
            };

        private static bool TryMeasure(
            Geometry shape,
            MeasurementProofKind kind,
            string method,
            out double value)
        {
            value = 0;
            if (kind == MeasurementProofKind.Area && shape is Polygon polygon)
            {
                value = method == "GEODESIC"
                    ? Math.Abs(GeometryEngine.Instance.GeodesicArea(polygon))
                    : Math.Abs(polygon.Area);
                return true;
            }
            if (kind == MeasurementProofKind.Length && shape is Polyline polyline)
            {
                value = method == "GEODESIC"
                    ? Math.Abs(GeometryEngine.Instance.GeodesicLength(polyline))
                    : Math.Abs(polyline.Length);
                return true;
            }
            return false;
        }

        private static bool TryUnitFactor(
            MeasurementProofKind kind,
            string unit,
            out double factor)
        {
            factor = 0;
            string normalized = unit.Trim().ToUpperInvariant();
            if (kind == MeasurementProofKind.Length)
            {
                factor = normalized switch
                {
                    "METERS" => 1,
                    "KILOMETERS" => 1000,
                    "MILES" => 1609.344,
                    _ => 0
                };
                return factor > 0;
            }
            if (kind == MeasurementProofKind.Area)
            {
                factor = normalized switch
                {
                    "SQUARE_METERS" => 1,
                    "HECTARES" => 10000,
                    "SQUARE_KILOMETERS" => 1_000_000,
                    "MU" => 666.66667,
                    _ => 0
                };
                return factor > 0;
            }
            return false;
        }

        private static (bool Passed, string Detail) VerifyInternalNoOverlap(FeatureLayer layer, FeatureClass featureClass)
        {
            long count = featureClass.GetCount();
            if (count < 2)
                return (true, $"「{layer.Name}」要素数 {count}，无需内部重叠检查");

            var spatialFilter = new SpatialQueryFilter
            {
                SpatialRelationship = SpatialRelationship.Intersects
            };

            long inspected = 0;
            using (var cursor = featureClass.Search(null, false))
            {
                while (cursor.MoveNext())
                {
                    if (cursor.Current is not Feature feature)
                        continue;

                    long oid = feature.GetObjectID();
                    var shape = feature.GetShape();
                    if (shape == null || shape.IsEmpty)
                    {
                        inspected++;
                        continue;
                    }

                    spatialFilter.FilterGeometry = shape;
                    using var hits = featureClass.Search(spatialFilter, false);
                    while (hits.MoveNext())
                    {
                        if (hits.Current is not Feature hitFeature)
                            continue;

                        long hitOid = hitFeature.GetObjectID();
                        if (hitOid <= oid)
                            continue;

                        var hitShape = hitFeature.GetShape();
                        if (hitShape == null || hitShape.IsEmpty)
                            continue;

                        if (HasMaterialOverlap(shape, hitShape))
                            return (false, $"检测到「{layer.Name}」内部要素 {oid} 与 {hitOid} 存在实质重叠");
                    }

                    inspected++;
                }
            }

            return (true, $"「{layer.Name}」内部无实质重叠，已检查 {inspected} 个要素");
        }

        private static bool HasMaterialOverlap(Geometry left, Geometry right)
        {
            var intersection = GeometryEngine.Instance.Intersection(
                left,
                right,
                GeometryDimensionType.EsriGeometryUnknownDimension);

            return intersection != null && !intersection.IsEmpty;
        }

        private static FeatureLayer? FindFeatureLayer(
            Map map,
            string name,
            string datasetId = "")
        {
            foreach (var layer in map.GetLayersAsFlattenedList())
            {
                if (layer is not FeatureLayer fl)
                    continue;

                try
                {
                    FeatureClass? featureClass = fl.GetFeatureClass();
                    if (!string.IsNullOrWhiteSpace(datasetId))
                    {
                        if (MapReader.BuildDatasetId(featureClass).Equals(
                                datasetId,
                                StringComparison.OrdinalIgnoreCase))
                            return fl;
                        continue;
                    }
                    if (layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || featureClass?.GetName()?.Equals(
                            name,
                            StringComparison.OrdinalIgnoreCase) == true)
                        return fl;
                }
                catch
                {
                    // ignore feature-class lookup failures while probing
                }
            }

            return null;
        }
    }
}
