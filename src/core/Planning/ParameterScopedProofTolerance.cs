using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    public static class ParameterScopedProofTolerance
    {
        public const double GeodesicAreaRelativeTolerance = 1e-5;

        public static double CalculateAllowedAbsoluteError(
            MeasurementProofKind kind,
            string measurementMethod,
            int decimalPlaces,
            double expectedValue)
        {
            double roundingTolerance = Math.Max(
                1e-9,
                0.5 * Math.Pow(10, -decimalPlaces));
            if (kind != MeasurementProofKind.Area
                || !measurementMethod.Equals(
                    "GEODESIC",
                    StringComparison.OrdinalIgnoreCase)
                || !double.IsFinite(expectedValue))
            {
                return roundingTolerance;
            }

            return Math.Max(
                roundingTolerance,
                Math.Abs(expectedValue) * GeodesicAreaRelativeTolerance);
        }
    }
}
