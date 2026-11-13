// Casting Style (docs/CASTING-STYLE.md), ported from the Mac's
// Sources/InnerVoiceKit/CastingStyle.swift: how a book's parts are shared. Just Me (the
// narrator reads everyone, shading each character), Three Narrators (narration, men, women,
// and an Other group), or Full Cast (a narrator per main character). The style only decides
// who reads each character: no assignment is lost when it changes. In a book with AI voices
// the same styles share voices (One Voice, Narrator + Two Voices, Full Cast); the rules give
// the same answers (golden-checked against the Mac's Fixtures/casting-style.json).

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InnerVoice.Core;

public enum CastingStyle { OneVoice, Shared, FullCast }

/// Who reads a character under the Casting Style.
public enum VoiceRole { Narrator, Men, Women, Other, Own }

/// A character's gender as the narrator chose it.
public enum CharacterGender { Male, Female, Other }

/// Full Cast: who reads characters the narrator didn't mark main.
public enum MinorVoices { Narrator, ByGender, Own }

/// How far characters move from the narrator's voice.
public enum ShadeStrength { Subtle, Clear, Strong }

public enum VoiceAge { Child, Young, Adult, Older }

public static class CastingStyleNames
{
    /// The Narration Tools name of each style.
    public static string Title(this CastingStyle s) => s switch
    {
        CastingStyle.OneVoice => "Just Me",
        CastingStyle.Shared => "Three Narrators",
        _ => "Full Cast",
    };

    /// The name in each project type: AI voices share voices, Narration Tools shares people.
    public static string Title(this CastingStyle s, ProjectPurpose? purpose) => (s, purpose) switch
    {
        (CastingStyle.OneVoice, ProjectPurpose.Narration) => "Just Me",
        (CastingStyle.OneVoice, _) => "One Voice",
        (CastingStyle.Shared, ProjectPurpose.Narration) => "Three Narrators",
        (CastingStyle.Shared, _) => "Narrator + Two Voices",
        _ => "Full Cast",
    };

    /// The voice's name in lists and menus: "Men’s Voice", "Other Voice".
    public static string VoiceTitle(this VoiceRole r) => r switch
    {
        VoiceRole.Narrator => "Narrator’s Voice",
        VoiceRole.Men => "Men’s Voice",
        VoiceRole.Women => "Women’s Voice",
        VoiceRole.Other => "Other Voice",
        _ => "Own Voice",
    };

    /// The same in a caption: "Men’s voice".
    public static string VoiceCaption(this VoiceRole r) => r switch
    {
        VoiceRole.Narrator => "Narrator’s voice",
        VoiceRole.Men => "Men’s voice",
        VoiceRole.Women => "Women’s voice",
        VoiceRole.Other => "Other voice",
        _ => "Own voice",
    };

    public static string Title(this VoiceRole r) => r switch
    {
        VoiceRole.Narrator => "Narrator",
        VoiceRole.Men => "Men",
        VoiceRole.Women => "Women",
        VoiceRole.Other => "Other",
        _ => "Own Voice",
    };

    /// Who reads under the role, in menus: "Men’s Narrator".
    public static string NarratorTitle(this VoiceRole r) => r switch
    {
        VoiceRole.Narrator => "The Narrator",
        VoiceRole.Men => "Men’s Narrator",
        VoiceRole.Women => "Women’s Narrator",
        VoiceRole.Other => "Other Narrator",
        _ => "A Narrator of Their Own",
    };

    public static string Title(this CharacterGender g) => g switch
    {
        CharacterGender.Male => "Male",
        CharacterGender.Female => "Female",
        _ => "Other",
    };

    public static string Title(this ShadeStrength s) => s switch
    {
        ShadeStrength.Subtle => "Subtle",
        ShadeStrength.Clear => "Clear",
        _ => "Strong",
    };

    /// Pitch steps for a character of another gender than the narrator.
    public static int Steps(this ShadeStrength s) => s switch { ShadeStrength.Subtle => 1, ShadeStrength.Clear => 2, _ => 3 };

    /// The shared role for a gender.
    public static VoiceRole Role(this CharacterGender g) => g switch
    {
        CharacterGender.Male => VoiceRole.Men,
        CharacterGender.Female => VoiceRole.Women,
        _ => VoiceRole.Other,
    };

    public static CharacterGender ToCharacterGender(this Gender g) => g switch
    {
        Gender.Male => CharacterGender.Male,
        Gender.Female => CharacterGender.Female,
        _ => CharacterGender.Other,
    };

    public static Gender ToGender(this CharacterGender g) => g switch
    {
        CharacterGender.Male => Gender.Male,
        CharacterGender.Female => Gender.Female,
        _ => Gender.Neutral,
    };
}

/// How one voice changes for a character: pitch and pace steps, plus a few words. For a
/// narrator it's a cue: "lower, slower, gravelly".
public sealed class Shade : ModelObject
{
    public const int MinPitch = -4, MaxPitch = 4, MinPace = -2, MaxPace = 2;

    public int Pitch { get; set; }
    public int Pace { get; set; }
    public string? Words { get; set; }

    /// Clamped, with empty words left out (as the Mac's Shade.init).
    public static Shade Make(int pitch = 0, int pace = 0, string? words = null)
    {
        var w = words?.Trim();
        return new Shade
        {
            Pitch = Math.Clamp(pitch, MinPitch, MaxPitch),
            Pace = Math.Clamp(pace, MinPace, MaxPace),
            Words = string.IsNullOrEmpty(w) ? null : w,
        };
    }

    public Shade Copy() => new() { Pitch = Pitch, Pace = Pace, Words = Words, Extra = CopyExtra() };

    [JsonIgnore]
    public bool IsNeutral => Pitch == 0 && Pace == 0 && Words is null;

    public bool SameAs(Shade? o) => o is not null && o.Pitch == Pitch && o.Pace == Pace && o.Words == Words;

    /// What a narrator reads beside the line; null: their own voice.
    [JsonIgnore]
    public string? Cue
    {
        get
        {
            var parts = new List<string>();
            if (Pitch <= -3) parts.Add("much lower");
            else if (Pitch < 0) parts.Add("lower");
            else if (Pitch >= 3) parts.Add("much higher");
            else if (Pitch > 0) parts.Add("higher");
            if (Pace < 0) parts.Add("slower"); else if (Pace > 0) parts.Add("quicker");
            if (Words is { } w) parts.Add(w);
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    /// ↓ or ↑ for the margin (doubled for 3 steps or more); null when the pitch stays.
    [JsonIgnore]
    public string? Arrow => Pitch switch { <= -3 => "↓↓", < 0 => "↓", >= 3 => "↑↑", > 0 => "↑", _ => null };
}

/// What the book says a character sounds like: the parts of the Mac's CastingProfile that
/// shading needs.
public sealed class ShadeProfile
{
    public Gender? Gender { get; set; }
    /// The gender rests on a single pronoun.
    public bool GenderGuessed { get; set; }
    public VoiceAge? Age { get; set; }
    public List<string> Timbre { get; set; } = [];
    /// From the voice brief: -1 low or slow, 0 neither, 1 high or quick.
    public int PitchLean { get; set; }
    public int PaceLean { get; set; }

    static readonly string[] TimbreWords =
    [
        "deep", "low", "gravelly", "rough", "husky", "hoarse", "raspy", "gruff", "soft", "high", "shrill",
        "booming", "warm", "bright", "thin", "smooth", "quiet", "loud", "lilting", "melodic", "nasal",
        "breathy", "rich", "reedy", "silky", "throaty", "sing-song", "clipped", "measured",
    ];

    /// The Mac's VoiceCasting.profile, for gender, age, timbre and the brief's leans.
    public static ShadeProfile Read(BookCharacter c, Project project, BookIndex index)
    {
        string FactText(params FactKind[] kinds) => string.Join(" ", c.Facts.Where(f => kinds.Contains(f.Kind)).Select(f => f.Text));
        var p = new ShadeProfile();
        if (c.Gender is { } chosen) p.Gender = chosen.ToGender();
        else if (GenderReader.GenderIn(FactText(FactKind.Gender)) is { } g1) p.Gender = g1;
        else if (GenderReader.TitleGender(c.Name) is { } g2) p.Gender = g2;
        else if (GenderReader.PronounGender(c.Id, project, index) is { } g3) { p.Gender = g3.Gender; p.GenderGuessed = !g3.Sure; }
        else if (GenderReader.GenderIn(c.VoiceNotes) is { } g4) p.Gender = g4;
        else p.Gender = GenderReader.GenderIn(FactText(FactKind.Relationships, FactKind.Appearance, FactKind.Age, FactKind.History, FactKind.Other));

        p.Age = AgeIn(FactText(FactKind.Age)) ?? AgeIn(FactText(FactKind.Appearance), strict: true) ?? AgeIn(c.VoiceNotes, strict: true);

        var w = GenderReader.WordsIn(FactText(FactKind.Voice) + " " + c.VoiceNotes).ToHashSet();
        p.Timbre = TimbreWords.Where(w.Contains).ToList();
        var (pitch, pace) = VoiceBrief.Read(c.VoiceNotes);
        p.PitchLean = Lean(pitch, ["low", "deep", "bass", "baritone", "lower"], ["high", "bright", "light", "higher", "squeaky", "thin"]);
        p.PaceLean = Lean(pace, ["slow", "deliberate", "measured", "unhurried", "drawling", "slower"], ["quick", "fast", "brisk", "rapid", "clipped", "hurried", "faster"]);
        return p;
    }

    static int Lean(string? text, string[] low, string[] high)
    {
        if (text is null) return 0;
        var w = GenderReader.WordsIn(text).ToHashSet();
        int l = low.Count(w.Contains), h = high.Count(w.Contains);
        return l > h ? -1 : h > l ? 1 : 0;
    }

    static readonly Regex Number = new(@"\b(\d{1,3})\b", RegexOptions.CultureInvariant);

    /// "About 60", "sixty-two", "in her forties", "elderly" (the Mac's VoiceCasting.age).
    public static VoiceAge? AgeIn(string text, bool strict = false)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Length == 0) return null;
        if (Number.Match(lower) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n > 0 && n < 120)
            return n < 13 ? VoiceAge.Child : n < 25 ? VoiceAge.Young : n < 55 ? VoiceAge.Adult : VoiceAge.Older;
        (string, VoiceAge)[] decades = [("teen", VoiceAge.Young), ("twent", VoiceAge.Young), ("thirt", VoiceAge.Adult), ("fort", VoiceAge.Adult),
            ("fift", VoiceAge.Older), ("sixt", VoiceAge.Older), ("sevent", VoiceAge.Older), ("eight", VoiceAge.Older), ("ninet", VoiceAge.Older)];
        var words = GenderReader.WordsIn(lower);
        foreach (var word in words)
        {
            if (!strict && word is "child" or "kid" or "little" or "toddler" or "baby" or "infant" or "boy" or "girl" or "small" && !words.Contains("old")) return VoiceAge.Child;
            if (word is "elderly" or "aged" or "grey" or "gray" or "white-haired" or "grandmother" or "grandfather" or "granny" or "grandpa" or "ancient" or "retired" or "widow" or "widower") return VoiceAge.Older;
            if (word is "teenage" or "teenager" or "adolescent" or "youth" or "young" or "student" or "lad" or "lass") return VoiceAge.Young;
            if (word is "middle-aged" or "middle") return VoiceAge.Adult;
            if (word == "old") return VoiceAge.Older;
            foreach (var (prefix, age) in decades)
                if (word.StartsWith(prefix, StringComparison.Ordinal) && (word.EndsWith("ies") || word.EndsWith('s') || (word.EndsWith('y') && word.Length <= 7))) return age;
        }
        return null;
    }
}

/// Who reads each character. Pure, like the Mac's CastingStyleRules.
public static class CastingStyleRules
{
    /// The roles one shared voice reads (they have a voice in `RoleVoices`).
    public static readonly VoiceRole[] SharedRoles = [VoiceRole.Men, VoiceRole.Women, VoiceRole.Other];

    /// The shared roles the style reads characters in, so each needs a voice: all three in
    /// Narrator + Two Voices, and in Full Cast when minor characters go by gender.
    public static IReadOnlyList<VoiceRole> SharedRolesNeeded(ProjectSettings s) =>
        s.CastingStyle == CastingStyle.Shared || (s.CastingStyle == CastingStyle.FullCast && s.MinorVoices == MinorVoices.ByGender)
            ? SharedRoles : [];

    /// Which role reads a character. `gender`: the narrator's choice, else the book's; null
    /// if unknown. `anyMainMarked`: whether anyone is marked main (Full Cast).
    public static VoiceRole Role(BookCharacter c, ProjectSettings s, CharacterGender? gender, bool anyMainMarked)
    {
        if (s.CastingStyle is not { } style) return VoiceRole.Own;  // not chosen yet: as before
        switch (style)
        {
            case CastingStyle.OneVoice:
                return c.Role == VoiceRole.Own ? VoiceRole.Own : VoiceRole.Narrator;
            case CastingStyle.Shared:
                return c.Role ?? (gender ?? CharacterGender.Other).Role();
            default:
                if (c.Role is { } r) return r;
                if (c.IsMain == true || !anyMainMarked) return VoiceRole.Own;
                return (s.MinorVoices ?? MinorVoices.Narrator) switch
                {
                    MinorVoices.Narrator => VoiceRole.Narrator,
                    MinorVoices.ByGender => (gender ?? CharacterGender.Other).Role(),
                    _ => VoiceRole.Own,
                };
        }
    }

    public static bool AnyMainMarked(Project p) => p.Characters.Any(c => c.IsMain == true);

    /// The shared roles the style sorts by gender into.
    public static bool UsesGender(ProjectSettings s) =>
        s.CastingStyle == CastingStyle.Shared || (s.CastingStyle == CastingStyle.FullCast && s.MinorVoices == MinorVoices.ByGender);

    /// In Three Narrators, Narrator means whoever reads the narration (a two-person book
    /// gives it to the men's or women's narrator).
    public static VoiceRole Reader(VoiceRole role, ProjectSettings s)
    {
        if (role != VoiceRole.Narrator || s.CastingStyle != CastingStyle.Shared || s.NarrationRole is not { } n || n == VoiceRole.Own) return role;
        return n;
    }

    public static CharacterGender? GenderOf(BookCharacter c, ShadeProfile p) => c.Gender ?? p.Gender?.ToCharacterGender();

    /// The book doesn't settle the gender and the narrator hasn't chosen.
    public static bool NeedsGenderReview(BookCharacter c, ShadeProfile p) => c.Gender is null && (p.Gender is null || p.GenderGuessed);

    /// Characters with lines whose gender needs the narrator, most lines first.
    public static List<Guid> GenderToReview(Project project, BookIndex index) => project.Characters
        .Where(c => (index.StatsFor(c.Id)?.Lines ?? 0) > 0)
        .Where(c => NeedsGenderReview(c, ShadeProfile.Read(c, project, index)))
        .OrderByDescending(c => index.StatsFor(c.Id)?.Lines ?? 0)
        .Select(c => c.Id).ToList();

    /// Characters to suggest marking main (over 15% of the lines), leaving out anyone marked.
    public static List<Guid> SuggestedMain(Project project, BookIndex index) => project.Characters
        .Where(c => c.IsMain is null && (index.StatsFor(c.Id)?.Lines ?? 0) > 0 && !index.ShowsInIndexByDefault(c.Id))
        .OrderByDescending(c => index.StatsFor(c.Id)?.Lines ?? 0)
        .Select(c => c.Id).ToList();

    /// Who reads each character (after `Reader`). Genders are read only when the style needs them.
    public static Dictionary<Guid, VoiceRole> Readers(Project project, BookIndex index)
    {
        bool usesGender = UsesGender(project.Settings);
        bool anyMain = AnyMainMarked(project);
        var output = new Dictionary<Guid, VoiceRole>();
        foreach (var c in project.Characters)
        {
            var g = c.Gender ?? (usesGender ? ShadeProfile.Read(c, project, index).Gender?.ToCharacterGender() : null);
            output[c.Id] = Reader(Role(c, project.Settings, g, anyMain), project.Settings);
        }
        return output;
    }

    /// The narrator's shade for a character (their own, else from gender, age and brief).
    public static Shade ShadeFor(BookCharacter c, ShadeProfile p, CharacterGender? gender, ShadeStrength? strength, CharacterGender? narratorGender)
    {
        if (c.Shade is { } own) return own;
        int steps = (strength ?? ShadeStrength.Clear).Steps(), pitch = 0, pace = 0;
        if (gender == CharacterGender.Male && narratorGender != CharacterGender.Male) pitch -= steps;
        else if (gender == CharacterGender.Female && narratorGender != CharacterGender.Female) pitch += steps;
        if (p.Age == VoiceAge.Child) { pitch += 2; pace += 1; }
        else if (p.Age == VoiceAge.Older) { pitch -= 1; pace -= 1; }
        pitch += p.PitchLean;
        pace += p.PaceLean;
        HashSet<string> pitchWords = ["low", "lower", "deep", "deeper", "high", "higher", "bass", "baritone"];
        var texture = p.Timbre.Where(t => !pitchWords.Contains(t.ToLowerInvariant())).Take(2).ToList();
        return Shade.Make(pitch, pace, texture.Count == 0 ? null : string.Join(", ", texture));
    }

    /// Shades for every character the narrator reads; neutral ones left out.
    public static Dictionary<Guid, Shade> Shades(Project project, BookIndex index, CharacterGender? narratorGender)
    {
        var output = new Dictionary<Guid, Shade>();
        if (project.Settings.CastingStyle is null) return output;
        foreach (var (id, r) in Readers(project, index))
        {
            if (r != VoiceRole.Narrator || project.Character(id) is not { } c) continue;
            var p = ShadeProfile.Read(c, project, index);
            var s = ShadeFor(c, p, GenderOf(c, p), project.Settings.ShadeStrength, narratorGender);
            if (!s.IsNeutral) output[id] = s;
        }
        return output;
    }

    /// The person who reads the narration: whose part it is, else the only narrator.
    public static Narrator? NarrationNarrator(Project project)
    {
        var reads = Reader(VoiceRole.Narrator, project.Settings);
        return project.Narrators.FirstOrDefault(n => n.Role == reads) ?? project.Narrators.FirstOrDefault(n => n.Role == VoiceRole.Narrator)
            ?? (project.Narrators.Count == 1 ? project.Narrators[0] : null);
    }

    /// The person who reads a character: their own assignment, else their part's narrator,
    /// else the narration narrator. Own Voice needs an assignment.
    public static Narrator? NarratorFor(BookCharacter c, Project project, CharacterGender? gender)
    {
        if (c.NarratorID is { } id) return project.Narrators.FirstOrDefault(n => n.Id == id);
        var r = Role(c, project.Settings, gender, AnyMainMarked(project));
        if (r == VoiceRole.Own) return null;
        var reads = Reader(r, project.Settings);
        return project.Narrators.FirstOrDefault(n => n.Role == reads) ?? NarrationNarrator(project);
    }
}
