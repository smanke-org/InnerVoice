// Byte-level BPE, Orpheus's text tokenizer (Llama 3's tokenizer.json). Small and dependency
// free, so the same code runs in the tests on a Mac and in the app on Windows. (Pocket TTS
// uses SentencePiece: SentencePiece.cs.)

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InnerVoice.Core.Speech.Neural;

public sealed class BpeTokenizer
{
    // Llama 3's pre-tokenizer: numbers up to three digits at a time.
    const string LlamaSplit = @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";

    readonly Dictionary<string, int> vocab;
    readonly Dictionary<(string, string), int> ranks;
    readonly Dictionary<string, int> special;
    readonly Regex split;
    readonly Regex? specialSplit;
    readonly bool ignoreMerges;
    readonly bool nfc;
    readonly ConcurrentDictionary<string, int[]> cache = new();

    static readonly char[] ByteToChar = BuildByteMap();

    BpeTokenizer(Dictionary<string, int> vocab, IEnumerable<(string, string)> merges, Dictionary<string, int> special, string pattern,
                 bool ignoreMerges, bool nfc)
    {
        this.vocab = vocab;
        ranks = [];
        int r = 0;
        foreach (var m in merges) ranks.TryAdd(m, r++);
        this.special = special;
        split = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        if (special.Count > 0)
            specialSplit = new Regex("(" + string.Join("|", special.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + ")",
                                     RegexOptions.Compiled | RegexOptions.CultureInvariant);
        this.ignoreMerges = ignoreMerges;
        this.nfc = nfc;
    }

    public int VocabCount => vocab.Count + special.Count;

    /// A Hugging Face tokenizer.json with a byte-level BPE model (Llama 3, so Orpheus).
    public static BpeTokenizer FromTokenizerJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var model = doc.RootElement.GetProperty("model");
        var vocab = new Dictionary<string, int>();
        foreach (var p in model.GetProperty("vocab").EnumerateObject()) vocab[p.Name] = p.Value.GetInt32();
        var merges = new List<(string, string)>();
        foreach (var m in model.GetProperty("merges").EnumerateArray())
        {
            if (m.ValueKind == JsonValueKind.Array) merges.Add((m[0].GetString()!, m[1].GetString()!));
            else if (m.GetString() is { } s && s.IndexOf(' ') is var i and > 0) merges.Add((s[..i], s[(i + 1)..]));
        }
        var special = new Dictionary<string, int>();
        if (doc.RootElement.TryGetProperty("added_tokens", out var added))
            foreach (var t in added.EnumerateArray())
                if (t.TryGetProperty("special", out var sp) && sp.GetBoolean())
                    special[t.GetProperty("content").GetString()!] = t.GetProperty("id").GetInt32();
        bool ignore = model.TryGetProperty("ignore_merges", out var im) && im.ValueKind == JsonValueKind.True;
        return new BpeTokenizer(vocab, merges, special, LlamaSplit, ignore, nfc: false);
    }

    /// The token IDs for the text. Special markers ("<|im_start|>") written in the text are
    /// their own tokens.
    public List<int> Encode(string text)
    {
        if (nfc) text = text.Normalize(NormalizationForm.FormC);
        var ids = new List<int>();
        var parts = specialSplit is null ? [text] : specialSplit.Split(text);
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            if (special.TryGetValue(part, out var sid)) { ids.Add(sid); continue; }
            foreach (Match m in split.Matches(part)) ids.AddRange(cache.GetOrAdd(m.Value, Word));
        }
        return ids;
    }

    public int? TokenID(string token) => special.TryGetValue(token, out var s) ? s : vocab.TryGetValue(token, out var v) ? v : null;

    int[] Word(string word)
    {
        var bytes = Encoding.UTF8.GetBytes(word);
        var mapped = new string(bytes.Select(b => ByteToChar[b]).ToArray());
        if (ignoreMerges && vocab.TryGetValue(mapped, out var whole)) return [whole];
        var symbols = mapped.Select(c => c.ToString()).ToList();
        while (symbols.Count > 1)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i < symbols.Count - 1; i++)
                if (ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank) { bestRank = rank; best = i; }
            if (best < 0) break;
            var (a, b) = (symbols[best], symbols[best + 1]);
            // Merge every occurrence of the pair, left to right, as the reference does.
            var merged = new List<string>(symbols.Count);
            for (int i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == a && symbols[i + 1] == b) { merged.Add(a + b); i++; }
                else merged.Add(symbols[i]);
            }
            symbols = merged;
        }
        var ids = new List<int>(symbols.Count);
        foreach (var s in symbols)
        {
            if (vocab.TryGetValue(s, out var id)) ids.Add(id);
            else foreach (var c in s) if (vocab.TryGetValue(c.ToString(), out var cid)) ids.Add(cid);
        }
        return ids.ToArray();
    }

    /// GPT-2's map from bytes to printable characters.
    static char[] BuildByteMap()
    {
        var map = new char[256];
        var keep = new List<int>();
        for (int b = '!'; b <= '~'; b++) keep.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) keep.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) keep.Add(b);
        int n = 0;
        for (int b = 0; b < 256; b++)
            map[b] = keep.Contains(b) ? (char)b : (char)(256 + n++);
        return map;
    }
}
