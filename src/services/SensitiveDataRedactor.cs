using System.Collections;
using System.Text.RegularExpressions;

namespace SpatialAgent.Services
{
    internal static partial class SensitiveDataRedactor
    {
        private static readonly string[] SensitiveKeyParts =
        {
            "password", "passwd", "pwd", "secret", "token", "api_key",
            "apikey", "authorization", "credential", "connection_string"
        };

        public static Dictionary<string, object?> Redact(
            IReadOnlyDictionary<string, object?> source)
        {
            var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source)
            {
                output[item.Key] = IsSensitiveKey(item.Key)
                    ? "[REDACTED]"
                    : RedactValue(item.Value);
            }
            return output;
        }

        public static string RedactText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";
            string value = BearerPattern().Replace(text, "$1[REDACTED]");
            value = AssignmentPattern().Replace(value, "$1[REDACTED]");
            return value;
        }

        private static object? RedactValue(object? value)
        {
            if (value is IReadOnlyDictionary<string, object?> readOnly)
                return Redact(readOnly);
            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry item in dictionary)
                {
                    string key = item.Key?.ToString() ?? "";
                    result[key] = IsSensitiveKey(key) ? "[REDACTED]" : RedactValue(item.Value);
                }
                return result;
            }
            if (value is IEnumerable sequence and not string)
                return sequence.Cast<object?>().Select(RedactValue).ToList();
            return value;
        }

        private static bool IsSensitiveKey(string key) =>
            SensitiveKeyParts.Any(part =>
                key.Contains(part, StringComparison.OrdinalIgnoreCase));

        [GeneratedRegex(@"(?i)\b(bearer\s+)[A-Za-z0-9._~+/=-]{8,}")]
        private static partial Regex BearerPattern();

        [GeneratedRegex(@"(?i)\b((?:password|passwd|pwd|secret|token|api[_-]?key)\s*[:=]\s*)[^\s,;]+")]
        private static partial Regex AssignmentPattern();
    }
}
