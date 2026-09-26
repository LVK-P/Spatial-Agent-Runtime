// ApiEmbeddingService.cs — LLM-backed embedding via DeepSeek API.
// Delegates to DeepSeekProvider.GetEmbeddingAsync(). EmbedBatchAsync
// fans out single-text calls. BuildVocabularyAsync is a no-op — the API
// model is pre-trained and does not need corpus indexing.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpatialAgent.Adapters.LLM;

namespace SpatialAgent.Knowledge
{
    public class ApiEmbeddingService : IEmbeddingService
    {
        private readonly DeepSeekProvider _provider;

        public string Name => $"API ({_provider.ProviderName})";
        public bool IsLocal => false;
        public int Dimension => 0;
        public bool IsReady => _provider.IsConfigured && _provider.SupportsEmbeddings;

        public ApiEmbeddingService(DeepSeekProvider provider)
        {
            _provider = provider;
        }

        public async Task<float[]> EmbedAsync(string text)
        {
            return await _provider.GetEmbeddingAsync(text);
        }

        public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts)
        {
            var tasks = texts.Select(EmbedAsync);
            return await Task.WhenAll(tasks);
        }

        public Task BuildVocabularyAsync(IEnumerable<string> corpus)
        {
            return Task.CompletedTask;
        }
    }
}
