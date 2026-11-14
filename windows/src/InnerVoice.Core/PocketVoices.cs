// Ported from the Mac's Sources/InnerVoiceKit/PocketTTS.swift: Pocket TTS's voice IDs and how
// it says each tone. Same rules and numbers as the Mac, so a book sounds the same on both.

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
    /// from Kyutai's voice collection).
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
