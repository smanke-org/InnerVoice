// Ported from the Mac's Sources/InnerVoice/Speech/ExportSpeed.swift: how many lines Export
// Audio makes at once, chosen per engine (Settings › Voices and the Export Audio window).

namespace InnerVoice.Core.Speech;

/// One at a time is slowest and always right; more at once is faster but might not work with
/// every voice.
public enum ExportSpeed { Slowest, Faster, Fastest }

public static class ExportSpeeds
{
    public static readonly ExportSpeed[] All = Enum.GetValues<ExportSpeed>();

    /// The engines with a speed of their own (Mix Engines uses each voice's engine).
    public static readonly VoiceEngine[] Engines = [VoiceEngine.Apple, VoiceEngine.Pocket, VoiceEngine.Orpheus, VoiceEngine.Elevenlabs];

    public static string Title(this ExportSpeed s) => s switch
    {
        ExportSpeed.Slowest => "Slowest but most reliable",
        ExportSpeed.Faster => "Faster",
        _ => "Fastest (may not work with every voice)",
    };

    /// Lines at once.
    public static int LinesAtOnce(this ExportSpeed s, VoiceEngine engine) => s switch
    {
        ExportSpeed.Slowest => 1,
        ExportSpeed.Faster => 2,
        _ => engine == VoiceEngine.Apple ? 3 : 4,
    };

    /// ElevenLabs gains the most (it waits on the network) and works at full speed; the
    /// voices on this PC gain little, so they start one at a time.
    public static ExportSpeed Standard(VoiceEngine engine) => engine == VoiceEngine.Elevenlabs ? ExportSpeed.Fastest : ExportSpeed.Slowest;

    /// The narrator's choice for an engine; the app points this at its settings.
    public static Func<VoiceEngine, ExportSpeed> Current { get; set; } = Standard;

    /// The engines a book's voices use, for the Export Speed pickers: the book's engine, or
    /// for Mix Engines, the engines of the characters' and narrator's mixed voices.
    public static List<VoiceEngine> EnginesFor(Project project, VoiceEngine engineInUse)
    {
        if (engineInUse != VoiceEngine.Mixed) return [engineInUse];
        var used = project.Characters.Select(c => c.Voice(VoiceEngine.Mixed)?.Choice.Engine).OfType<VoiceEngine>().ToHashSet();
        if (project.Settings.NarratorVoice(VoiceEngine.Mixed) is { } narrator) used.Add(narrator.Choice.Engine);
        return Engines.Where(used.Contains).ToList();
    }
}
