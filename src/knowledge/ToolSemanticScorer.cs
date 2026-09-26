// ToolSemanticScorer.cs — L2 semantic tool ranking via embedding cosine similarity.
// Builds embedding vectors for all registered tool descriptors (name + description +
// category + parameter metadata), then ranks tools against a user query by cosine
// distance. Used as a fallback/second-pass when keyword-based ToolIndex matching
// produces too few or too many results.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Knowledge
{
    /// <summary>
    /// Local semantic scorer for tool retrieval using the existing embedding service.
    /// </summary>
    public class ToolSemanticScorer : IToolSemanticScorer
    {
        private readonly IEmbeddingService _embedding;
        private readonly SemaphoreSlim _buildGate = new(1, 1);
        private readonly Dictionary<string, float[]> _toolVectors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _toolTexts = new(StringComparer.OrdinalIgnoreCase);
        private bool _ready;

        public bool IsReady => _ready && _toolVectors.Count > 0;

        public ToolSemanticScorer(IEmbeddingService embedding)
        {
            _embedding = embedding;
        }

        public async Task BuildAsync(IReadOnlyList<IToolDescriptor> tools, CancellationToken cancellation = default)
        {
            if (tools == null || tools.Count == 0)
            {
                _toolVectors.Clear();
                _toolTexts.Clear();
                _ready = false;
                return;
            }

            await _buildGate.WaitAsync(cancellation);
            try
            {
                var texts = tools
                    .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                    .Select(t => (Tool: t, Text: BuildToolText(t)))
                    .ToList();

                if (texts.Count == 0)
                {
                    _toolVectors.Clear();
                    _toolTexts.Clear();
                    _ready = false;
                    return;
                }

                await _embedding.BuildVocabularyAsync(texts.Select(t => t.Text));
                var vectors = await _embedding.EmbedBatchAsync(texts.Select(t => t.Text));

                _toolVectors.Clear();
                _toolTexts.Clear();
                for (int i = 0; i < texts.Count && i < vectors.Length; i++)
                {
                    var name = texts[i].Tool.Name;
                    var vec = vectors[i] ?? Array.Empty<float>();
                    if (vec.Length == 0)
                        continue;

                    _toolVectors[name] = vec;
                    _toolTexts[name] = texts[i].Text;
                }

                _ready = _toolVectors.Count > 0;
            }
            finally
            {
                _buildGate.Release();
            }
        }

        public async Task<IReadOnlyList<string>> RankAsync(string query, int topN = 50, CancellationToken cancellation = default)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(query))
                return Array.Empty<string>();

            var queryVec = await _embedding.EmbedAsync(query);
            if (queryVec.Length == 0)
                return Array.Empty<string>();

            var scored = new List<(string Name, double Score)>();
            foreach (var kv in _toolVectors)
            {
                double score = CosineSimilarity(queryVec, kv.Value);
                if (score > 0)
                    scored.Add((kv.Key, score));
            }

            return scored
                .OrderByDescending(s => s.Score)
                .Take(Math.Max(1, topN))
                .Select(s => s.Name)
                .ToList();
        }

        private static string BuildToolText(IToolDescriptor tool)
        {
            var parts = new List<string>
            {
                tool.Name,
                tool.Description,
                tool.Category,
                tool.Annotation.ToString()
            };

            if (tool.Parameters != null && tool.Parameters.Count > 0)
            {
                foreach (var parameter in tool.Parameters)
                {
                    parts.Add(parameter.Key);
                    if (!string.IsNullOrWhiteSpace(parameter.Value.Description))
                        parts.Add(parameter.Value.Description);
                    if (parameter.Value.Enum != null && parameter.Value.Enum.Count > 0)
                        parts.Add(string.Join(" ", parameter.Value.Enum));
                }
            }

            return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
        {
            int len = Math.Min(left.Count, right.Count);
            if (len == 0)
                return 0;

            double dot = 0;
            double leftNorm = 0;
            double rightNorm = 0;

            for (int i = 0; i < len; i++)
            {
                double l = left[i];
                double r = right[i];
                dot += l * r;
                leftNorm += l * l;
                rightNorm += r * r;
            }

            if (leftNorm <= 0 || rightNorm <= 0)
                return 0;

            return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        }
    }
}
