// Ported from Sources/InnerVoiceKit/PhysicalProfile.swift. What a character looks like, for
// the author's character profiles and tools that draw or film the book later (never for
// narration). The JSON is the Mac's: `values` is an object keyed by field name.

using System.Text;
using System.Text.Json.Serialization;
using InnerVoice.Core.AI;

namespace InnerVoice.Core;

public enum PhysicalField { Age, Height, Build, Hair, Eyes, Skin, Face, Marks, Clothing, Other }

public static class PhysicalFields
{
    public static readonly PhysicalField[] All = Enum.GetValues<PhysicalField>();

    /// The key in JSON (the Swift raw value).
    public static string Raw(this PhysicalField f) => f.ToString().ToLowerInvariant();

    public static PhysicalField? Parse(string raw) =>
        All.Cast<PhysicalField?>().FirstOrDefault(f => f!.Value.Raw() == raw);

    public static string Title(this PhysicalField f) => f switch
    {
        PhysicalField.Age => "Apparent Age",
        PhysicalField.Height => "Height",
        PhysicalField.Build => "Build",
        PhysicalField.Hair => "Hair",
        PhysicalField.Eyes => "Eyes",
        PhysicalField.Skin => "Skin",
        PhysicalField.Face => "Face",
        PhysicalField.Marks => "Distinguishing Marks",
        PhysicalField.Clothing => "Typical Clothing",
        _ => "Other",
    };
}

/// One field's text, and whether the AI guessed it rather than read it in the book.
public sealed class PhysicalValue : ModelObject
{
    public string Text { get; set; } = "";
    public bool Inferred { get; set; }

    public PhysicalValue() { }
    public PhysicalValue(string text, bool inferred = false) { Text = text; Inferred = inferred; }
    public PhysicalValue Copy() => new() { Text = Text, Inferred = Inferred, Extra = CopyExtra() };
}

/// How their looks change during the story, from a chapter on.
public sealed class PhysicalChange : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// The chapter it happens in (index into `Project.Chapters`); null: not known.
    public int? Chapter { get; set; }
    public PhysicalField Field { get; set; }
    public string Text { get; set; } = "";
    public bool Inferred { get; set; }

    public PhysicalChange Copy() => new() { Id = Id, Chapter = Chapter, Field = Field, Text = Text, Inferred = Inferred, Extra = CopyExtra() };
}

/// A place where the book contradicts itself about their looks, with no change in the story
/// to explain it.
public sealed class PhysicalFlag : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PhysicalField Field { get; set; }
    public string Text { get; set; } = "";

    public PhysicalFlag Copy() => new() { Id = Id, Field = Field, Text = Text, Extra = CopyExtra() };
}

public sealed class PhysicalProfile : ModelObject
{
    /// A short prose description built from the fields.
    public string Summary { get; set; } = "";
    public bool SummaryInferred { get; set; }
    /// Keyed by `PhysicalField.Raw()`.
    public Dictionary<string, PhysicalValue> Values { get; set; } = [];
    public List<PhysicalChange> Changes { get; set; } = [];
    public List<PhysicalFlag> Flags { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty =>
        Summary.Trim().Length == 0
        && Values.Values.All(v => v.Text.Trim().Length == 0)
        && Changes.Count == 0 && Flags.Count == 0;

    public PhysicalValue Value(PhysicalField f) => Values.TryGetValue(f.Raw(), out var v) ? v : new PhysicalValue();

    public void Set(PhysicalField f, PhysicalValue v)
    {
        if (v.Text.Trim().Length == 0) Values.Remove(f.Raw()); else Values[f.Raw()] = v;
    }

    /// Changes in story order (unknown chapters last).
    [JsonIgnore]
    public List<PhysicalChange> OrderedChanges =>
        Changes.OrderBy(c => c.Chapter ?? int.MaxValue).ThenBy(c => c.Field.Raw(), StringComparer.Ordinal).ToList();

    public PhysicalProfile Copy() => new()
    {
        Summary = Summary, SummaryInferred = SummaryInferred,
        Values = Values.ToDictionary(kv => kv.Key, kv => kv.Value.Copy()),
        Changes = Changes.ConvertAll(c => c.Copy()), Flags = Flags.ConvertAll(f => f.Copy()), Extra = CopyExtra(),
    };

    /// Value equality, like the Mac's `==` (unknown fields don't count).
    public static bool Same(PhysicalProfile? a, PhysicalProfile? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Summary == b.Summary && a.SummaryInferred == b.SummaryInferred
            && a.Values.Count == b.Values.Count
            && a.Values.All(kv => b.Values.TryGetValue(kv.Key, out var o) && o.Text == kv.Value.Text && o.Inferred == kv.Value.Inferred)
            && a.Changes.Count == b.Changes.Count
            && a.Changes.Zip(b.Changes).All(z => z.First.Id == z.Second.Id && z.First.Chapter == z.Second.Chapter && z.First.Field == z.Second.Field
                && z.First.Text == z.Second.Text && z.First.Inferred == z.Second.Inferred)
            && a.Flags.Count == b.Flags.Count
            && a.Flags.Zip(b.Flags).All(z => z.First.Id == z.Second.Id && z.First.Field == z.Second.Field && z.First.Text == z.Second.Text);
    }

    /// Puts a freshly written profile on the character, but only while the narrator hasn't
    /// edited the last one the AI wrote (same rule as Voice Notes' generated blocks).
    /// Returns whether it changed.
    public static bool Put(PhysicalProfile fresh, BookCharacter c)
    {
        var current = c.Physical ?? new PhysicalProfile();
        bool unedited = current.IsEmpty || Same(current, c.PhysicalGenerated);
        if (!unedited || Same(current, fresh)) return false;
        c.Physical = fresh.IsEmpty ? null : fresh;
        c.PhysicalGenerated = c.Physical?.Copy();
        return true;
    }

    /// The narrator changed the profile since the AI last wrote it.
    public static bool IsEdited(BookCharacter c) =>
        c.Physical is { IsEmpty: false } p && !Same(p, c.PhysicalGenerated);

    /// Markdown for the Character Profiles export.
    public string Markdown(string name, Func<int, string> chapterTitle)
    {
        var output = new List<string>();
        const string tag = " *(inferred)*";
        if (Summary.Trim().Length > 0) output.Add(Summary + (SummaryInferred ? tag : ""));
        var rows = PhysicalFields.All
            .Where(f => Values.TryGetValue(f.Raw(), out var v) && v.Text.Length > 0)
            .Select(f => $"- **{f.Title()}:** {Values[f.Raw()].Text}" + (Values[f.Raw()].Inferred ? tag : "")).ToList();
        if (rows.Count > 0) output.Add(string.Join("\n", rows));
        if (Changes.Count > 0)
            output.Add("**Changes during the story**\n" + string.Join("\n", OrderedChanges.Select(c =>
                "- " + (c.Chapter is { } ch ? chapterTitle(ch) + ": " : "") + $"{c.Field.Title()}: {c.Text}" + (c.Inferred ? tag : ""))));
        if (Flags.Count > 0)
            output.Add("**Continuity to check**\n" + string.Join("\n", Flags.Select(f => $"- {f.Field.Title()}: {f.Text}")));
        return string.Join("\n\n", output);
    }
}

/// The Character Profiles export: every character with a physical description or summary.
public static class CharacterProfiles
{
    public static string Markdown(Project project, BookIndex index)
    {
        string Title(int i)
        {
            var t = i >= 0 && i < project.Chapters.Count ? project.Chapters[i].Title : "";
            return t.Length == 0 ? $"Chapter {i + 1}" : t;
        }
        var output = new List<string> { "# Character Profiles" };
        var cast = project.Characters
            .OrderBy(c => c.IsMain ?? false ? 0 : 1)
            .ThenBy(c => -(index.StatsFor(c.Id)?.Lines ?? 0))
            .ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        foreach (var c in cast)
        {
            var other = FactKinds.NarratorOrder.Where(k => k != FactKind.Appearance && k != FactKind.Voice && k != FactKind.Accent)
                .SelectMany(k => c.Facts.Where(f => f.Kind == k).Select(f => $"- **{k.Title()}:** {f.Text}")).ToList();
            if (c.Physical is not { IsEmpty: false } && other.Count == 0) continue;
            var s = new StringBuilder($"## {c.Name}");
            if (c.Aliases.Count > 0) s.Append("\n\n*Also called:* ").Append(string.Join(", ", c.Aliases));
            if (c.Physical is { IsEmpty: false } p) s.Append("\n\n### Physical Description\n\n").Append(p.Markdown(c.Name, Title));
            if (other.Count > 0) s.Append("\n\n### From the Summary\n\n").Append(string.Join("\n", other));
            output.Add(s.ToString());
        }
        if (output.Count == 1) output.Add("No character profiles yet. Run Build Summaries first.");
        output.Add("*Items marked (inferred) are the AI’s interpretation, not stated in the book.*");
        return string.Join("\n\n", output) + "\n";
    }
}
