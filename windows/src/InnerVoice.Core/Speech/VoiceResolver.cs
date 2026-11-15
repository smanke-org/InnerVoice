// Ported from the Mac's Speech/EngineSwitches.swift and the VoiceResolver in
// Speech/SpeechPlayer.swift, as plain logic over the registered engines.

namespace InnerVoice.Core.Speech;

/// The voice engines this computer can speak with, and the default for new books (Settings ›
/// Voices). Each book speaks with one engine (`ProjectSettings.VoiceEngine`): previews, table
/// reads and exports all use it. Mix Engines is gone (2026-11-15): a book saved with it asks
/// which one engine to use when it opens. A local engine is available once downloaded, a
/// cloud engine once it's set up (a key and the narrator's yes). Kept per computer.
public sealed class EngineSwitches
{
    public static EngineSwitches Shared { get; set; } = new();

    static readonly VoiceEngine[] BestFirst =
        [VoiceEngine.Qwen, VoiceEngine.Pocket, VoiceEngine.Chatterbox, VoiceEngine.Orpheus, VoiceEngine.Apple];

    VoiceEngine? chosenDefault;
    readonly HashSet<VoiceEngine>? oldOff;

    /// Saves the default for new books (the app sets this; test runs leave it null).
    public Action<VoiceEngine>? SaveDefault { get; set; }

    /// `defaultEngine`: the saved default for new books. With none saved, `oldOff` (the
    /// engines switched off before 2026-11-15) picks the best engine that was on and is
    /// downloaded; with neither (test runs), Apple Voices.
    public EngineSwitches(VoiceEngine? defaultEngine = null, IEnumerable<VoiceEngine>? oldOff = null)
    {
        if (defaultEngine is { } d && d != VoiceEngine.Mixed) chosenDefault = d;
        else if (oldOff is not null) this.oldOff = [.. oldOff];
        else chosenDefault = VoiceEngine.Apple;
    }

    /// The engine new books start with.
    public VoiceEngine DefaultEngine =>
        chosenDefault ?? BestFirst.FirstOrDefault(e => !oldOff!.Contains(e) && Usable(e), VoiceEngine.Apple);

    public void SetDefault(VoiceEngine engine)
    {
        if (engine == VoiceEngine.Mixed) return;
        chosenDefault = engine;
        SaveDefault?.Invoke(engine);
    }

    /// The engine a new book starts with: the default, else the best engine that's ready on
    /// this computer (the Mac's project-type step).
    public VoiceEngine StartingEngine
    {
        get
        {
            var d = DefaultEngine;
            if (Usable(d)) return d;
            return new[] { VoiceEngine.Qwen, VoiceEngine.Pocket, VoiceEngine.Orpheus, VoiceEngine.Apple }.FirstOrDefault(Usable, VoiceEngine.Apple);
        }
    }

    /// Whether the engine can be chosen: this computer has it, and a cloud engine only once
    /// it's set up (a key and the narrator's yes).
    public bool IsOn(VoiceEngine e) =>
        e != VoiceEngine.Mixed && VoiceEngineRegistry.Get(e) is { } engine && (!e.IsCloud() || engine.IsReady);

    public IReadOnlyList<VoiceEngine> Enabled => VoiceEngines.Engines.Where(IsOn).ToList();

    /// Available and ready: its voices can be used now.
    public bool Usable(VoiceEngine e) => IsOn(e) && VoiceEngineRegistry.IsReady(e);

    /// What engine menus offer: one engine per book, never a mix.
    public IReadOnlyList<VoiceEngine> Choices => VoiceEngines.Engines.Where(Usable).ToList();

    /// Kept for setup code that "turns an engine on": every set-up engine is available now.
    public void Set(VoiceEngine e, bool on) { }

    /// The engine a book speaks with: its own. A book saved with Mix Engines uses the default
    /// until the narrator picks one (the window asks); a cloud engine that's no longer set up
    /// falls back to the default (or Apple Voices). A cloud engine the book hasn't allowed
    /// stays chosen: playing asks for permission first (`CloudGate`).
    public VoiceEngine InUse(Project project)
    {
        var chosen = project.Settings.Engine;
        var d = DefaultEngine;
        var fallback = d.IsCloud() && !IsOn(d) ? VoiceEngine.Apple : d;
        if (chosen == VoiceEngine.Mixed) return fallback;
        return IsOn(chosen) ? chosen : fallback.IsCloud() ? VoiceEngine.Apple : fallback;
    }

    /// An engine's voices. Mixing includes a cloud engine only for a book that allows that company.
    public IReadOnlyList<VoiceInfo> VoicesFor(VoiceEngine engine, Project? project = null) => engine switch
    {
        VoiceEngine.Mixed => VoiceEngines.Engines.Where(e => Usable(e) && (!e.IsCloud() || project?.Settings.AllowsCloud(e) == true))
            .SelectMany(e => VoiceEngineRegistry.VoicesOf(e)).ToList(),
        _ => VoiceEngineRegistry.VoicesOf(engine),
    };

    /// A voice when nothing is cast yet: the best system voice, Orpheus's first, Pocket TTS's or
    /// Chatterbox's own voice, Qwen3-TTS's narrator preset, or Gemini's first.
    public VoiceChoice? Fallback(VoiceEngine engine) => engine switch
    {
        VoiceEngine.Orpheus => new VoiceChoice(VoiceEngine.Orpheus, "tara"),
        VoiceEngine.Pocket => VoiceInfo.Pocket[0].Choice,
        VoiceEngine.Chatterbox => VoiceInfo.Chatterbox[0].Choice,
        VoiceEngine.Gemini => GeminiTTS.Voices[0].Choice,
        VoiceEngine.Qwen => VoiceInfo.QwenPresets[0].Choice,
        VoiceEngine.Mixed => Enabled.Select(Fallback).FirstOrDefault(c => c is not null),
        _ => VoiceEngineRegistry.VoicesOf(engine).FirstOrDefault()?.Choice,
    };
}

public static class VoiceNames
{
    /// "Aria", "Tara", or the start of a Qwen3-TTS design.
    public static string Name(VoiceChoice choice)
    {
        if (choice.IsNarrator) return "Same as Narrator";
        if (choice.Engine == VoiceEngine.Qwen && !choice.VoiceID.StartsWith("preset:", StringComparison.Ordinal) && !choice.VoiceID.StartsWith("clone:", StringComparison.Ordinal))
        {
            // Short enough for a list row, cut at a word.
            if (choice.Design is not { } d) return "Designed voice";
            if (d.Length <= 48) return d;
            var head = d[..47];
            int cut = head.LastIndexOf(' ');
            return (cut > 0 ? head[..cut] : head).TrimEnd(' ', ',', ';') + "…";
        }
        return VoiceEngineRegistry.Info(choice)?.Name ?? "Missing voice";
    }
}

/// Which voice says each segment of a project, on the project's engine: the character's cast
/// voice, or the narrator's for narration and lines nobody is cast for.
public sealed class VoiceResolver
{
    public VoiceEngine Engine { get; }
    public Dictionary<Guid, VoiceChoice> Characters { get; } = [];
    public VoiceChoice? Narrator { get; set; }
    /// Engines that can speak now (on and ready).
    public HashSet<VoiceEngine> Usable { get; }
    /// Casting Style shades for characters the narrator plays, by character.
    public Dictionary<Guid, Shade> Shades { get; } = [];
    /// An audition of a shade (Voices page): every line of it is said with this shade.
    public Shade? AuditionShade { get; set; }
    /// The cloud engines this book allows its text to go to (each company has its own yes).
    public HashSet<VoiceEngine> CloudAllowed { get; }
    public string UsageBook { get; }
    /// Whether letters, signs and other written text are read by the narrator
    /// (Settings › Voices) rather than the character who wrote them.
    public bool WrittenByNarrator { get; set; } = true;

    /// `index`: the book's index if the caller has it; otherwise one is made only when the
    /// Casting Style sorts characters by gender.
    public VoiceResolver(Project project, VoiceEngine? engine = null, BookIndex? index = null, EngineSwitches? switches = null,
                         bool writtenByNarrator = true)
    {
        switches ??= EngineSwitches.Shared;
        var e = engine ?? switches.InUse(project);
        Engine = e;
        WrittenByNarrator = writtenByNarrator;
        // A cloud engine only for a book that allows its text to go to that company.
        Usable = VoiceEngines.Engines.Where(x => switches.Usable(x) && project.Settings.AllowsCloud(x)).ToHashSet();
        foreach (var c in project.Characters)
            if (c.Voice(e)?.Choice is { } v) Characters[c.Id] = WithNotes(v, c);
        // Casting Style: a character read by the narrator or a shared voice (Men, Women,
        // Other) speaks in that voice; Own Voice keeps their own.
        var roleChoices = new Dictionary<VoiceRole, VoiceChoice>();
        foreach (var r in CastingStyleRules.SharedRoles)
            if (project.Settings.RoleVoice(r, e)?.Choice is { } v && Usable.Contains(v.Engine)) roleChoices[r] = v;
        var readers = project.Settings.CastingStyle is null ? [] : CastingStyleRules.Readers(project, index ?? new BookIndex(project));
        foreach (var (id, r) in readers)
        {
            switch (r)
            {
                case VoiceRole.Own: break;
                case VoiceRole.Narrator: Characters.Remove(id); break;
                default:
                    if (roleChoices.TryGetValue(r, out var rv)) Characters[id] = rv; else Characters.Remove(id);
                    break;
            }
        }
        // First-Person Narrator: the "I" character speaks in the narrator's voice (Same,
        // Variant) or their own (Different). Which chapter a paragraph is in decides who "I" is.
        if (project.Settings.CastingStyle == CastingStyle.FirstPerson)
        {
            var fp = project.Settings.FirstPerson ?? new FirstPersonSettings();
            FirstPerson = fp;
            Tellers = FirstPersonRules.Tellers(project.Settings);
            bookTeller = project.Settings.PovCharacterID;
            chapterTellers = fp.ChapterNarrators ?? [];
            if (chapterTellers.Count > 0)
                foreach (var ch in project.Chapters) foreach (var para in ch.Paragraphs) chapterOf[para.Id] = ch.Id;
            if (fp.VoiceMode != FirstPersonVoice.Different) foreach (var id in Tellers) Characters.Remove(id);
        }
        CloudAllowed = VoiceEngines.Engines.Where(x => x.IsCloud() && project.Settings.AllowsCloud(x)).ToHashSet();
        UsageBook = project.UsageKey();
        // The narrator's voice: one the narrator chose; else, in a first-person book, the POV
        // character's; else the one casting picked; else any voice that works.
        var n = project.Settings.NarratorVoice(e);
        if (n is not null && n.Source == VoiceSource.User && Usable.Contains(n.Choice.Engine)) Narrator = n.Choice;
        else if (PovNarrator(project, e, switches) is { } pov && Characters.TryGetValue(pov.Id, out var pv)) Narrator = pv;
        else if (n is not null && Usable.Contains(n.Choice.Engine)) Narrator = n.Choice;
        else Narrator = switches.Fallback(e) ?? switches.Fallback(VoiceEngine.Apple);
        // A two-person book: the men's or women's voice reads the narration too.
        var narrationReader = CastingStyleRules.Reader(VoiceRole.Narrator, project.Settings);
        if (narrationReader != VoiceRole.Narrator && roleChoices.TryGetValue(narrationReader, out var nr)) Narrator = nr;
        // The narrator playing a character shades their voice for them (One Voice, and anyone
        // else the Casting Style gives the narrator): a man a little lower, a woman higher…
        if (readers.Values.Contains(VoiceRole.Narrator))
        {
            var g = Narrator is { } nv ? VoiceEngineRegistry.Info(nv)?.Gender ?? (nv.Design is { } d ? VoiceCasting.GenderOfDescription(d) : null) : null;
            var narratorGender = g is null or Gender.Neutral ? (CharacterGender?)null : g.Value.ToCharacterGender();
            foreach (var (id, s) in CastingStyleRules.Shades(project, index ?? new BookIndex(project), narratorGender)) Shades[id] = s;
        }
    }

    /// The character whose voice reads the narration of a first-person book: the one marked
    /// as the book's narrator, unless the narrator has a voice of their own choosing.
    public static BookCharacter? PovNarrator(Project project, VoiceEngine engine, EngineSwitches switches)
    {
        if (project.Settings.NarratorVoice(engine)?.Source == VoiceSource.User) return null;
        // First-Person Narrator keeps the narrator's voice; its own settings decide the rest.
        if (project.Settings.CastingStyle == CastingStyle.FirstPerson) return null;
        if (project.Character(project.Settings.PovCharacterID) is not { } pov || pov.Voice(engine)?.Choice is not { } v || v.IsNarrator
            || !switches.Usable(v.Engine) || !project.Settings.AllowsCloud(v.Engine)) return null;
        return pov;
    }

    /// Qwen3-TTS designs a voice from a description: the character's Voice Notes go with it,
    /// so what the narrator writes there shapes how they sound. The other on-device engines
    /// (Pocket TTS, Orpheus, the system's voices) can't take a description; ElevenLabs gets the
    /// notes once, when it designs a voice from them (Voice Design, on the narrator's click).
    public static VoiceChoice WithNotes(VoiceChoice v, BookCharacter? c)
    {
        if (c is null || v.Engine != VoiceEngine.Qwen || v.VoiceID != "design") return v;
        var output = v.Copy();
        output.Design = VoiceDescription.Design(v.Design ?? "", c);
        return output;
    }

    /// First-Person Narrator, when the book uses it.
    public FirstPersonSettings? FirstPerson { get; }
    /// Everyone who tells part of the book in the first person.
    public HashSet<Guid> Tellers { get; } = [];
    readonly Guid? bookTeller;
    readonly Dictionary<Guid, Guid> chapterTellers = [];
    readonly Dictionary<Guid, Guid> chapterOf = [];

    /// Who says "I" where this segment is.
    public Guid? Teller(SpeechSegment segment) =>
        chapterOf.TryGetValue(segment.ParagraphID, out var ch) && chapterTellers.TryGetValue(ch, out var id) ? id : bookTeller;

    /// Narration the "I" character reads (First-Person Narrator, "reads the narration" on).
    Guid? TellerNarrating(SpeechSegment segment) =>
        FirstPerson is { ReadsNarration: true } && segment.LineID is null && segment.SpeakerID is null && segment.Source is not null
            ? Teller(segment) : null;

    /// The "I" character's thought the narrator reads plainly (their thoughts toggle off).
    bool NarratorReadsThought(SpeechSegment segment) =>
        FirstPerson is { VoicesThoughts: false } && segment.Kind == LineKind.Thought && segment.SpeakerID is { } id && Tellers.Contains(id);

    /// The shade a segment is said with: the speaker's, when the narrator's voice says their
    /// spoken or thought line (not narration, and not letters the narrator reads out).
    public Shade? ShadeFor(SpeechSegment segment, VoiceChoice choice)
    {
        if (FirstPerson is { } fp && choice == Narrator)
        {
            if (TellerNarrating(segment) is not null) return fp.VoiceMode == FirstPersonVoice.Variant ? fp.VariantShade : null;
            if (NarratorReadsThought(segment)) return null;
            if (segment.SpeakerID is { } t && Tellers.Contains(t) && segment.LineID is not null && segment.Kind != LineKind.Written)
                return AuditionShade ?? (fp.VoiceMode == FirstPersonVoice.Variant ? fp.VariantShade : null);
        }
        if (segment.LineID is null || segment.Kind == LineKind.Written) return null;
        if (AuditionShade is not null) return AuditionShade;
        if (segment.SpeakerID is not { } id || choice != Narrator) return null;
        return Shades.GetValueOrDefault(id);
    }

    public VoiceChoice? ChoiceFor(SpeechSegment segment)
    {
        if (segment.LineID is not null && segment.Kind == LineKind.Written && WrittenByNarrator) return Narrator;
        if (TellerNarrating(segment) is { } teller)
            return Characters.TryGetValue(teller, out var tc) && !tc.IsNarrator && Usable.Contains(tc.Engine) ? tc : Narrator;
        if (NarratorReadsThought(segment)) return Narrator;
        // "Same as Narrator", and a voice from an engine that's turned off or not ready,
        // use the narrator's voice.
        if (segment.SpeakerID is { } id && Characters.TryGetValue(id, out var c) && !c.IsNarrator && Usable.Contains(c.Engine)) return c;
        return Narrator;
    }

    public SpeechRequest? RequestFor(SpeechSegment segment)
    {
        if (ChoiceFor(segment) is not { } choice) return null;
        return new SpeechRequest
        {
            Text = segment.Text, Choice = choice,
            Tone = segment.SpeakerID is null && segment.LineID is null ? Tone.Neutral : segment.Tone,
            Kind = segment.LineID is null ? null : segment.Kind, Sound = segment.Sound, CloudAllowed = CloudAllowed.Contains(choice.Engine),
            Shade = ShadeFor(segment, choice),
            // Delivery hints go only to the cloud engines that take directions.
            Delivery = choice.Engine is VoiceEngine.Elevenlabs or VoiceEngine.Gemini && segment.LineID is not null ? segment.Delivery : null,
            UsageBook = UsageBook,
        };
    }
}
