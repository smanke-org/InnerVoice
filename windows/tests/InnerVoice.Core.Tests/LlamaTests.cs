using System.Runtime.InteropServices;
using InnerVoice.Core.AI;

namespace InnerVoice.Core.Tests;

public class LlamaTests
{
    /// Sizes and offsets from clang against b11205's llama.h (64-bit; the same on Windows x64/ARM64).
    [Fact]
    public void StructLayoutsMatchLlamaH()
    {
        Assert.Equal(80, Marshal.SizeOf<LlamaModelParams>());
        Assert.Equal(16, (int)Marshal.OffsetOf<LlamaModelParams>(nameof(LlamaModelParams.NGpuLayers)));
        Assert.Equal(32, (int)Marshal.OffsetOf<LlamaModelParams>(nameof(LlamaModelParams.MainGpu)));
        Assert.Equal(40, (int)Marshal.OffsetOf<LlamaModelParams>(nameof(LlamaModelParams.TensorSplit)));
        Assert.Equal(64, (int)Marshal.OffsetOf<LlamaModelParams>(nameof(LlamaModelParams.KvOverrides)));
        Assert.Equal(72, (int)Marshal.OffsetOf<LlamaModelParams>(nameof(LlamaModelParams.VocabOnly)));
        Assert.Equal(160, Marshal.SizeOf<LlamaContextParams>());
        Assert.Equal(28, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.NThreads)));
        Assert.Equal(56, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.RopeFreqBase)));
        Assert.Equal(88, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.CbEval)));
        Assert.Equal(104, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.TypeK)));
        Assert.Equal(112, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.AbortCallback)));
        Assert.Equal(128, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.Embeddings)));
        Assert.Equal(132, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.SwaFull)));
        Assert.Equal(136, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.Samplers)));
        Assert.Equal(152, (int)Marshal.OffsetOf<LlamaContextParams>(nameof(LlamaContextParams.CtxOther)));
        Assert.Equal(56, Marshal.SizeOf<LlamaBatch>());
        Assert.Equal(1, Marshal.SizeOf<LlamaSamplerChainParams>());
    }

    [Fact]
    public void DefaultParamsComeBackSane()
    {
        if (!LlamaLibrary.IsAvailable) return; // no llama library on this machine
        var mp = LlamaNative.ModelDefaultParams();
        var cp = LlamaNative.ContextDefaultParams();
        Assert.True(cp.NBatch is > 0 and <= 65536, $"n_batch {cp.NBatch}");
        Assert.True(cp.NSeqMax >= 1, $"n_seq_max {cp.NSeqMax}");
        Assert.Equal(0, mp.VocabOnly);
        Assert.Equal(1, cp.OffloadKqv);
    }

    /// Set INNERVOICE_TEST_MODEL to a GGUF file to run a real generation with a grammar.
    [Fact]
    public void GeneratesWithAGrammar()
    {
        var path = Environment.GetEnvironmentVariable("INNERVOICE_TEST_MODEL");
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !LlamaLibrary.IsAvailable) return;
        using var engine = new LlamaEngine();
        engine.Load(path, new LlamaEngine.Options(ContextLength: 2048));
        var prompt = engine.Template.Render([new(ChatRole.System, "Answer in JSON."), new(ChatRole.User, "Who says “Slow down, Pip”? The choices are Mara and Tom. Mara is talking to Pip.")], "", engine.CloseThinking);
        const string grammar = "root ::= \"{\\\"speaker\\\": \\\"\" (\"Mara\" | \"Tom\") \"\\\"}\"";
        var reply = engine.Generate(prompt, 32, new LlamaEngine.Sampling(Temperature: 0), grammar);
        Assert.NotNull(reply);
        Assert.Matches("^\\{\"speaker\": \"(Mara|Tom)\"\\}$", reply!.Trim());
        Console.WriteLine($"Model {engine.Description} ({engine.Template}) replied {reply}");
    }
}
