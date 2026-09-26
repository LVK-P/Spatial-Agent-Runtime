namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// A search result with provenance metadata for citation.
    /// </summary>
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
    public interface ISearchProvider
    {
        bool IsIndexed { get; }
        /// <summary>Legacy: content-only results.</summary>
        Task<List<string>> SearchAsync(string query, int topK = 5);
        /// <summary>Results with provenance metadata for citation.</summary>
        Task<List<SearchResult>> SearchWithMetaAsync(string query, int topK = 5);
        Task BuildIndexAsync();
    }
}
