// Pocket TTS (Kyutai, CC BY 4.0) on ONNX Runtime: the same model the Mac runs on MLX
// (mlx-community/pocket-tts, Kyutai's tts_b6369a24 weights), from the ONNX export at
// KevinAHM/pocket-tts-onnx (VoicePacks.cs pins the files; windows/tools/PocketCheck shows every
// weight is Kyutai's). Five graphs:
//   mimi_encoder      a voice clip (24 kHz) → its conditioning frames: Mimi's encoder and the
//                     model's speaker projection, [1, T, 1024]
//   text_conditioner  SentencePiece token ids → text embeddings, [1, N, 1024]
//   flow_lm_main      the transformer: takes the voice, then the text, then one latent at a
//                     time, with its key/value cache as state_* in and out_state_* out; gives the
//                     next frame's conditioning and the end-of-speech logit
//   flow_lm_flow      the flow net: noise → the next latent, Euler steps
//   mimi_decoder      latents → audio (1920 samples a frame), streaming state in and out
// The steps follow mlx-audio's PocketTTSModel (what the Mac uses): the text is split into groups
// of whole sentences of up to 50 tokens, each said after the voice; the noise is scaled by the
// square root of the temperature; one flow step; speech ends 3 frames (5 for four words or
// fewer) after the end logit passes -4; at most (words + 2) seconds a group.

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace InnerVoice.Core.Speech.Neural;

public sealed class PocketModel : IDisposable
{
    public const int SampleRate = 24_000;
    public const int SamplesPerFrame = 1920;
    public const double FrameRate = 12.5;
    const int LatentDim = 32, ConditioningDim = 1024;
    const float EosThreshold = -4;
    const int LsdSteps = 1;
    const int MaxTokensPerGroup = 50;
    const int DecodeChunk = 15;

    readonly InferenceSession encoder, text, main, flow, decoder;
    public SentencePiece Tokenizer { get; }
    readonly HashSet<int> endTokens;
    readonly string[] mainStateNames, mainOutStateNames, decoderStateNames, decoderOutStateNames;

    public PocketModel(string folder)
    {
        Tokenizer = SentencePiece.FromFile(Path.Combine(folder, "tokenizer.model"));
        string M(string name) => Path.Combine(folder, "onnx", name + ".onnx");
        encoder = Onnx.Open(M("mimi_encoder"), small: true);
        text = Onnx.Open(M("text_conditioner"), small: true);
        main = Onnx.Open(M("flow_lm_main"), small: true);
        flow = Onnx.Open(M("flow_lm_flow"), small: true);
        decoder = Onnx.Open(M("mimi_decoder"), small: true);
        endTokens = [.. Tokenizer.Encode(".!...?")];
        mainStateNames = StateNames(main);
        mainOutStateNames = mainStateNames.Select(n => "out_" + n).ToArray();
        decoderStateNames = StateNames(decoder);
        decoderOutStateNames = decoderStateNames.Select(n => "out_" + n).ToArray();
    }

    static string[] StateNames(InferenceSession s) =>
        s.InputMetadata.Keys.Where(k => k.StartsWith("state_")).OrderBy(k => int.Parse(k[6..])).ToArray();

    public void Dispose()
    {
        encoder.Dispose(); text.Dispose(); main.Dispose(); flow.Dispose(); decoder.Dispose();
    }

    // MARK: The voice

    /// A clip's conditioning frames ([T, 1024], row-major), what the model clones the voice
    /// from. Worth keeping per clip: it's the same for every line.
    public float[] EncodeVoice(float[] clip)
    {
        if (clip.Length < SamplesPerFrame) throw SpeechException.Failed("the voice clip is too short to clone from.");
        using var audio = Onnx.Floats(clip, 1, 1, clip.Length);
        using var outs = Onnx.Run(encoder, new Dictionary<string, OrtValue> { ["audio"] = audio });
        var shape = Onnx.Shape(outs[0]);
        if (shape[^1] != ConditioningDim) throw SpeechException.Failed("the voice clip couldn’t be read by the voice model.");
        return outs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    // MARK: Text

    /// Pocket TTS's own preparation: one line, a capital first, a full stop when it ends in a
    /// letter or digit. Returns how many frames to keep after the end of speech is seen.
    public static (string Text, int FramesAfterEos) Prepare(string text)
    {
        var t = text.Trim().Replace("\n", " ").Replace("\r", " ").Replace("  ", " ");
        if (t.Length == 0) return ("", 3);
        int words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int guess = words <= 4 ? 3 : 1;
        if (!char.IsUpper(t[0])) t = char.ToUpperInvariant(t[0]) + t[1..];
        if (char.IsLetterOrDigit(t[^1])) t += ".";
        return (t, guess + 2);
    }

    /// The text in groups of whole sentences of at most 50 tokens, as mlx-audio and Kyutai's
    /// reference split it.
    public List<string> Groups(string text) => Groups(Tokenizer, text, endTokens);

    /// The same, with a tokenizer of one's own (tests). A sentence ends after any of the tokens
    /// of ".!...?" (the reference's own rule, which counts a lone "▁" too).
    public static List<string> Groups(SentencePiece tokenizer, string text) => Groups(tokenizer, text, [.. tokenizer.Encode(".!...?")]);

    static List<string> Groups(SentencePiece tokenizer, string text, HashSet<int> endTokens)
    {
        var (prepared, _) = Prepare(text);
        if (prepared.Length == 0) return [];
        var tokens = tokenizer.Encode(prepared);
        if (tokens.Count == 0) return [prepared];
        var ends = new List<int> { 0 };
        bool previousWasEnd = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (endTokens.Contains(tokens[i])) previousWasEnd = true;
            else
            {
                if (previousWasEnd) ends.Add(i);
                previousWasEnd = false;
            }
        }
        ends.Add(tokens.Count);
        var groups = new List<string>();
        string current = "";
        int count = 0;
        for (int i = 0; i + 1 < ends.Count; i++)
        {
            int n = ends[i + 1] - ends[i];
            if (n == 0) continue;
            var piece = tokenizer.Decode(tokens.GetRange(ends[i], n));
            if (current.Length == 0) { current = piece; count = n; continue; }
            if (count + n > MaxTokensPerGroup)
            {
                groups.Add(current.Trim());
                current = piece;
                count = n;
            }
            else
            {
                current += " " + piece;
                count += n;
            }
        }
        if (current.Length > 0) groups.Add(current.Trim());
        return groups;
    }

    // MARK: Speaking

    /// Says `text` in the voice of `voice` (from EncodeVoice). `temperature` is the sampler's
    /// (0.7 is the model's own); the same seed gives the same take on this PC.
    public float[] Speak(string text, float[] voice, double temperature, ulong seed, CancellationToken ct, Action<double>? progress = null)
    {
        var groups = Groups(text);
        var rng = new Sampler(seed);
        var audio = new List<float>();
        for (int g = 0; g < groups.Count; g++)
        {
            ct.ThrowIfCancellationRequested();
            var latents = Generate(groups[g], voice, temperature, rng, ct);
            audio.AddRange(Decode(latents, ct));
            progress?.Invoke((g + 1) / (double)groups.Count);
        }
        return audio.ToArray();
    }

    /// One group of sentences → its latents ([frames × 32]).
    List<float[]> Generate(string group, float[] voice, double temperature, Sampler rng, CancellationToken ct)
    {
        int framesAfterEos = Prepare(group).FramesAfterEos;
        int words = group.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int maxFrames = (int)((words + 2.0) * FrameRate);
        var tokens = Tokenizer.Encode(group).Select(t => (long)t).ToArray();

        var state = InitialState(main, mainStateNames);
        try
        {
            using var emptySeq = Onnx.Floats([], 1, 0, LatentDim);
            using var emptyText = Onnx.Floats([], 1, 0, ConditioningDim);
            // The voice, then the text, go into the transformer's cache.
            using (var v = Onnx.Floats(voice, 1, voice.Length / ConditioningDim, ConditioningDim))
                Step(state, emptySeq, v);
            if (tokens.Length > 0)
            {
                using var ids = Onnx.Longs(tokens, 1, tokens.Length);
                using var embOuts = Onnx.Run(text, new Dictionary<string, OrtValue> { ["token_ids"] = ids });
                Step(state, emptySeq, embOuts[0]);
            }

            var latents = new List<float[]>();
            var input = Enumerable.Repeat(float.NaN, LatentDim).ToArray();
            int? eosStep = null;
            float std = temperature > 0 ? (float)Math.Sqrt(temperature) : 0;
            for (int step = 0; step < maxFrames; step++)
            {
                ct.ThrowIfCancellationRequested();
                float[] conditioning;
                float eos;
                using (var seq = Onnx.Floats(input, 1, 1, LatentDim))
                    (conditioning, eos) = Step(state, seq, emptyText);
                if (eosStep is null && eos > EosThreshold) eosStep = step;
                if (eosStep is { } e && step >= e + framesAfterEos) break;

                var x = new float[LatentDim];
                for (int i = 0; i < LatentDim; i++) x[i] = std * (float)rng.NextGaussian();
                for (int j = 0; j < LsdSteps; j++)
                {
                    using var c = Onnx.Floats(conditioning, 1, ConditioningDim);
                    using var s = Onnx.Floats([j / (float)LsdSteps], 1, 1);
                    using var t = Onnx.Floats([(j + 1) / (float)LsdSteps], 1, 1);
                    using var xv = Onnx.Floats(x, 1, LatentDim);
                    using var outs = Onnx.Run(flow, new Dictionary<string, OrtValue> { ["c"] = c, ["s"] = s, ["t"] = t, ["x"] = xv });
                    var dir = outs[0].GetTensorDataAsSpan<float>();
                    var next = new float[LatentDim];
                    for (int i = 0; i < LatentDim; i++) next[i] = x[i] + dir[i] / LsdSteps;
                    x = next;
                }
                latents.Add(x);
                input = x;
            }
            return latents;
        }
        finally
        {
            foreach (var v in state.Values) v.Dispose();
        }
    }

    /// One transformer pass; the state is replaced by what comes out.
    (float[] Conditioning, float Eos) Step(Dictionary<string, OrtValue> state, OrtValue sequence, OrtValue textEmbeddings)
    {
        var names = new List<string> { "sequence", "text_embeddings" };
        var values = new List<OrtValue> { sequence, textEmbeddings };
        foreach (var n in mainStateNames) { names.Add(n); values.Add(state[n]); }
        var outNames = new List<string> { "conditioning", "eos_logit" };
        outNames.AddRange(mainOutStateNames);
        using var options = new RunOptions();
        var outs = main.Run(options, names, values, outNames);
        float[] cond = [];
        float eos = float.NegativeInfinity;
        try
        {
            var c = outs[0].GetTensorDataAsSpan<float>();
            if (c.Length >= ConditioningDim) cond = c[^ConditioningDim..].ToArray();
            var e = outs[1].GetTensorDataAsSpan<float>();
            if (e.Length > 0) eos = e[^1];
        }
        finally
        {
            outs[0].Dispose();
            outs[1].Dispose();
        }
        for (int i = 0; i < mainStateNames.Length; i++)
        {
            state[mainStateNames[i]].Dispose();
            state[mainStateNames[i]] = outs[2 + i];
        }
        return (cond, eos);
    }

    /// Latents → audio, a few frames at a time with the decoder's streaming state.
    float[] Decode(List<float[]> latents, CancellationToken ct)
    {
        if (latents.Count == 0) return [];
        var state = InitialState(decoder, decoderStateNames);
        var audio = new List<float>(latents.Count * SamplesPerFrame);
        try
        {
            for (int i = 0; i < latents.Count; i += DecodeChunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(DecodeChunk, latents.Count - i);
                var flat = new float[n * LatentDim];
                for (int k = 0; k < n; k++) latents[i + k].CopyTo(flat, k * LatentDim);
                using var latent = Onnx.Floats(flat, 1, n, LatentDim);
                var names = new List<string> { "latent" };
                var values = new List<OrtValue> { latent };
                foreach (var s in decoderStateNames) { names.Add(s); values.Add(state[s]); }
                var outNames = new List<string> { "audio_frame" };
                outNames.AddRange(decoderOutStateNames);
                using var options = new RunOptions();
                var outs = decoder.Run(options, names, values, outNames);
                using (outs[0]) audio.AddRange(outs[0].GetTensorDataAsSpan<float>().ToArray());
                for (int k = 0; k < decoderStateNames.Length; k++)
                {
                    state[decoderStateNames[k]].Dispose();
                    state[decoderStateNames[k]] = outs[1 + k];
                }
            }
        }
        finally
        {
            foreach (var v in state.Values) v.Dispose();
        }
        return audio.ToArray();
    }

    /// A stateful graph's starting state: zeros (false for flags, empty for dimensions the
    /// graph leaves open), as the export's reference code starts it.
    static Dictionary<string, OrtValue> InitialState(InferenceSession s, string[] names)
    {
        var state = new Dictionary<string, OrtValue>();
        foreach (var n in names)
        {
            var meta = s.InputMetadata[n];
            var shape = meta.Dimensions.Select(d => (long)Math.Max(0, d)).ToArray();
            long count = shape.Aggregate(1L, (a, b) => a * b);
            state[n] = meta.ElementDataType switch
            {
                TensorElementType.Int64 => OrtValue.CreateTensorValueFromMemory(new long[count], shape),
                TensorElementType.Bool => OrtValue.CreateTensorValueFromMemory(new bool[count], shape),
                _ => OrtValue.CreateTensorValueFromMemory(new float[count], shape),
            };
        }
        return state;
    }
}
