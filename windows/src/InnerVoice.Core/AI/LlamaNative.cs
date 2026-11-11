// A thin binding to the llama.cpp C API, pinned to build b11205 (the same build as the Mac
// app's Frameworks/llama.xcframework). Struct layouts match b11205's llama.h exactly; the
// tests check their sizes and offsets against numbers taken from the C compiler.
//
// Windows: llama.dll, ggml*.dll next to the app (from the official b11205 Windows release).
// macOS (development): the Mac app's llama.framework, found by `LlamaLibrary.Resolve`.

using System.Reflection;
using System.Runtime.InteropServices;

namespace InnerVoice.Core.AI;

[StructLayout(LayoutKind.Sequential)]
public struct LlamaModelParams
{
    public nint Devices;
    public nint TensorBuftOverrides;
    public int NGpuLayers;
    public int SplitMode;
    public int LoadMode;
    public int LazyMode;
    public int MainGpu;
    public nint TensorSplit;
    public nint ProgressCallback;
    public nint ProgressCallbackUserData;
    public nint KvOverrides;
    public byte VocabOnly;
    public byte CheckTensors;
    public byte UseExtraBufts;
    public byte NoHost;
    public byte NoAlloc;
    public byte LoadMtp;
}

[StructLayout(LayoutKind.Sequential)]
public struct LlamaContextParams
{
    public uint NCtx;
    public uint NBatch;
    public uint NUbatch;
    public uint NSeqMax;
    public uint NRsSeq;
    public uint NOutputsMax;
    public uint NOutputsMaxPerSeq;
    public int NThreads;
    public int NThreadsBatch;
    public int CtxType;
    public int RopeScalingType;
    public int PoolingType;
    public int AttentionType;
    public int FlashAttnType;
    public float RopeFreqBase;
    public float RopeFreqScale;
    public float YarnExtFactor;
    public float YarnAttnFactor;
    public float YarnBetaFast;
    public float YarnBetaSlow;
    public uint YarnOrigCtx;
    public float DefragThold;
    public nint CbEval;
    public nint CbEvalUserData;
    public int TypeK;
    public int TypeV;
    public nint AbortCallback;
    public nint AbortCallbackData;
    public byte Embeddings;
    public byte OffloadKqv;
    public byte NoPerf;
    public byte OpOffload;
    public byte SwaFull;
    public byte KvUnified;
    public nint Samplers;
    public nuint NSamplers;
    public nint CtxOther;
}

[StructLayout(LayoutKind.Sequential)]
public struct LlamaBatch
{
    public int NTokens;
    public nint Token;
    public nint Embd;
    public nint Pos;
    public nint NSeqId;
    public nint SeqId;
    public nint Logits;
}

[StructLayout(LayoutKind.Sequential)]
public struct LlamaSamplerChainParams
{
    public byte NoPerf;
}

public static unsafe partial class LlamaNative
{
    public const string Lib = "llama";
    public const string Ggml = "ggml";
    public const string GgmlBase = "ggml-base";
    public const int LogLevelError = 4;

    [LibraryImport(Ggml, EntryPoint = "ggml_backend_dev_count")] public static partial nuint DevCount();
    [LibraryImport(Ggml, EntryPoint = "ggml_backend_dev_get")] public static partial nint DevGet(nuint index);
    [LibraryImport(GgmlBase, EntryPoint = "ggml_backend_dev_type")] public static partial int DevType(nint dev);
    [LibraryImport(GgmlBase, EntryPoint = "ggml_backend_dev_description")] public static partial nint DevDescription(nint dev);
    [LibraryImport(GgmlBase, EntryPoint = "ggml_backend_dev_memory")] public static partial void DevMemory(nint dev, out nuint free, out nuint total);
    [LibraryImport(Lib, EntryPoint = "llama_backend_init")] public static partial void BackendInit();
    [LibraryImport(Ggml, EntryPoint = "ggml_backend_load_all")] public static partial void GgmlBackendLoadAll();
    [LibraryImport(Ggml, EntryPoint = "ggml_backend_load_all_from_path", StringMarshalling = StringMarshalling.Utf8)] public static partial void GgmlBackendLoadAllFromPath(string dir);
    [LibraryImport(Lib, EntryPoint = "llama_log_set")] public static partial void LogSet(delegate* unmanaged[Cdecl]<int, byte*, nint, void> callback, nint userData);
    [LibraryImport(Lib, EntryPoint = "llama_model_default_params")] public static partial LlamaModelParams ModelDefaultParams();
    [LibraryImport(Lib, EntryPoint = "llama_context_default_params")] public static partial LlamaContextParams ContextDefaultParams();
    [LibraryImport(Lib, EntryPoint = "llama_model_load_from_file", StringMarshalling = StringMarshalling.Utf8)] public static partial nint ModelLoadFromFile(string path, LlamaModelParams p);
    [LibraryImport(Lib, EntryPoint = "llama_init_from_model")] public static partial nint InitFromModel(nint model, LlamaContextParams p);
    [LibraryImport(Lib, EntryPoint = "llama_free")] public static partial void Free(nint ctx);
    [LibraryImport(Lib, EntryPoint = "llama_model_free")] public static partial void ModelFree(nint model);
    [LibraryImport(Lib, EntryPoint = "llama_model_chat_template", StringMarshalling = StringMarshalling.Utf8)] public static partial nint ModelChatTemplate(nint model, string? name);
    [LibraryImport(Lib, EntryPoint = "llama_model_desc")] public static partial int ModelDesc(nint model, byte* buf, nuint size);
    [LibraryImport(Lib, EntryPoint = "llama_model_size")] public static partial ulong ModelSize(nint model);
    [LibraryImport(Lib, EntryPoint = "llama_model_get_vocab")] public static partial nint ModelGetVocab(nint model);
    [LibraryImport(Lib, EntryPoint = "llama_n_ctx")] public static partial uint NCtx(nint ctx);
    [LibraryImport(Lib, EntryPoint = "llama_get_memory")] public static partial nint GetMemory(nint ctx);
    [LibraryImport(Lib, EntryPoint = "llama_memory_seq_rm")][return: MarshalAs(UnmanagedType.U1)] public static partial bool MemorySeqRm(nint mem, int seqId, int p0, int p1);
    [LibraryImport(Lib, EntryPoint = "llama_memory_clear")] public static partial void MemoryClear(nint mem, [MarshalAs(UnmanagedType.U1)] bool data);
    [LibraryImport(Lib, EntryPoint = "llama_decode")] public static partial int Decode(nint ctx, LlamaBatch batch);
    [LibraryImport(Lib, EntryPoint = "llama_batch_get_one")] public static partial LlamaBatch BatchGetOne(int* tokens, int n);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_chain_default_params")] public static partial LlamaSamplerChainParams SamplerChainDefaultParams();
    [LibraryImport(Lib, EntryPoint = "llama_sampler_chain_init")] public static partial nint SamplerChainInit(LlamaSamplerChainParams p);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_chain_add")] public static partial void SamplerChainAdd(nint chain, nint smpl);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_init_grammar", StringMarshalling = StringMarshalling.Utf8)] public static partial nint SamplerInitGrammar(nint vocab, string grammar, string root);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_init_penalties")] public static partial nint SamplerInitPenalties(int nVocab, int lastN, float repeat, float freq, float present);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_init_greedy")] public static partial nint SamplerInitGreedy();
    [LibraryImport(Lib, EntryPoint = "llama_sampler_init_top_k")] public static partial nint SamplerInitTopK(int k);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_init_top_p")] public static partial nint SamplerInitTopP(float p, nuint minKeep);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_init_temp")] public static partial nint SamplerInitTemp(float t);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_init_dist")] public static partial nint SamplerInitDist(uint seed);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_sample")] public static partial int SamplerSample(nint smpl, nint ctx, int idx);
    [LibraryImport(Lib, EntryPoint = "llama_sampler_free")] public static partial void SamplerFree(nint smpl);
    [LibraryImport(Lib, EntryPoint = "llama_vocab_is_eog")][return: MarshalAs(UnmanagedType.U1)] public static partial bool VocabIsEog(nint vocab, int token);
    [LibraryImport(Lib, EntryPoint = "llama_vocab_n_tokens")] public static partial int VocabNTokens(nint vocab);
    [LibraryImport(Lib, EntryPoint = "llama_tokenize")] public static partial int Tokenize(nint vocab, byte* text, int textLen, int* tokens, int nTokensMax, [MarshalAs(UnmanagedType.U1)] bool addSpecial, [MarshalAs(UnmanagedType.U1)] bool parseSpecial);
    [LibraryImport(Lib, EntryPoint = "llama_token_to_piece")] public static partial int TokenToPiece(nint vocab, int token, byte* buf, int length, int lstrip, [MarshalAs(UnmanagedType.U1)] bool special);
}

/// Finds the native library. On Windows the DLLs sit next to the app (or in
/// INNERVOICE_LLAMA_DIR); on a Mac (for development) it's the Mac app's llama.framework, or
/// INNERVOICE_LLAMA_LIB.
public static class LlamaLibrary
{
    static int installed;

    /// The folder the Windows DLLs were loaded from, where ggml finds its CPU and GPU backends.
    public static string? BackendFolder { get; private set; }

    public static void InstallResolver()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(LlamaNative).Assembly, Resolve);
    }

    static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name is not (LlamaNative.Lib or LlamaNative.Ggml or LlamaNative.GgmlBase)) return 0;
        if (Environment.GetEnvironmentVariable("INNERVOICE_LLAMA_LIB") is { Length: > 0 } custom && NativeLibrary.TryLoad(custom, out var h0)) return h0;
        if (OperatingSystem.IsWindows())
        {
            var dirs = new[] { Environment.GetEnvironmentVariable("INNERVOICE_LLAMA_DIR"), AppContext.BaseDirectory };
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var file = Path.Combine(dir, name + ".dll");
                if (File.Exists(file) && NativeLibrary.TryLoad(file, out var hw))
                {
                    BackendFolder ??= dir;
                    return hw;
                }
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            // The Mac build has llama and ggml in one framework binary.
            foreach (var candidate in MacCandidates())
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h)) return h;
        }
        return 0; // default search: next to the app on Windows
    }

    static IEnumerable<string> MacCandidates()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "Frameworks/llama.xcframework/macos-arm64_x86_64/llama.framework/llama");
            dir = dir.Parent;
        }
    }

    /// True when llama.cpp can be loaded on this machine.
    public static bool IsAvailable
    {
        get
        {
            InstallResolver();
            try { _ = LlamaNative.ModelDefaultParams(); return true; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }
}
