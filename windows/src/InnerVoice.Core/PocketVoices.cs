// Ported from the Mac's Sources/InnerVoiceKit/PocketTTS.swift: Pocket TTS's voice IDs, how it
// says each tone, and moving old books' Qwen3-TTS voices over to it. Same rules and numbers as
// the Mac, so a book migrates and sounds the same on both.

using System.Text.Json;

namespace InnerVoice.Core;

/// Pocket TTS (Kyutai, CC BY 4.0) voices: every voice is cloned from a short clip of speech.
/// A stock voice's clip ships with the app; a voice cloned on this PC uses the narrator's
/// recording. Pocket TTS can't design a voice from a description, so Voice Notes don't
/// change how it sounds.
public static class PocketVoice
{
    /// `VoiceChoice.VoiceID` of a stock voice: "stock:" and its name ("stock:tara").
    public const string StockPrefix = "stock:";
    /// `VoiceChoice.VoiceID` of a voice cloned on this PC: "clone:" and its id.
    public const string ClonePrefix = "clone:";
    /// Pocket TTS's own default voice, Alba (a clip voice-acted by Alba MacKenna, CC BY 4.0,
    /// from Kyutai's voice collection). It's the narrator's voice in books moved over from
    /// Qwen3-TTS.
    public const string BuiltInID = StockPrefix + "alba";

    /// The stock voice's name ("tara") for a voice ID, or null for any other voice.
    public static string? StockName(string voiceID) =>
        voiceID.StartsWith(StockPrefix, StringComparison.Ordinal) ? voiceID[StockPrefix.Length..] : null;

    /// The semitones a voice's pitch (1 is its own) shifts the clip by, rounded to whole
    /// semitones and kept to ±4 (more stops sounding like the same person).
    public static int Semitones(double? pitch)
    {
        if (pitch is not { } p || p <= 0) return 0;
        return Math.Max(-4, Math.Min(4, (int)Math.Round(12 * Math.Log2(p), MidpointRounding.AwayFromZero)));
    }
}

/// How Pocket TTS says a line in each tone.
///
/// Pocket TTS has no tone, emotion or speed setting: it carries on in the manner of the clip
/// it clones from and reads the line's own punctuation. What it does have is the
/// temperature of its sampler (`Temperature`; the model's default is 0.7): the noise it
/// draws each step is scaled by its square root. Higher lets the delivery wander more (a
/// livelier, less even read), lower keeps it close to the most likely read (steadier,
/// flatter). Its other settings (decode steps, the end-of-speech threshold) change quality
/// and where it stops, not how a line feels, so they stay at the model's values.
///
/// So each tone is three things this app can do around the model, plus the temperature:
/// - `Temperature`: angry and excited go up to 0.95 and 0.9, scared to 0.85, sarcastic and
///   confused stay near the default (0.75: the words carry them); tender, sad and whispered
///   go down to 0.55–0.5 for a held, even read. Kept within 0.5…0.95: far above the
///   default the words start to slur, far below it every line comes out alike.
/// - `Semitones`: the clip is shifted before cloning, as for a shade. Excited and scared
///   speak a semitone higher, sad a semitone lower. One semitone keeps the voice.
/// - `Rate`: the finished audio is stretched without changing its pitch. Excited, angry
///   and scared come a little quicker (4–6%); tender, sad and whispered slower (5–8%).
/// - `Volume`: after leveling to −20 dB. Pocket TTS can't whisper: whispered is said softly
///   (0.7, about −3 dB) and slowly, the nearest it comes. Tender and sad are a touch
///   quieter (0.9).
/// - Narration is always read even (the defaults), like an audiobook narrator.
public readonly record struct PocketDelivery(double Temperature, int Semitones = 0, double Rate = 1, double Volume = 1)
{
    /// The model's own temperature.
    public const double DefaultTemperature = 0.7;
    public static readonly PocketDelivery Even = new(DefaultTemperature);

    /// `narration`: the line is narration (read even whatever its tone says).
    public static PocketDelivery For(Tone tone, bool narration = false)
    {
        if (narration) return Even;
        return tone switch
        {
            Tone.Neutral => Even,
            Tone.Excited => new(0.9, Semitones: 1, Rate: 1.06),
            Tone.Angry => new(0.95, Rate: 1.04),
            Tone.Scared => new(0.85, Semitones: 1, Rate: 1.06),
            Tone.Sarcastic => new(0.75, Rate: 0.97),
            Tone.Confused => new(0.75, Rate: 0.96),
            Tone.Tender => new(0.55, Rate: 0.95, Volume: 0.9),
            Tone.Sad => new(0.5, Semitones: -1, Rate: 0.92, Volume: 0.9),
            Tone.Whispered => new(0.5, Rate: 0.95, Volume: 0.7),
            _ => Even,
        };
    }
}

/// Projects from before Pocket TTS: their Qwen3-TTS voices become Pocket TTS voices when
/// the project is opened (`ProjectDocument.Open` and the Series Voices reader call `Migrate`).
/// - A ready-made Qwen3-TTS voice ("preset:…") becomes the nearest stock voice, with the
///   pitch and pace casting gives a stock voice for that age.
/// - A designed voice ("design") becomes the stock voice casting picks for that character.
/// - A voice cloned on this PC ("clone:…") keeps its recording; Pocket TTS speaks it.
/// - "Same as Narrator" stays.
/// - The narrator's own choices (`Source == User`) stay the narrator's: mapped, never dropped.
/// The project's engine "qwen" already reads as `Pocket` (`VoiceEngineConverter`).
public static class LegacyVoices
{
    /// Each Qwen3-TTS ready-made voice and the stock voice nearest it (same gender; the
    /// age it was made for, so casting's age shift applies).
    public static readonly IReadOnlyDictionary<string, (string Stock, VoiceAge Age)> Presets = new Dictionary<string, (string, VoiceAge)>
    {
        ["narrator"] = ("alba", VoiceAge.Adult),
        ["storyteller"] = ("tara", VoiceAge.Adult), ["bright"] = ("leah", VoiceAge.Young), ["soft"] = ("mia", VoiceAge.Young),
        ["brisk"] = ("zoe", VoiceAge.Adult), ["dame"] = ("tara", VoiceAge.Older), ["grandmother"] = ("mia", VoiceAge.Older),
        ["lass"] = ("jess", VoiceAge.Young), ["girl"] = ("zoe", VoiceAge.Child),
        ["baritone"] = ("dan", VoiceAge.Adult), ["hero"] = ("zac", VoiceAge.Young), ["gentleman"] = ("leo", VoiceAge.Adult),
        ["elder"] = ("dan", VoiceAge.Older), ["sailor"] = ("leo", VoiceAge.Older), ["clerk"] = ("zac", VoiceAge.Young), ["boy"] = ("zac", VoiceAge.Child),
    };

    const string Qwen = VoiceEngines.RetiredQwen;
    static string Pocket => VoiceEngine.Pocket.Raw();

    /// Whether the project still has Qwen3-TTS voices.
    public static bool NeedsMigration(Project project) =>
        project.Characters.Any(c => c.Voices?.ContainsKey(Qwen) == true)
        || project.Settings.NarratorVoices?.ContainsKey(Qwen) == true
        || project.Settings.RoleVoices?.Values.Any(v => v.ContainsKey(Qwen)) == true;

    /// The stock voice a ready-made, cloned or "Same as Narrator" voice becomes; null for a
    /// designed voice (casting picks one) or anything unknown.
    public static CastVoice? Mapped(CastVoice v)
    {
        var choice = new VoiceChoice(VoiceEngine.Pocket, v.Choice.VoiceID, null, v.Choice.Rate, v.Choice.Pitch);
        if (choice.IsNarrator || choice.VoiceID.StartsWith(PocketVoice.ClonePrefix, StringComparison.Ordinal))
            return new CastVoice(choice, v.Confidence, v.Source);
        if (!choice.VoiceID.StartsWith("preset:", StringComparison.Ordinal) || !Presets.TryGetValue(choice.VoiceID["preset:".Length..], out var target)
            || VoiceInfo.Pocket.FirstOrDefault(s => s.VoiceID == PocketVoice.StockPrefix + target.Stock) is not { } stock)
            return null;
        var outChoice = VoiceCasting.AgeAdjusted(stock, target.Age);
        // The narrator's own speed and pitch on the old voice carry over.
        if (v.Choice.Rate is { } r) outChoice.Rate = Round2((outChoice.Rate ?? 1) * r);
        if (v.Choice.Pitch is { } p) outChoice.Pitch = Round2((outChoice.Pitch ?? 1) * p);
        return new CastVoice(outChoice, v.Confidence, v.Source);
    }

    static double Round2(double x) => Math.Round(x * 100, MidpointRounding.AwayFromZero) / 100;

    /// Turns the project's Qwen3-TTS voices into Pocket TTS voices. Returns whether anything
    /// changed (the document then marks itself edited).
    public static bool Migrate(Project project)
    {
        if (!NeedsMigration(project)) return false;
        // Designed voices, to cast: who had one, and whether it was the narrator's choice.
        var designed = new Dictionary<Guid, VoiceSource>();
        VoiceSource? narratorDesigned = null;
        var rolesDesigned = new List<(VoiceRole Role, VoiceSource Source)>();
        var hadNoVoice = project.Characters.Where(c => c.Voices?.ContainsKey(Qwen) != true && c.Voices?.ContainsKey(Pocket) != true)
            .Select(c => c.Id).ToHashSet();

        foreach (var c in project.Characters)
        {
            if (c.Voices is not { } all || !all.Remove(Qwen, out var old)) continue;
            if (!all.ContainsKey(Pocket))
            {
                if (Mapped(old) is { } mapped) all[Pocket] = mapped; else designed[c.Id] = old.Source;
            }
            c.Voices = all.Count == 0 ? null : all;
        }
        if (project.Settings.NarratorVoices is { } narrator && narrator.Remove(Qwen, out var oldNarrator))
        {
            if (!narrator.ContainsKey(Pocket))
            {
                if (Mapped(oldNarrator) is { } mapped) narrator[Pocket] = mapped; else narratorDesigned = oldNarrator.Source;
            }
            project.Settings.NarratorVoices = narrator.Count == 0 ? null : narrator;
        }
        if (project.Settings.RoleVoices is { } roles)
        {
            foreach (var key in roles.Keys.ToList())
            {
                var one = roles[key];
                if (!one.Remove(Qwen, out var old)) continue;
                if (!one.ContainsKey(Pocket))
                {
                    if (Mapped(old) is { } mapped) one[Pocket] = mapped;
                    else if (RoleFromKey(key) is { } r) rolesDesigned.Add((r, old.Source));
                }
                if (one.Count == 0) roles.Remove(key);
            }
            project.Settings.RoleVoices = roles.Count == 0 ? null : roles;
        }

        if (designed.Count == 0 && narratorDesigned is null && rolesDesigned.Count == 0) return true;
        // Designed voices: what casting picks, keeping every voice already there (mapped or
        // the narrator's) so nobody else's voice moves. The date casting last ran stays.
        var castDate = project.VoicesCast;
        var stock = VoiceInfo.Pocket;
        var index = new BookIndex(project);
        VoiceCasting.Cast(project, index, VoiceEngine.Pocket, stock, keepExisting: true);
        project.VoicesCast = castDate;
        foreach (var c in project.Characters)
        {
            if (hadNoVoice.Contains(c.Id))
            {
                // Casting only stood in for the old designs; characters with no voice keep none.
                c.SetVoice(null, VoiceEngine.Pocket);
                continue;
            }
            if (!designed.TryGetValue(c.Id, out var source)) continue;
            if (c.Voice(VoiceEngine.Pocket) is null)
            {
                // Casting only voices characters with lines: the best fit for the rest.
                var p = VoiceCasting.Profile(c, project, index);
                var best = stock.MaxBy(s => VoiceCasting.Score(s, p));
                if (best is not null && VoiceCasting.Score(best, p) >= 0)
                    c.SetVoice(new CastVoice(VoiceCasting.AgeAdjusted(best, p.Age), 0.5, source), VoiceEngine.Pocket);
                else if (stock.Count > 0)
                    c.SetVoice(new CastVoice(stock[0].Choice, 0.3, source), VoiceEngine.Pocket);
            }
            else if (source == VoiceSource.User && c.Voice(VoiceEngine.Pocket) is { } v)
            {
                v.Source = VoiceSource.User;
                c.SetVoice(v, VoiceEngine.Pocket);
            }
        }
        if (narratorDesigned is { } ns)
        {
            if (project.Settings.NarratorVoice(VoiceEngine.Pocket) is null && stock.Count > 0)
                project.Settings.SetNarratorVoice(new CastVoice(stock[0].Choice, 0.9, ns), VoiceEngine.Pocket);
            else if (ns == VoiceSource.User && project.Settings.NarratorVoice(VoiceEngine.Pocket) is { } v)
            {
                v.Source = VoiceSource.User;
                project.Settings.SetNarratorVoice(v, VoiceEngine.Pocket);
            }
        }
        foreach (var (role, source) in rolesDesigned)
        {
            if (project.Settings.RoleVoice(role, VoiceEngine.Pocket) is null)
            {
                // A role the Casting Style doesn't use now: the stock voice it would get.
                var want = role == VoiceRole.Men ? Gender.Male : role == VoiceRole.Women ? Gender.Female : Gender.Neutral;
                var pick = stock.FirstOrDefault(s => s.Gender == want && s.VoiceID != PocketVoice.BuiltInID) ?? stock.LastOrDefault();
                if (pick is not null) project.Settings.SetRoleVoice(new CastVoice(pick.Choice, 0.6, source), role, VoiceEngine.Pocket);
            }
            else if (source == VoiceSource.User && project.Settings.RoleVoice(role, VoiceEngine.Pocket) is { } v)
            {
                v.Source = VoiceSource.User;
                project.Settings.SetRoleVoice(v, role, VoiceEngine.Pocket);
            }
        }
        return true;
    }

    /// A role's key in the file ("men") back to the role, as the Mac's `VoiceRole(rawValue:)`.
    static VoiceRole? RoleFromKey(string key) =>
        Enum.GetValues<VoiceRole>().Cast<VoiceRole?>().FirstOrDefault(r => JsonNamingPolicy.CamelCase.ConvertName(r!.Value.ToString()) == key);
}
