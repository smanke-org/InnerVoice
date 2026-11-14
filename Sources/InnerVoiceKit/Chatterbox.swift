import Foundation

/// Chatterbox (Resemble AI, MIT), the third on-device engine that clones (engine
/// "chatterbox"). It shares Pocket TTS's stock clips and the voices cloned on this Mac
/// (voiceID "stock:<name>" or "clone:<id>"), plus its own default voice. Its speech tokenizer
/// and the flow decoder come from Alibaba's CosyVoice and its speaker encoder (CAM++) from
/// Alibaba's 3D-Speaker, so it carries a "Uses parts developed in China" label (the user
/// chose that on 2026-11-14, like Qwen3-TTS). It can't design a voice from a description.
public enum ChatterboxVoice {
    /// Chatterbox's own default voice, as a stock clip (Resources/StockVoices/chatterbox.m4a,
    /// made once from the default voice Resemble ships with Chatterbox Turbo).
    public static let builtInID = PocketVoice.stockPrefix + "chatterbox"
}

public extension VoiceInfo {
    /// Chatterbox's voices: its own, then Pocket TTS's stock voices (the same clips).
    static let chatterbox: [VoiceInfo] =
        [VoiceInfo(engine: .chatterbox, voiceID: ChatterboxVoice.builtInID, name: "Chatterbox", gender: .female, age: .adult,
                   accent: "American", quality: 3, detail: "The engine’s own voice; calm, clear")]
        + pocket.map { v in
            var c = v
            c.engine = .chatterbox
            return c
        }
}

/// How Chatterbox says a line in each tone. Chatterbox has no tone words; it has two dials
/// (the values are the model's own, 0…1):
/// - `exaggeration` (emotion exaggeration): 0.5 is its default, an even read. Higher is more
///   intense and more animated; it also speeds the speech up, and past about 0.9 it gets
///   unsteady (Resemble's notes on the model), so nothing here goes above 0.8. Lower is
///   flatter and calmer.
/// - `cfgWeight` (how closely it follows the clip's pacing): 0.5 is its default. Resemble's
///   advice for dramatic lines is a higher exaggeration with a lower weight (about 0.7 and
///   0.3): the lower weight slows the delivery down again, so an intense line doesn't rush.
///
/// The values were chosen by reasoning from those two notes, not by listening:
/// - Angry and excited are the most intense, so the highest exaggeration with the lowest
///   weight (0.8/0.3 and 0.75/0.35).
/// - Scared is tense but held in (0.7/0.4); sarcastic and confused only color the words a
///   little (0.6 and 0.55, weight 0.45: the words carry them).
/// - Tender and sad are quieter than an even read: exaggeration below 0.5 flattens them;
///   sad gets the lower weight (0.35/0.4) so it comes out slower than tender (0.4/0.45).
/// - Whispered: Chatterbox can't whisper. The lowest exaggeration (0.3) and a quieter
///   line (`volume` 0.7, about −3 dB after leveling) are the nearest it comes.
/// - Narration is always read even (0.5/0.5), like an audiobook narrator.
public struct ChatterboxDelivery: Hashable, Sendable {
    public var exaggeration: Double
    public var cfgWeight: Double
    /// Loudness after leveling to −20 dB, 1 is unchanged.
    public var volume: Double

    public static let even = ChatterboxDelivery(exaggeration: 0.5, cfgWeight: 0.5, volume: 1)

    /// `narration`: the line is narration (read even whatever its tone says).
    public static func `for`(_ tone: Tone, narration: Bool = false) -> ChatterboxDelivery {
        if narration { return .even }
        switch tone {
        case .neutral: return .even
        case .excited: return .init(exaggeration: 0.75, cfgWeight: 0.35, volume: 1)
        case .angry: return .init(exaggeration: 0.8, cfgWeight: 0.3, volume: 1)
        case .scared: return .init(exaggeration: 0.7, cfgWeight: 0.4, volume: 1)
        case .sarcastic: return .init(exaggeration: 0.6, cfgWeight: 0.45, volume: 1)
        case .confused: return .init(exaggeration: 0.55, cfgWeight: 0.45, volume: 1)
        case .tender: return .init(exaggeration: 0.4, cfgWeight: 0.45, volume: 1)
        case .sad: return .init(exaggeration: 0.35, cfgWeight: 0.4, volume: 1)
        case .whispered: return .init(exaggeration: 0.3, cfgWeight: 0.5, volume: 0.7)
        }
    }
}

/// Chatterbox sometimes runs on after a short line: the line, a pause, then a second or so
/// of made-up speech ("The path was underwater" came back as 2 s of the line, 0.7 s of
/// silence and 1.5 s more; 2026-11-13). The run-on follows a clear pause once the line has
/// had time to be said, so the audio is cut there. A pause that comes before then (a comma
/// in a long line) is kept.
public enum ChatterboxRunOn {
    /// About how long the text takes to say: 14 characters a second, at least 0.6 s.
    public static func expectedSeconds(_ text: String) -> Double {
        max(0.6, Double(text.trimmingCharacters(in: .whitespacesAndNewlines).count) / 14)
    }

    /// The audio up to the first pause of 0.3 s or more that comes after at least 90% of
    /// the expected time of speech, with 0.1 s of the pause kept. Unchanged when there is
    /// no such pause or nothing follows it.
    public static func trim(_ samples: [Float], sampleRate: Double, text: String) -> [Float] {
        let frame = max(1, Int(sampleRate * 0.02))
        guard samples.count > frame * 10 else { return samples }
        var rms: [Float] = []
        var i = 0
        while i < samples.count {
            let end = min(samples.count, i + frame)
            var e: Float = 0
            for k in i..<end { e += samples[k] * samples[k] }
            rms.append((e / Float(end - i)).squareRoot())
            i = end
        }
        guard let loudest = rms.max(), loudest > 0 else { return samples }
        let silent = rms.map { $0 < loudest * 0.03 }
        let need = expectedSeconds(text) * 0.9
        let minGap = 15 // frames: 0.3 s
        var speech = 0 // frames of speech so far
        var f = 0
        while f < silent.count {
            guard silent[f] else { speech += 1; f += 1; continue }
            var g = f
            while g < silent.count, silent[g] { g += 1 }
            if speech > 0, g < silent.count, g - f >= minGap, Double(speech) * 0.02 >= need {
                return Array(samples.prefix(min(samples.count, (f + 5) * frame)))
            }
            f = g
        }
        return samples
    }
}
