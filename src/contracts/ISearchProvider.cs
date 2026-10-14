namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// A search result with provenance metadata for citation.
    /// </summary>
    /// <remarks>中文：带有来源元数据的检索结果，用于引用。</remarks>
    public class SearchResult
    {
        public string Content { get; set; } = "";
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";
        public double Score { get; set; }
    }

    /// <summary>
    /// Knowledge search provider.
    /// Concrete implementation in SpatialAgent.Knowledge wraps KnowledgeRetriever.
    /// </summary>
    /// <remarks>中文：知识搜索提供商。SpatialAgent.Knowledge 包裹 KnowledgeRetriever 的具体实现。</remarks>
    public interface ISearchProvider
    {
        bool IsIndexed { get; }
        /// <summary>Legacy: content-only results.</summary>
        /// <remarks>中文：遗产：仅内容结果。</remarks>
        Task<List<string>> SearchAsync(string query, int topK = 5);
        /// <summary>Results with provenance metadata for citation.</summary>
        /// <remarks>中文：结果含来源元数据以供引用。</remarks>
        Task<List<SearchResult>> SearchWithMetaAsync(string query, int topK = 5);
        Task BuildIndexAsync();
    }
}
