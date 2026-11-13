import Foundation

/// Chatterbox (Resemble AI, MIT) voices: every voice is cloned from a short clip of speech.
/// A stock voice's clip ships with the app; a voice cloned on this Mac uses the narrator's
/// recording. Chatterbox can't design a voice from a description, so Voice Notes don't
/// change how it sounds.
public enum ChatterboxVoice {
    /// `VoiceChoice.voiceID` of a stock voice: "stock:" and its name ("stock:tara").
    public static let stockPrefix = "stock:"
    /// `VoiceChoice.voiceID` of a voice cloned on this Mac: "clone:" and its id.
    public static let clonePrefix = "clone:"
    /// Chatterbox's own default voice, as a stock clip (the model InnerVoice downloads has no
    /// built-in voice, so its clip was made once from the default voice Resemble ships).
    public static let builtInID = stockPrefix + "chatterbox"

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

/// Projects from before Chatterbox: their Qwen3-TTS voices become Chatterbox voices when
/// the project is read (`InnerVoiceDocument` and `readProject` call `migrate`).
/// - A ready-made Qwen3-TTS voice ("preset:…") becomes the nearest stock voice, with the
///   pitch and pace casting gives a stock voice for that age.
/// - A designed voice ("design") becomes the stock voice casting picks for that character.
/// - A voice cloned on this Mac ("clone:…") keeps its recording; Chatterbox speaks it.
/// - "Same as Narrator" stays.
/// - The narrator's own choices (`source == .user`) stay the narrator's: mapped, never dropped.
/// The project's engine "qwen" already reads as `.chatterbox` (`VoiceEngine.init(from:)`).
public enum LegacyVoices {
    /// Each Qwen3-TTS ready-made voice and the stock voice nearest it (same gender; the
    /// age it was made for, so casting's age shift applies).
    static let presets: [String: (stock: String, age: VoiceAge)] = [
        "narrator": ("chatterbox", .adult),
        "storyteller": ("tara", .adult), "bright": ("leah", .young), "soft": ("mia", .young),
        "brisk": ("zoe", .adult), "dame": ("tara", .older), "grandmother": ("mia", .older),
        "lass": ("jess", .young), "girl": ("zoe", .child),
        "baritone": ("dan", .adult), "hero": ("zac", .young), "gentleman": ("leo", .adult),
        "elder": ("dan", .older), "sailor": ("leo", .older), "clerk": ("zac", .young), "boy": ("zac", .child),
    ]

    static var qwen: String { VoiceEngine.retiredQwen }
    static var chatterbox: String { VoiceEngine.chatterbox.rawValue }

    /// Whether the project still has Qwen3-TTS voices.
    public static func needsMigration(_ project: Project) -> Bool {
        project.characters.contains { $0.voices?[qwen] != nil }
            || project.settings.narratorVoices?[qwen] != nil
            || project.settings.roleVoices?.values.contains { $0[qwen] != nil } == true
    }

    /// The stock voice a ready-made, cloned or "Same as Narrator" voice becomes; nil for a
    /// designed voice (casting picks one) or anything unknown.
    static func mapped(_ v: CastVoice) -> CastVoice? {
        var choice = v.choice
        choice.engine = .chatterbox
        choice.design = nil
        if choice.isNarrator || choice.voiceID.hasPrefix(ChatterboxVoice.clonePrefix) {
            return CastVoice(choice: choice, confidence: v.confidence, source: v.source)
        }
        guard choice.voiceID.hasPrefix("preset:"), let target = presets[String(choice.voiceID.dropFirst("preset:".count))],
              let stock = VoiceInfo.chatterbox.first(where: { $0.voiceID == ChatterboxVoice.stockPrefix + target.stock }) else { return nil }
        var out = VoiceCasting.ageAdjusted(stock, for: target.age)
        // The narrator's own speed and pitch on the old voice carry over.
        if let r = v.choice.rate { out.rate = ((out.rate ?? 1) * r * 100).rounded() / 100 }
        if let p = v.choice.pitch { out.pitch = ((out.pitch ?? 1) * p * 100).rounded() / 100 }
        return CastVoice(choice: out, confidence: v.confidence, source: v.source)
    }

    /// Turns the project's Qwen3-TTS voices into Chatterbox voices. Returns whether anything
    /// changed (the document then saves the change).
    @discardableResult
    public static func migrate(_ project: inout Project) -> Bool {
        guard needsMigration(project) else { return false }
        // Designed voices, to cast: who had one, and whether it was the narrator's choice.
        var designed: [UUID: VoiceSource] = [:]
        var narratorDesigned: VoiceSource?
        var rolesDesigned: [VoiceRole: VoiceSource] = [:]
        let hadNoVoice = Set(project.characters.filter { $0.voices?[qwen] == nil && $0.voices?[chatterbox] == nil }.map(\.id))

        for i in project.characters.indices {
            guard var all = project.characters[i].voices, let old = all.removeValue(forKey: qwen) else { continue }
            if all[chatterbox] == nil {
                if let new = mapped(old) { all[chatterbox] = new } else { designed[project.characters[i].id] = old.source }
            }
            project.characters[i].voices = all.isEmpty ? nil : all
        }
        if var all = project.settings.narratorVoices, let old = all.removeValue(forKey: qwen) {
            if all[chatterbox] == nil {
                if let new = mapped(old) { all[chatterbox] = new } else { narratorDesigned = old.source }
            }
            project.settings.narratorVoices = all.isEmpty ? nil : all
        }
        if var roles = project.settings.roleVoices {
            for (key, var one) in roles {
                guard let old = one.removeValue(forKey: qwen) else { continue }
                if one[chatterbox] == nil {
                    if let new = mapped(old) { one[chatterbox] = new } else if let r = VoiceRole(rawValue: key) { rolesDesigned[r] = old.source }
                }
                roles[key] = one.isEmpty ? nil : one
            }
            project.settings.roleVoices = roles.isEmpty ? nil : roles
        }

        guard !designed.isEmpty || narratorDesigned != nil || !rolesDesigned.isEmpty else { return true }
        // Designed voices: what casting picks, keeping every voice already there (mapped or
        // the narrator's) so nobody else's voice moves. The date casting last ran stays.
        let castDate = project.voicesCast
        let stock = VoiceInfo.chatterbox
        let index = BookIndex(project)
        VoiceCasting.cast(&project, index: index, engine: .chatterbox, voices: stock, keepExisting: true)
        project.voicesCast = castDate
        for i in project.characters.indices {
            let id = project.characters[i].id
            if hadNoVoice.contains(id) {
                // Casting only stood in for the old designs; characters with no voice keep none.
                project.characters[i].setVoice(nil, for: .chatterbox)
                continue
            }
            guard let source = designed[id] else { continue }
            if project.characters[i].voice(for: .chatterbox) == nil {
                // Casting only voices characters with lines: the best fit for the rest.
                let p = VoiceCasting.profile(project.characters[i], project: project, index: index)
                if let best = stock.max(by: { VoiceCasting.score($0, p) < VoiceCasting.score($1, p) }), VoiceCasting.score(best, p) >= 0 {
                    project.characters[i].setVoice(CastVoice(choice: VoiceCasting.ageAdjusted(best, for: p.age), confidence: 0.5, source: source), for: .chatterbox)
                } else if let first = stock.first {
                    project.characters[i].setVoice(CastVoice(choice: first.choice, confidence: 0.3, source: source), for: .chatterbox)
                }
            } else if source == .user, var v = project.characters[i].voice(for: .chatterbox) {
                v.source = .user
                project.characters[i].setVoice(v, for: .chatterbox)
            }
        }
        if let source = narratorDesigned {
            if project.settings.narratorVoice(for: .chatterbox) == nil, let first = stock.first {
                project.settings.setNarratorVoice(CastVoice(choice: first.choice, confidence: 0.9, source: source), for: .chatterbox)
            } else if source == .user, var v = project.settings.narratorVoice(for: .chatterbox) {
                v.source = .user
                project.settings.setNarratorVoice(v, for: .chatterbox)
            }
        }
        for (role, source) in rolesDesigned {
            if project.settings.roleVoice(role, for: .chatterbox) == nil {
                // A role the Casting Style doesn't use now: the stock voice it would get.
                let want: VoiceGender = role == .men ? .male : role == .women ? .female : .neutral
                let pick = stock.first { $0.gender == want && $0.voiceID != ChatterboxVoice.builtInID } ?? stock.last
                if let pick { project.settings.setRoleVoice(CastVoice(choice: pick.choice, confidence: 0.6, source: source), role, for: .chatterbox) }
            } else if source == .user, var v = project.settings.roleVoice(role, for: .chatterbox) {
                v.source = .user
                project.settings.setRoleVoice(v, role, for: .chatterbox)
            }
        }
        return true
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
