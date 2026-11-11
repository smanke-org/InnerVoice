// Ported from Sources/InnerVoice/AI/LlamaEngine.swift.

using System.Runtime.InteropServices;
using System.Text;

namespace InnerVoice.Core.AI;

/// A loaded GGUF model and one llama.cpp context. Not thread-safe: call it from one
/// background task at a time (the tagging loop). `Cancel` may be called from any thread.
///
/// Consecutive passages share the same instructions, so the engine keeps the tokens in
/// the KV cache and only evaluates what changed.
public sealed unsafe class LlamaEngine : IDisposable
{
    public sealed class EngineException(string message) : Exception(message);

    public sealed record Options(uint ContextLength = 8192, int GpuLayers = 999);

    public sealed record Sampling(float Temperature = 0.1f, int TopK = 40, float TopP = 0.9f, uint Seed = 42);

    public string? ModelPath { get; private set; }
    public ChatTemplate Template { get; private set; } = ChatTemplate.Plain;
    public bool CloseThinking { get; private set; }
    public string Description { get; private set; } = "";

    nint model, ctx;
    readonly List<int> cached = [];
    int batchSize = 512;
    int generation;

    static int backendReady;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void Log(int level, byte* text, nint user)
    {
        // Only errors are worth the noise; llama.cpp logs every tensor at load otherwise.
        if (level != LlamaNative.LogLevelError || text is null) return;
        System.Diagnostics.Debug.WriteLine("llama: " + Marshal.PtrToStringUTF8((nint)text)?.Trim());
    }

    /// The graphics cards llama.cpp can use (Vulkan on Windows, Metal on a Mac), with their
    /// own memory in GB. Empty when only the processor is available.
    public static List<(string Name, double TotalGB)> Gpus()
    {
        var list = new List<(string, double)>();
        try
        {
            EnsureBackend();
            nuint n = LlamaNative.DevCount();
            for (nuint i = 0; i < n; i++)
            {
                var dev = LlamaNative.DevGet(i);
                if (LlamaNative.DevType(dev) != 1) continue; // dedicated-memory GPUs only
                LlamaNative.DevMemory(dev, out _, out var total);
                list.Add((Marshal.PtrToStringUTF8(LlamaNative.DevDescription(dev)) ?? "GPU", total / 1_073_741_824.0));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return list;
    }

    public static void EnsureBackend()
    {
        if (Interlocked.Exchange(ref backendReady, 1) == 1) return;
        LlamaLibrary.InstallResolver();
        LlamaNative.LogSet(&Log, 0); // loads the library, which sets BackendFolder
        // The Windows release loads its CPU and GPU backends as separate DLLs.
        try
        {
            if (LlamaLibrary.BackendFolder is { } dir) LlamaNative.GgmlBackendLoadAllFromPath(dir);
            else LlamaNative.GgmlBackendLoadAll();
        }
        catch (EntryPointNotFoundException) { }
        LlamaNative.BackendInit();
    }

    public bool IsLoaded => ctx != 0;

    /// Loads a model, replacing the current one.
    public void Load(string path, Options? options = null)
    {
        options ??= new Options();
        EnsureBackend();
        Unload();
        var mp = LlamaNative.ModelDefaultParams();
        mp.NGpuLayers = options.GpuLayers;
        var m = LlamaNative.ModelLoadFromFile(path, mp);
        if (m == 0) throw new EngineException($"Couldn't load the model at {path}.");
        model = m;
        var cp = LlamaNative.ContextDefaultParams();
        cp.NCtx = options.ContextLength;
        cp.NBatch = 512;
        cp.NUbatch = 512;
        // Sliding-window caches can't drop a tail of positions; the full-size cache can,
        // which keeps prefix reuse working on Gemma.
        cp.SwaFull = 1;
        int threads = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 2));
        cp.NThreads = threads;
        cp.NThreadsBatch = threads;
        var c = LlamaNative.InitFromModel(m, cp);
        if (c == 0)
        {
            Unload();
            throw new EngineException("Couldn't create a model context. The PC may be low on memory.");
        }
        ctx = c;
        batchSize = (int)cp.NBatch;
        var tmplPtr = LlamaNative.ModelChatTemplate(m, null);
        var tmpl = tmplPtr == 0 ? null : Marshal.PtrToStringUTF8(tmplPtr);
        Template = ChatTemplates.Detect(tmpl);
        CloseThinking = ChatTemplates.HasThinking(tmpl);
        var buf = stackalloc byte[256];
        int n = LlamaNative.ModelDesc(m, buf, 256);
        Description = n > 0 ? Encoding.UTF8.GetString(buf, Math.Min(n, 255)) : "";
        ModelPath = path;
        cached.Clear();
    }

    public void Unload()
    {
        if (ctx != 0) LlamaNative.Free(ctx);
        if (model != 0) LlamaNative.ModelFree(model);
        ctx = 0;
        model = 0;
        ModelPath = null;
        cached.Clear();
    }

    public void Dispose() => Unload();

    /// Stops any generation in flight at its next token.
    public void Cancel() => Interlocked.Increment(ref generation);

    /// Evaluates `prompt` and generates up to `maxTokens`. Returns null when cancelled.
    /// `shouldStop` sees the text so far and can end generation early. `progress` hears
    /// 0…1 as the prompt is read (to 0.5) and the reply is written (to about 0.95).
    public string? Generate(string prompt, int maxTokens, Sampling? sampling = null, string? grammar = null,
                            Func<string, bool>? shouldStop = null, Action<double>? progress = null)
    {
        if (ctx == 0 || model == 0) return null;
        sampling ??= new Sampling();
        int myGeneration = Volatile.Read(ref generation);
        var vocab = LlamaNative.ModelGetVocab(model);
        var tokens = Tokenize(prompt, vocab);
        int limit = (int)LlamaNative.NCtx(ctx) - maxTokens - 8;
        if (tokens.Count > limit)
        {
            // Keep the start (template and instructions) and the end (the question).
            var head = tokens.Take(limit / 3).ToList();
            var tail = tokens.Skip(tokens.Count - (limit - head.Count)).ToList();
            tokens = [.. head, .. tail];
        }

        // Reuse the shared prefix; always re-evaluate at least the last token for fresh logits.
        int common = 0;
        while (common < cached.Count && common < tokens.Count && cached[common] == tokens[common]) common++;
        if (common == tokens.Count) common--;
        var mem = LlamaNative.GetMemory(ctx);
        if (common < cached.Count)
        {
            if (!LlamaNative.MemorySeqRm(mem, 0, common, -1))
            {
                LlamaNative.MemoryClear(mem, true);
                common = 0;
            }
            cached.RemoveRange(common, cached.Count - common);
        }
        int pos = common;
        var arr = tokens.ToArray();
        while (pos < arr.Length)
        {
            if (Volatile.Read(ref generation) != myGeneration) return null;
            int n = Math.Min(batchSize, arr.Length - pos);
            int result;
            fixed (int* p = &arr[pos]) result = LlamaNative.Decode(ctx, LlamaNative.BatchGetOne(p, n));
            if (result != 0)
            {
                LlamaNative.MemoryClear(mem, true);
                cached.Clear();
                throw new EngineException("The model failed while evaluating the prompt.");
            }
            cached.AddRange(arr.AsSpan(pos, n).ToArray());
            pos += n;
            progress?.Invoke(0.5 * (pos - common) / Math.Max(1, arr.Length - common));
        }

        var sampler = MakeSampler(sampling, vocab, grammar);
        try
        {
            var output = new StringBuilder();
            var pending = new List<byte>();
            var decoder = new UTF8Encoding(false, true);
            for (int step = 0; step < maxTokens; step++)
            {
                if (Volatile.Read(ref generation) != myGeneration) return null;
                if (step % 8 == 0) progress?.Invoke(0.5 + 0.45 * Math.Min(1, (double)step / Math.Max(1, maxTokens / 2)));
                int token = LlamaNative.SamplerSample(sampler, ctx, -1);
                if (LlamaNative.VocabIsEog(vocab, token)) break;
                pending.AddRange(Piece(token, vocab));
                // A multi-byte character can span tokens; only flush complete UTF-8.
                try
                {
                    output.Append(decoder.GetString(pending.ToArray()));
                    pending.Clear();
                }
                catch (DecoderFallbackException) { }
                int one = token;
                if (LlamaNative.Decode(ctx, LlamaNative.BatchGetOne(&one, 1)) != 0) break;
                cached.Add(token);
                if (shouldStop?.Invoke(output.ToString()) == true) break;
            }
            return output.ToString();
        }
        finally
        {
            LlamaNative.SamplerFree(sampler);
        }
    }

    static nint MakeSampler(Sampling s, nint vocab, string? grammar)
    {
        var chain = LlamaNative.SamplerChainInit(LlamaNative.SamplerChainDefaultParams());
        // The grammar only lets through tokens that keep the reply valid JSON of the right shape.
        if (grammar is not null)
        {
            var g = LlamaNative.SamplerInitGrammar(vocab, grammar, "root");
            if (g != 0) LlamaNative.SamplerChainAdd(chain, g);
        }
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitPenalties(LlamaNative.VocabNTokens(vocab), 64, 1.1f, 0, 0));
        if (s.Temperature <= 0)
        {
            LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitGreedy());
        }
        else
        {
            LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitTopK(s.TopK));
            LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitTopP(s.TopP, 1));
            LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitTemp(s.Temperature));
            LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitDist(s.Seed));
        }
        return chain;
    }

    static List<int> Tokenize(string text, nint vocab)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var tokens = new int[utf8.Length + 16];
        int n;
        fixed (byte* t = utf8)
        {
            fixed (int* o = tokens) n = LlamaNative.Tokenize(vocab, t, utf8.Length, o, tokens.Length, true, true);
            if (n < 0)
            {
                tokens = new int[-n];
                fixed (int* o = tokens) n = LlamaNative.Tokenize(vocab, t, utf8.Length, o, tokens.Length, true, true);
            }
        }
        if (n < 0) throw new EngineException("Couldn't tokenize the prompt.");
        return tokens.Take(n).ToList();
    }

    static byte[] Piece(int token, nint vocab)
    {
        var buf = new byte[64];
        int n;
        fixed (byte* b = buf) n = LlamaNative.TokenToPiece(vocab, token, b, buf.Length, 0, false);
        if (n < 0)
        {
            buf = new byte[-n];
            fixed (byte* b = buf) n = LlamaNative.TokenToPiece(vocab, token, b, buf.Length, 0, false);
        }
        return buf.Take(Math.Max(0, n)).ToArray();
    }
}
