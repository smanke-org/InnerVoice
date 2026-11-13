// Ported from the Mac's Sources/InnerVoiceKit/Voices.swift: tones, voice engines, the voice
// a character has on each engine, and casting (a voice for each character from what the
// book says about them). Raw values and JSON fields match the Mac's exactly, so a book's
// voices round-trip both ways. The `apple` engine is the system's own voices: Apple's on the
// Mac, Windows' built-in voices here ("Windows Voices" in the UI); the raw value stays
// `apple` so a book cast on one computer keeps its casting on the other.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InnerVoice.Core;

/// How a line is said. Raw values match the Mac's (`neutral`, `whispered`, …).
public enum Tone { Neutral, Excited, Confused, Sad, Angry, Whispered, Scared, Tender, Sarcastic }

/// Who decided a line's tone or delivery hint. The narrator's is never changed by the rules or the AI.
public enum ToneSource { Rule, Ai, User }

/// A sound a speech tag asks for ("she sighed"). Orpheus makes these; ElevenLabs takes them
/// as audio tags; other engines leave them out.
public enum VocalSound { Laugh, Chuckle, Sigh, Gasp, Groan, Sniffle, Yawn, Cough }

/// A text-to-speech engine. Raw values match the Mac's `VoiceEngine`.
public enum VoiceEngine
{
    /// The system's voices: Apple's on the Mac, Windows' built-in voices here.
    Apple,
    Qwen,
    Orpheus,
    /// ElevenLabs, in the cloud: the only engine that sends text off the computer. Off until
    /// the narrator sets it up, and each book must allow it.
    Elevenlabs,
    /// Not an engine itself: each character can have a voice from any engine that's on. A
    /// voice's own `VoiceChoice.Engine` is always a real one.
    Mixed,
}

/// Who chose a voice.
public enum VoiceSource
{
    /// Picked by casting from the character's summary.
    Auto,
    /// Chosen or confirmed by the narrator; casting never changes it.
    User,
}

public static class Tones
{
    public static readonly Tone[] All = Enum.GetValues<Tone>();

    public static string Raw(this Tone t) => t.ToString().ToLowerInvariant();

    public static string Title(this Tone t) => t.ToString();

    /// 1…9: the key that picks the tone in Review.
    public static int Key(this Tone t) => (int)t + 1;

    /// A one-character mark for compact lists (the Mac shows an SF Symbol).
    public static string Glyph(this Tone t) => t switch
    {
        Tone.Neutral => "–",
        Tone.Excited => "!",
        Tone.Confused => "?",
        Tone.Sad => "☂",
        Tone.Angry => "♨",
        Tone.Whispered => "…",
        Tone.Scared => "⚡",
        Tone.Tender => "♡",
        _ => "≈",
    };

    /// The tone as an ElevenLabs eleven_v4 audio tag; none for neutral.
    public static string? AudioTag(this Tone t) => t switch
    {
        Tone.Neutral => null,
        Tone.Whispered => "[whispers]",
        Tone.Tender => "[tenderly]",
        _ => $"[{t.Raw()}]",
    };

    /// How a speech engine that takes directions is told to say it.
    public static string Direction(this Tone t) => t switch
    {
        Tone.Neutral => "in a natural, even tone",
        Tone.Excited => "excited and full of energy",
        Tone.Confused => "confused and unsure, puzzling it out",
        Tone.Sad => "sad and subdued, close to tears",
        Tone.Angry => "angry and sharp",
        Tone.Whispered => "in a quiet whisper",
        Tone.Scared => "frightened, voice shaking",
        Tone.Tender => "tender and warm, speaking gently",
        _ => "dry and sarcastic",
    };

    public static Tone? Parse(string? raw) =>
        Enum.TryParse<Tone>(raw, ignoreCase: true, out var t) && Enum.IsDefined(t) && !int.TryParse(raw, out _) ? t : null;

    /// The line's tone; null: none set yet.
    public static Tone? ToneOf(Line line) => line.Tone;

    public static string Raw(this VocalSound s) => s.ToString().ToLowerInvariant();

    /// ElevenLabs' audio tag for the sound ("[sighs]").
    public static string AudioTag(this VocalSound s) => $"[{s.Raw()}s]";
}

public static class VoiceEngines
{
    /// The engines that make sound (everything but Mixed).
    public static readonly VoiceEngine[] Engines = [VoiceEngine.Apple, VoiceEngine.Qwen, VoiceEngine.Orpheus, VoiceEngine.Elevenlabs];

    /// The raw value stored in the file ("apple", "qwen", "orpheus", "elevenlabs", "mixed").
    public static string Raw(this VoiceEngine e) => e.ToString().ToLowerInvariant();

    public static VoiceEngine? Parse(string? raw) =>
        Enum.TryParse<VoiceEngine>(raw, ignoreCase: true, out var e) && Enum.IsDefined(e) && !int.TryParse(raw, out _) ? e : null;

    /// Sends the book's text to a company's servers to make the audio.
    public static bool IsCloud(this VoiceEngine e) => e == VoiceEngine.Elevenlabs;

    /// "Windows Voices" for the system engine (the Mac says "Apple Voices").
    public static string Title(this VoiceEngine e) => e switch
    {
        VoiceEngine.Apple => "Windows Voices",
        VoiceEngine.Qwen => "Qwen3-TTS",
        VoiceEngine.Orpheus => "Orpheus",
        VoiceEngine.Elevenlabs => "ElevenLabs",
        _ => "Mix Engines",
    };

    /// Whether the engine acts out a line's tone (the system's voices can't).
    public static bool ActsTones(this VoiceEngine e) => e != VoiceEngine.Apple;
}

public static class VoiceGenders
{
    public static string Title(this Gender g) => g switch { Gender.Female => "Female", Gender.Male => "Male", _ => "Neutral" };

    public static string Title(this VoiceAge a) => a.ToString();
}

/// A voice on one engine, with the narrator's adjustments.
public sealed class VoiceChoice : ModelObject, IEquatable<VoiceChoice>
{
    public VoiceEngine Engine { get; set; }
    /// Windows/Apple: the voice identifier. Orpheus: the voice name. Qwen3-TTS: "design",
    /// "preset:…" or "clone:…". ElevenLabs: its voice ID.
    public string VoiceID { get; set; } = "";
    /// Qwen3-TTS designs a voice from this description ("A woman in her sixties…").
    public string? Design { get; set; }
    /// Speed and pitch, 1 is the voice's own.
    public double? Rate { get; set; }
    public double? Pitch { get; set; }

    /// "Same as Narrator": the character speaks in whatever voice the narrator has.
    public const string NarratorID = "narrator";
    [JsonIgnore] public bool IsNarrator => VoiceID == NarratorID;

    public VoiceChoice() { }
    public VoiceChoice(VoiceEngine engine, string voiceID, string? design = null, double? rate = null, double? pitch = null)
    {
        Engine = engine;
        VoiceID = voiceID;
        Design = design;
        Rate = rate;
        Pitch = pitch;
    }

    public VoiceChoice Copy() => new(Engine, VoiceID, Design, Rate, Pitch) { Extra = CopyExtra() };

    public bool Equals(VoiceChoice? o) =>
        o is not null && Engine == o.Engine && VoiceID == o.VoiceID && Design == o.Design && Rate == o.Rate && Pitch == o.Pitch;
    public override bool Equals(object? obj) => Equals(obj as VoiceChoice);
    public override int GetHashCode() => HashCode.Combine(Engine, VoiceID, Design, Rate, Pitch);
    public static bool operator ==(VoiceChoice? a, VoiceChoice? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(VoiceChoice? a, VoiceChoice? b) => !(a == b);
}

/// A character's (or the narrator's) voice on one engine.
public sealed class CastVoice : ModelObject, IEquatable<CastVoice>
{
    public VoiceChoice Choice { get; set; } = new();
    public double Confidence { get; set; }
    public VoiceSource Source { get; set; }

    public CastVoice() { }
    public CastVoice(VoiceChoice choice, double confidence, VoiceSource source)
    {
        Choice = choice;
        Confidence = confidence;
        Source = source;
    }

    public CastVoice Copy() => new(Choice.Copy(), Confidence, Source) { Extra = CopyExtra() };

    public bool Equals(CastVoice? o) => o is not null && Choice == o.Choice && Confidence == o.Confidence && Source == o.Source;
    public override bool Equals(object? obj) => Equals(obj as CastVoice);
    public override int GetHashCode() => HashCode.Combine(Choice, Confidence, Source);

    internal static Dictionary<string, CastVoice>? CopyMap(Dictionary<string, CastVoice>? m) =>
        m?.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
}

/// One voice an engine offers.
public sealed record VoiceInfo(
    VoiceEngine Engine,
    /// What `VoiceChoice.VoiceID` stores.
    string VoiceID,
    string Name,
    Gender Gender,
    VoiceAge Age = VoiceAge.Adult,
    /// "American", "British", "Scottish"…
    string Accent = "English",
    /// Higher sounds better: the system's are 0 classic, 1 standard, 2 natural.
    int Quality = 0,
    /// A short description for the voice list ("Bright, conversational").
    string Detail = "",
    /// Qwen3-TTS: the description the voice is designed from.
    string? Design = null)
{
    public string Id => Engine.Raw() + ":" + VoiceID;

    /// What choosing this voice stores.
    public VoiceChoice Choice => new(Engine, VoiceID, Design);

    /// Orpheus's eight voices (its model card's names; all young adult, American).
    public static readonly IReadOnlyList<VoiceInfo> Orpheus = new (string Id, Gender G, string Detail)[]
    {
        ("tara", Gender.Female, "Warm, conversational"), ("leah", Gender.Female, "Bright, friendly"),
        ("jess", Gender.Female, "Lively, expressive"), ("mia", Gender.Female, "Soft, light"), ("zoe", Gender.Female, "Clear, youthful"),
        ("leo", Gender.Male, "Steady, relaxed"), ("dan", Gender.Male, "Deep, easygoing"), ("zac", Gender.Male, "Energetic, youthful"),
    }.Select(v => new VoiceInfo(VoiceEngine.Orpheus, v.Id, char.ToUpperInvariant(v.Id[0]) + v.Id[1..], v.G, VoiceAge.Young, "American", 2, v.Detail)).ToList();

    /// Ready-made Qwen3-TTS voices: a description each, for characters the book says little
    /// about and for choosing from a list. Any other description works too.
    public static readonly IReadOnlyList<VoiceInfo> QwenPresets = new (string Id, string Name, Gender G, VoiceAge A, string Accent, string Detail, string Design)[]
    {
        ("narrator", "Audiobook Narrator", Gender.Neutral, VoiceAge.Adult, "American", "Calm, clear, measured", "A calm, clear audiobook narrator with a warm, measured voice."),
        ("storyteller", "Warm Storyteller", Gender.Female, VoiceAge.Adult, "American", "Warm, low, unhurried", "A woman in her forties with a warm, low, unhurried voice."),
        ("bright", "Bright Young Woman", Gender.Female, VoiceAge.Young, "American", "Bright, clear, lively", "A young woman with a bright, clear, lively voice."),
        ("soft", "Soft-Spoken", Gender.Female, VoiceAge.Young, "American", "Soft, gentle, breathy", "A young woman with a soft, gentle, slightly breathy voice."),
        ("brisk", "Brisk Professional", Gender.Female, VoiceAge.Adult, "American", "Crisp, confident", "A woman in her thirties with a crisp, confident, brisk voice."),
        ("dame", "Grand Dame", Gender.Female, VoiceAge.Older, "British", "Rich, commanding, cultured", "An older woman with a rich, commanding, cultured voice, speaking English with a British accent."),
        ("grandmother", "Kind Grandmother", Gender.Female, VoiceAge.Older, "American", "Soft, kind, a little creaky", "An older woman with a soft, kind, slightly creaky voice."),
        ("lass", "Highland Lass", Gender.Female, VoiceAge.Young, "Scottish", "Light, musical", "A young woman with a light, musical voice, speaking English with a Scottish accent."),
        ("girl", "Little Girl", Gender.Female, VoiceAge.Child, "American", "High, eager", "A young girl with a high, eager voice."),
        ("baritone", "Easy Baritone", Gender.Male, VoiceAge.Adult, "American", "Warm, easygoing", "A man in his forties with a warm, easygoing baritone voice."),
        ("hero", "Young Hero", Gender.Male, VoiceAge.Young, "American", "Earnest, energetic", "A young man with an earnest, energetic, clear voice."),
        ("gentleman", "Gentleman", Gender.Male, VoiceAge.Adult, "British", "Refined, measured", "A man with a refined, measured voice, speaking English with a British accent."),
        ("elder", "Gravelly Elder", Gender.Male, VoiceAge.Older, "American", "Deep, gravelly", "An older man with a deep, gravelly voice."),
        ("sailor", "Weathered Sailor", Gender.Male, VoiceAge.Older, "Irish", "Rough, booming", "A man in his fifties with a rough, weathered, booming voice, speaking English with an Irish accent."),
        ("clerk", "Nervous Clerk", Gender.Male, VoiceAge.Young, "American", "Thin, quick, nervous", "A young man with a thin, quick, nervous voice."),
        ("boy", "Little Boy", Gender.Male, VoiceAge.Child, "American", "High, cheerful", "A young boy with a high, cheerful voice."),
    }.Select(v => new VoiceInfo(VoiceEngine.Qwen, "preset:" + v.Id, v.Name, v.G, v.A, v.Accent, 2, v.Detail, v.Design)).ToList();
}

/// What the book says a character should sound like, read from the summary and the lines.
public sealed class CastingProfile
{
    public Gender? Gender { get; set; }
    /// The gender rests on a single pronoun.
    public bool GenderGuessed { get; set; }
    public VoiceAge? Age { get; set; }
    public string? Accent { get; set; }
    /// Words about the voice itself: "deep", "gravelly", "soft"…
    public List<string> Timbre { get; set; } = [];
    /// From the voice brief: -1 low or slow, 0 neither, 1 high or quick.
    public int PitchLean { get; set; }
    public int PaceLean { get; set; }
    public int Lines { get; set; }
    /// Where each piece came from, for the Voices page ("Gender: the summary").
    public List<string> Evidence { get; set; } = [];

    /// How much casting has to go on: 0 nothing, up to 1.
    public double Completeness
    {
        get
        {
            double n = 0;
            if (Gender is not null) n += 0.5;
            if (Age is not null) n += 0.25;
            if (Accent is not null || Timbre.Count > 0) n += 0.25;
            return n;
        }
    }
}

/// Picks a voice for each character from what the book says about them: gender, age and
/// accent from the summary (or, failing that, titles and "she said" tags), main characters
/// first so they get the most fitting and most distinct voices.
public static class VoiceCasting
{
    static readonly (string Accent, string[] Words)[] Accents =
    [
        ("Scottish", ["scottish", "scots", "glaswegian", "highland", "edinburgh", "glasgow", "aberdeen", "brogue"]),
        ("Irish", ["irish", "dublin", "belfast", "cork"]),
        ("Welsh", ["welsh", "cardiff"]),
        ("British", ["british", "english", "london", "cockney", "yorkshire", "cornish", "cornwall", "oxford", "posh",
                     "received pronunciation", "liverpool", "scouse", "manchester", "devon"]),
        ("American", ["american", "southern", "texas", "texan", "boston", "new york", "brooklyn", "midwest", "california", "drawl"]),
        ("Australian", ["australian", "aussie", "sydney", "melbourne"]),
        ("New Zealand", ["new zealand", "kiwi"]),
        ("Canadian", ["canadian", "toronto"]),
        ("Indian", ["indian", "delhi", "mumbai", "bombay"]),
        ("South African", ["south african", "johannesburg", "cape town"]),
        ("French", ["french", "paris", "parisian"]),
        ("German", ["german", "berlin", "bavarian"]),
        ("Italian", ["italian", "rome", "naples", "sicilian"]),
        ("Spanish", ["spanish", "madrid", "castilian"]),
        ("Russian", ["russian", "moscow"]),
    ];

    static readonly string[] TimbreWords =
    [
        "deep", "low", "gravelly", "rough", "husky", "hoarse", "raspy", "gruff", "soft", "high", "shrill",
        "booming", "warm", "bright", "thin", "smooth", "quiet", "loud", "lilting", "melodic", "nasal",
        "breathy", "rich", "reedy", "silky", "throaty", "sing-song", "clipped", "measured",
    ];

    // MARK: Profile

    public static CastingProfile Profile(BookCharacter character, Project project, BookIndex index)
    {
        var p = new CastingProfile { Lines = index.StatsFor(character.Id)?.Lines ?? 0 };
        string FactText(params FactKind[] kinds) => string.Join(" ", character.Facts.Where(f => kinds.Contains(f.Kind)).Select(f => f.Text));

        // Gender: the narrator's choice, then the summary, then a title, then "she said"
        // after their lines, then other facts (relationships often name other people, so
        // they come last).
        var first = character.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (character.Gender is { } chosen)
        {
            p.Gender = chosen.ToGender();
            p.Evidence.Add("Gender: you chose");
        }
        else if (GenderReader.GenderIn(FactText(FactKind.Gender)) is { } g1)
        {
            p.Gender = g1;
            p.Evidence.Add("Gender: the summary");
        }
        else if (first is not null && GenderReader.TitleGender(character.Name) is { } g2)
        {
            p.Gender = g2;
            p.Evidence.Add($"Gender: “{first}”");
        }
        else if (GenderReader.PronounGender(character.Id, project, index) is { } g3)
        {
            p.Gender = g3.Gender;
            p.GenderGuessed = !g3.Sure;
            p.Evidence.Add(g3.Sure
                ? $"Gender: “{(g3.Gender == Core.Gender.Female ? "she" : "he")}” in the book"
                : $"Gender: a guess from one “{(g3.Gender == Core.Gender.Female ? "her" : "his")}”");
        }
        else if (GenderReader.GenderIn(character.VoiceNotes) is { } g4)
        {
            p.Gender = g4;
            p.Evidence.Add("Gender: your voice notes");
        }
        else if (GenderReader.GenderIn(FactText(FactKind.Relationships, FactKind.Appearance, FactKind.Age, FactKind.History, FactKind.Other)) is { } g5)
        {
            p.Gender = g5;
            p.Evidence.Add("Gender: the summary");
        }

        // Age: a number, or words like "elderly" or "teenage".
        if ((ShadeProfile.AgeIn(FactText(FactKind.Age)) ?? ShadeProfile.AgeIn(FactText(FactKind.Appearance), strict: true)) is { } a)
        {
            p.Age = a;
            p.Evidence.Add("Age: the summary");
        }
        else if (ShadeProfile.AgeIn(character.VoiceNotes, strict: true) is { } a2)
        {
            p.Age = a2;
            p.Evidence.Add("Age: your voice notes");
        }

        var voiceText = (FactText(FactKind.Accent, FactKind.Voice) + " " + character.VoiceNotes).ToLowerInvariant();
        foreach (var (accent, words) in Accents)
        {
            if (!words.Any(w => voiceText.Contains(w, StringComparison.Ordinal))) continue;
            p.Accent = accent;
            p.Evidence.Add($"Accent: {accent}");
            break;
        }
        var w = GenderReader.WordsIn(FactText(FactKind.Voice) + " " + character.VoiceNotes).ToHashSet();
        p.Timbre = TimbreWords.Where(w.Contains).ToList();
        // The voice brief's pitch and pace (the AI's, or the narrator's edit of it).
        var (pitch, pace) = VoiceBrief.Read(character.VoiceNotes);
        p.PitchLean = Lean(pitch, ["low", "deep", "bass", "baritone", "lower"], ["high", "bright", "light", "higher", "squeaky", "thin"]);
        p.PaceLean = Lean(pace, ["slow", "deliberate", "measured", "unhurried", "drawling", "slower"], ["quick", "fast", "brisk", "rapid", "clipped", "hurried", "faster"]);
        if (p.PitchLean != 0 || p.PaceLean != 0) p.Evidence.Add("Pitch and pace: the voice brief");
        return p;
    }

    static int Lean(string? text, string[] low, string[] high)
    {
        if (text is null) return 0;
        var w = GenderReader.WordsIn(text).ToHashSet();
        int l = low.Count(w.Contains), h = high.Count(w.Contains);
        return l > h ? -1 : h > l ? 1 : 0;
    }

    /// "Mrs. Penrose" is a woman and "Sir Alec" a man, before the book says anything else.
    public static Gender? TitleGender(string name) => GenderReader.TitleGender(name);

    /// The gender a voice description speaks of ("A woman in her forties…"), for a designed
    /// voice with no catalog entry.
    public static Gender? GenderOfDescription(string text) => GenderReader.GenderIn(text);

    // MARK: Matching

    static int Rank(VoiceAge a) => (int)a;

    /// How well a voice fits a profile; below 0 it doesn't fit at all (wrong gender).
    public static double Score(VoiceInfo v, CastingProfile p)
    {
        double s = 0.12 * v.Quality;
        if (p.Gender is { } g)
        {
            if (v.Gender == g) s += 0.5;
            else if (v.Gender == Gender.Neutral) s += 0.15;
            else return -1;
        }
        if (p.Age is { } a)
        {
            int gap = Math.Abs(Rank(a) - Rank(v.Age));
            s += gap == 0 ? 0.2 : gap == 1 ? 0.08 : -0.1;
        }
        if (p.Accent is { } accent)
        {
            if (v.Accent == accent) s += 0.25;
            else if (accent is "Scottish" or "Irish" or "Welsh" && v.Accent == "British") s += 0.1;
        }
        return s;
    }

    /// A description Qwen3-TTS designs a voice from.
    public static string Describe(CastingProfile p, string name)
    {
        string who = (p.Gender, p.Age) switch
        {
            (Gender.Female, VoiceAge.Child) => "A young girl",
            (Gender.Male, VoiceAge.Child) => "A young boy",
            (Gender.Female, VoiceAge.Young) => "A young woman",
            (Gender.Male, VoiceAge.Young) => "A young man",
            (Gender.Female, VoiceAge.Older) => "An older woman",
            (Gender.Male, VoiceAge.Older) => "An older man",
            (Gender.Female, _) => "A woman",
            (Gender.Male, _) => "A man",
            (_, VoiceAge.Child) => "A child",
            (_, VoiceAge.Older) => "An older adult",
            _ => "An adult",
        };
        var parts = new List<string> { who };
        // Something about the voice itself, so Qwen3-TTS doesn't make a thin one.
        string? pitch = p.PitchLean < 0 ? "low-pitched" : p.PitchLean > 0 ? "high-pitched" : null;
        // Timbre words that only repeat the pitch ("low" after "low-pitched") are left out.
        var timbre = p.Timbre.Where(t => pitch is null || !new[] { "low", "deep", "high", "higher", "lower" }.Contains(t.ToLowerInvariant())).ToList();
        var quality = new List<string>();
        if (pitch is not null) quality.Add(pitch);
        if (timbre.Count == 0 && pitch is null) quality.Add("clear, natural");
        else quality.AddRange(timbre.Take(3));
        parts.Add($"with a {string.Join(", ", quality)} voice");
        if (p.PaceLean != 0) parts.Add(p.PaceLean < 0 ? "speaking slowly" : "speaking quickly");
        if (p.Accent is { } ac) parts.Add($"speaking English with {("AEIOU".Contains(ac[0]) ? "an" : "a")} {ac} accent");
        return string.Join(" ", parts) + ".";
    }

    /// How sure casting is of a voice: how much the book says, less when the voice is
    /// shared with someone in the same chapters.
    public static double Confidence(CastingProfile p, double sharedOverlap)
    {
        double b = p.Gender is null ? 0.35 : p.GenderGuessed ? 0.5 : 0.6 + 0.3 * (p.Completeness - 0.5) / 0.5;
        return Math.Max(0.2, Math.Min(0.95, b - 0.35 * sharedOverlap));
    }

    /// The best voices for one character, best first, with a fit score.
    public static List<(VoiceInfo Voice, double Score)> Suggestions(BookCharacter character, Project project, BookIndex index,
                                                                  IReadOnlyList<VoiceInfo> voices, int limit = 3)
    {
        var p = Profile(character, project, index);
        var used = UsedVoices(project, voices.Count > 0 ? voices[0].Engine : project.Settings.Engine, character.Id);
        var myChapters = index.StatsFor(character.Id)?.Chapters ?? [];
        return voices.Select(v =>
            {
                double s = Score(v, p);
                if (s >= 0 && used.TryGetValue(v.VoiceID, out var others))
                    s -= 0.4 * Overlap(myChapters, others.Select(o => index.StatsFor(o)?.Chapters ?? []).ToList());
                return (Voice: v, Score: s);
            })
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .ToList();
    }

    static Dictionary<string, List<Guid>> UsedVoices(Project project, VoiceEngine engine, Guid? except = null)
    {
        var used = new Dictionary<string, List<Guid>>();
        foreach (var c in project.Characters.Where(c => c.Id != except))
            if (c.Voice(engine) is { } v)
            {
                if (!used.TryGetValue(v.Choice.VoiceID, out var list)) used[v.Choice.VoiceID] = list = [];
                list.Add(c.Id);
            }
        return used;
    }

    /// The share of my chapters someone else with this voice also speaks in, 0…1.
    public static double Overlap(HashSet<int> mine, List<HashSet<int>> others)
    {
        if (mine.Count == 0) return others.Count == 0 ? 0 : 0.5;
        var theirs = new HashSet<int>();
        foreach (var o in others) theirs.UnionWith(o);
        return (double)mine.Count(theirs.Contains) / mine.Count;
    }

    /// Casts every character with lines who has no voice the narrator chose, and the
    /// narrator if they have none. Main characters choose first. Characters in `keep` keep
    /// the voice they have, like the narrator's choices. Returns how many characters got a voice.
    public static int Cast(Project project, BookIndex index, VoiceEngine engine, IReadOnlyList<VoiceInfo> voices, IReadOnlySet<Guid>? keep = null)
    {
        keep ??= new HashSet<Guid>();
        bool Kept(BookCharacter c) => c.Voice(engine) is { } v && (v.Source == VoiceSource.User || keep.Contains(c.Id));
        int LinesOf(Guid id) => index.StatsFor(id)?.Lines ?? 0;
        HashSet<int> ChaptersOf(Guid id) => index.StatsFor(id)?.Chapters ?? [];

        // Main characters first (a stable sort, like Swift's on a list in cast order).
        var speaking = project.Characters.Where(c => LinesOf(c.Id) > 0).OrderByDescending(c => LinesOf(c.Id)).Select(c => c.Id).ToList();
        // Start from the voices the narrator chose; every automatic one is decided again.
        var taken = new Dictionary<string, List<HashSet<int>>>();
        void Take(string id, HashSet<int> chapters)
        {
            if (!taken.TryGetValue(id, out var l)) taken[id] = l = [];
            l.Add(chapters);
        }
        foreach (var c in project.Characters.Where(Kept))
            Take(c.Voice(engine)!.Choice.VoiceID, ChaptersOf(c.Id));

        // The narrator first: heard most of all, and nobody should share their voice.
        string? narratorID = null;
        if (project.Settings.NarratorVoice(engine) is { } n && (n.Source == VoiceSource.User || keep.Count > 0))
            narratorID = n.Choice.VoiceID;
        else if (engine == VoiceEngine.Qwen && VoiceInfo.QwenPresets.Count > 0)
        {
            var preset = VoiceInfo.QwenPresets[0];
            narratorID = preset.VoiceID;
            project.Settings.SetNarratorVoice(new CastVoice(preset.Choice, 0.9, VoiceSource.Auto), engine);
        }
        else if (voices.OrderByDescending(v => v.Quality).ThenByDescending(v => v.Gender == Gender.Neutral ? 1 : 0).FirstOrDefault() is { } best)
        {
            narratorID = best.VoiceID;
            project.Settings.SetNarratorVoice(new CastVoice(best.Choice, 0.9, VoiceSource.Auto), engine);
        }

        int count = 0;
        foreach (var id in speaking)
        {
            if (project.Character(id) is not { } c || Kept(c)) continue;
            var p = Profile(c, project, index);
            var chapters = ChaptersOf(id);
            if (engine == VoiceEngine.Qwen && (p.Timbre.Count > 0 || p.Accent is not null || VoiceDescription.Notes(c) is not null))
            {
                // The book or the Voice Notes say how they sound: a voice designed from that
                // (the notes are added when it speaks, so editing them changes the voice).
                // Characters nothing describes get a distinct ready-made voice below.
                var designed = new VoiceChoice(VoiceEngine.Qwen, "design", Describe(p, c.Name));
                c.SetVoice(new CastVoice(designed, Confidence(p, 0), VoiceSource.Auto), engine);
                count++;
                continue;
            }
            (VoiceInfo Voice, double Score, double Shared)? pick = null;
            foreach (var v in voices)
            {
                double s = Score(v, p);
                if (s < 0) continue;
                double shared = taken.TryGetValue(v.VoiceID, out var t) ? Overlap(chapters, t) : 0;
                s -= 0.45 * shared + (v.VoiceID == narratorID ? 0.5 : 0) + 0.05 * (taken.TryGetValue(v.VoiceID, out var t2) ? t2.Count : 0);
                if (pick is null || s > pick.Value.Score) pick = (v, s, shared);
            }
            if (pick is not { } chosen)
            {
                c.SetVoice(null, engine);
                continue;
            }
            var choice = chosen.Voice.Choice;
            // The system's voices are all adults: a child sounds higher, an older voice lower and slower.
            if (chosen.Voice.Engine == VoiceEngine.Apple && p.Age is { } age && age != chosen.Voice.Age)
            {
                switch (age)
                {
                    case VoiceAge.Child: choice.Pitch = 1.3; choice.Rate = 1.05; break;
                    case VoiceAge.Young: choice.Pitch = 1.08; break;
                    case VoiceAge.Older: choice.Pitch = 0.88; choice.Rate = 0.92; break;
                }
            }
            // The system's voices can't take a description: the brief's pitch and pace nudge them.
            if (chosen.Voice.Engine == VoiceEngine.Apple && (p.PitchLean != 0 || p.PaceLean != 0))
            {
                choice.Pitch = Math.Round((choice.Pitch ?? 1) * (1 + 0.06 * p.PitchLean) * 100, MidpointRounding.AwayFromZero) / 100;
                choice.Rate = Math.Round((choice.Rate ?? 1) * (1 + 0.06 * p.PaceLean) * 100, MidpointRounding.AwayFromZero) / 100;
            }
            double sharedNarrator = chosen.Voice.VoiceID == narratorID ? 0.5 : 0;
            c.SetVoice(new CastVoice(choice, Confidence(p, Math.Max(chosen.Shared, sharedNarrator)), VoiceSource.Auto), engine);
            Take(chosen.Voice.VoiceID, chapters);
            count++;
        }
        CastRoles(project, engine, voices, keepAutomatic: keep.Count > 0);
        project.VoicesCast = AppleDate.Now;
        return count;
    }

    /// Casting Style: a voice for each shared role the style uses (Men, Women, Other) that
    /// the narrator hasn't chosen. Men get a man's voice, Women a woman's, Other a neutral
    /// one when the engine has it. Never the narrator's voice or another role's. Callers pass
    /// castable voices, so clones are never picked. `keepAutomatic` keeps voices casting
    /// already picked. Returns how many roles got a voice.
    public static int CastRoles(Project project, VoiceEngine engine, IReadOnlyList<VoiceInfo> voices, bool keepAutomatic = false)
    {
        var roles = CastingStyleRules.SharedRolesNeeded(project.Settings);
        if (roles.Count == 0) return 0;
        var used = new HashSet<string>();
        if (project.Settings.NarratorVoice(engine) is { } n) used.Add(n.Choice.VoiceID);
        foreach (var r in roles)
            if (project.Settings.RoleVoice(r, engine) is { } v && (v.Source == VoiceSource.User || keepAutomatic)) used.Add(v.Choice.VoiceID);
        int count = 0;
        foreach (var r in roles)
        {
            if (project.Settings.RoleVoice(r, engine) is { } v && (v.Source == VoiceSource.User || keepAutomatic)) continue;
            var want = r == VoiceRole.Men ? Gender.Male : r == VoiceRole.Women ? Gender.Female : Gender.Neutral;
            // Best first: the gender wanted (Other: neutral, else anyone), an adult, quality.
            double RankOf(VoiceInfo x) => (x.Gender == want ? 10 : 0) + (x.Age == VoiceAge.Adult ? 2 : x.Age == VoiceAge.Young ? 1 : 0) + x.Quality;
            var pool = voices.Where(x => !used.Contains(x.VoiceID) && (r == VoiceRole.Other || x.Gender == want)).ToList();
            if (pool.Count == 0)
            {
                project.Settings.SetRoleVoice(null, r, engine);
                continue;
            }
            // Swift's max(by:) keeps the first of equals.
            var best = pool[0];
            foreach (var x in pool) if (RankOf(x) > RankOf(best)) best = x;
            project.Settings.SetRoleVoice(new CastVoice(best.Choice, best.Gender == want ? 0.9 : 0.6, VoiceSource.Auto), r, engine);
            used.Add(best.VoiceID);
            count++;
        }
        return count;
    }
}
