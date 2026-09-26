// SearchProviderAdapter.cs — Adapter bridging KnowledgeRetriever → ISearchProvider.
// Wraps the concrete KnowledgeRetriever so the Runtime layer only depends on
// the ISearchProvider contract. SearchAsync returns plain content strings;
// SearchWithMetaAsync returns structured results with source and score.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Knowledge
{
    /// <summary>
    /// Adapter: wraps KnowledgeRetriever and exposes ISearchProvider for the Runtime.
    /// </summary>
    public class SearchProviderAdapter : ISearchProvider
    {
        private readonly KnowledgeRetriever _inner;

        public bool IsIndexed => _inner.IsIndexed;

        public SearchProviderAdapter(KnowledgeRetriever inner) { _inner = inner; }

        public async Task<List<string>> SearchAsync(string query, int topK = 5)
        {
            var chunks = await _inner.SearchAsync(query, topK);
            return chunks.Select(c => c.Content).ToList();
        }

        public async Task<List<SearchResult>> SearchWithMetaAsync(string query, int topK = 5)
        {
            var chunks = await _inner.SearchAsync(query, topK);
            return chunks.Select(c => new SearchResult
            {
                Content = c.Content,
                Title = c.Title ?? "",
                Source = c.Source ?? "",
                Score = c.Score
            }).ToList();
        }

        public async Task BuildIndexAsync() => await _inner.BuildIndexAsync();
    }
}
