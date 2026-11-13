// The book model, ported from Sources/InnerVoiceKit/Project.swift.
//
// Property names, enum values and optionality match the Swift model exactly, because both
// apps read and write the same project.json (see Json/ProjectJson.cs for the rules). Every
// class keeps the JSON fields it doesn't know in `Extra`, so voice fields and anything a
// newer Mac version adds come back out unchanged on save.
//
// Swift structs are values; these are classes. `Copy()` makes the deep copy that a Swift
// assignment would, and undo keeps one per change.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InnerVoice.Core;

/// A range of UTF-16 code units inside one paragraph's text. C# strings are UTF-16 like
/// NSString, so a span means the same thing in both apps.
public struct TextSpan : IEquatable<TextSpan>
{
    public int Location { get; set; }
    public int Length { get; set; }

    public TextSpan(int location, int length) { Location = location; Length = length; }

    [JsonIgnore] public readonly int End => Location + Length;

    public readonly bool Equals(TextSpan o) => Location == o.Location && Length == o.Length;
    public override readonly bool Equals(object? obj) => obj is TextSpan o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(Location, Length);
    public static bool operator ==(TextSpan a, TextSpan b) => a.Equals(b);
    public static bool operator !=(TextSpan a, TextSpan b) => !a.Equals(b);
    public override readonly string ToString() => $"{{{Location}, {Length}}}";
}

/// Swift's `Date` as JSON: seconds since 2001-01-01 00:00:00 UTC. Kept as the exact double
/// so a date survives a round trip bit for bit.
public readonly record struct AppleDate(double Seconds) : IComparable<AppleDate>
{
    public static readonly DateTime ReferenceUtc = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static AppleDate Now => FromDateTime(DateTime.UtcNow);
    public static AppleDate FromDateTime(DateTime d) => new((d.ToUniversalTime() - ReferenceUtc).TotalSeconds);
    public DateTime ToUtc() => ReferenceUtc.AddSeconds(Seconds);
    public DateTime ToLocal() => ToUtc().ToLocalTime();
    public int CompareTo(AppleDate other) => Seconds.CompareTo(other.Seconds);
}

/// Base for every model class: unknown JSON fields are kept and written back.
public abstract class ModelObject
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    protected Dictionary<string, JsonElement>? CopyExtra() => Extra is null ? null : new(Extra);
}

public enum ParagraphStyle { Body, Heading, SceneBreak, BlockQuote }

/// One block of text in a chapter.
public sealed class Paragraph : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public ParagraphStyle Style { get; set; } = ParagraphStyle.Body;
    /// Italic runs, used to find thoughts.
    public List<TextSpan> Italic { get; set; } = [];

    public Paragraph() { }
    public Paragraph(string text, ParagraphStyle style = ParagraphStyle.Body, List<TextSpan>? italic = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Text = text;
        Style = style;
        Italic = italic ?? [];
    }

    public string Substring(TextSpan range)
    {
        int start = Math.Clamp(range.Location, 0, Text.Length);
        int end = Math.Clamp(range.End, start, Text.Length);
        return Text[start..end];
    }

    [JsonIgnore] public int WordCount => Words.Count(Text);

    public Paragraph Copy() => new() { Id = Id, Text = Text, Style = Style, Italic = [.. Italic], Extra = CopyExtra() };
}

/// A section of the manuscript that starts at a heading.
public sealed class Chapter : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public List<Paragraph> Paragraphs { get; set; } = [];

    public Chapter() { }
    public Chapter(string title, List<Paragraph> paragraphs, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Title = title;
        Paragraphs = paragraphs;
    }

    /// "Previously…": a short recap of the story so far, heard at the chapter's start
    /// (Chapter Recaps). Optional, like `RecapEdited`, so older projects open unchanged.
    public string? Recap { get; set; }
    /// The narrator changed the recap: Write Chapter Recaps leaves it alone.
    public bool? RecapEdited { get; set; }

    [JsonIgnore] public int WordCount => Paragraphs.Sum(p => p.WordCount);

    /// The recap to show, if there is one.
    [JsonIgnore] public string? RecapText => string.IsNullOrWhiteSpace(Recap) ? null : Recap.Trim();

    public Chapter Copy() => new()
    {
        Id = Id, Title = Title, Paragraphs = Paragraphs.ConvertAll(p => p.Copy()), Recap = Recap, RecapEdited = RecapEdited, Extra = CopyExtra(),
    };
}

public enum LineKind { Spoken, Thought, Written }

/// Who decided a line's speaker. Lines the narrator assigned are never changed by the AI.
public enum AssignmentSource { None, Rule, Exchange, Ai, User }

public static class ModelTitles
{
    public static string Title(this LineKind k) => k switch
    {
        LineKind.Spoken => "Spoken",
        LineKind.Thought => "Thought",
        _ => "Written",
    };

    public static string Title(this AssignmentSource s) => s switch
    {
        AssignmentSource.None => "Not assigned",
        AssignmentSource.Rule => "Found from “said …”",
        AssignmentSource.Exchange => "Back-and-forth",
        AssignmentSource.Ai => "Assigned by the AI",
        _ => "Assigned by you",
    };

    public static string Title(this FactKind k) => k switch
    {
        FactKind.Age => "Age",
        FactKind.Gender => "Gender",
        FactKind.Accent => "Accent & Origin",
        FactKind.Voice => "Voice",
        FactKind.Appearance => "Appearance",
        FactKind.Personality => "Personality",
        FactKind.History => "History",
        FactKind.Relationships => "Relationships",
        _ => "Other",
    };

    public static string Title(this ColorStyle s) => s switch
    {
        ColorStyle.Highlight => "Highlight",
        ColorStyle.Outline => "Outline",
        _ => "Underline",
    };
}

/// One piece of text spoken, thought or written by a character. Tone and delivery-hint
/// fields (for AI voices: `tone`, `delivery`, `deliverySource`) aren't modeled here; they stay
/// in `Extra` and round-trip.
public sealed class Line : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParagraphID { get; set; }
    public TextSpan Range { get; set; }
    public LineKind Kind { get; set; } = LineKind.Spoken;
    public Guid? SpeakerID { get; set; }
    /// 0…1. Lines below the project's threshold show as Unsure.
    public double Confidence { get; set; }
    public AssignmentSource Source { get; set; } = AssignmentSource.None;
    /// A quote that carries on from the previous paragraph (no closing mark there).
    public bool ContinuesPrevious { get; set; }
    /// The AI model that tagged the line (for its accuracy in Activity).
    public string? AiModel { get; set; }

    public Line() { }
    public Line(Guid paragraphID, TextSpan range, LineKind kind = LineKind.Spoken, Guid? speakerID = null,
                double confidence = 0, AssignmentSource source = AssignmentSource.None, bool continuesPrevious = false)
    {
        ParagraphID = paragraphID;
        Range = range;
        Kind = kind;
        SpeakerID = speakerID;
        Confidence = confidence;
        Source = source;
        ContinuesPrevious = continuesPrevious;
    }

    [JsonIgnore] public bool IsConfirmed => Source == AssignmentSource.User;

    public Line Copy() => new()
    {
        Id = Id, ParagraphID = ParagraphID, Range = Range, Kind = Kind, SpeakerID = SpeakerID, Confidence = Confidence,
        Source = Source, ContinuesPrevious = ContinuesPrevious, AiModel = AiModel, Extra = CopyExtra(),
    };

    /// True when the parts the Windows app edits are the same.
    public bool SameAssignment(Line o) =>
        SpeakerID == o.SpeakerID && Source == o.Source && Confidence == o.Confidence && Kind == o.Kind && Range == o.Range;
}

public enum FactKind { Age, Gender, Accent, Voice, Appearance, Personality, History, Relationships, Other }

public static class FactKinds
{
    /// The facts a narrator needs before the first line come first.
    public static readonly FactKind[] NarratorOrder =
        [FactKind.Age, FactKind.Gender, FactKind.Accent, FactKind.Voice, FactKind.Personality, FactKind.Appearance,
         FactKind.History, FactKind.Relationships, FactKind.Other];
}

/// One item in a character's summary, with the paragraph it came from.
public sealed class Fact : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public FactKind Kind { get; set; }
    public string Text { get; set; } = "";
    public Guid? SourceParagraphID { get; set; }
    /// The words in the manuscript that support the fact.
    public string Quote { get; set; } = "";
    public bool FromAI { get; set; }
    /// The narrator changed or wrote it; the AI never replaces it.
    public bool Edited { get; set; }

    public Fact Copy() => new()
    {
        Id = Id, Kind = Kind, Text = Text, SourceParagraphID = SourceParagraphID, Quote = Quote, FromAI = FromAI,
        Edited = Edited, Extra = CopyExtra(),
    };
}

/// Someone in the book who has lines. Per-engine voices stay in `Extra`.
public sealed class BookCharacter : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    /// "#RRGGBB" from `CharacterPalette`.
    public string ColorHex { get; set; } = CharacterPalette.Swatches[0].Hex;
    /// true: picked by the narrator. false: Automatic. null (before Mac 0.2.5): Automatic
    /// while it's a palette color.
    public bool? ColorChosen { get; set; }
    public List<Fact> Facts { get; set; } = [];
    public string Notes { get; set; } = "";
    /// How the narrator plans to voice the character.
    public string VoiceNotes { get; set; } = "";
    public Guid? NarratorID { get; set; }
    /// null follows the automatic rule (main characters start hidden in the index).
    public bool? ShowInIndex { get; set; }
    /// Pinned to the top of every speaker list.
    public bool? Pinned { get; set; }
    /// The "From the book:" block Build Summary last wrote into `VoiceNotes`.
    public string? VoiceNotesFromBook { get; set; }
    /// The "Voice brief:" block the AI last wrote into `VoiceNotes`.
    public string? VoiceBriefGenerated { get; set; }
    /// Casting Style (docs/CASTING-STYLE.md): the narrator's choice of gender; null is what
    /// the book says.
    public CharacterGender? Gender { get; set; }
    /// Marked a main character (true) or not main (false); null: not marked.
    public bool? IsMain { get; set; }
    /// Who reads this character, chosen by the narrator; null: from the Casting Style.
    public VoiceRole? Role { get; set; }
    /// How the narrator shades this character, set by the narrator; null: automatic.
    public Shade? Shade { get; set; }

    public BookCharacter() { }
    public BookCharacter(string name, string colorHex) { Name = name; ColorHex = colorHex; }

    [JsonIgnore] public bool IsPinned => Pinned ?? false;

    /// InnerVoice keeps the color matched to the character.
    [JsonIgnore]
    public bool ColorIsAutomatic => ColorChosen == false || (ColorChosen is null && CharacterPalette.FamilyOf(ColorHex) is not null);

    /// True when a search typed in a speaker list finds this character: any of its names
    /// contains the text (ignoring case and accents), or the text starts its initials.
    public bool Matches(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return true;
        var ci = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        if (new[] { Name }.Concat(Aliases).Any(n => ci.IndexOf(n, q, opts) >= 0)) return true;
        return q.Length <= 3 && Initials.StartsWith(q, StringComparison.OrdinalIgnoreCase);
    }

    /// Every name the character goes by, longest first, for matching in text.
    [JsonIgnore]
    public List<string> AllNames
    {
        get
        {
            var names = new List<string> { Name };
            names.AddRange(Aliases);
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length > 1 && !Names.Titles.Contains(p)).ToList();
            if (parts.Count > 1) names.AddRange(parts);
            return names.Where(n => n.Length > 0).Distinct().OrderByDescending(n => n.Length).ToList();
        }
    }

    [JsonIgnore]
    public string Initials
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !Names.Titles.Contains(p)).Take(2).ToList();
            if (parts.Count == 0) return Name.Length <= 2 ? Name : Name[..2];
            return string.Concat(parts.Select(p => char.IsSurrogate(p[0]) && p.Length > 1 ? p[..2] : p[..1])).ToUpperInvariant();
        }
    }

    /// The first name, skipping titles: "Dr. Edda Voss" is Edda. A title on its own or
    /// with only a surname keeps the whole name.
    [JsonIgnore]
    public string FirstName
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Name;
            if (Names.Titles.Contains(parts[0])) return parts.Length > 2 ? parts[1] : Name;
            return parts[0];
        }
    }

    public BookCharacter Copy() => new()
    {
        Id = Id, Name = Name, Aliases = [.. Aliases], ColorHex = ColorHex, ColorChosen = ColorChosen,
        Facts = Facts.ConvertAll(f => f.Copy()), Notes = Notes, VoiceNotes = VoiceNotes, NarratorID = NarratorID,
        ShowInIndex = ShowInIndex, Pinned = Pinned, VoiceNotesFromBook = VoiceNotesFromBook,
        VoiceBriefGenerated = VoiceBriefGenerated, Gender = Gender, IsMain = IsMain, Role = Role, Shade = Shade?.Copy(),
        Extra = CopyExtra(),
    };
}

/// A person reading the audiobook.
public sealed class Narrator : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    /// Three Narrators: the part this person reads (narrator, men, women, other); null: one
    /// character at a time.
    public VoiceRole? Role { get; set; }

    public Narrator Copy() => new() { Id = Id, Name = Name, Notes = Notes, Role = Role, Extra = CopyExtra() };
}

/// A term and how to say it.
public sealed class Pronunciation : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Term { get; set; } = "";
    public string SayItAs { get; set; } = "";
    public string Notes { get; set; } = "";
    /// Checked with the author or rights holder.
    public bool Confirmed { get; set; }
    /// File name inside the project's Audio folder.
    public string? AudioFile { get; set; }
    public int Occurrences { get; set; }
    public Guid? FirstParagraphID { get; set; }
    /// Found automatically; hidden once the narrator says it needs no entry.
    public bool Dismissed { get; set; }
    public bool AddedByUser { get; set; }

    public Pronunciation Copy() => new()
    {
        Id = Id, Term = Term, SayItAs = SayItAs, Notes = Notes, Confirmed = Confirmed, AudioFile = AudioFile,
        Occurrences = Occurrences, FirstParagraphID = FirstParagraphID, Dismissed = Dismissed, AddedByUser = AddedByUser,
        Extra = CopyExtra(),
    };
}

/// A paragraph marked to read again.
public sealed class Retake : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParagraphID { get; set; }
    public string Note { get; set; } = "";
    public AppleDate Created { get; set; } = AppleDate.Now;
    public bool Done { get; set; }

    public Retake Copy() => new() { Id = Id, ParagraphID = ParagraphID, Note = Note, Created = Created, Done = Done, Extra = CopyExtra() };
}

public enum ColorStyle { Highlight, Outline, Underline }

/// What a project is for. The Windows app only has Narration Tools; it never changes this.
public enum ProjectPurpose { Narration, AiVoices }

public sealed class ProjectSettings : ModelObject
{
    public ColorStyle Style { get; set; } = ColorStyle.Highlight;
    /// Lines the AI is less sure of than this show as Unsure.
    public double UnsureBelow { get; set; } = 0.6;
    public bool ShowInitials { get; set; } = true;
    /// The point-of-view character in a first-person book ("I said").
    public Guid? PovCharacterID { get; set; }
    /// Color narration too (for a first-person narrator voice).
    public bool ColorNarration { get; set; }
    /// What the project is for. null until asked on the Mac.
    public ProjectPurpose? Purpose { get; set; }
    /// Steps (`GuidedStep` raw values) the narrator marked done or skipped.
    public List<string>? StepsDone { get; set; }
    /// Casting Style (docs/CASTING-STYLE.md). null: not chosen yet. (The AI voices for each
    /// shared role, `roleVoices`, stay in Extra for the Mac.)
    public CastingStyle? CastingStyle { get; set; }
    /// Three Narrators: who reads the narration; null is a narrator of its own.
    public VoiceRole? NarrationRole { get; set; }
    /// Full Cast: who reads characters not marked main; null is the narrator.
    public MinorVoices? MinorVoices { get; set; }
    /// How far characters move from the narrator's voice; null is Clear.
    public ShadeStrength? ShadeStrength { get; set; }
    /// Just Me: the narrator's own gender; null is not said.
    public CharacterGender? NarratorGender { get; set; }
    /// When the narrator allowed this book's text to be sent to Claude (Anthropic's cloud
    /// model) for the AI features. null: never sent.
    public AppleDate? CloudTextAllowed { get; set; }
    /// Show Claude's changes in a list to keep or undo after each Tag Speakers run.
    /// null: on (the default); false: keep them straight away like a local model's.
    [JsonPropertyName("reviewCloudAI")] public bool? ReviewCloudAI { get; set; }

    /// Claude's changes are listed for review unless the narrator turned it off.
    [JsonIgnore] public bool ReviewsCloudAI => ReviewCloudAI ?? true;

    public ProjectSettings Copy() => new()
    {
        Style = Style, UnsureBelow = UnsureBelow, ShowInitials = ShowInitials, PovCharacterID = PovCharacterID,
        ColorNarration = ColorNarration, Purpose = Purpose, StepsDone = StepsDone is null ? null : [.. StepsDone],
        CastingStyle = CastingStyle, NarrationRole = NarrationRole, MinorVoices = MinorVoices, ShadeStrength = ShadeStrength,
        NarratorGender = NarratorGender, CloudTextAllowed = CloudTextAllowed, ReviewCloudAI = ReviewCloudAI, Extra = CopyExtra(),
    };
}

public sealed class RemovedParagraph : ModelObject
{
    public string Text { get; set; } = "";
    public string ChapterTitle { get; set; } = "";

    public RemovedParagraph Copy() => new() { Text = Text, ChapterTitle = ChapterTitle, Extra = CopyExtra() };
}

/// What happened in a revision import, for the review list.
public sealed class RevisionRecord : ModelObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AppleDate Date { get; set; } = AppleDate.Now;
    public string FileName { get; set; } = "";
    public List<Guid> Added { get; set; } = [];
    public List<Guid> Changed { get; set; } = [];
    public List<RemovedParagraph> Removed { get; set; } = [];
    public int Unchanged { get; set; }
    /// Paragraphs the narrator has looked at in the review list.
    public HashSet<Guid> Reviewed { get; set; } = [];

    public RevisionRecord Copy() => new()
    {
        Id = Id, Date = Date, FileName = FileName, Added = [.. Added], Changed = [.. Changed],
        Removed = Removed.ConvertAll(r => r.Copy()), Unchanged = Unchanged, Reviewed = [.. Reviewed], Extra = CopyExtra(),
    };
}

/// One book: the manuscript, its lines and cast, and the narrator's notes.
public sealed class Project : ModelObject
{
    /// The newest project format this app understands.
    public const int CurrentFormat = 1;

    public int Format { get; set; } = 1;
    public string Title { get; set; } = "Untitled";
    public string Author { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public AppleDate ImportedAt { get; set; } = AppleDate.Now;
    public List<Chapter> Chapters { get; set; } = [];
    /// Every line, in book order.
    public List<Line> Lines { get; set; } = [];
    public List<BookCharacter> Characters { get; set; } = [];
    public List<Narrator> Narrators { get; set; } = [];
    /// Pronunciations, recorded marks and retakes from older versions and the Mac: kept in the
    /// file and carried through revisions, but nothing in the Windows app shows or edits them.
    public List<Pronunciation> Pronunciations { get; set; } = [];
    public HashSet<Guid> Recorded { get; set; } = [];
    public List<Retake> Retakes { get; set; } = [];
    public ProjectSettings Settings { get; set; } = new();
    public AppleDate? SummariesBuilt { get; set; }
    public AppleDate? LastTagged { get; set; }
    public List<RevisionRecord> Revisions { get; set; } = [];

    [JsonIgnore] public bool IsEmpty => Chapters.Count == 0;
    [JsonIgnore] public int WordCount => Chapters.Sum(c => c.WordCount);

    public BookCharacter? Character(Guid? id) => id is { } g ? Characters.Find(c => c.Id == g) : null;
    public int CharacterIndex(Guid id) => Characters.FindIndex(c => c.Id == id);

    /// A line needs the narrator when nobody is assigned or the AI was unsure.
    public bool NeedsYou(Line line) =>
        line.SpeakerID is null || (line.Source != AssignmentSource.User && line.Confidence < Settings.UnsureBelow);

    [JsonIgnore] public int LinesNeedingYou => Lines.Count(NeedsYou);

    /// Lines without any assignment yet (what Tag Speakers still has to do).
    [JsonIgnore] public int UntaggedCount => Lines.Count(l => l.Source == AssignmentSource.None);

    /// Shows the Narration Tools pages: always on Windows. A book set to AI voices on the
    /// Mac keeps that setting; the Windows app just doesn't show voices.
    [JsonIgnore] public bool IsAIVoicesProject => Settings.Purpose == ProjectPurpose.AiVoices;

    public Project Copy() => new()
    {
        Format = Format, Title = Title, Author = Author, SourceFileName = SourceFileName, ImportedAt = ImportedAt,
        Chapters = Chapters.ConvertAll(c => c.Copy()), Lines = Lines.ConvertAll(l => l.Copy()),
        Characters = Characters.ConvertAll(c => c.Copy()), Narrators = Narrators.ConvertAll(n => n.Copy()),
        Pronunciations = Pronunciations.ConvertAll(p => p.Copy()), Recorded = [.. Recorded],
        Retakes = Retakes.ConvertAll(r => r.Copy()), Settings = Settings.Copy(), SummariesBuilt = SummariesBuilt,
        LastTagged = LastTagged, Revisions = Revisions.ConvertAll(r => r.Copy()), Extra = CopyExtra(),
    };
}

/// Counting words the same way everywhere: runs of letters, marks and digits (Swift's
/// CharacterSet.alphanumerics) plus apostrophes.
public static class Words
{
    public static int Count(string text)
    {
        int n = 0;
        bool inWord = false;
        foreach (var r in text.EnumerateRunes())
        {
            bool letter = IsAlphanumeric(r) || r.Value == '’' || r.Value == '\'';
            if (letter && !inWord) n++;
            inWord = letter;
        }
        return n;
    }

    public static bool IsAlphanumeric(System.Text.Rune r) => System.Text.Rune.GetUnicodeCategory(r) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };
}

/// Name helpers shared by rules, prompts and initials.
public static class Names
{
    public static readonly HashSet<string> Titles =
    [
        "Mr.", "Mrs.", "Ms.", "Miss", "Mister", "Dr.", "Mr", "Mrs", "Ms", "Dr", "Lady", "Lord", "Sir", "Dame", "Captain", "Capt.",
        "Aunt", "Uncle", "Professor", "Prof.", "Father", "Mother", "Sister", "Brother", "King", "Queen", "Prince",
        "Princess", "Detective", "Inspector", "Sergeant", "Officer", "Agent", "Madam", "Madame", "Señor", "Señora",
        "General", "Colonel", "Major", "Lieutenant", "Admiral", "Doctor", "Granny", "Grandpa", "Grandma", "Saint",
    ];

    /// Capitalized words that start sentences or name things but are never speakers.
    public static readonly HashSet<string> NotNames =
    [
        "I", "He", "She", "They", "We", "You", "It", "The", "A", "An", "Then", "But", "And", "Or", "So", "Yes", "No",
        "Oh", "Well", "This", "That", "There", "Here", "What", "Who", "Why", "When", "Where", "How", "His", "Her",
        "Their", "Our", "My", "Your", "Its", "Someone", "Somebody", "Everyone", "Nobody", "Everybody", "Anyone",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "God", "Chapter", "Part",
        "Now", "Still", "Just", "After", "Before", "As", "If", "Once", "Even", "Another", "One", "Each", "Both",
        "Something", "Nothing", "All", "Some", "Most", "Not", "Again", "Finally", "Instead", "Later", "Soon",
    ];
}
