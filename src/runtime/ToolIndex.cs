// ToolIndex.cs — Tool tag index for context-aware tool filtering.
// Loads an optional, locally generated tools_index.json, then scores
// tools against the current project context using two strategies:
//   1. Tag-based: geometry type + operation hint tags with configurable
//      score weights (RuntimePolicy).
//   2. Token fallback: when tag scoring returns too few results, tokens
//      from the user message are matched against tool name/description.
// Consumed by ContextManager as part of the two-tier tool exposure system.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SpatialAgent.Core;

namespace SpatialAgent.Runtime
{
    public class ToolIndexEntry
    {
        [JsonProperty("toolbox")]
        public string Toolbox { get; set; } = "";

        [JsonProperty("description")]
        public string Description { get; set; } = "";

        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new();
    }

    public class ToolIndex
    {
        private Dictionary<string, ToolIndexEntry> _index = new();
        private bool _loaded;

        public int Count => _index.Count;
        public bool IsLoaded => _loaded;

        /// <summary>Load from tools_index.json at the given path.</summary>
        public void Load(string indexPath)
        {
            if (!File.Exists(indexPath))
            {
                _loaded = false;
                return;
            }

            try
            {
                string json = File.ReadAllText(indexPath);
                _index = JsonConvert.DeserializeObject<Dictionary<string, ToolIndexEntry>>(json)
                    ?? new Dictionary<string, ToolIndexEntry>();
                _loaded = true;
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("ToolIndex.Load",
                    $"Failed to load tool index from {indexPath} — tool filtering will be unavailable.", ex);
                _loaded = false;
                _index = new Dictionary<string, ToolIndexEntry>();
            }
        }

        /// <summary>
        /// Score tools by tag overlap with geometry types and operation hints.
        /// Returns tool names ordered by descending relevance.
        /// </summary>
        public List<string> FindRelevant(
            HashSet<string>? layerGeomTypes = null,
            HashSet<string>? operationHints = null,
            int? topN = null)
        {
            if (!_loaded || _index.Count == 0)
                return new List<string>();

            int actualTopN = topN ?? RuntimePolicy.ToolIndexTopN;
            var scored = new List<(string name, int score)>();

            foreach (var kv in _index)
            {
                int score = 0;
                var tags = kv.Value.Tags;

                if (layerGeomTypes != null)
                {
                    foreach (var geomTag in layerGeomTypes)
                    {
                        if (tags.Contains($"geom:{geomTag}"))
                            score += RuntimePolicy.ToolIndexGeomMatchScore;
                    }
                    if (tags.Contains("geom:feature") && layerGeomTypes.Count > 0)
                        score += RuntimePolicy.ToolIndexGeomMatchScore / 2;
                }

                if (operationHints != null)
                {
                    foreach (var opTag in operationHints)
                    {
                        if (tags.Contains($"op:{opTag}"))
                            score += RuntimePolicy.ToolIndexOpMatchScore;
                    }
                }

                string tb = kv.Value.Toolbox.ToLower();
                if (tb.Contains("analysis") || tb.Contains("data management"))
                    score += RuntimePolicy.ToolIndexToolboxBoost;

                if (score > 0)
                    scored.Add((kv.Key, score));
            }

            return scored
                .OrderByDescending(s => s.score)
                .Take(actualTopN)
                .Select(s => s.name)
                .ToList();
        }

        /// <summary>
        /// Primary tag-based scoring, with token-based fallback when too few results are found.
        /// </summary>
        public List<string> FindRelevantWithFallback(
            string userMessage,
            HashSet<string>? layerGeomTypes = null,
            HashSet<string>? operationHints = null,
            int? topN = null)
        {
            var primary = FindRelevant(layerGeomTypes, operationHints, topN);
            int actualTopN = topN ?? RuntimePolicy.ToolIndexTopN;
            if (primary.Count >= Math.Max(3, actualTopN / 4))
                return primary;

            var tokens = Tokenize(userMessage).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tokens.Count == 0)
                return primary;

            var scored = new List<(string name, int score)>();
            foreach (var kv in _index)
            {
                int score = 0;
                var entryTokens = Tokenize($"{kv.Key} {kv.Value.Description} {string.Join(" ", kv.Value.Tags)}")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var token in tokens)
                {
                    if (entryTokens.Contains(token))
                        score += 4;
                    else if (entryTokens.Any(t => t.Contains(token, StringComparison.OrdinalIgnoreCase) || token.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        score += 2;
                }

                if (score > 0)
                    scored.Add((kv.Key, score));
            }

            var fallback = scored
                .OrderByDescending(s => s.score)
                .Take(actualTopN)
                .Select(s => s.name)
                .ToList();

            if (primary.Count == 0)
                return fallback;

            return primary
                .Concat(fallback)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(actualTopN)
                .ToList();
        }

        // ── Tokenization helpers ──

        private static IEnumerable<string> Tokenize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            var current = new System.Text.StringBuilder();
            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    current.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    if (current.Length >= 2)
                        yield return current.ToString();
                    current.Clear();

                    if (IsCjk(ch))
                        yield return ch.ToString();
                }
            }

            if (current.Length >= 2)
                yield return current.ToString();
        }

        private static bool IsCjk(char ch) =>
            (ch >= '一' && ch <= '鿿') ||   // CJK Unified Ideographs
            (ch >= '㐀' && ch <= '䶿') ||   // CJK Extension A
            (ch >= '぀' && ch <= 'ヿ');      // Hiragana + Katakana
    }
}
