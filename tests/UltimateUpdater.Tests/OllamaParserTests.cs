using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Ollama models. The whole source stands on one comparison, the local manifest digest against the one the
/// registry serves for the same tag, and both halves were verified on a clean VM: for `all-minilm:latest`
/// the local API and the registry's `ollama-content-digest` header agreed bit for bit
/// (`1b226e2802dbb772b5fc32a58f103ca1804ef7501331012de126ab22f67475ef`).
/// </summary>
public class OllamaParserTests
{
    /// <summary>Captured verbatim from `GET http://127.0.0.1:11434/api/tags` on the VM.</summary>
    private const string RealResponse = """
        {
          "models": [
            {
              "name": "all-minilm:latest",
              "model": "all-minilm:latest",
              "modified_at": "2026-07-26T18:03:01.8176022+02:00",
              "size": 45960996,
              "digest": "1b226e2802dbb772b5fc32a58f103ca1804ef7501331012de126ab22f67475ef",
              "details": { "format": "gguf", "family": "bert", "parameter_size": "23M" }
            }
          ]
        }
        """;

    [Fact]
    public void The_local_api_gives_name_digest_size_and_date()
    {
        var models = OllamaParsers.ParseLocalModels(RealResponse);

        var only = Assert.Single(models);
        Assert.Equal("all-minilm:latest", only.Name);
        Assert.Equal("1b226e2802dbb772b5fc32a58f103ca1804ef7501331012de126ab22f67475ef", only.Digest);
        Assert.Equal(45960996, only.Size);
        Assert.Equal(2026, only.Modified?.Year);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"models\":[]}")]
    [InlineData("{\"models\":[{\"name\":\"x:latest\"}]}")]
    public void Anything_without_a_usable_digest_yields_nothing(string json)
    {
        Assert.Empty(OllamaParsers.ParseLocalModels(json));
    }

    /// <summary>The registry writes "sha256:…"; the local API writes the bare hex. Both are the same digest.</summary>
    [Fact]
    public void A_digest_normalizes_with_or_without_its_prefix()
    {
        const string Hex = "1b226e2802dbb772b5fc32a58f103ca1804ef7501331012de126ab22f67475ef";

        Assert.Equal(Hex, OllamaParsers.NormalizeDigest(Hex));
        Assert.Equal(Hex, OllamaParsers.NormalizeDigest("sha256:" + Hex));
        Assert.Equal(Hex, OllamaParsers.NormalizeDigest("  SHA256:" + Hex.ToUpperInvariant() + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("sha256:deadbeef")]
    [InlineData("zzzz6e2802dbb772b5fc32a58f103ca1804ef7501331012de126ab22f67475ef")]
    public void Something_that_is_not_a_digest_is_refused(string? value)
    {
        Assert.Null(OllamaParsers.NormalizeDigest(value));
    }

    [Theory]
    [InlineData("all-minilm:latest", "all-minilm", "latest")]
    [InlineData("all-minilm", "all-minilm", "latest")]
    [InlineData("qwen3:0.6b", "qwen3", "0.6b")]
    [InlineData("nomic_embed-text:v1.5", "nomic_embed-text", "v1.5")]
    public void A_library_model_splits_into_name_and_tag(string model, string name, string tag)
    {
        Assert.True(OllamaParsers.TrySplitLibraryModel(model, out var actualName, out var actualTag));
        Assert.Equal(name, actualName);
        Assert.Equal(tag, actualTag);
    }

    /// <summary>
    /// A name is put into a URL, so anything that could steer that URL somewhere else is refused rather than
    /// escaped and hoped for: a user namespace lives at a path this app cannot know, and the rest is hostile.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("myuser/mymodel:latest")]
    [InlineData("registry.example.com/x/y:1")]
    [InlineData("..:latest")]
    [InlineData("model:a:b")]
    [InlineData("model:tag with space")]
    [InlineData("model:tag?x=1")]
    [InlineData("back\\slash:latest")]
    public void Anything_that_is_not_a_plain_library_model_is_refused(string? model)
    {
        Assert.False(OllamaParsers.TrySplitLibraryModel(model, out _, out _));
    }
}
