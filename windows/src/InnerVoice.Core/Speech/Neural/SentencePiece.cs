// A SentencePiece unigram tokenizer: reads Pocket TTS's tokenizer.model (Kyutai's; 4000 pieces,
// identity normalization, byte fallback) and encodes the way the SentencePiece library does
// (the best-scoring split by Viterbi; a character no piece covers becomes its UTF-8 bytes).
// Checked against the library's own answers in NeuralVoiceTests.

using System.Text;

namespace InnerVoice.Core.Speech.Neural;

public sealed class SentencePiece
{
    const char Space = '▁'; // ▁, SentencePiece's visible space
    const float UnkPenalty = 10;

    readonly Dictionary<string, (int Id, float Score)> pieces = [];
    readonly string[] byId;
    readonly int[] byteIds = new int[256];
    readonly int maxPieceLength;
    readonly float minScore;
    readonly bool addDummyPrefix, removeExtraWhitespaces, byteFallback;
    readonly int unkId;

    public int Count => byId.Length;

    SentencePiece(List<(string Piece, float Score, int Type)> list, bool addDummyPrefix, bool removeExtraWhitespaces, bool byteFallback, int unkId)
    {
        byId = list.Select(p => p.Piece).ToArray();
        Array.Fill(byteIds, -1);
        minScore = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            var (piece, score, type) = list[i];
            // 1 normal, 4 user-defined: matched in text. 6 byte: <0xXX>, for byte fallback.
            if (type is 1 or 4)
            {
                pieces[piece] = (i, score);
                maxPieceLength = Math.Max(maxPieceLength, piece.Length);
                if (type == 1) minScore = Math.Min(minScore, score);
            }
            else if (type == 6 && piece.Length == 6 && piece.StartsWith("<0x"))
                byteIds[Convert.ToInt32(piece[3..5], 16)] = i;
        }
        if (minScore == float.MaxValue) minScore = 0;
        this.addDummyPrefix = addDummyPrefix;
        this.removeExtraWhitespaces = removeExtraWhitespaces;
        this.byteFallback = byteFallback;
        this.unkId = unkId;
    }

    public static SentencePiece FromFile(string path) => FromModel(File.ReadAllBytes(path));

    /// Reads a SentencePiece ModelProto (protobuf): the pieces (field 1), the trainer spec's
    /// model type, byte fallback and unk id (field 2), the normalizer spec (field 3).
    public static SentencePiece FromModel(byte[] data)
    {
        var list = new List<(string, float, int)>();
        bool addDummyPrefix = true, removeExtra = true, byteFallback = false;
        int modelType = 1, unkId = 0;
        string normalizer = "nmt_nfkc";
        var top = new Proto(data, 0, data.Length);
        while (top.Next(out int field, out int wire))
        {
            if (field == 1 && wire == 2)
            {
                var m = top.Message();
                string piece = ""; float score = 0; int type = 1;
                while (m.Next(out int f, out int w))
                {
                    if (f == 1 && w == 2) piece = m.String();
                    else if (f == 2 && w == 5) score = m.Float();
                    else if (f == 3 && w == 0) type = (int)m.Varint();
                    else m.Skip(w);
                }
                list.Add((piece, score, type));
            }
            else if (field == 2 && wire == 2)
            {
                var m = top.Message();
                while (m.Next(out int f, out int w))
                {
                    if (f == 3 && w == 0) modelType = (int)m.Varint();
                    else if (f == 35 && w == 0) byteFallback = m.Varint() != 0;
                    else if (f == 40 && w == 0) unkId = (int)m.Varint();
                    else m.Skip(w);
                }
            }
            else if (field == 3 && wire == 2)
            {
                var m = top.Message();
                while (m.Next(out int f, out int w))
                {
                    if (f == 1 && w == 2) normalizer = m.String();
                    else if (f == 3 && w == 0) addDummyPrefix = m.Varint() != 0;
                    else if (f == 4 && w == 0) removeExtra = m.Varint() != 0;
                    else m.Skip(w);
                }
            }
            else top.Skip(wire);
        }
        if (modelType != 1) throw new InvalidDataException("Only SentencePiece unigram models are supported.");
        if (normalizer != "identity") throw new InvalidDataException($"Unsupported SentencePiece normalizer {normalizer}.");
        return new SentencePiece(list, addDummyPrefix, removeExtra, byteFallback, unkId);
    }

    /// The text as the model sees it: spaces as ▁, one in front.
    string Normalize(string text)
    {
        if (removeExtraWhitespaces)
            text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var sb = new StringBuilder(text.Length + 1);
        if (addDummyPrefix && text.Length > 0) sb.Append(Space);
        foreach (var c in text) sb.Append(c == ' ' ? Space : c);
        return sb.ToString();
    }

    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        var s = Normalize(text);
        int n = s.Length;
        if (n == 0) return ids;
        // Viterbi over UTF-16 positions that start a character.
        var best = new float[n + 1];
        var from = new int[n + 1];
        var piece = new int[n + 1]; // -1: unknown character
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        for (int i = 0; i < n; i++)
        {
            if (float.IsNegativeInfinity(best[i]) || char.IsLowSurrogate(s[i])) continue;
            int charLen = char.IsHighSurrogate(s[i]) && i + 1 < n && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            bool single = false;
            for (int len = 1; len <= maxPieceLength && i + len <= n; len++)
            {
                if (i + len < n && char.IsLowSurrogate(s[i + len])) continue;
                if (!pieces.TryGetValue(s.Substring(i, len), out var p)) continue;
                if (len == charLen) single = true;
                float score = best[i] + p.Score;
                if (score > best[i + len]) { best[i + len] = score; from[i + len] = i; piece[i + len] = p.Id; }
            }
            if (!single)
            {
                // SentencePiece's unknown node: one character, scored below every piece.
                float score = best[i] + minScore - UnkPenalty;
                if (score > best[i + charLen]) { best[i + charLen] = score; from[i + charLen] = i; piece[i + charLen] = -1; }
            }
        }
        var path = new List<(int Start, int End, int Id)>();
        for (int end = n; end > 0; end = from[end]) path.Add((from[end], end, piece[end]));
        path.Reverse();
        foreach (var (start, end, id) in path)
        {
            if (id >= 0) { ids.Add(id); continue; }
            var bytes = Encoding.UTF8.GetBytes(s[start..end]);
            if (byteFallback && bytes.All(b => byteIds[b] >= 0)) ids.AddRange(bytes.Select(b => byteIds[b]));
            else ids.Add(unkId);
        }
        return ids;
    }

    /// Back to text: ▁ is a space, byte pieces are joined into characters, and the space in front goes.
    public string Decode(IEnumerable<int> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (id < 0 || id >= byId.Length) continue;
            int b = Array.IndexOf(byteIds, id);
            if (b >= 0) { bytes.Add((byte)b); continue; }
            if (id == unkId) { bytes.AddRange(" ⁇ "u8.ToArray()); continue; }
            var p = byId[id];
            if (p.StartsWith('<') && p.EndsWith('>') && !pieces.ContainsKey(p)) continue; // <s>, </s>, <pad>
            bytes.AddRange(Encoding.UTF8.GetBytes(p.Replace(Space, ' ')));
        }
        var text = Encoding.UTF8.GetString(bytes.ToArray());
        return addDummyPrefix && text.StartsWith(' ') ? text[1..] : text;
    }

    /// A minimal protobuf reader (wire types 0, 1, 2, 5).
    ref struct Proto(byte[] data, int pos, int end)
    {
        int pos = pos;

        public bool Next(out int field, out int wire)
        {
            field = wire = 0;
            if (pos >= end) return false;
            ulong key = Varint();
            field = (int)(key >> 3);
            wire = (int)(key & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong v = 0;
            for (int shift = 0; pos < end; shift += 7)
            {
                byte b = data[pos++];
                v |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) break;
            }
            return v;
        }

        public float Float()
        {
            float f = BitConverter.ToSingle(data, pos);
            pos += 4;
            return f;
        }

        public string String()
        {
            int len = (int)Varint();
            var s = Encoding.UTF8.GetString(data, pos, len);
            pos += len;
            return s;
        }

        public Proto Message()
        {
            int len = (int)Varint();
            var m = new Proto(data, pos, pos + len);
            pos += len;
            return m;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: pos += 8; break;
                case 2: int len = (int)Varint(); pos += len; break; // not `pos += Varint()`: that adds to the old pos
                case 5: pos += 4; break;
                default: throw new InvalidDataException("Unexpected protobuf wire type.");
            }
        }
    }
}
