// Ported from Sources/InnerVoiceKit/AI/SpellingPrompt.swift.

using System.Text;
using System.Text.Json.Serialization;

namespace InnerVoice.Core.AI;

/// A Say It As spelling Claude proposes for one pronunciation term.
public sealed record SpellingProposal(Guid PronunciationID, string Term, string SayItAs, string Note = "", int Confidence = 50);

/// Suggest Spellings: for terms in the pronunciation guide that have no Say It As yet,
/// Claude proposes a respelling. Only each term and the sentence it first appears in are
/// sent. The narrator confirms every one before it's used.
public static class SpellingPrompt
{
    public const string System =
        "You help an audiobook narrator pronounce names, places and invented words. For each " +
        "term, give a respelling that an English speaker and a text-to-speech voice will read the " +
        "intended way, with hyphens between syllables and the stressed syllable in capitals, like " +
        "\"SHIV-awn\" for Siobhan or \"her-MY-oh-nee\" for Hermione. Use the sentence, the book and " +
        "the name's origin. Ordinary English words need no entry: leave them out. Say how sure you " +
        "are, 0 to 100. Answer with JSON only.";

    public sealed record Chunk(Dictionary<string, Guid> Labels, List<ChatMessage> Messages);

    public sealed class Reply
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("sayItAs")] public string SayItAs { get; set; } = "";
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("confidence")] public int? Confidence { get; set; }
    }

    /// Terms still to do: not dismissed, not confirmed, and no Say It As yet.
    public static bool Wanted(Pronunciation p) => !p.Dismissed && !p.Confirmed && string.IsNullOrWhiteSpace(p.SayItAs);

    /// The sentence a term first appears in (at most 40 words), or "" when it isn't found.
    public static string Context(Pronunciation p, Project project, BookIndex index)
    {
        if (p.FirstParagraphID is not { } pid || project.Paragraph(pid, index) is not { } para) return "";
        var sentence = RecapPrompt.Sentences(para.Text).FirstOrDefault(s => s.Contains(p.Term, StringComparison.CurrentCultureIgnoreCase)) ?? "";
        var words = sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 40 ? string.Join(' ', words.Take(40)) + "…" : sentence;
    }

    public static List<Chunk> Chunks(Project project, BookIndex index, int perRequest = 60)
    {
        var terms = project.Pronunciations.Where(Wanted).ToList();
        var output = new List<Chunk>();
        for (int start = 0; start < terms.Count; start += perRequest)
        {
            var labels = new Dictionary<string, Guid>();
            var list = new StringBuilder();
            foreach (var (t, k) in terms.Skip(start).Take(perRequest).Select((t, k) => (t, k)))
            {
                labels[$"T{k + 1}"] = t.Id;
                var ctx = Context(t, project, index);
                list.Append($"[T{k + 1}] {t.Term}").Append(ctx.Length == 0 ? "" : $" — “{ctx}”").Append('\n');
            }
            var byline = project.Author.Length == 0 ? "" : $" by {project.Author}";
            var user = $"Book: {project.Title}{byline}\n\nTerms, each with the sentence it first appears in:\n{list}\n" +
                "note says in a few words where the spelling comes from (\"Irish name\", \"invented; follows the book's spelling\").\n" +
                "Reply as {\"terms\":[{\"id\":\"T1\",\"sayItAs\":\"SHIV-awn\",\"note\":\"…\",\"confidence\":80}]}";
            output.Add(new Chunk(labels, [new(ChatRole.System, System), new(ChatRole.User, user)]));
        }
        return output;
    }

    sealed class Wrapper { [JsonPropertyName("terms")] public List<Reply>? Terms { get; set; } }

    public static List<SpellingProposal>? Parse(string text, Chunk chunk, Project project)
    {
        List<Reply> replies;
        if (JsonText.Extract(text) is { } json && Replies.Decode<Wrapper>(json)?.Terms is { } whole) replies = whole;
        else
        {
            replies = JsonText.Objects(text).Select(Replies.Decode<Reply>).Where(r => r is not null).Cast<Reply>().ToList();
            if (replies.Count == 0) return null;
        }
        var byID = new Dictionary<Guid, Pronunciation>();
        foreach (var p in project.Pronunciations) byID.TryAdd(p.Id, p);
        var output = new List<SpellingProposal>();
        foreach (var r in replies)
        {
            var say = r.SayItAs.Trim();
            if (!chunk.Labels.TryGetValue(r.Id.Trim(), out var id) || !byID.TryGetValue(id, out var p) || !Wanted(p) || say.Length == 0 || say.Length > 80
                || string.Equals(say, p.Term, StringComparison.OrdinalIgnoreCase)) continue;
            output.Add(new SpellingProposal(id, p.Term, say, r.Note ?? "", Math.Clamp(r.Confidence ?? 50, 0, 100)));
        }
        return output;
    }

    /// Uses the spellings the narrator kept. A term filled in meanwhile keeps its own.
    /// Confirmed (checked with the author) stays the narrator's to tick.
    public static int Apply(IEnumerable<SpellingProposal> kept, Project p)
    {
        int n = 0;
        foreach (var s in kept)
        {
            if (p.Pronunciations.Find(x => x.Id == s.PronunciationID) is not { } pr || !string.IsNullOrWhiteSpace(pr.SayItAs)) continue;
            pr.SayItAs = s.SayItAs;
            if (pr.Notes.Length == 0 && s.Note.Length > 0) pr.Notes = s.Note + " (suggested by Claude)";
            n++;
        }
        return n;
    }
}
