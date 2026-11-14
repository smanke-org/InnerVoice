import Foundation

/// Pocket TTS (Kyutai, CC BY 4.0) voices: every voice is cloned from a short clip of speech.
/// A stock voice's clip ships with the app; a voice cloned on this Mac uses the narrator's
/// recording. Pocket TTS can't design a voice from a description, so Voice Notes don't
/// change how it sounds.
public enum PocketVoice {
    /// `VoiceChoice.voiceID` of a stock voice: "stock:" and its name ("stock:tara").
    public static let stockPrefix = "stock:"
    /// `VoiceChoice.voiceID` of a voice cloned on this Mac: "clone:" and its id.
    public static let clonePrefix = "clone:"
    /// Pocket TTS's own default voice, Alba (a clip voice-acted by Alba MacKenna, CC BY 4.0,
    /// from Kyutai's voice collection).
    public static let builtInID = stockPrefix + "alba"

    /// The stock voice's name ("tara") for a voice ID, or nil for any other voice.
    public static func stockName(_ voiceID: String) -> String? {
        voiceID.hasPrefix(stockPrefix) ? String(voiceID.dropFirst(stockPrefix.count)) : nil
    }

    /// The semitones a voice's pitch (1 is its own) shifts the clip by, rounded to whole
    /// semitones and kept to ±4 (more stops sounding like the same person).
    public static func semitones(pitch: Double?) -> Int {
        guard let pitch, pitch > 0 else { return 0 }
        return max(-4, min(4, Int((12 * log2(pitch)).rounded())))
    }
}

/// How Pocket TTS says a line in each tone.
///
/// Pocket TTS has no tone, emotion or speed setting: it carries on in the manner of the clip
/// it clones from and reads the line's own punctuation. What it does have is the
/// temperature of its sampler (`temperature`; the model's default is 0.7): the noise it
/// draws each step is scaled by its square root. Higher lets the delivery wander more (a
/// livelier, less even read), lower keeps it close to the most likely read (steadier,
/// flatter). Its other settings (decode steps, the end-of-speech threshold) change quality
/// and where it stops, not how a line feels, so they stay at the model's values.
///
/// So each tone is three things this app can do around the model, plus the temperature:
/// - `temperature`: angry and excited go up to 0.95 and 0.9, scared to 0.85, sarcastic and
///   confused stay near the default (0.75: the words carry them); tender, sad and whispered
///   go down to 0.55–0.5 for a held, even read. Kept within 0.5…0.95: far above the
///   default the words start to slur, far below it every line comes out alike.
/// - `semitones`: the clip is shifted before cloning, as for a shade. Excited and scared
///   speak a semitone higher, sad a semitone lower. One semitone keeps the voice.
/// - `rate`: the finished audio is stretched without changing its pitch. Excited, angry
///   and scared come a little quicker (4–6%); tender, sad and whispered slower (5–8%).
/// - `volume`: after leveling to −20 dB. Pocket TTS can't whisper: whispered is said softly
///   (0.7, about −3 dB) and slowly, the nearest it comes. Tender and sad are a touch
///   quieter (0.9).
/// - Narration is always read even (the defaults), like an audiobook narrator.
public struct PocketDelivery: Hashable, Sendable {
    public var temperature: Double
    /// Semitones the clip shifts by for this tone (added to a shade's and casting's).
    public var semitones: Int
    /// Speed of the finished audio, 1 is as said.
    public var rate: Double
    /// Loudness after leveling to −20 dB, 1 is unchanged.
    public var volume: Double

    public init(temperature: Double, semitones: Int = 0, rate: Double = 1, volume: Double = 1) {
        self.temperature = temperature
        self.semitones = semitones
        self.rate = rate
        self.volume = volume
    }

    /// The model's own temperature.
    public static let defaultTemperature = 0.7
    public static let even = PocketDelivery(temperature: defaultTemperature)

    /// `narration`: the line is narration (read even whatever its tone says).
    public static func `for`(_ tone: Tone, narration: Bool = false) -> PocketDelivery {
        if narration { return .even }
        switch tone {
        case .neutral: return .even
        case .excited: return .init(temperature: 0.9, semitones: 1, rate: 1.06)
        case .angry: return .init(temperature: 0.95, rate: 1.04)
        case .scared: return .init(temperature: 0.85, semitones: 1, rate: 1.06)
        case .sarcastic: return .init(temperature: 0.75, rate: 0.97)
        case .confused: return .init(temperature: 0.75, rate: 0.96)
        case .tender: return .init(temperature: 0.55, rate: 0.95, volume: 0.9)
        case .sad: return .init(temperature: 0.5, semitones: -1, rate: 0.92, volume: 0.9)
        case .whispered: return .init(temperature: 0.5, rate: 0.95, volume: 0.7)
        }
    }
}
