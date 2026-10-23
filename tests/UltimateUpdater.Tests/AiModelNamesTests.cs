using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Table-driven check of <see cref="AiModelNames.Friendly"/> against real Ollama tags across the current
/// model families, plus a namespace prefix and a family this table has never heard of, to prove the
/// generic fallback (title-case the parts, keep the digits) rather than just the table hits.
/// </summary>
public class AiModelNamesTests
{
    [Theory]
    [InlineData("llama3.2:3b-instruct-q4_K_M", "Llama 3.2 3B Instruct")]
    [InlineData("llama3.1:8b", "Llama 3.1 8B")]
    [InlineData("llama3:70b-instruct-q4_0", "Llama 3 70B Instruct")]
    [InlineData("llama2-uncensored:7b", "Llama 2 Uncensored 7B")]
    [InlineData("codellama:13b-instruct", "Code Llama 13B Instruct")]
    [InlineData("llama-guard3:8b", "Llama Guard 3 8B")]
    [InlineData("qwen2.5:7b", "Qwen 2.5 7B")]
    [InlineData("qwen2.5-coder:7b-instruct-q8_0", "Qwen 2.5 Coder 7B Instruct")]
    [InlineData("qwq:32b", "QwQ 32B")]
    [InlineData("gemma2:9b", "Gemma 2 9B")]
    [InlineData("gemma3:27b-it-q4_K_M", "Gemma 3 27B It")]
    [InlineData("codegemma:7b", "CodeGemma 7B")]
    [InlineData("mistral:7b-instruct-v0.2-fp16", "Mistral 7B Instruct V0.2")]
    [InlineData("mixtral:8x7b", "Mixtral 8x7B")]
    [InlineData("mistral-nemo:12b", "Mistral Nemo 12B")]
    [InlineData("mistral-small:22b", "Mistral Small 22B")]
    [InlineData("codestral:22b", "Codestral 22B")]
    [InlineData("ministral:8b", "Ministral 8B")]
    [InlineData("magistral:24b", "Magistral 24B")]
    [InlineData("devstral:24b", "Devstral 24B")]
    [InlineData("phi3:14b-medium-q4_K_M", "Phi 3 14B Medium")]
    [InlineData("phi4:latest", "Phi 4")]
    [InlineData("deepseek-r1:7b", "DeepSeek-R1 7B")]
    [InlineData("deepseek-coder:6.7b", "DeepSeek Coder 6.7B")]
    [InlineData("gpt-oss:20b", "gpt-oss 20B")]
    [InlineData("granite3.1-dense:8b", "Granite 3.1 Dense 8B")]
    [InlineData("command-r:35b", "Command R 35B")]
    [InlineData("aya:8b", "Aya 8B")]
    [InlineData("falcon:7b", "Falcon 7B")]
    [InlineData("starcoder2:15b", "StarCoder 2 15B")]
    [InlineData("yi:34b", "Yi 34B")]
    [InlineData("smollm2:1.7b", "SmolLM 2 1.7B")]
    [InlineData("tinyllama:1.1b", "TinyLlama 1.1B")]
    [InlineData("nomic-embed-text:latest", "Nomic Embed Text")]
    [InlineData("mxbai-embed-large:latest", "mxbai Embed Large")]
    [InlineData("all-minilm:l6-v2", "all-MiniLM L6 V2")]
    [InlineData("bge-m3:latest", "BGE M 3")]
    [InlineData("snowflake-arctic-embed:335m", "Snowflake Arctic Embed 335M")]
    [InlineData("llava:13b", "LLaVA 13B")]
    [InlineData("moondream:1.8b", "Moondream 1.8B")]
    [InlineData("minicpm-v:8b", "MiniCPM V 8B")]
    [InlineData("olmo2:13b", "OLMo 2 13B")]
    [InlineData("nemotron-mini:4b", "Nemotron Mini 4B")]
    [InlineData("hermes3:8b", "Hermes 3 8B")]
    [InlineData("dolphin-mistral:7b", "Dolphin Mistral 7B")]
    [InlineData("glm4:9b", "GLM 4 9B")]
    [InlineData("internlm2:7b", "InternLM 2 7B")]
    [InlineData("exaone3.5:7.8b", "EXAONE 3.5 7.8B")]
    [InlineData("solar:10.7b", "Solar 10.7B")]
    [InlineData("wizardlm2:7b", "WizardLM 2 7B")]
    [InlineData("openchat:7b", "OpenChat 7B")]
    [InlineData("user/llama3.2:3b", "Llama 3.2 3B")]
    [InlineData("registry.ollama.ai/library/qwen2.5:7b", "Qwen 2.5 7B")]
    [InlineData("foo-bar2:7b-q5_K_S", "Foo Bar 2 7B")]
    [InlineData("zorblax", "Zorblax")]
    [InlineData("zorblax-nova9:latest", "Zorblax Nova 9")]
    public void Friendly_shortens_a_raw_tag(string raw, string expected)
    {
        Assert.Equal(expected, AiModelNames.Friendly(raw));
    }

    [Fact]
    public void Friendly_returns_the_input_unchanged_for_null_or_blank()
    {
        Assert.Equal("", AiModelNames.Friendly(""));
    }
}
