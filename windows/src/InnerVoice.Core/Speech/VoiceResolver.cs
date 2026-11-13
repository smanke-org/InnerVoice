// Ported from the Mac's Speech/EngineSwitches.swift and the VoiceResolver in
// Speech/SpeechPlayer.swift, as plain logic over the registered engines.

namespace InnerVoice.Core.Speech;

/// Which voice engines are turned on (Settings › Voices). A turned-off engine isn't offered
/// on the Voices page, and a project set to it uses the first engine that's on, without
/// changing the project, so turning it back on brings its casting back. At least one engine
/// stays on. Kept per computer (the app saves `Off` in its settings).
public sealed class EngineSwitches
{
    public static EngineSwitches Shared { get; set; } = new();

    public HashSet<VoiceEngine> Off { get; } = [];

    /// Saves the choice (the app sets this; test runs leave it null).
    public Action<IReadOnlySet<VoiceEngine>>? Save { get; set; }

    public EngineSwitches(IEnumerable<VoiceEngine>? off = null)
    {
        if (off is not null) Off.UnionWith(off);
        if (VoiceEngines.Engines.All(Off.Contains)) Off.Clear();
    }

    /// This computer has the engine (part B adds the local models). ElevenLabs counts as on
    /// only once it's set up (a key and the narrator's yes).
    public bool IsOn(VoiceEngine e) =>
        e != VoiceEngine.Mixed && VoiceEngineRegistry.Get(e) is { } engine && !Off.Contains(e) && (!e.IsCloud() || engine.IsReady);

    public IReadOnlyList<VoiceEngine> Enabled => VoiceEngines.Engines.Where(IsOn).ToList();

    /// On and ready: its voices can be used now.
    public bool Usable(VoiceEngine e) => IsOn(e) && VoiceEngineRegistry.IsReady(e);

    /// What the Voices page's engine menu offers: the engines that are on, and Mix Engines
    /// when two or more are.
    public IReadOnlyList<VoiceEngine> Choices
    {
        get
        {
            var on = Enabled.ToList();
            if (on.Count > 1) on.Add(VoiceEngine.Mixed);
            return on;
        }
    }

    /// The last engine that's on can't be turned off.
    public bool CanTurnOff(VoiceEngine e) => IsOn(e) && Enabled.Count > 1;

    public void Set(VoiceEngine e, bool on)
    {
        if (on) Off.Remove(e);
        else
        {
            if (!CanTurnOff(e)) return;
            Off.Add(e);
            // Free the memory a turned-off model engine was using.
            VoiceEngineRegistry.Get(e)?.Unload();
        }
        Save?.Invoke(Off);
    }

    /// The engine a project speaks with: its own choice while that's on.
    public VoiceEngine InUse(Project project)
    {
        var chosen = project.Settings.Engine;
        var enabled = Enabled;
        var local = enabled.Where(e => !e.IsCloud()).ToList();
        if (chosen == VoiceEngine.Mixed) return enabled.Count > 1 ? VoiceEngine.Mixed : enabled.FirstOrDefault(VoiceEngine.Apple);
        // A cloud engine only for a book that allows it.
        if (chosen.IsCloud() && project.Settings.CloudVoicesAllowed is null) return local.FirstOrDefault(VoiceEngine.Apple);
        return IsOn(chosen) ? chosen : local.Count > 0 ? local[0] : enabled.Count > 0 ? enabled[0] : chosen;
    }

    /// An engine's voices. Mixing includes ElevenLabs only for a book that allows it.
    public IReadOnlyList<VoiceInfo> VoicesFor(VoiceEngine engine, Project? project = null) => engine switch
    {
        VoiceEngine.Mixed => VoiceEngines.Engines.Where(e => Usable(e) && (!e.IsCloud() || project?.Settings.CloudVoicesAllowed is not null))
            .SelectMany(e => VoiceEngineRegistry.VoicesOf(e)).ToList(),
        _ => VoiceEngineRegistry.VoicesOf(engine),
    };

    /// A voice when nothing is cast yet: the best system voice, Orpheus's first, or a plain
    /// Qwen3-TTS design.
    public VoiceChoice? Fallback(VoiceEngine engine) => engine switch
    {
        VoiceEngine.Orpheus => new VoiceChoice(VoiceEngine.Orpheus, "tara"),
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
    public VoiceChoice? Narrator { get; private set; }
    /// Engines that can speak now (on and ready).
    public HashSet<VoiceEngine> Usable { get; }
    /// Casting Style shades for characters the narrator plays, by character.
    public Dictionary<Guid, Shade> Shades { get; } = [];
    /// An audition of a shade (Voices page): every line of it is said with this shade.
    public Shade? AuditionShade { get; set; }
    /// The book allows its text to go to ElevenLabs.
    public bool CloudAllowed { get; }
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
        // ElevenLabs only for a book that allows its text to be sent.
        Usable = VoiceEngines.Engines.Where(x => switches.Usable(x) && (!x.IsCloud() || project.Settings.CloudVoicesAllowed is not null)).ToHashSet();
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
        CloudAllowed = project.Settings.CloudVoicesAllowed is not null;
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
        if (project.Character(project.Settings.PovCharacterID) is not { } pov || pov.Voice(engine)?.Choice is not { } v || v.IsNarrator
            || !switches.Usable(v.Engine) || (v.Engine.IsCloud() && project.Settings.CloudVoicesAllowed is null)) return null;
        return pov;
    }

    /// Qwen3-TTS designs a voice from a description: the character's Voice Notes go with it,
    /// so what the narrator writes there shapes how they sound.
    public static VoiceChoice WithNotes(VoiceChoice v, BookCharacter? c)
    {
        if (c is null || v.Engine != VoiceEngine.Qwen || v.VoiceID != "design") return v;
        var output = v.Copy();
        output.Design = VoiceDescription.Design(v.Design ?? "", c);
        return output;
    }

    /// The shade a segment is said with: the speaker's, when the narrator's voice says their
    /// spoken or thought line (not narration, and not letters the narrator reads out).
    public Shade? ShadeFor(SpeechSegment segment, VoiceChoice choice)
    {
        if (segment.LineID is null || segment.Kind == LineKind.Written) return null;
        if (AuditionShade is not null) return AuditionShade;
        if (segment.SpeakerID is not { } id || choice != Narrator) return null;
        return Shades.GetValueOrDefault(id);
    }

    public VoiceChoice? ChoiceFor(SpeechSegment segment)
    {
        if (segment.LineID is not null && segment.Kind == LineKind.Written && WrittenByNarrator) return Narrator;
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
            Kind = segment.LineID is null ? null : segment.Kind, Sound = segment.Sound, CloudAllowed = CloudAllowed,
            Shade = ShadeFor(segment, choice),
            // Delivery hints are ElevenLabs' audio tags: never sent to another engine.
            Delivery = choice.Engine == VoiceEngine.Elevenlabs && segment.LineID is not null ? segment.Delivery : null,
            UsageBook = UsageBook,
        };
    }
}
