using InnerVoice.Core.AI;

namespace InnerVoice.Core.Tests;

/// The Mac's `modelAdviceFollowsMemory` and `mistralAndHarmonyTemplates`, adapted to PC hardware.
public class ModelCatalogTests
{
    /// Mistral Small and gpt-oss chat formats are found from their GGUF templates and rendered
    /// with the system prompt in its own place; gpt-oss starts its answer on the final channel.
    [Fact]
    public void MistralAndHarmonyTemplates()
    {
        Assert.Equal(ChatTemplate.Mistral, ChatTemplates.Detect("{{ '[SYSTEM_PROMPT]' }}{{ '[INST]' }}"));
        Assert.Equal(ChatTemplate.Harmony, ChatTemplates.Detect("<|start|>assistant<|channel|>final<|message|>"));
        ChatMessage[] msgs = [new(ChatRole.System, "Be brief."), new(ChatRole.User, "Who speaks?")];
        Assert.Equal("[SYSTEM_PROMPT]Be brief.[/SYSTEM_PROMPT][INST]Who speaks?[/INST]", ChatTemplate.Mistral.Render(msgs, ""));
        Assert.EndsWith("<|start|>user<|message|>Who speaks?<|end|><|start|>assistant<|channel|>final<|message|>", ChatTemplate.Harmony.Render(msgs, ""));
        Assert.StartsWith("<|start|>developer<|message|># Instructions\n\nBe brief.<|end|>", ChatTemplate.Harmony.Render(msgs, ""));
        Assert.Equal("[SYSTEM_PROMPT]Be brief.[/SYSTEM_PROMPT][INST]Who speaks?[/INST]{\"", ChatTemplate.Mistral.Render(msgs, "{\""));
        Assert.Contains("</s>", ChatTemplate.Mistral.StopStrings());
        Assert.Contains("<|return|>", ChatTemplate.Harmony.StopStrings());
        // The older formats still win where a template has their markers.
        Assert.Equal(ChatTemplate.Phi, ChatTemplates.Detect("<|user|>{{ m }}<|end|><|assistant|>"));
        Assert.Equal(ChatTemplate.Llama3, ChatTemplates.Detect("<|start_header_id|>user<|end_header_id|>"));
        Assert.DoesNotContain(ModelCatalog.All, m => m.Id.StartsWith("qwen"));
    }

    /// Same files, sizes and SHA-256 as the Mac's ModelCatalog.swift.
    [Fact]
    public void CatalogMatchesTheMac()
    {
        (string id, long bytes, string sha)[] mac =
        [
            ("gemma-4-e2b", 3_184_496_736, "b52f438017efaec5debf1c0d8be690571e212a07c312f1102bbce927258cfc32"),
            ("gemma-4-e4b", 5_126_306_944, "3cf61de12daa015ee0f7b68e7b7c541405bf220e1e942bad8b47cab827d7df80"),
            ("llama-3.1-8b", 4_920_739_200, "b3bdbf23b47d7e6bb791c99b206deb169cd5a96362a9e3399028df2faacdc506"),
            ("phi-4", 8_890_306_112, "01e1f25b3e6931054c6c2227b06f4969828434eebc299e8e171f55dab6814485"),
            ("gpt-oss-20b", 11_624_759_488, "c27536640e410032865dc68781d80a08b98f8db5e93575919af8ccc0568aeb4f"),
            ("mistral-small-3.2", 14_548_880_928, "bcf82c1d4963f91d744d202efeee0724c0987625cd97a737ea2af68b49f141cf"),
            ("gemma-4-26b-a4b", 17_010_980_576, "ef728c8e0c337fd1067b947af006e38a9ef2419e56feced4fd29b4bf0636e30c"),
            ("gemma-4-31b", 18_822_970_304, "9e92cb6236044c6a9870af406029c74a76e0571c157a6f95df724dcc8c7a1575"),
        ];
        Assert.Equal(mac.Select(m => m.id), ModelCatalog.All.Select(m => m.Id));
        foreach (var (id, bytes, sha) in mac)
        {
            var m = ModelCatalog.Model(id)!;
            Assert.Equal(bytes, m.Bytes);
            Assert.Equal(sha, m.Sha256);
        }
        Assert.Equal("Llama 3.1 Community License", ModelCatalog.Model("llama-3.1-8b")!.License);
        Assert.Equal("MIT", ModelCatalog.Model("phi-4")!.License);
    }

    /// A saved Qwen 3 choice is cleared; others stay.
    [Fact]
    public void RetiredQwenChoicesAreCleared()
    {
        foreach (var id in new[] { "qwen3-4b", "qwen3-8b", "qwen3-14b", "qwen3-30b-a3b" })
            Assert.True(ModelCatalog.IsRetiredSelection("catalog:" + id));
        Assert.False(ModelCatalog.IsRetiredSelection("catalog:gemma-4-e4b"));
        Assert.False(ModelCatalog.IsRetiredSelection("file:C:\\Models\\qwen3-4b.gguf"));
        Assert.False(ModelCatalog.IsRetiredSelection(null));
    }

    /// Without a card: Gemma 4 E2B under 16 GB, E4B from 16 GB (the Mac: E2B under 12 GB, E4B
    /// under 24 GB). With one: the most accurate of the Mac's suggestions that fits in it.
    [Fact]
    public void AdviceFollowsMemoryAndCard()
    {
        Assert.Equal("gemma-4-e2b", ModelCatalog.RecommendedID(new Hardware(8, 0, null)));
        Assert.Equal("gemma-4-e4b", ModelCatalog.RecommendedID(new Hardware(16, 0, null)));
        Assert.Equal("gemma-4-e4b", ModelCatalog.RecommendedID(new Hardware(32, 0, null)));
        Assert.Equal("gemma-4-e4b", ModelCatalog.RecommendedID(new Hardware(16, 8, "RTX 3060 Ti")));
        Assert.Equal("phi-4", ModelCatalog.RecommendedID(new Hardware(32, 12, "RTX 3060")));
        Assert.Equal("phi-4", ModelCatalog.RecommendedID(new Hardware(32, 16, "RTX 4080")));
        Assert.Equal("gemma-4-31b", ModelCatalog.RecommendedID(new Hardware(64, 24, "RTX 4090")));
        var big = ModelCatalog.Model("gemma-4-31b")!;
        Assert.Equal(Fit.TooLarge, ModelCatalog.Advice(big, new Hardware(16, 0, null)).Fit);
        Assert.Contains("16 GB", ModelCatalog.Advice(big, new Hardware(16, 0, null)).Note);
        var small = ModelCatalog.Model("gemma-4-e2b")!;
        Assert.NotEqual(Fit.TooLarge, ModelCatalog.Advice(small, new Hardware(64, 24, "RTX 4090")).Fit);
        // gpt-oss and Mistral Small are offered but never the suggestion.
        Assert.Equal(Fit.Good, ModelCatalog.Advice(ModelCatalog.Model("gpt-oss-20b")!, new Hardware(64, 16, "RTX 4080")).Fit);
    }
}
