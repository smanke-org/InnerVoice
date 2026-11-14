// Chatterbox on ONNX Runtime: Resemble AI's model (MIT) in the ONNX Community's export
// (onnx-community/chatterbox-ONNX, the original English Chatterbox with its emotion
// exaggeration). Four graphs: the speech encoder reads a voice clip (24 kHz) into the language
// model's conditioning, the clip's speech tokens, its speaker embedding (CAM++) and its mel
// features; embed_tokens embeds text and speech tokens (and the exaggeration); the Llama-based
// language model writes speech tokens (25 a second); the conditional decoder (CosyVoice's flow
// matching and HiFT vocoder) turns them into 24 kHz audio in the clip's voice. The loop follows
// the export's own example (its README: prompt, positions, KV cache), but samples the way
// Chatterbox does (temperature 0.8, min-p 0.05, repetition penalty 1.2): the example's greedy
// pick got stuck on the silence token halfway through a long line (measured 2026-11-14). The
// export has no classifier-free guidance pass, so the CFG weight the Mac sets isn't available
// here (`ChatterboxDelivery.Rate` stands in for it). Everything is read from the Voice Models
// folder: nothing is fetched. Measured on an Apple M-series processor (ONNX Runtime CPU, 4-bit
// language model): about real time for a long line, the decoder taking most of it.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

namespace InnerVoice.Core.Speech.Neural;

/// Chatterbox's text tokenizer (its tokenizer.json): a character-level BPE over the text with
/// spaces collapsed, no pre-tokenizer, its sound tags ("[sigh]") as whole tokens. Small and
/// dependency free, so the tests run it on a Mac.
public sealed class ChatterboxTokenizer
{
    public const int Start = 255, Stop = 0, Unknown = 1;

    readonly Dictionary<string, int> vocab;
    readonly Dictionary<(string, string), int> ranks = [];
    readonly Dictionary<string, int> special;
    readonly Regex? specialSplit;
    static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    public ChatterboxTokenizer(Dictionary<string, int> vocab, IEnumerable<(string, string)> merges, Dictionary<string, int> special)
    {
        this.vocab = vocab;
        int r = 0;
        foreach (var m in merges) ranks.TryAdd(m, r++);
        this.special = special;
        var words = special.Keys.Where(k => k.StartsWith('[')).OrderByDescending(k => k.Length).ToList();
        if (words.Count > 0) specialSplit = new Regex("(" + string.Join("|", words.Select(Regex.Escape)) + ")", RegexOptions.CultureInvariant);
    }

    public static ChatterboxTokenizer FromJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var model = doc.RootElement.GetProperty("model");
        var vocab = model.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var merges = model.GetProperty("merges").EnumerateArray().Select(m =>
        {
            if (m.ValueKind == JsonValueKind.Array) return (m[0].GetString()!, m[1].GetString()!);
            var s = m.GetString()!;
            int sp = s.IndexOf(' ');
            return (s[..sp], s[(sp + 1)..]);
        }).ToList();
        var special = doc.RootElement.TryGetProperty("added_tokens", out var added)
            ? added.EnumerateArray().ToDictionary(a => a.GetProperty("content").GetString()!, a => a.GetProperty("id").GetInt32())
            : [];
        return new ChatterboxTokenizer(vocab, merges, special);
    }

    /// The text's tokens, without the template around them.
    public List<int> Encode(string text)
    {
        text = Spaces.Replace(text, " ");
        var output = new List<int>();
        var parts = specialSplit is null ? [text] : specialSplit.Split(text);
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            if (special.TryGetValue(part, out var s)) { output.Add(s); continue; }
            // No merge joins across a space (no token holds one), so each word is merged alone.
            var words = part.Split(' ');
            for (int w = 0; w < words.Length; w++)
            {
                if (w > 0) output.Add(vocab.TryGetValue(" ", out var space) ? space : Unknown);
                if (words[w].Length > 0) output.AddRange(Word(words[w]));
            }
        }
        return output;
    }

    /// The model's input: [EXAGGERATION] [START] text [STOP] [START_SPEECH] [START_SPEECH], the
    /// template in its tokenizer.json.
    public List<int> Prompt(string text)
    {
        var ids = new List<int> { ChatterboxModel.ExaggerationToken, Start };
        ids.AddRange(Encode(text));
        ids.AddRange([Stop, ChatterboxModel.StartSpeech, ChatterboxModel.StartSpeech]);
        return ids;
    }

    IEnumerable<int> Word(string word)
    {
        var symbols = new List<string>();
        // Characters, one symbol each (a surrogate pair stays whole).
        for (int i = 0; i < word.Length;)
        {
            int len = char.IsHighSurrogate(word[i]) && i + 1 < word.Length ? 2 : 1;
            symbols.Add(word.Substring(i, len));
            i += len;
        }
        while (symbols.Count > 1)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i < symbols.Count - 1; i++)
                if (ranks.TryGetValue((symbols[i], symbols[i + 1]), out var r) && r < bestRank) { best = i; bestRank = r; }
            if (best < 0) break;
            var (a, b) = (symbols[best], symbols[best + 1]);
            // Merge every occurrence of this pair, left to right, as Hugging Face's BPE does.
            var merged = new List<string>(symbols.Count);
            for (int i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == a && symbols[i + 1] == b) { merged.Add(a + b); i++; }
                else merged.Add(symbols[i]);
            }
            symbols = merged;
        }
        return symbols.Select(s => vocab.TryGetValue(s, out var id) ? id : Unknown);
    }
}

/// Chatterbox's text clean-up before tokenizing (its `punc_norm`): a capital first letter,
/// one space between words, plain punctuation for the marks it wasn't trained on, and a full
/// stop at the end when the line has none.
public static class ChatterboxText
{
    static readonly (string Old, string New)[] Replacements =
    [
        ("...", ", "), ("…", ", "), (":", ","), (" - ", ", "), (";", ", "), ("—", "-"), ("–", "-"), (" ,", ","),
        ("“", "\""), ("”", "\""), ("‘", "'"), ("’", "'"),
    ];

    public static string Normalize(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return text;
        if (char.IsLower(text[0])) text = char.ToUpperInvariant(text[0]) + text[1..];
        foreach (var (o, n) in Replacements) text = text.Replace(o, n, StringComparison.Ordinal);
        // One space again (the tokenizer would collapse them anyway).
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!".!?-,".Contains(text[^1])) text += ".";
        return text;
    }
}

/// What the speech encoder reads from a voice clip, used for every line in that voice.
public sealed record ChatterboxVoiceConditioning(float[] CondEmbedding, int CondLength, long[] PromptTokens, float[] SpeakerEmbedding,
                                                 float[] SpeakerFeatures, int FeatureFrames);

public sealed class ChatterboxModel : IDisposable
{
    public const int StartSpeech = 6561, StopSpeech = 6562, ExaggerationToken = 6563;
    public const int SampleRate = 24_000;
    /// Chatterbox's own sampling (its generate defaults, the Mac's too): temperature 0.8, min-p
    /// 0.05, and a repetition penalty of 1.2 over everything said so far. Greedy decoding (the
    /// export's example) got stuck repeating its silence token after half a long line.
    public float Temperature { get; init; } = 0.8f;
    public float MinP { get; init; } = 0.05f;
    public float RepetitionPenalty { get; init; } = 1.2f;
    /// At most this many speech tokens (40 s) for one piece of text.
    public const int MaxTokens = 1000;
    const int Hidden = 1024;

    readonly ChatterboxTokenizer tok;
    readonly InferenceSession encoder, embed, lm, decoder;
    readonly int layers, kvHeads, headDim;
    readonly SemaphoreSlim gate = new(1, 1);

    /// `folder` holds tokenizer.json and onnx/ with the four graphs (and their .onnx_data).
    /// `languageModel`: another precision of the language model (VoiceBench compares them).
    public ChatterboxModel(string folder, string? languageModel = null)
    {
        tok = ChatterboxTokenizer.FromJson(Path.Combine(folder, "tokenizer.json"));
        var onnx = Path.Combine(folder, "onnx");
        encoder = Onnx.Open(Path.Combine(onnx, "speech_encoder.onnx"));
        embed = Onnx.Open(Path.Combine(onnx, "embed_tokens.onnx"));
        lm = Onnx.Open(Path.Combine(onnx, languageModel ?? LanguageModelFile));
        decoder = Onnx.Open(Path.Combine(onnx, "conditional_decoder.onnx"));
        layers = lm.InputNames.Count(n => n.StartsWith("past_key_values.", StringComparison.Ordinal) && n.EndsWith(".key", StringComparison.Ordinal));
        var meta = lm.InputMetadata["past_key_values.0.key"].Dimensions;
        kvHeads = meta[1];
        headDim = meta[3];
    }

    /// The language model the pack downloads (VoicePacks.Chatterbox).
    public const string LanguageModelFile = "language_model_q4.onnx";

    public ChatterboxTokenizer Tokenizer => tok;

    /// The most speech tokens a piece of text may take: about two and a half times the time
    /// it should take to say (25 tokens a second), at least 4 s.
    public static int TokenBudget(string text) => Math.Min(MaxTokens, (int)(Math.Max(4, ChatterboxRunOn.ExpectedSeconds(text) * 2.5) * 25));

    /// Reads a voice clip (24 kHz mono) once; every line in that voice is spoken from it.
    public ChatterboxVoiceConditioning Encode(float[] clip)
    {
        gate.Wait();
        try
        {
            using var audio = Onnx.Floats(clip, 1, clip.Length);
            using var outs = Onnx.Run(encoder, new Dictionary<string, OrtValue> { ["audio_values"] = audio });
            var cond = outs[0].GetTensorDataAsSpan<float>().ToArray();
            int condLength = (int)Onnx.Shape(outs[0])[1];
            var tokens = outs[1].GetTensorDataAsSpan<long>().ToArray();
            var speaker = outs[2].GetTensorDataAsSpan<float>().ToArray();
            var features = outs[3].GetTensorDataAsSpan<float>().ToArray();
            int frames = (int)Onnx.Shape(outs[3])[1];
            return new ChatterboxVoiceConditioning(cond, condLength, tokens, speaker, features, frames);
        }
        finally { gate.Release(); }
    }

    /// `text` in the clip's voice at the tone's exaggeration (0.5 is even), 24 kHz.
    public float[] Speak(string text, ChatterboxVoiceConditioning voice, double exaggeration, ulong seed, CancellationToken ct)
    {
        gate.Wait(ct);
        try
        {
            var speech = SpeechTokensNow(text, voice, exaggeration, seed, ct);
            if (speech.Count == 0) return [];
            return Decode(voice, speech);
        }
        finally { gate.Release(); }
    }

    /// The speech tokens the language model writes for `text` (for VoiceBench and the tests).
    public List<long> SpeechTokens(string text, ChatterboxVoiceConditioning voice, double exaggeration, ulong seed, CancellationToken ct)
    {
        gate.Wait(ct);
        try { return SpeechTokensNow(text, voice, exaggeration, seed, ct); }
        finally { gate.Release(); }
    }

    List<long> SpeechTokensNow(string text, ChatterboxVoiceConditioning voice, double exaggeration, ulong seed, CancellationToken ct) =>
        Generate(tok.Prompt(ChatterboxText.Normalize(text)), voice, (float)exaggeration, TokenBudget(text), new Sampler(seed), ct);

    /// Speech tokens to audio in the clip's voice (24 kHz).
    public float[] Audio(ChatterboxVoiceConditioning voice, List<long> speech)
    {
        gate.Wait();
        try { return Decode(voice, speech); }
        finally { gate.Release(); }
    }

    float[] Embed(long[] ids, long[] positions, float exaggeration)
    {
        using var i = Onnx.Longs(ids, 1, ids.Length);
        using var p = Onnx.Longs(positions, 1, positions.Length);
        using var e = Onnx.Floats([exaggeration], 1);
        using var outs = Onnx.Run(embed, new Dictionary<string, OrtValue> { ["input_ids"] = i, ["position_ids"] = p, ["exaggeration"] = e });
        return outs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    List<long> Generate(List<int> prompt, ChatterboxVoiceConditioning voice, float exaggeration, int budget, Sampler sampler, CancellationToken ct)
    {
        // Text positions count from [START] (0); the speech tokens (and [EXAGGERATION]) sit at 0.
        var ids = prompt.Select(t => (long)t).ToArray();
        var positions = ids.Select((t, i) => t >= StartSpeech ? 0L : i - 1).ToArray();
        var text = Embed(ids, positions, exaggeration);
        var first = new float[voice.CondEmbedding.Length + text.Length];
        voice.CondEmbedding.CopyTo(first, 0);
        text.CopyTo(first, voice.CondEmbedding.Length);
        int length = voice.CondLength + ids.Length;

        var past = new OrtValue[layers * 2];
        for (int i = 0; i < past.Length; i++) past[i] = Onnx.Floats([], 1, kvHeads, 0, headDim);
        var said = new List<int> { StartSpeech };
        var output = new List<long>();
        try
        {
            var embeds = first;
            int seq = length, total = 0;
            for (int step = 0; step < budget; step++)
            {
                ct.ThrowIfCancellationRequested();
                total += seq;
                using var input = Onnx.Floats(embeds, 1, seq, Hidden);
                using var mask = Onnx.Longs(Enumerable.Repeat(1L, total).ToArray(), 1, total);
                var feeds = new Dictionary<string, OrtValue> { ["inputs_embeds"] = input, ["attention_mask"] = mask };
                for (int l = 0; l < layers; l++)
                {
                    feeds[$"past_key_values.{l}.key"] = past[2 * l];
                    feeds[$"past_key_values.{l}.value"] = past[2 * l + 1];
                }
                var outs = Onnx.Run(lm, feeds);
                int vocab = (int)Onnx.Shape(outs[0])[2];
                var logits = outs[0].GetTensorDataAsSpan<float>()[^vocab..].ToArray();
                outs[0].Dispose();
                for (int i = 0; i < past.Length; i++)
                {
                    past[i].Dispose();
                    past[i] = outs[i + 1];
                }
                Sampler.Penalize(logits, said, RepetitionPenalty);
                int next = Pick(logits, sampler);
                if (next == StopSpeech) break;
                said.Add(next);
                output.Add(next);
                embeds = Embed([next], [step + 1], exaggeration);
                seq = 1;
            }
        }
        finally
        {
            foreach (var p in past) p?.Dispose();
        }
        // Only real speech tokens reach the decoder (the start token or a stray text id never).
        return output.Where(t => t < StartSpeech).ToList();
    }

    /// Temperature, then min-p (drops every token less likely than `MinP` times the likeliest),
    /// then a seeded draw. Temperature 0 picks the likeliest.
    int Pick(float[] logits, Sampler sampler)
    {
        if (Temperature <= 0) return sampler.Sample(logits, 0);
        float max = logits.Max();
        // exp((l - max) / T) < MinP  <=>  l < max + T ln(MinP)
        float floor = MinP > 0 ? max + Temperature * MathF.Log(MinP) : float.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++) if (logits[i] < floor) logits[i] = float.NegativeInfinity;
        return sampler.Sample(logits, Temperature);
    }

    float[] Decode(ChatterboxVoiceConditioning voice, List<long> speech)
    {
        var tokens = voice.PromptTokens.Concat(speech).ToArray();
        using var t = Onnx.Longs(tokens, 1, tokens.Length);
        using var s = Onnx.Floats(voice.SpeakerEmbedding, 1, voice.SpeakerEmbedding.Length);
        using var f = Onnx.Floats(voice.SpeakerFeatures, 1, voice.FeatureFrames, 80);
        using var outs = Onnx.Run(decoder, new Dictionary<string, OrtValue> { ["speech_tokens"] = t, ["speaker_embeddings"] = s, ["speaker_features"] = f });
        return outs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    public void Dispose()
    {
        encoder.Dispose();
        embed.Dispose();
        lm.Dispose();
        decoder.Dispose();
    }
}
