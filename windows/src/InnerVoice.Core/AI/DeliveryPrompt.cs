// Ported from Sources/InnerVoiceKit/AI/DeliveryPrompt.swift.

using System.Text;
using System.Text.Json.Serialization;

namespace InnerVoice.Core.AI;

/// A delivery hint Claude proposes for one line.
public sealed record DeliveryProposal(Guid LineID, string Hint, string? Before = null);

/// Delivery Hints: how a line is performed beyond its tone ("barely audible", "through
/// gritted teeth", "laughing"). They reach ElevenLabs only, as an audio tag in square
/// brackets before the line (eleven_v4 takes free-form tags); no other engine is sent them.
public static class DeliveryPrompt
{
    public const string System =
        "You help an audiobook producer direct AI voice actors. For each marked line, decide " +
        "whether it needs a performance direction beyond its tone, from what the passage says or " +
        "clearly shows: how loud (whispers, shouts, barely audible), the pace (slowly, rushed, " +
        "hesitant), a reaction (laughing, sighs, crying, out of breath), or the manner (through " +
        "gritted teeth, mocking, pleading). A direction is one to four plain words, lowercase. " +
        "Leave out lines that need nothing beyond their tone; most lines need nothing. Answer " +
        "with JSON only.";

    public sealed record Chunk(Dictionary<string, Guid> Labels, List<ChatMessage> Messages);

    public sealed class Reply
    {
        [JsonPropertyName("line")] public string Line { get; set; } = "";
        [JsonPropertyName("hint")] public string Hint { get; set; } = "";
    }

    /// Lines Claude may give a hint: spoken or thought lines with a speaker, whose hint isn't
    /// the narrator's own.
    public static bool Asks(Line line) =>
        line.SpeakerID is not null && line.DeliverySource != ToneSource.User && line.Kind != LineKind.Written;

    /// The whole book, or `chapters`, in passages of about `maxWords`. Each asked line is
    /// marked in the text with its label, speaker and tone: {L3 Mara, sad}.
    public static List<Chunk> Chunks(Project project, BookIndex index, IReadOnlyList<int>? chapters = null, int maxWords = 2500)
    {
        var picked = (chapters ?? Enumerable.Range(0, project.Chapters.Count).ToList()).Where(c => c >= 0 && c < project.Chapters.Count);
        var output = new List<Chunk>();
        var labels = new Dictionary<string, Guid>();
        var passage = new StringBuilder();
        int words = 0, n = 0;
        void Flush()
        {
            if (labels.Count > 0)
            {
                var user = "Passage:\n\n" + passage.ToString().Trim() + "\n\n" +
                    "Reply as {\"lines\":[{\"line\":\"L1\",\"hint\":\"barely audible\"}]}, only for lines that need a direction, or {\"lines\":[]}.";
                output.Add(new Chunk(labels, [new(ChatRole.System, System), new(ChatRole.User, user)]));
            }
            labels = [];
            passage.Clear();
            words = 0;
            n = 0;
        }
        foreach (int c in picked)
            foreach (var p in project.Chapters[c].Paragraphs.Where(x => x.Style is ParagraphStyle.Body or ParagraphStyle.BlockQuote))
            {
                var lines = index.LinesIn(p.Id).Select(i => project.Lines[i]).OrderBy(l => l.Range.Location).ToList();
                var text = new StringBuilder(p.Text);
                var inserts = new List<(int At, string Tag)>();
                foreach (var line in lines.Where(l => Asks(l) && l.Range.End <= p.Text.Length))
                {
                    n++;
                    labels[$"L{n}"] = line.Id;
                    var who = project.Character(line.SpeakerID)?.Name ?? "?";
                    var tone = Tones.ToneOf(line) is { } t && t != Tone.Neutral ? $", {t.Raw()}" : "";
                    // Before the opening quote mark, so the quote reads as it does in the book.
                    int at = line.Range.Location;
                    if (at > 0 && "“\"‘'«—".Contains(p.Text[at - 1])) at--;
                    inserts.Add((at, $"{{L{n} {who}{tone}}}"));
                }
                for (int i = inserts.Count - 1; i >= 0; i--) text.Insert(inserts[i].At, inserts[i].Tag);
                passage.Append(text).Append("\n\n");
                words += p.WordCount;
                if (words >= maxWords) Flush();
            }
        Flush();
        return output;
    }

    sealed class Wrapper { [JsonPropertyName("lines")] public List<Reply>? Lines { get; set; } }

    public static List<(string Label, string Hint)>? Parse(string text)
    {
        if (JsonText.Extract(text) is { } json && Replies.Decode<Wrapper>(json)?.Lines is { } whole)
            return whole.Select(r => (r.Line, r.Hint)).ToList();
        var partial = JsonText.Objects(text).Select(Replies.Decode<Reply>).Where(r => r is not null && r.Line.Length > 0).Cast<Reply>().ToList();
        return partial.Count == 0 ? null : partial.Select(r => (r.Line, r.Hint)).ToList();
    }

    /// A hint as ElevenLabs is sent it: plain words, no brackets or braces (they would be read
    /// aloud or end the tag early), at most six words. null when nothing is left.
    public static string? Clean(string hint)
    {
        var kept = new string(hint.Where(c => char.IsLetter(c) || char.IsWhiteSpace(c) || ",'’-".Contains(c)).ToArray());
        var words = kept.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(6);
        var output = string.Join(' ', words).Trim(' ', ',', '-');
        return output.Length == 0 ? null : output.Length > 60 ? output[..60] : output;
    }

    /// Proposals from a reply: known labels, cleaned hints that say more than the tone, and
    /// never a line whose hint is the narrator's.
    public static List<DeliveryProposal> Proposals(IEnumerable<(string Label, string Hint)> replies, Chunk chunk, Project project, BookIndex index)
    {
        var output = new List<DeliveryProposal>();
        foreach (var (label, raw) in replies)
        {
            if (!chunk.Labels.TryGetValue(label.Trim(), out var id) || !index.LineIndexByID.TryGetValue(id, out var i) || Clean(raw.ToLowerInvariant()) is not { } hint) continue;
            var line = project.Lines[i];
            if (!Asks(line) || hint == line.Delivery) continue;
            if (Tones.ToneOf(line) is { } t && (hint == t.Raw() || $"[{hint}]" == t.AudioTag())) continue;
            output.Add(new DeliveryProposal(id, hint, line.Delivery));
        }
        return output;
    }

    /// Stores the hints (the AI's), never over the narrator's own.
    public static void Apply(IEnumerable<DeliveryProposal> proposals, Project p)
    {
        var byLine = new Dictionary<Guid, string>();
        foreach (var x in proposals) byLine.TryAdd(x.LineID, x.Hint);
        foreach (var l in p.Lines)
        {
            if (!byLine.TryGetValue(l.Id, out var hint) || l.DeliverySource == ToneSource.User) continue;
            l.Delivery = hint;
            l.DeliverySource = ToneSource.Ai;
        }
    }

    /// The narrator's own hint; empty clears it (and Claude may suggest one again).
    public static void Set(string hint, IEnumerable<Guid> ids, Project p)
    {
        var set = ids.ToHashSet();
        var cleaned = Clean(hint);
        foreach (var l in p.Lines.Where(l => set.Contains(l.Id)))
        {
            l.Delivery = cleaned;
            l.DeliverySource = cleaned is null ? null : ToneSource.User;
        }
    }

    /// The audio tag ElevenLabs gets for a line: tone and hint in one bracket
    /// ("[sad, barely audible]"), since stacked tags can blur the performance.
    /// TODO(voices): the ElevenLabs engine puts this before the line's text in its request
    /// (the Mac's VoiceResolver / SpeechRequest); no other engine is sent the hint.
    public static string? AudioTag(Tone? tone, string? delivery)
    {
        var parts = new List<string>();
        if (tone?.AudioTag() is { } tag) parts.Add(tag[1..^1]);
        if (delivery is not null && Clean(delivery) is { } d) parts.Add(d);
        return parts.Count == 0 ? null : "[" + string.Join(", ", parts) + "]";
    }
}
