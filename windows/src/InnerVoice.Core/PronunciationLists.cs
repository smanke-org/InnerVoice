// Two pronunciation lists, and only two, ported from Sources/InnerVoiceKit/PronunciationLists.swift:
// - This Book (`Project.Pronunciations`): the book's own entries, saved in the project.
// - This PC (the Mac's This Mac; `PronunciationList`): one list for the whole computer, used by
//   every book that has "Use This PC's Pronunciations" on (`ProjectSettings.UsesComputerPronunciations`,
//   on unless turned off).
//
// A book's own entry always wins over the computer's for the same word. Each book keeps a copy
// of the computer entries it used (`Project.ComputerPronunciations`, refreshed every time it's
// saved), so it sounds the same on another computer: there, the copy fills in the words that
// computer's list doesn't have. On the computer the copy came from, the live list is the truth:
// a word deleted there is gone, never brought back from a book's copy (each entry remembers the
// list it came from, `ListedPronunciation.List`).
//
// The Mac and Windows apps share this format and the import/export files (PronunciationFile.cs).

using System.Globalization;
using System.Text.Json.Serialization;

namespace InnerVoice.Core;

/// One entry in a computer's list, or in a book's copy of one. JSON as the Mac writes it: `id`,
/// `term`, `sayItAs`, `notes` (left out when empty), `matchCase` (only when true), `list` (left
/// out when null).
public sealed class ListedPronunciation : IEquatable<ListedPronunciation>
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired] public string Term { get; set; } = "";
    public string SayItAs { get; set; } = "";
    [JsonIgnore] public string Notes { get; set; } = "";
    [JsonPropertyName("notes")] public string? NotesStored { get => Notes.Length == 0 ? null : Notes; set => Notes = value ?? ""; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool MatchCase { get; set; }
    /// In a book's copy: the `PronunciationList.ListID` the entry came from.
    public string? List { get; set; }

    public ListedPronunciation() { }

    public ListedPronunciation(string term, string sayItAs, string notes = "", bool matchCase = false, string? list = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Term = term;
        SayItAs = sayItAs;
        Notes = notes;
        MatchCase = matchCase;
        List = list;
    }

    /// The word as a lookup key: trimmed, any case.
    [JsonIgnore] public string Key => KeyOf(Term);
    public static string KeyOf(string term) => PronunciationText.Trim(term).ToLowerInvariant();

    /// Something to say: a word and how to say it.
    [JsonIgnore] public bool IsUsable => Key.Length > 0 && PronunciationText.Trim(SayItAs).Length > 0;

    [JsonIgnore] public SpeechScript.Saying Saying => new(PronunciationText.Trim(Term), PronunciationText.Trim(SayItAs), MatchCase);

    public ListedPronunciation Copy() => new(Term, SayItAs, Notes, MatchCase, List, Id);

    public bool Equals(ListedPronunciation? o) => o is not null && Id == o.Id && Term == o.Term && SayItAs == o.SayItAs
        && Notes == o.Notes && MatchCase == o.MatchCase && List == o.List;
    public override bool Equals(object? obj) => Equals(obj as ListedPronunciation);
    public override int GetHashCode() => HashCode.Combine(Id, Term, SayItAs, Notes, MatchCase, List);
    public override string ToString() => $"{Term} = {SayItAs}";
}

/// The computer's list. `ListID` names it (made once; on the Mac it travels with the list
/// through settings sync, so Macs that share settings share one list).
public sealed class PronunciationList
{
    [JsonPropertyName("listID"), JsonRequired] public string ListID { get; set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();
    [JsonRequired] public List<ListedPronunciation> Entries { get; set; } = [];

    public PronunciationList() { }

    public PronunciationList(string listID, IEnumerable<ListedPronunciation>? entries = null)
    {
        ListID = listID;
        Entries = entries?.ToList() ?? [];
    }

    public ListedPronunciation? Entry(string term)
    {
        var k = ListedPronunciation.KeyOf(term);
        return Entries.FirstOrDefault(e => e.Key == k);
    }

    /// Sorted for showing: A to Z, any case.
    [JsonIgnore] public List<ListedPronunciation> Sorted => PronunciationText.SortedByTerm(Entries);

    public PronunciationList Copy() => new(ListID, Entries.Select(e => e.Copy()));

    public bool SameAs(PronunciationList? o) => o is not null && ListID == o.ListID && Entries.SequenceEqual(o.Entries);
}

/// Where a word's pronunciation comes from, for the Pronunciations page.
public enum PronunciationSource
{
    /// The book's own entry.
    Book,
    /// The computer's list.
    Computer,
    /// The book's copy of another computer's list (this computer's list doesn't have it).
    BookCopy,
}

/// What to do with a word both lists (or an import and the list) have.
public enum PronunciationConflict
{
    /// Keep what's there.
    KeepExisting,
    /// Use the one coming in.
    Replace,
}

public static class PronunciationLists
{
    static readonly object gate = new();
    static PronunciationList? computer;

    /// The computer's list as the app has it now; null before the app set it (Core tests) or
    /// when a test wants none. The app sets it at launch and whenever the list changes, so
    /// `SpeechScript.Sayings` and saving read it without reaching into the app.
    public static PronunciationList? Computer
    {
        get { lock (gate) return computer; }
        set { lock (gate) computer = value; }
    }

    /// The computer entries a book uses, in order of what wins: this computer's list, then the
    /// book's copy of another computer's words that this one doesn't have. Empty when the book
    /// has the computer's list turned off.
    public static List<(ListedPronunciation Entry, PronunciationSource Source)> ComputerEntries(Project project, PronunciationList? computer)
    {
        var output = new List<(ListedPronunciation, PronunciationSource)>();
        if (!project.Settings.UsesComputerPronunciations) return output;
        var seen = new HashSet<string>();
        foreach (var e in computer?.Entries ?? [])
            if (e.IsUsable && seen.Add(e.Key)) output.Add((e, PronunciationSource.Computer));
        foreach (var e in project.ComputerPronunciations ?? [])
        {
            if (!e.IsUsable || seen.Contains(e.Key)) continue;
            // A word from this computer's own list that isn't in it anymore was deleted here.
            if (computer is not null && e.List == computer.ListID) continue;
            seen.Add(e.Key);
            output.Add((e, PronunciationSource.BookCopy));
        }
        return output;
    }

    /// The words the book's own list covers (entries with something to say).
    public static HashSet<string> BookKeys(Project project) =>
        project.Pronunciations.Where(p => !p.Dismissed && PronunciationText.Trim(p.SayItAs).Length > 0)
            .Select(p => ListedPronunciation.KeyOf(p.Term)).Where(k => k.Length > 0).ToHashSet();

    /// Every saying the book speaks with: its own entries, then the computer's for words it
    /// doesn't have. Longest first, so "New York" is swapped before "York".
    public static List<SpeechScript.Saying> Sayings(Project project, PronunciationList? computer)
    {
        var own = project.Pronunciations
            .Where(p => !p.Dismissed && PronunciationText.Trim(p.SayItAs).Length > 0 && PronunciationText.Trim(p.Term).Length > 0)
            .Select(p => new SpeechScript.Saying(PronunciationText.Trim(p.Term), PronunciationText.Trim(p.SayItAs), p.MatchCase))
            .ToList();
        var ownKeys = own.Select(s => s.Term.ToLowerInvariant()).ToHashSet();
        var shared = ComputerEntries(project, computer).Select(x => x.Entry).Where(e => !ownKeys.Contains(e.Key)).Select(e => e.Saying);
        return own.Concat(shared).OrderByDescending(s => s.Term.Length).ToList();
    }

    /// The copy a book saves of the computer entries it uses (null when it uses none and never
    /// had a copy). Each entry keeps the list it came from.
    public static List<ListedPronunciation>? CopyForSaving(Project project, PronunciationList? computer)
    {
        if (!project.Settings.UsesComputerPronunciations) return project.ComputerPronunciations;
        var entries = ComputerEntries(project, computer).Select(x =>
        {
            var e = x.Entry.Copy();
            if (x.Source == PronunciationSource.Computer) e.List = computer?.ListID;
            return e;
        }).ToList();
        if (entries.Count == 0 && project.ComputerPronunciations is null) return null;
        return entries;
    }

    /// The book's entries that could go to the computer's list: with something to say.
    public static List<ListedPronunciation> BookEntries(Project project) =>
        project.Pronunciations
            .Where(p => !p.Dismissed && PronunciationText.Trim(p.SayItAs).Length > 0 && PronunciationText.Trim(p.Term).Length > 0)
            .Select(p => new ListedPronunciation(PronunciationText.Trim(p.Term), PronunciationText.Trim(p.SayItAs), p.Notes, p.MatchCase))
            .ToList();

    /// Incoming entries that are already in the list with a different pronunciation (the same
    /// word saying the same thing isn't a conflict).
    public static List<ListedPronunciation> Conflicts(IEnumerable<ListedPronunciation> incoming, PronunciationList list) =>
        incoming.Where(e => list.Entry(e.Term) is { } have
            && (PronunciationText.Trim(have.SayItAs) != PronunciationText.Trim(e.SayItAs) || have.MatchCase != e.MatchCase)).ToList();

    public sealed record MergeResult(int Added = 0, int Replaced = 0, int Kept = 0, int Unchanged = 0);

    /// Adds entries to the list. Words it already has are kept or replaced as `conflict` says;
    /// the same entry again changes nothing. Entries without a word or a pronunciation are
    /// skipped.
    public static MergeResult Merge(IEnumerable<ListedPronunciation> incoming, PronunciationList list, PronunciationConflict conflict)
    {
        int added = 0, replaced = 0, kept = 0, unchanged = 0;
        var seen = new HashSet<string>();
        foreach (var source in incoming)
        {
            if (!source.IsUsable || !seen.Add(source.Key)) continue;
            var e = source.Copy();
            e.List = null;
            e.Term = PronunciationText.Trim(e.Term);
            e.SayItAs = PronunciationText.Trim(e.SayItAs);
            int i = list.Entries.FindIndex(x => x.Key == e.Key);
            if (i >= 0)
            {
                var have = list.Entries[i];
                if (have.SayItAs == e.SayItAs && have.MatchCase == e.MatchCase)
                {
                    if (have.Notes.Length == 0 && e.Notes.Length > 0) list.Entries[i] = Also(have.Copy(), x => x.Notes = e.Notes);
                    unchanged++;
                }
                else if (conflict == PronunciationConflict.Replace)
                {
                    e.Id = have.Id;
                    list.Entries[i] = e;
                    replaced++;
                }
                else kept++;
            }
            else
            {
                e.Id = Guid.NewGuid();
                list.Entries.Add(e);
                added++;
            }
        }
        return new MergeResult(added, replaced, kept, unchanged);
    }

    static ListedPronunciation Also(ListedPronunciation e, Action<ListedPronunciation> edit) { edit(e); return e; }
}

/// Text rules both apps share for pronunciations.
public static class PronunciationText
{
    /// Swift's `trimmingCharacters(in: .whitespaces)`: spaces and tabs, not line breaks.
    public static string Trim(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
    }

    static bool IsSpace(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// A to Z, any case (the Mac's `localizedCaseInsensitiveCompare`). Stable for ties.
    public static List<ListedPronunciation> SortedByTerm(IEnumerable<ListedPronunciation> entries) =>
        entries.OrderBy(e => e.Term, StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase)).ToList();

    /// Swift's `localizedCaseInsensitiveContains`.
    public static bool Contains(string text, string search) => text.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}
