using FluentAssertions;
using SpatialAgent.Knowledge;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class KnowledgeIndexFreshnessTests : IDisposable
    {
        private readonly string _folder = Path.Combine(
            Path.GetTempPath(),
            "knowledge-v3-" + Guid.NewGuid().ToString("N"));

        public KnowledgeIndexFreshnessTests() => Directory.CreateDirectory(_folder);

        [Fact]
        public async Task Changed_markdown_invalidates_persisted_index()
        {
            string document = Path.Combine(_folder, "rules.md");
            await File.WriteAllTextAsync(document, "# Rule\nlegacyterm content");
            var first = new KnowledgeRetriever(new TfidfEmbeddingService(), _folder);
            await first.BuildIndexAsync();
            (await first.SearchAsync("legacyterm")).Should().NotBeEmpty();

            await File.WriteAllTextAsync(document, "# Rule\nfreshterm replacement");
            File.SetLastWriteTimeUtc(document, DateTime.UtcNow.AddSeconds(2));
            var second = new KnowledgeRetriever(new TfidfEmbeddingService(), _folder);
            await second.BuildIndexAsync();

            var fresh = await second.SearchAsync("freshterm");
            fresh.Should().ContainSingle();
            fresh[0].Content.Should().Contain("freshterm");
            fresh[0].Content.Should().NotContain("legacyterm");
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
    }
}
