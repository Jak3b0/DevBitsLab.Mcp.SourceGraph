using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using FluentAssertions;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Regression guard for the FastBertTokenizer → Microsoft.ML.Tokenizers migration. Loads the real
/// <c>tokenizer.json</c> for <c>jinaai/jina-embeddings-v2-base-code</c> (the documented default
/// model) and asserts it loads cleanly via <see cref="JinaCodeEmbeddingGenerator.TryLoadTokenizer"/>
/// and produces the expected token-id sequence for a known input.
///
/// <para>
/// The fixture is **lazily fetched** rather than committed: the upstream Jina v2 release ships
/// the tokenizer.json under a non-permissive license that would risk redistribution. On first
/// run on a given machine, the test downloads the file from huggingface.co into
/// <c>tests/fixtures/.cache/jina-v2-base-code-tokenizer.json</c> (gitignored) and reuses the
/// cached copy thereafter. If the dev machine has already populated the live cache (via a
/// real <c>serve</c> run), the test prefers that copy to avoid the network hop entirely.
/// </para>
///
/// <para>
/// The expected token ids for <c>"Hello world"</c> were captured during the migration spike
/// against the same tokenizer.json file: <c>[0, 10564, 7509, 2]</c> (= <c>&lt;s&gt;</c> +
/// "Hello" + " world" + <c>&lt;/s&gt;</c>). If this assertion ever fails, either the upstream
/// tokenizer file changed or the loading dispatch in
/// <see cref="JinaCodeEmbeddingGenerator.TryLoadTokenizer"/> drifted.
/// </para>
/// </summary>
public sealed class JinaTokenizerLoadTests : IClassFixture<JinaTokenizerLoadTests.TokenizerFixture>
{
    private readonly TokenizerFixture _fixture;

    public JinaTokenizerLoadTests(TokenizerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public void TryLoadTokenizer_succeedsAgainstRealJinaTokenizerJson()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var ok = JinaCodeEmbeddingGenerator.TryLoadTokenizer(_fixture.Path, out var tokenizer, out var padId, out var error);

        ok.Should().BeTrue($"loading the real Jina tokenizer.json should succeed; got error: {error}");
        tokenizer.Should().NotBeNull();
        padId.Should().Be(1, "Jina v2's <pad> token sits at id 1 in tokenizer.json");
    }

    [SkippableFact]
    public void Tokenizer_encodesHelloWorld_toExpectedIds()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var ok = JinaCodeEmbeddingGenerator.TryLoadTokenizer(_fixture.Path, out var tokenizer, out _, out _);
        ok.Should().BeTrue();

        var ids = tokenizer!.EncodeToIds(
            "Hello world",
            maxTokenCount: 16,
            out _, out _,
            considerPreTokenization: true,
            considerNormalization: true);

        // Captured during the migration spike (Microsoft.ML.Tokenizers 2.0 against
        // jinaai/jina-embeddings-v2-base-code's committed-as-of-2026-05-10 tokenizer.json).
        // Sequence is: <s>=0, "Hello", " world", </s>=2.
        ids.Should().Equal(new[] { 0, 10564, 7509, 2 });
    }

    [Fact]
    public void TryLoadTokenizer_returnsFalseWithReason_whenFileMalformed()
    {
        // Construct a tokenizer.json with a missing model.type to verify the graceful-fail path.
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-tokenizer-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, "{ \"model\": {} }");
        try
        {
            var ok = JinaCodeEmbeddingGenerator.TryLoadTokenizer(tmp, out var tokenizer, out _, out var error);
            ok.Should().BeFalse();
            tokenizer.Should().BeNull();
            error.Should().NotBeNull();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TryLoadTokenizer_returnsFalse_whenModelTypeUnsupported()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"unigram-tokenizer-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, "{ \"model\": { \"type\": \"Unigram\" }, \"added_tokens\": [] }");
        try
        {
            var ok = JinaCodeEmbeddingGenerator.TryLoadTokenizer(tmp, out var tokenizer, out _, out var error);
            ok.Should().BeFalse();
            tokenizer.Should().BeNull();
            error.Should().Contain("Unigram");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>
    /// xUnit class fixture: resolves a path to the Jina v2 tokenizer.json, preferring (in order)
    /// the live <c>~/.cache</c> copy populated by a real <c>serve</c> run, then a per-project
    /// gitignored cache, then a one-shot HTTP download. If all three fail, sets
    /// <see cref="SkipReason"/> so the tests report as skipped rather than failing on a
    /// network-restricted CI box.
    /// </summary>
    public sealed class TokenizerFixture : IDisposable
    {
        private const string Url = "https://huggingface.co/jinaai/jina-embeddings-v2-base-code/resolve/main/tokenizer.json";

        public string Path { get; }
        public string? SkipReason { get; }

        public TokenizerFixture()
        {
            // 1. Live cache from a real serve run.
            var liveCache = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "devbitslab.sourcegraph", "models",
                "jinaai_jina-embeddings-v2-base-code", "tokenizer.json");
            if (File.Exists(liveCache))
            {
                Path = liveCache;
                return;
            }

            // 2. Per-project gitignored fixture cache.
            var fixtureCache = System.IO.Path.Combine(LocateRepoRoot(), "tests", "fixtures", ".cache", "jina-v2-base-code-tokenizer.json");
            if (File.Exists(fixtureCache))
            {
                Path = fixtureCache;
                return;
            }

            // 3. One-shot download.
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixtureCache)!);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                using var response = http.GetAsync(Url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                File.WriteAllBytes(fixtureCache, bytes);
                Path = fixtureCache;
                return;
            }
            catch (Exception ex)
            {
                Path = string.Empty;
                SkipReason = $"Could not obtain Jina v2 tokenizer.json (live cache + fixture cache + HF download all failed): {ex.GetType().Name}: {ex.Message}";
            }
        }

        public void Dispose() { }

        private static string LocateRepoRoot()
        {
            // Walk up from the test assembly location until we find Directory.Build.props.
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 12; i++)
            {
                if (File.Exists(System.IO.Path.Combine(dir, "Directory.Build.props"))) return dir;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent is null || parent == dir) break;
                dir = parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
