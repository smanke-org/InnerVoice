// Ported from Sources/InnerVoiceKit/AI/RecapPrompt.swift.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InnerVoice.Core.AI;

/// Chapter Recaps: a short "Previously…" for each chapter after the first, written by Claude
/// from the chapter before it (and that chapter's own recap, so the thread carries on).
public static class RecapPrompt
{
    public const string System =
        "You write the short \"Previously…\" recap a listener hears at the start of an audiobook " +
        "chapter, so someone coming back to the book remembers where the story is. From the " +
        "chapter given, write two to four plain sentences, under 70 words, in the past tense: who " +
        "did what, and what is at stake now. Use the characters' names. Don't quote the book, " +
        "don't judge it, and don't reveal anything that happens later. Answer with JSON only.";

    /// Words a chapter needs to be worth recapping (title pages and part openers aren't).
    public const int MinimumWords = 150;
    public const int MaximumWords = 90;

    /// One request: the chapter whose recap this is.
    public sealed record Job(Guid ChapterID, List<ChatMessage> Messages);

    /// One job per chapter that has a story chapter before it and no recap of the narrator's
    /// own. `chapters` (indexes) limits it; `replace` also rewrites Claude's earlier recaps.
    public static List<Job> Jobs(Project project, IReadOnlyCollection<int>? chapters = null, bool replace = true)
    {
        var output = new List<Job>();
        int? source = null; // the last story chapter before this one
        for (int c = 0; c < project.Chapters.Count; c++)
        {
            var chapter = project.Chapters[c];
            if (source is { } s && (chapters is null || chapters.Contains(c)) && chapter.RecapEdited != true && (replace || chapter.RecapText is null))
            {
                var before = project.Chapters[s];
                var text = string.Join("\n\n", before.Paragraphs.Where(p => p.Style != ParagraphStyle.SceneBreak).Select(p => p.Text));
                var earlier = before.RecapText is { } r ? $"\nWhat happened before that chapter: {r}\n" : "";
                var user = $"Book: {project.Title}\n{earlier}\nThe chapter before \"{chapter.Title}\" ({before.Title}):\n\n{text}\n\nReply as {{\"recap\":\"…\"}}";
                output.Add(new Job(chapter.Id, [new(ChatRole.System, System), new(ChatRole.User, user)]));
            }
            if (chapter.WordCount >= MinimumWords) source = c;
        }
        return output;
    }

    sealed class Wrapper { [JsonPropertyName("recap")] public string? Recap { get; set; } }

    static readonly string[] Leads = ["Previously…", "Previously...", "Previously,", "Previously:", "Previously"];

    /// The recap from a reply, tidied: no "Previously" lead-in, at most `MaximumWords`.
    public static string? Parse(string text)
    {
        if (JsonText.Extract(text) is not { } json || Replies.Decode<Wrapper>(json)?.Recap is not { } recap) return null;
        recap = recap.Trim();
        foreach (var lead in Leads)
            if (recap.StartsWith(lead, StringComparison.Ordinal)) { recap = recap[lead.Length..].Trim(); break; }
        if (Words.Count(recap) < 3) return null;
        if (Words.Count(recap) > MaximumWords)
        {
            // Keep whole sentences up to the limit.
            var kept = "";
            foreach (var s in Sentences(recap))
            {
                if (Words.Count(kept + " " + s) > MaximumWords) break;
                kept += (kept.Length == 0 ? "" : " ") + s;
            }
            if (kept.Length > 0) recap = kept;
        }
        return recap.Length == 0 ? recap : char.ToUpperInvariant(recap[0]) + recap[1..];
    }

    static readonly Regex SentenceEnd = new(@"(?<=[.!?…][""”’)\]]*)\s+", RegexOptions.CultureInvariant);

    /// The sentences of a short text (for trimming a recap).
    public static IEnumerable<string> Sentences(string text) =>
        SentenceEnd.Split(text.Trim()).Where(s => s.Length > 0);

    /// Stores a recap unless the narrator wrote or edited this chapter's own. True if stored.
    public static bool Put(string recap, Guid chapterID, Project p)
    {
        if (p.Chapters.Find(c => c.Id == chapterID) is not { } chapter || chapter.RecapEdited == true) return false;
        chapter.Recap = recap;
        return true;
    }

    /// The narrator's edit: kept from then on. Clearing it lets Claude write one again.
    public static void Edit(string text, Guid chapterID, Project p)
    {
        if (p.Chapters.Find(c => c.Id == chapterID) is not { } chapter) return;
        bool empty = string.IsNullOrWhiteSpace(text);
        chapter.Recap = empty ? null : text;
        chapter.RecapEdited = empty ? null : true;
    }
}
