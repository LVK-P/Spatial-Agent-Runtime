// SearchProviderAdapter.cs — Adapter bridging KnowledgeRetriever → ISearchProvider.
// Wraps the concrete KnowledgeRetriever so the Runtime layer only depends on
// the ISearchProvider contract. SearchAsync returns plain content strings;
// SearchWithMetaAsync returns structured results with source and score.
// 中文：SearchProviderAdapter.cs — 连接 KnowledgeRetriever → ISearchProvider。它封装了具体的 KnowledgeRetriever，使运行时层仅依赖于 ISearchProvider 合同。SearchAsync 返回的是纯内容字符串;SearchWithMetaAsync 返回结构化结果，包含来源和评分。

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Knowledge
{
    /// <summary>
    /// Adapter: wraps KnowledgeRetriever and exposes ISearchProvider for the Runtime.
    /// </summary>
    /// <remarks>中文：适配器：封装 KnowledgeRetriever，并为运行时暴露 ISearchProvider。</remarks>
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
