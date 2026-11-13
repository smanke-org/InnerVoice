// Qwen3-TTS on ONNX Runtime: the same two jobs the Mac's MLX models do.
//   Design  (VoiceDesign model): a few seconds of speech in a voice described in words.
//   Clone   (Base model): any text in the voice of a short sample and what it says (in-context).
// The models are wavekat's ONNX exports of Qwen's weights (docs/WINDOWS-PLAN.md § Local voices):
// talker prefill and decode, the code predictor for codebooks 1-15, the 12 Hz codec's decoder
// (the "vocoder"), and for cloning the speaker encoder and the codec's encoder. The embeddings
// and the text projection run here, from .npy files, as in wavekat's reference scripts.

using System.Numerics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace InnerVoice.Core.Speech.Neural;

/// The token IDs and sizes in a model's config.json.
public sealed record QwenConfig(
    int ImStart, int ImEnd, int TtsPad, int TtsBos, int TtsEos,
    int CodecEos, int CodecThink, int CodecNoThink, int CodecThinkBos, int CodecThinkEos, int CodecPad, int CodecBos,
    int Hidden, int Layers, int VocabSize, int CodeGroups, int CpLayers, int CpKvHeads, int CpHeadDim,
    IReadOnlyDictionary<string, int> Languages,
    float Temperature, int TopK, float RepetitionPenalty, float SubTemperature, int SubTopK)
{
    public static QwenConfig Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var r = doc.RootElement;
        int I(string k) => r.GetProperty(k).GetInt32();
        var g = r.GetProperty("generate_config");
        var langs = r.GetProperty("codec_language_id").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        return new QwenConfig(I("im_start_token_id"), I("im_end_token_id"), I("tts_pad_token_id"), I("tts_bos_token_id"), I("tts_eos_token_id"),
            I("codec_eos_token_id"), I("codec_think_id"), I("codec_nothink_id"), I("codec_think_bos_id"), I("codec_think_eos_id"), I("codec_pad_id"), I("codec_bos_id"),
            I("talker_hidden_size"), I("talker_num_layers"), I("talker_vocab_size"), I("talker_num_code_groups"),
            I("cp_num_layers"), I("cp_num_kv_heads"), I("cp_head_dim"), langs,
            g.GetProperty("temperature").GetSingle(), g.GetProperty("top_k").GetInt32(), g.GetProperty("repetition_penalty").GetSingle(),
            g.GetProperty("subtalker_temperature").GetSingle(), g.GetProperty("subtalker_top_k").GetInt32());
    }
}

/// What the two Qwen3-TTS models share, loaded once: the text tokenizer and the codec's decoder.
public sealed class QwenShared : IDisposable
{
    public BpeTokenizer Tokenizer { get; }
    public InferenceSession Vocoder { get; }

    public QwenShared(string folder)
    {
        Tokenizer = BpeTokenizer.Qwen(Path.Combine(folder, "tokenizer", "vocab.json"), Path.Combine(folder, "tokenizer", "merges.txt"));
        Vocoder = Onnx.Open(Path.Combine(folder, "int4", "vocoder.onnx"));
    }

    public void Dispose() => Vocoder.Dispose();
}

/// One Qwen3-TTS model loaded: its embeddings, projection and ONNX sessions.
public sealed class QwenModel : IDisposable
{
    public const int SampleRate = 24000;
    /// Samples per codec frame (12.5 frames a second).
    public const int FrameSamples = 1920;
    /// The codec encoder takes exactly 10 seconds (wavekat's export); a longer sample is cut.
    public const int EncoderSamples = 10 * SampleRate;

    readonly QwenConfig cfg;
    readonly BpeTokenizer tok;
    readonly NpyMatrix textEmb, codecEmb;
    readonly NpyMatrix[] cpEmb;
    readonly float[] fc1W, fc1B, fc2W, fc2B;
    readonly int textDim, mid;
    readonly InferenceSession prefill, decode, predictor;
    readonly QwenShared shared;
    InferenceSession vocoder => shared.Vocoder;
    readonly InferenceSession? speaker, encoder;
    readonly SemaphoreSlim gate = new(1, 1);

    public QwenConfig Config => cfg;
    public bool CanClone => speaker is not null && encoder is not null;

    /// `folder`: the model's own files (config.json, embeddings/, int4/…); `shared`: the
    /// tokenizer and vocoder the two models have in common.
    public QwenModel(string folder, QwenShared shared)
    {
        this.shared = shared;
        cfg = QwenConfig.Read(Path.Combine(folder, "config.json"));
        tok = shared.Tokenizer;
        string E(string n) => Path.Combine(folder, "embeddings", n + ".npy");
        textEmb = NpyMatrix.Open(E("text_embedding"));
        codecEmb = NpyMatrix.Open(E("talker_codec_embedding"));
        cpEmb = Enumerable.Range(0, cfg.CodeGroups - 1).Select(i => NpyMatrix.Open(E($"cp_codec_embedding_{i}"))).ToArray();
        using (var w1 = NpyMatrix.Open(E("text_projection_fc1_weight"), inMemory: true)) { fc1W = w1.All(); mid = w1.Rows; textDim = w1.Columns; }
        using (var b1 = NpyMatrix.Open(E("text_projection_fc1_bias"), inMemory: true)) fc1B = b1.All();
        using (var w2 = NpyMatrix.Open(E("text_projection_fc2_weight"), inMemory: true)) fc2W = w2.All();
        using (var b2 = NpyMatrix.Open(E("text_projection_fc2_bias"), inMemory: true)) fc2B = b2.All();
        string M(string n) => Path.Combine(folder, "int4", n + ".onnx");
        prefill = Onnx.Open(M("talker_prefill"));
        decode = Onnx.Open(M("talker_decode"));
        predictor = Onnx.Open(M("code_predictor"));
        if (File.Exists(Path.Combine(folder, "speaker_encoder.onnx")))
        {
            speaker = Onnx.Open(Path.Combine(folder, "speaker_encoder.onnx"));
            encoder = Onnx.Open(Path.Combine(folder, "tokenizer_encoder.onnx"));
        }
    }

    // MARK: Embeddings

    /// The text projection (a SiLU MLP) of one text token: Hidden floats.
    float[] Text(int token)
    {
        var e = textEmb.Row(token);
        var h = new float[mid];
        for (int o = 0; o < mid; o++)
        {
            float v = Dot(fc1W.AsSpan(o * textDim, textDim), e) + fc1B[o];
            h[o] = v / (1 + MathF.Exp(-v));
        }
        var outv = new float[cfg.Hidden];
        for (int o = 0; o < cfg.Hidden; o++) outv[o] = Dot(fc2W.AsSpan(o * mid, mid), h) + fc2B[o];
        return outv;
    }

    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length, i = 0;
        var acc = Vector<float>.Zero;
        for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            acc += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        float s = Vector.Sum(acc);
        for (; i < n; i++) s += a[i] * b[i];
        return s;
    }

    static float[] Add(float[] a, float[] b)
    {
        var r = new float[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] + b[i];
        return r;
    }

    float[] Codec(int id) => codecEmb.Row(id);

    /// One frame's 16 codes as one input: group 0 from the talker's table, 1-15 from the
    /// code predictor's.
    float[] Frame(IReadOnlyList<long> codes)
    {
        var e = Codec((int)codes[0]);
        for (int g = 1; g < cfg.CodeGroups; g++) cpEmb[g - 1].AddRow((int)codes[g], e);
        return e;
    }

    List<int> Ids(string text) => tok.Encode(text);

    int[] CodecPrefix(string? language) =>
        language is { } l && cfg.Languages.TryGetValue(l.ToLowerInvariant(), out var id)
            ? [cfg.CodecThink, cfg.CodecThinkBos, id, cfg.CodecThinkEos]
            : [cfg.CodecNoThink, cfg.CodecThinkBos, cfg.CodecThinkEos];

    // MARK: The two jobs

    /// Speech in a voice described in words (the VoiceDesign model).
    public float[] Design(string text, string description, string language, ulong seed, int maxFrames, CancellationToken ct, Action<double>? progress = null)
    {
        var ids = Ids($"<|im_start|>assistant\n{text}<|im_end|>\n<|im_start|>assistant\n");
        var rows = new List<float[]>();
        foreach (var t in Ids($"<|im_start|>user\n{description}<|im_end|>\n")) rows.Add(Text(t));
        foreach (var t in ids.Take(3)) rows.Add(Text(t));
        var pad = Text(cfg.TtsPad);
        foreach (var c in CodecPrefix(language)) rows.Add(Add(pad, Codec(c)));
        var codecPad = Codec(cfg.CodecPad);
        rows.Add(Add(Text(cfg.TtsBos), codecPad));
        foreach (var t in ids.Skip(3).Take(ids.Count - 8)) rows.Add(Add(Text(t), codecPad));
        rows.Add(Add(Text(cfg.TtsEos), codecPad));
        rows.Add(Add(pad, Codec(cfg.CodecBos)));
        var codes = Generate(rows, pad, new Sampler(seed), maxFrames, ct, progress);
        return Vocode(codes, []);
    }

    /// `text` in the voice of `reference` (24 kHz), which says `referenceText` (the Base model).
    public float[] Clone(string text, float[] reference, string referenceText, string language, ulong seed, int maxFrames, CancellationToken ct, Action<double>? progress = null)
    {
        if (!CanClone) throw new InvalidOperationException("This model can’t clone.");
        if (reference.Length > EncoderSamples) reference = reference[..EncoderSamples];
        var speakerEmbedding = SpeakerEmbedding(reference);
        var refCodes = ReferenceCodes(reference);

        var ids = Ids($"<|im_start|>assistant\n{text}<|im_end|>\n<|im_start|>assistant\n");
        var refIds = Ids($"<|im_start|>assistant\n{referenceText}<|im_end|>\n");
        var rows = new List<float[]>();
        foreach (var t in ids.Take(3)) rows.Add(Text(t));
        var pad = Text(cfg.TtsPad);
        foreach (var c in CodecPrefix(language)) rows.Add(Add(pad, Codec(c)));
        rows.Add(Add(pad, speakerEmbedding));
        var codecPad = Codec(cfg.CodecPad);
        rows.Add(Add(Text(cfg.TtsBos), codecPad));
        // In-context: the sample's words then the new words (each with the codec pad), then the
        // sample's audio frames (each with the text pad), and generation carries on from there.
        foreach (var t in refIds.Skip(3).Take(refIds.Count - 5).Concat(ids.Skip(3).Take(ids.Count - 8))) rows.Add(Add(Text(t), codecPad));
        rows.Add(Add(Text(cfg.TtsEos), codecPad));
        rows.Add(Add(Codec(cfg.CodecBos), pad));
        foreach (var frame in refCodes) rows.Add(Add(Frame(frame), pad));
        var codes = Generate(rows, pad, new Sampler(seed), maxFrames, ct, progress);
        return Vocode(codes, refCodes);
    }

    float[] SpeakerEmbedding(float[] audio)
    {
        var mel = Mel.LogMel(audio, out int frames);
        using var input = Onnx.Floats(mel, 1, frames, Mel.Bands);
        using var outs = Onnx.Run(speaker!, new Dictionary<string, OrtValue> { ["mels"] = input });
        return outs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    /// The sample as codec frames (16 codes each).
    List<long[]> ReferenceCodes(float[] audio)
    {
        var padded = new float[EncoderSamples];
        Array.Copy(audio, padded, Math.Min(audio.Length, EncoderSamples));
        using var input = Onnx.Floats(padded, 1, EncoderSamples);
        using var outs = Onnx.Run(encoder!, new Dictionary<string, OrtValue> { ["waveform"] = input });
        var shape = Onnx.Shape(outs[0]);
        var data = outs[0].GetTensorDataAsSpan<long>();
        int groups = (int)shape[1], total = (int)shape[2];
        int frames = Math.Min(total, (int)Math.Ceiling(audio.Length / (double)FrameSamples));
        var list = new List<long[]>(frames);
        for (int f = 0; f < frames; f++)
        {
            var c = new long[groups];
            for (int g = 0; g < groups; g++) c[g] = data[g * total + f];
            list.Add(c);
        }
        return list;
    }

    // MARK: Generation

    /// The talker and code predictor, frame by frame, until the end code or `maxFrames`.
    List<long[]> Generate(List<float[]> rows, float[] trailing, Sampler sampler, int maxFrames, CancellationToken ct, Action<double>? progress)
    {
        gate.Wait(ct);
        try
        {
            int h = cfg.Hidden, T = rows.Count;
            var embeds = new float[T * h];
            for (int i = 0; i < T; i++) rows[i].CopyTo(embeds, i * h);
            var positions = new long[3 * T];
            for (int s = 0; s < 3; s++) for (int i = 0; i < T; i++) positions[s * T + i] = i;

            float[] logits, hidden;
            OrtValue keys, values;
            using (var e = Onnx.Floats(embeds, 1, T, h))
            using (var m = Onnx.Longs(Enumerable.Repeat(1L, T).ToArray(), 1, T))
            using (var p = Onnx.Longs(positions, 3, 1, T))
            using (var outs = Onnx.Run(prefill, new Dictionary<string, OrtValue> { ["inputs_embeds"] = e, ["attention_mask"] = m, ["position_ids"] = p }))
            {
                logits = Last(outs[0]);
                hidden = Last(outs[1]);
                (keys, values) = Stack(outs, 2);
            }

            var all = new List<long[]>();
            var said = new List<int>();
            int pos = T;
            try
            {
                for (int step = 0; step < maxFrames; step++)
                {
                    ct.ThrowIfCancellationRequested();
                    // Only codec codes and the end code may come next; the end not before two frames.
                    for (int i = cfg.VocabSize - 1024; i < cfg.VocabSize; i++) if (i != cfg.CodecEos) logits[i] = float.NegativeInfinity;
                    if (step < 2) logits[cfg.CodecEos] = float.NegativeInfinity;
                    Sampler.Penalize(logits, said, cfg.RepetitionPenalty);
                    int first = sampler.Sample(logits, cfg.Temperature, cfg.TopK);
                    if (first == cfg.CodecEos) break;
                    said.Add(first);

                    var frame = Predict(hidden, first, sampler);
                    all.Add(frame);
                    progress?.Invoke((double)all.Count / maxFrames);

                    var next = Add(Frame(frame), trailing);
                    using var e = Onnx.Floats(next, 1, 1, h);
                    using var m = Onnx.Longs(Enumerable.Repeat(1L, pos + 1).ToArray(), 1, pos + 1);
                    using var p = Onnx.Longs([pos, pos, pos], 3, 1, 1);
                    var outs = Onnx.Run(decode, new Dictionary<string, OrtValue>
                    {
                        ["inputs_embeds"] = e, ["attention_mask"] = m, ["position_ids"] = p, ["past_keys"] = keys, ["past_values"] = values,
                    });
                    logits = Last(outs[0]);
                    hidden = Last(outs[1]);
                    keys.Dispose();
                    values.Dispose();
                    keys = outs[2];
                    values = outs[3];
                    outs[0].Dispose();
                    outs[1].Dispose();
                    pos++;
                }
            }
            finally
            {
                keys.Dispose();
                values.Dispose();
            }
            return all;
        }
        finally { gate.Release(); }
    }

    /// Codebooks 1-15 for a frame whose first code is `first`.
    long[] Predict(float[] talkerHidden, int first, Sampler sampler)
    {
        int h = cfg.Hidden;
        var frame = new long[cfg.CodeGroups];
        frame[0] = first;
        var input = new float[2 * h];
        talkerHidden.CopyTo(input, 0);
        Codec(first).CopyTo(input, h);
        int seq = 2;
        var keys = Onnx.Floats([], cfg.CpLayers, 1, cfg.CpKvHeads, 0, cfg.CpHeadDim);
        var values = Onnx.Floats([], cfg.CpLayers, 1, cfg.CpKvHeads, 0, cfg.CpHeadDim);
        try
        {
            for (int g = 0; g < cfg.CodeGroups - 1; g++)
            {
                using var e = Onnx.Floats(input, 1, seq, h);
                using var step = Onnx.Longs([g], 1);
                var outs = Onnx.Run(predictor, new Dictionary<string, OrtValue>
                {
                    ["inputs_embeds"] = e, ["generation_steps"] = step, ["past_keys"] = keys, ["past_values"] = values,
                });
                var logits = Last(outs[0]);
                keys.Dispose();
                values.Dispose();
                keys = outs[1];
                values = outs[2];
                outs[0].Dispose();
                int token = sampler.Sample(logits, cfg.SubTemperature, cfg.SubTopK);
                frame[g + 1] = token;
                input = cpEmb[g].Row(token);
                seq = 1;
            }
        }
        finally
        {
            keys.Dispose();
            values.Dispose();
        }
        return frame;
    }

    /// The last position's row of a [1, T, N] output.
    static float[] Last(OrtValue v)
    {
        var shape = Onnx.Shape(v);
        int n = (int)shape[^1];
        var data = v.GetTensorDataAsSpan<float>();
        return data[^n..].ToArray();
    }

    /// The prefill's per-layer keys and values stacked into the decoder's [layers, 1, heads, T, dim].
    (OrtValue Keys, OrtValue Values) Stack(IReadOnlyList<OrtValue> outs, int start)
    {
        var shape = Onnx.Shape(outs[start]);
        long per = shape.Aggregate(1L, (a, b) => a * b);
        var k = new float[per * cfg.Layers];
        var v = new float[per * cfg.Layers];
        for (int l = 0; l < cfg.Layers; l++)
        {
            outs[start + 2 * l].GetTensorDataAsSpan<float>().CopyTo(k.AsSpan((int)(l * per), (int)per));
            outs[start + 2 * l + 1].GetTensorDataAsSpan<float>().CopyTo(v.AsSpan((int)(l * per), (int)per));
        }
        long[] dims = [cfg.Layers, 1, shape[1], shape[2], shape[3]];
        return (Onnx.Floats(k, dims), Onnx.Floats(v, dims));
    }

    /// Codes to 24 kHz audio. With reference frames in front (cloning), the codec hears the
    /// sample first and the sample's share of the audio is cut off again.
    float[] Vocode(List<long[]> codes, List<long[]> reference)
    {
        if (codes.Count == 0) return [];
        var all = reference.Concat(codes).ToList();
        int n = all.Count, groups = cfg.CodeGroups;
        var flat = new long[groups * n];
        for (int f = 0; f < n; f++) for (int g = 0; g < groups; g++) flat[g * n + f] = all[f][g];
        using var input = Onnx.Longs(flat, 1, groups, n);
        using var outs = Onnx.Run(vocoder, new Dictionary<string, OrtValue> { ["codes"] = input });
        var wav = outs[0].GetTensorDataAsSpan<float>().ToArray();
        if (reference.Count == 0) return wav;
        int cut = (int)((long)reference.Count * wav.Length / n);
        return wav[cut..];
    }

    public void Dispose()
    {
        prefill.Dispose(); decode.Dispose(); predictor.Dispose();
        speaker?.Dispose(); encoder?.Dispose();
        textEmb.Dispose(); codecEmb.Dispose();
        foreach (var c in cpEmb) c.Dispose();
    }
}
