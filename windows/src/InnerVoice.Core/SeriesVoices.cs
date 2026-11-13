// Ported from the Mac's Sources/InnerVoiceKit/SeriesVoices.swift.

using System.Globalization;
using System.Text;

namespace InnerVoice.Core;

/// Voices carried over from an earlier book in a series: each voiced character in that book
/// is matched to a character in this one (the narrator confirms or changes every match), and
/// the matched characters get the same voices, so the series sounds the same book to book.
public static class SeriesVoices
{
    /// How a match was found, so the sheet can say how sure it is.
    public enum How
    {
        /// The same name, or a name one of them also goes by.
        Name,
        /// Only the first name is the same (and only one character here has it).
        FirstName,
        /// Chosen by the narrator.
        Chosen,
    }

    /// `Source`: the character in the earlier book. `Target`: who they are in this book; null
    /// leaves them out.
    public sealed record Match(Guid Source, Guid? Target, How? How);

    public sealed record Options(bool VoiceNotes = true, bool Narrator = true, bool Engine = true);

    /// Characters in the earlier book that have a voice on any engine, most lines first.
    public static List<BookCharacter> Voiced(Project old, BookIndex index) => old.Characters
        .Where(c => c.Voices is { Count: > 0 })
        .OrderByDescending(c => index.StatsFor(c.Id)?.Lines ?? 0)
        .ThenBy(c => c.Name, StringComparer.Ordinal)
        .ToList();

    /// The best guess for each voiced character: who they are in this book. Main characters
    /// choose first, and nobody here is matched twice.
    public static List<Match> Suggest(Project old, BookIndex oldIndex, Project @new)
    {
        var taken = new HashSet<Guid>();
        var output = new List<Match>();
        foreach (var o in Voiced(old, oldIndex))
        {
            var free = @new.Characters.Where(c => !taken.Contains(c.Id)).ToList();
            if (free.FirstOrDefault(n => SameName(o, n)) is { } n1)
            {
                taken.Add(n1.Id);
                output.Add(new Match(o.Id, n1.Id, How.Name));
                continue;
            }
            var first = FirstName(o);
            var sameFirst = first is null ? [] : @new.Characters.Where(c => FirstName(c) == first).ToList();
            if (sameFirst.Count == 1 && !taken.Contains(sameFirst[0].Id))
            {
                taken.Add(sameFirst[0].Id);
                output.Add(new Match(o.Id, sameFirst[0].Id, How.FirstName));
                continue;
            }
            output.Add(new Match(o.Id, null, null));
        }
        return output;
    }

    /// Case and accents folded, like Swift's folding(options: [.caseInsensitive, .diacriticInsensitive]).
    public static string Key(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
    }

    /// The same name, or a name one of them also goes by ("Mara Quinn" and "Mara", when the
    /// later book lists "Mara" as an alias).
    static bool SameName(BookCharacter a, BookCharacter b)
    {
        var an = new[] { a.Name }.Concat(a.Aliases).Select(Key).Where(k => k.Length > 0).ToHashSet();
        return new[] { b.Name }.Concat(b.Aliases).Select(Key).Any(k => k.Length > 0 && an.Contains(k));
    }

    static string? FirstName(BookCharacter c) =>
        c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => w.Length > 1 && !Names.Titles.Contains(w)) is { } f ? Key(f) : null;

    /// Gives the matched characters their voices from the earlier book, as the narrator's own
    /// choices (casting never replaces them). Returns how many characters got voices.
    public static int Apply(IEnumerable<Match> matches, Project old, Project @new, Options options)
    {
        int count = 0;
        foreach (var m in matches)
        {
            if (m.Target is not { } target || old.Character(m.Source) is not { } o || @new.Character(target) is not { } c
                || o.Voices is not { Count: > 0 } voices) continue;
            foreach (var (engine, v) in voices)
            {
                if (VoiceEngines.Parse(engine) is not { } e) continue;
                c.SetVoice(new CastVoice(v.Choice.Copy(), 1, VoiceSource.User), e);
            }
            if (options.VoiceNotes && o.VoiceNotes.Trim().Length > 0)
            {
                c.VoiceNotes = o.VoiceNotes;
                // Theirs now: Build Summaries won't rewrite these blocks for the new book.
                c.VoiceNotesFromBook = null;
                c.VoiceBriefGenerated = null;
            }
            count++;
        }
        if (options.Narrator)
            foreach (var (engine, v) in old.Settings.NarratorVoices ?? [])
                if (VoiceEngines.Parse(engine) is { } e)
                    @new.Settings.SetNarratorVoice(new CastVoice(v.Choice.Copy(), 1, VoiceSource.User), e);
        if (options.Engine && old.Settings.VoiceEngine is { } eng) @new.Settings.VoiceEngine = eng;
        return count;
    }
}
