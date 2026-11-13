// Ported from the Mac's Speech/VoiceActions.swift: the narrator's voice and tone edits (each
// one undo step), casting that happens on its own, and the voices' startup.

using InnerVoice.Core;
using InnerVoice.App.Document;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;

namespace InnerVoice.App.Speech;

/// Sets up the voices when the app starts: the audio cache, the engines this PC has, which
/// are turned on, and the narrator's pauses.
public static class VoiceSetup
{
    static bool started;

    public static string CacheFolder => AppPaths.IsTestRun
        ? Path.Combine(Path.GetTempPath(), "InnerVoice Speech (test)")
        : Path.Combine(AppPaths.Support, "Speech");

    public static void Start()
    {
        if (started) return;
        started = true;
        SpeechRenderer.Shared = new SpeechRenderer(CacheFolder);
        VoiceEngineRegistry.Register(new WindowsVoices());
        ElevenLabsEngine.Register();
        // Pocket TTS and Orpheus on ONNX Runtime (DirectML or the CPU). Test runs keep their
        // models and clones in temporary folders, never the narrator's own.
        VoicePacks.Folder = AppPaths.IsTestRun ? Path.Combine(Path.GetTempPath(), "InnerVoice Voice Models (test)") : Path.Combine(AppPaths.Support, "Voice Models");
        LocalClones.Shared = new LocalClones(AppPaths.IsTestRun ? Path.Combine(Path.GetTempPath(), $"InnerVoice Cloned Voices (test {Environment.ProcessId})") : Path.Combine(AppPaths.Support, "Cloned Voices"));
        VoiceEngineRegistry.Register(new PocketEngine());
        VoiceEngineRegistry.Register(new OrpheusEngine());
        VoiceModels.Shared.DetectHardware();
        VoiceHooks.Install();
        LoadSwitches();
        PauseSettings.Current = AppPaths.IsTestRun ? PauseSettings.Standard : AppSettings.Current.Pauses ?? PauseSettings.Standard;
    }

    static void LoadSwitches()
    {
        // Test runs start with every engine on and never write the choice back.
        var off = AppPaths.IsTestRun ? [] : AppSettings.Current.VoicesOffEngines.Select(VoiceEngines.Parse).OfType<VoiceEngine>();
        EngineSwitches.Shared = new EngineSwitches(off)
        {
            Save = set => AppSettings.Update(s => s.VoicesOffEngines = VoiceEngines.Engines.Where(set.Contains).Select(e => e.Raw()).ToList()),
        };
    }

    /// Settings › Voices › Pauses: kept per PC; changes apply to the next gap, even mid-play.
    public static void SetPauses(PauseSettings settings)
    {
        PauseSettings.Current = settings;
        if (!AppPaths.IsTestRun) AppSettings.Update(s => s.Pauses = settings);
    }

    public static readonly (string Name, PauseSettings Settings)[] Presets =
        [("Tight", PauseSettings.Tight), ("Natural", PauseSettings.Standard), ("Relaxed", PauseSettings.Relaxed)];

    /// The preset these settings match, if any.
    public static string? Preset(PauseSettings s) => Presets.FirstOrDefault(p => p.Settings == s).Name;

    /// The engine this project speaks with now (its own choice while that's on).
    public static VoiceEngine EngineInUse(this Project project) => EngineSwitches.Shared.InUse(project);

    /// A resolver with the narrator's Settings › Voices choices.
    public static VoiceResolver Resolver(ProjectDocument document, VoiceEngine? engine = null) =>
        new(document.Project, engine, document.Index, writtenByNarrator: AppSettings.Current.WrittenByNarrator);
}

/// The narrator's voice and tone edits. Each is one undo step.
public static class VoiceActions
{
    public static void SetTone(this ProjectDocument document, Tone tone, IReadOnlyCollection<Guid> lineIDs)
    {
        var set = lineIDs.ToHashSet();
        foreach (var l in document.Project.Lines.Where(l => set.Contains(l.Id) && l.ToneSource is ToneSource.Ai or ToneSource.Rule))
            Activity.Record(l.Tone == tone ? ActivityKey.TonesKept : ActivityKey.TonesChanged);
        document.Change(set.Count == 1 ? "Set Tone" : $"Set Tone of {set.Count} Lines", p =>
        {
            foreach (var l in p.Lines.Where(l => set.Contains(l.Id)))
            {
                l.Tone = tone;
                l.ToneConfidence = 1;
                l.ToneSource = ToneSource.User;
            }
        });
    }

    /// Gives a character (or, with null, the narrator) a voice on its engine. The narrator's
    /// choice: casting never changes it.
    public static void SetVoice(this ProjectDocument document, VoiceChoice choice, Guid? characterID)
    {
        var p0 = document.Project;
        // Mixing engines: the voice is kept with the project's mixed casting, whatever its engine.
        var key = p0.EngineInUse() == VoiceEngine.Mixed ? VoiceEngine.Mixed : choice.Engine;
        if (characterID is { } cid && p0.Character(cid)?.Voice(key) is { Source: VoiceSource.Auto } old)
            Activity.Record(old.Choice == choice ? ActivityKey.VoicesKept : ActivityKey.VoicesChanged);
        var voice = new CastVoice(choice.Copy(), 1, VoiceSource.User);
        document.Change(characterID is null ? "Choose Narrator Voice" : "Choose Voice", p =>
        {
            if (characterID is { } id) p.Character(id)?.SetVoice(voice.Copy(), key);
            else p.Settings.SetNarratorVoice(voice.Copy(), key);
            p.VoicesCast ??= AppleDate.Now;
        });
    }

    /// A shared role's voice (Men, Women, Other) in Narrator + Two Voices and Full Cast.
    public static void SetRoleVoice(this ProjectDocument document, VoiceChoice choice, VoiceRole role)
    {
        var key = document.Project.EngineInUse() == VoiceEngine.Mixed ? VoiceEngine.Mixed : choice.Engine;
        document.Change($"Choose {role.VoiceTitle()}", p => p.Settings.SetRoleVoice(new CastVoice(choice.Copy(), 1, VoiceSource.User), role, key));
    }

    /// Keeps the voice casting picked, as if the narrator had chosen it.
    public static void ConfirmVoice(this ProjectDocument document, Guid characterID)
    {
        var engine = document.Project.EngineInUse();
        if (document.Project.Character(characterID)?.Voice(engine) is { } v) document.SetVoice(v.Choice, characterID);
    }

    /// Lets this book's text go to ElevenLabs (the narrator said yes), or stops it.
    public static void SetCloudVoicesAllowed(this ProjectDocument document, bool allowed)
    {
        document.Change(allowed ? "Allow ElevenLabs" : "Stop Sending to ElevenLabs", p => p.Settings.CloudVoicesAllowed = allowed ? AppleDate.Now : null);
        if (allowed) VoiceCastingRun.CastIfNeeded(document);
    }

    public static void SetVoiceEngine(this ProjectDocument document, VoiceEngine engine)
    {
        document.Change("Change Voice Engine", p => p.Settings.VoiceEngine = engine == VoiceEngine.Apple ? null : engine);
        VoiceCastingRun.CastIfNeeded(document);
        if (VoiceEngineRegistry.Get(engine) is { } e) _ = e.Prepare(CancellationToken.None);
    }

    /// Casts every voice again from the summaries; the narrator's choices stay.
    public static void CastVoicesAgain(this ProjectDocument document)
    {
        var engine = document.Project.EngineInUse();
        var voices = VoiceInfo.Castable(EngineSwitches.Shared.VoicesFor(engine, document.Project));
        var index = document.Index;
        document.Change("Cast Voices", p => VoiceCasting.Cast(p, index, engine, voices));
    }

    /// The project type (Book › Project Type): what the window shows.
    public static void SetPurpose(this ProjectDocument document, ProjectPurpose purpose) =>
        document.Change("Change Project Type", p => p.Settings.Purpose = purpose);
}

/// Casting that happens on its own: after Tag Speakers and Build Summaries, and when the engine
/// changes. It fills in characters with no voice and decides again the voices it was unsure
/// of; voices it was sure of, and the narrator's choices, stay. Not an undo step of its own:
/// it rides along with the run that caused it.
public static class VoiceCastingRun
{
    public static void CastIfNeeded(ProjectDocument document)
    {
        var project = document.Project;
        if (!project.ShowsVoices) return;
        var engine = project.EngineInUse();
        var index = document.Index;
        var speaking = project.Characters.Where(c => (index.StatsFor(c.Id)?.Lines ?? 0) > 0).ToList();
        var open = speaking.Where(c =>
        {
            if (c.Voice(engine) is not { } v) return true;
            // Mixing: an automatic voice from an engine that's now off or gone is cast again.
            if (v.Source == VoiceSource.Auto && !EngineSwitches.Shared.Usable(v.Choice.Engine)) return true;
            return v.Source == VoiceSource.Auto && v.Confidence < project.Settings.UnsureBelow;
        }).Select(c => c.Id).ToHashSet();
        // Casting Style: a shared role (Men, Women, Other) the style uses with no voice yet.
        bool missingRole = CastingStyleRules.SharedRolesNeeded(project.Settings).Any(r => project.Settings.RoleVoice(r, engine) is null);
        if (open.Count == 0 && !missingRole && project.Settings.NarratorVoice(engine) is not null) return;
        var voices = VoiceInfo.Castable(EngineSwitches.Shared.VoicesFor(engine, project));
        if (voices.Count == 0) return;
        var sure = speaking.Where(c => !open.Contains(c.Id)).Select(c => c.Id).ToHashSet();
        document.ApplyQuietly(p => VoiceCasting.Cast(p, index, engine, voices, sure));
    }
}
