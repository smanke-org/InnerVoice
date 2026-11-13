// The voice engine abstraction (docs/WINDOWS-PLAN.md § Voice engines). Every engine that
// makes sound implements IVoiceEngine and is registered once at startup with
// `VoiceEngineRegistry.Register`. Engines are keyed by the Mac's `VoiceEngine` values, so a
// book's casting (`BookCharacter.Voices["pocket"]`…) means the same thing on both computers:
//
//   apple       Windows' built-in voices (App/Speech/WindowsVoices.cs; the Mac's Apple voices)
//   elevenlabs  ElevenLabs, in the cloud (App/Speech/ElevenLabs.cs)
//   pocket      Pocket TTS through ONNX Runtime (Speech/Neural)
//   orpheus     Orpheus through ONNX Runtime (Speech/Neural)
//
// The renderer (SpeechRenderer) owns caching, shading the engine can't do itself, and the
// one audio format; an engine only turns one SpeechRequest into 24 kHz mono samples.

using System.Security.Cryptography;
using System.Text;

namespace InnerVoice.Core.Speech;

/// One thing to say: text, a voice and how to say it.
public sealed record SpeechRequest
{
    public string Text { get; init; } = "";
    public VoiceChoice Choice { get; init; } = new();
    public Tone Tone { get; init; } = Tone.Neutral;
    public LineKind? Kind { get; init; }
    /// A sigh or laugh from the speech tag (Orpheus and ElevenLabs).
    public VocalSound? Sound { get; init; }
    /// The book allows its text to go to a cloud engine (ElevenLabs). Not part of the cache key.
    public bool CloudAllowed { get; init; }
    /// Casting Style: how the narrator's voice changes for the character saying this (pitch
    /// in semitones, pace in steps). null or neutral: the voice as it is.
    public Shade? Shade { get; init; }
    /// The line's delivery hint. ElevenLabs only: the resolver sets it for ElevenLabs voices
    /// alone, and no other engine reads it.
    public string? Delivery { get; init; }
    /// The book's `UsageKey`, for Cloud Usage. Not part of the cache key.
    public string? UsageBook { get; init; }

    public bool IsShaded => Shade is { } s && (s.Pitch != 0 || s.Pace != 0);

    /// The file name of the rendered audio in the cache: the Mac's key (version 1).
    public string CacheKey
    {
        get
        {
            var parts = new List<string>
            {
                "v1", Choice.Engine.Raw(), Choice.VoiceID, Choice.Design ?? "", Num(Choice.Rate ?? 1), Num(Choice.Pitch ?? 1),
                Tone.Raw(), Kind?.ToString().ToLowerInvariant() ?? "narration", Text,
            };
            if (Choice.Engine is VoiceEngine.Orpheus or VoiceEngine.Elevenlabs && Sound is { } s) parts.Add(s.Raw());
            if (Choice.Engine == VoiceEngine.Elevenlabs && Delivery is { } d) parts.Add($"delivery {d}");
            // Unshaded audio keeps the key it always had, so the cache stays warm.
            if (IsShaded) parts.Add($"shade {Shade!.Pitch} {Shade.Pace}");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001F', parts)));
            return Convert.ToHexStringLower(hash);
        }
    }

    /// Swift's String(Double): "1.0", "0.92".
    static string Num(double d) => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// Why a line couldn't be spoken, in words for the narrator.
public sealed class SpeechException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static SpeechException EngineUnavailable(VoiceEngine e) => new(e.IsCloud()
        ? $"{e.Title()} isn’t set up. Set it up in Settings › Voices, or choose another engine on the Voices page."
        : e == VoiceEngine.Apple
            ? "Windows Voices aren’t available on this computer. Choose another engine on the Voices page."
            : $"{e.Title()} isn’t downloaded yet. Download it in Settings › Voices, or choose another engine on the Voices page.");

    public static SpeechException VoiceMissing(string name) =>
        new($"The voice “{name}” isn’t on this computer anymore. Choose another on the Voices page.");

    public static SpeechException Failed(string why, Exception? inner = null) => new($"Unable to speak the line: {why}", inner);
}

/// A text-to-speech engine. Implementations must be safe to call from any thread; `Render`
/// may run on a background thread and several requests may be in flight (the player makes
/// the next two lines while one plays). Anything slow (loading a model) belongs in
/// `Prepare` or the first `Render`, never in the properties: views read them on every redraw.
public interface IVoiceEngine
{
    /// Which engine this is (never Mixed).
    VoiceEngine Engine { get; }

    /// Ready to speak now: installed or downloaded (local), or set up with a key and the
    /// narrator's yes (cloud). Cheap; read on every redraw.
    bool IsReady { get; }

    /// Why it isn't ready, for Settings › Voices and errors ("Download it in Settings › Voices…");
    /// null when ready.
    string? NotReadyReason { get; }

    /// The voices it offers, best first. Cheap (cached by the engine).
    IReadOnlyList<VoiceInfo> Voices { get; }

    /// The voice a choice names, for showing it; null when it isn't on this computer.
    VoiceInfo? Info(VoiceChoice choice);

    /// The engine changes pitch itself for a shade (the system voices take a pitch setting;
    /// Pocket TTS shifts the clip it clones from). When false, the renderer shifts the
    /// finished audio.
    bool ShadesPitch { get; }

    /// The engine changes pace itself for a shade (the system voices take a rate; Pocket TTS
    /// stretches its audio by the shade's, the voice's and the tone's speed together).
    bool ShadesPace { get; }

    /// Speaks one request: 24 kHz mono float samples (SpeechAudio.SampleRate). Throw a
    /// SpeechException with words for the narrator when it can't.
    Task<float[]> Render(SpeechRequest request, CancellationToken ct);

    /// Loads what the first line needs ahead of time (a model). Optional.
    Task Prepare(CancellationToken ct) => Task.CompletedTask;

    /// Frees memory when the engine is turned off. Optional.
    void Unload() { }
}

/// The engines this computer has, by `VoiceEngine` value. The app registers its engines at
/// startup (and tests register fakes): Pocket TTS and Orpheus among them.
public static class VoiceEngineRegistry
{
    static readonly Dictionary<VoiceEngine, IVoiceEngine> engines = [];
    static readonly Lock gate = new();

    public static void Register(IVoiceEngine engine)
    {
        if (engine.Engine == VoiceEngine.Mixed) throw new ArgumentException("Mixed isn't an engine.");
        lock (gate) engines[engine.Engine] = engine;
    }

    public static void Unregister(VoiceEngine engine)
    {
        lock (gate) engines.Remove(engine);
    }

    public static IVoiceEngine? Get(VoiceEngine engine)
    {
        lock (gate) return engines.GetValueOrDefault(engine);
    }

    /// Registered and ready to speak now.
    public static bool IsReady(VoiceEngine engine) => Get(engine)?.IsReady == true;

    public static IReadOnlyList<VoiceInfo> VoicesOf(VoiceEngine engine) => Get(engine)?.Voices ?? [];

    public static VoiceInfo? Info(VoiceChoice choice) => choice.Engine switch
    {
        // Stock voices need no engine to be named.
        VoiceEngine.Pocket when Get(VoiceEngine.Pocket) is null => VoiceInfo.Pocket.FirstOrDefault(v => v.VoiceID == choice.VoiceID),
        VoiceEngine.Orpheus when Get(VoiceEngine.Orpheus) is null => VoiceInfo.Orpheus.FirstOrDefault(v => v.VoiceID == choice.VoiceID),
        _ => Get(choice.Engine)?.Info(choice),
    };
}
