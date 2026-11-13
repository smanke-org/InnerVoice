// Orpheus on ONNX Runtime: Canopy Labs' Llama-based model (onnx-community's 4-bit export) writes
// SNAC audio codes, seven per frame, and SNAC's decoder (onnx-community's export) makes them
// 24 kHz audio. Prompt, sampling and code layout follow the Mac's mlx-audio model.

using Microsoft.ML.OnnxRuntime;

namespace InnerVoice.Core.Speech.Neural;

public sealed class OrpheusModel : IDisposable
{
    const int StartOfHuman = 128259, EndOfHuman = 128260, EndOfText = 128009, BeginOfText = 128000;
    const int StartOfAudio = 128261, StartOfSpeech = 128257, EndOfSpeech = 128258, CodeOffset = 128266;

    // The Mac's generation parameters (mlx-audio's Orpheus defaults).
    public const int MaxTokens = 1200;
    const float Temperature = 0.6f, TopP = 0.8f, RepetitionPenalty = 1.3f;
    const int RepetitionContext = 20;

    readonly BpeTokenizer tok;
    readonly InferenceSession lm, snac;
    readonly int layers, kvHeads, headDim;
    readonly SemaphoreSlim gate = new(1, 1);

    /// `folder` holds tokenizer.json, config.json, onnx/model_q4.onnx (+ data) and snac/decoder_model.onnx.
    public OrpheusModel(string folder)
    {
        tok = BpeTokenizer.FromTokenizerJson(Path.Combine(folder, "tokenizer.json"));
        lm = Onnx.Open(Path.Combine(folder, "onnx", "model_q4.onnx"));
        snac = Onnx.Open(Path.Combine(folder, "snac", "decoder_model.onnx"));
        layers = lm.InputNames.Count(n => n.StartsWith("past_key_values.", StringComparison.Ordinal) && n.EndsWith(".key", StringComparison.Ordinal));
        var meta = lm.InputMetadata["past_key_values.0.key"].Dimensions;
        kvHeads = meta[1];
        headDim = meta[3];
    }

    /// The prompt: [start of human] [begin] "voice: text" [end of text] [end of human]
    /// [start of audio] [start of speech].
    public List<int> Prompt(string text, string voice)
    {
        var ids = new List<int> { StartOfHuman, BeginOfText };
        ids.AddRange(tok.Encode($"{voice}: {text}"));
        ids.AddRange([EndOfText, EndOfHuman, StartOfAudio, StartOfSpeech]);
        return ids;
    }

    /// `text` (with an optional "<sigh> " in front) in one of Orpheus's eight voices.
    public float[] Speak(string text, string voice, ulong seed, CancellationToken ct, Action<double>? progress = null)
    {
        gate.Wait(ct);
        try
        {
            var prompt = Prompt(text, voice);
            var tokens = Generate(prompt, new Sampler(seed), ct, progress);
            return Decode(Codes(tokens));
        }
        finally { gate.Release(); }
    }

    List<int> Generate(List<int> prompt, Sampler sampler, CancellationToken ct, Action<double>? progress)
    {
        var past = new OrtValue[layers * 2];
        for (int i = 0; i < past.Length; i++) past[i] = Onnx.Floats([], 1, kvHeads, 0, headDim);
        var generated = new List<int>();
        var history = new List<int>(prompt);
        try
        {
            var input = prompt.Select(i => (long)i).ToArray();
            int total = 0;
            for (int step = 0; step < MaxTokens; step++)
            {
                ct.ThrowIfCancellationRequested();
                total += input.Length;
                var feeds = new Dictionary<string, OrtValue>();
                using var ids = Onnx.Longs(input, 1, input.Length);
                using var mask = Onnx.Longs(Enumerable.Repeat(1L, total).ToArray(), 1, total);
                feeds["input_ids"] = ids;
                feeds["attention_mask"] = mask;
                for (int l = 0; l < layers; l++)
                {
                    feeds[$"past_key_values.{l}.key"] = past[2 * l];
                    feeds[$"past_key_values.{l}.value"] = past[2 * l + 1];
                }
                var outs = Onnx.Run(lm, feeds);
                var shape = Onnx.Shape(outs[0]);
                int vocab = (int)shape[2];
                var logits = outs[0].GetTensorDataAsSpan<float>()[^vocab..].ToArray();
                outs[0].Dispose();
                for (int i = 0; i < past.Length; i++)
                {
                    past[i].Dispose();
                    past[i] = outs[i + 1];
                }
                Sampler.Penalize(logits, history.TakeLast(RepetitionContext), RepetitionPenalty);
                int next = sampler.Sample(logits, Temperature, 0, TopP);
                if (next is EndOfSpeech or EndOfText) break;
                generated.Add(next);
                history.Add(next);
                progress?.Invoke((double)generated.Count / MaxTokens);
                input = [next];
            }
        }
        finally
        {
            foreach (var p in past) p?.Dispose();
        }
        return generated;
    }

    /// The audio codes in the generated tokens: everything after the last start-of-speech,
    /// whole frames of seven, each code in its slot's range.
    public static List<int> Codes(List<int> tokens)
    {
        int start = tokens.LastIndexOf(StartOfSpeech);
        var codes = tokens.Skip(start + 1).Where(t => t >= CodeOffset).Select(t => t - CodeOffset).ToList();
        codes.RemoveRange(codes.Count / 7 * 7, codes.Count % 7);
        return codes;
    }

    /// SNAC's three levels from frames of seven: [a] [b e] [c d f g], each less its slot × 4096.
    public static (long[] L1, long[] L2, long[] L3) Levels(List<int> codes)
    {
        var l1 = new List<long>(); var l2 = new List<long>(); var l3 = new List<long>();
        for (int i = 0; i + 7 <= codes.Count; i += 7)
        {
            var f = new long[7];
            bool ok = true;
            for (int k = 0; k < 7; k++) { f[k] = codes[i + k] - k * 4096; ok &= f[k] is >= 0 and < 4096; }
            if (!ok) continue; // a stray token: drop the frame rather than make noise
            l1.Add(f[0]); l2.Add(f[1]); l3.Add(f[2]); l3.Add(f[3]); l2.Add(f[4]); l3.Add(f[5]); l3.Add(f[6]);
        }
        return (l1.ToArray(), l2.ToArray(), l3.ToArray());
    }

    float[] Decode(List<int> codes)
    {
        var (l1, l2, l3) = Levels(codes);
        if (l1.Length == 0) return [];
        using var a = Onnx.Longs(l1, 1, l1.Length);
        using var b = Onnx.Longs(l2, 1, l2.Length);
        using var c = Onnx.Longs(l3, 1, l3.Length);
        using var outs = Onnx.Run(snac, new Dictionary<string, OrtValue> { ["audio_codes.0"] = a, ["audio_codes.1"] = b, ["audio_codes.2"] = c });
        return outs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    public void Dispose()
    {
        lm.Dispose();
        snac.Dispose();
    }
}
