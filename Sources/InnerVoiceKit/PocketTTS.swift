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
    /// from Kyutai's voice collection). It's the narrator's voice in books moved over from
    /// Qwen3-TTS.
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

/// Projects from before Pocket TTS: their Qwen3-TTS voices become Pocket TTS voices when
/// the project is read (`InnerVoiceDocument` and `readProject` call `migrate`).
/// - A ready-made Qwen3-TTS voice ("preset:…") becomes the nearest stock voice, with the
///   pitch and pace casting gives a stock voice for that age.
/// - A designed voice ("design") becomes the stock voice casting picks for that character.
/// - A voice cloned on this Mac ("clone:…") keeps its recording; Pocket TTS speaks it.
/// - "Same as Narrator" stays.
/// - The narrator's own choices (`source == .user`) stay the narrator's: mapped, never dropped.
/// The project's engine "qwen" already reads as `.pocket` (`VoiceEngine.init(from:)`).
public enum LegacyVoices {
    /// Each Qwen3-TTS ready-made voice and the stock voice nearest it (same gender; the
    /// age it was made for, so casting's age shift applies).
    static let presets: [String: (stock: String, age: VoiceAge)] = [
        "narrator": ("alba", .adult),
        "storyteller": ("tara", .adult), "bright": ("leah", .young), "soft": ("mia", .young),
        "brisk": ("zoe", .adult), "dame": ("tara", .older), "grandmother": ("mia", .older),
        "lass": ("jess", .young), "girl": ("zoe", .child),
        "baritone": ("dan", .adult), "hero": ("zac", .young), "gentleman": ("leo", .adult),
        "elder": ("dan", .older), "sailor": ("leo", .older), "clerk": ("zac", .young), "boy": ("zac", .child),
    ]

    static var qwen: String { VoiceEngine.retiredQwen }
    static var pocket: String { VoiceEngine.pocket.rawValue }

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
        choice.engine = .pocket
        choice.design = nil
        if choice.isNarrator || choice.voiceID.hasPrefix(PocketVoice.clonePrefix) {
            return CastVoice(choice: choice, confidence: v.confidence, source: v.source)
        }
        guard choice.voiceID.hasPrefix("preset:"), let target = presets[String(choice.voiceID.dropFirst("preset:".count))],
              let stock = VoiceInfo.pocket.first(where: { $0.voiceID == PocketVoice.stockPrefix + target.stock }) else { return nil }
        var out = VoiceCasting.ageAdjusted(stock, for: target.age)
        // The narrator's own speed and pitch on the old voice carry over.
        if let r = v.choice.rate { out.rate = ((out.rate ?? 1) * r * 100).rounded() / 100 }
        if let p = v.choice.pitch { out.pitch = ((out.pitch ?? 1) * p * 100).rounded() / 100 }
        return CastVoice(choice: out, confidence: v.confidence, source: v.source)
    }

    /// Turns the project's Qwen3-TTS voices into Pocket TTS voices. Returns whether anything
    /// changed (the document then saves the change).
    @discardableResult
    public static func migrate(_ project: inout Project) -> Bool {
        guard needsMigration(project) else { return false }
        // Designed voices, to cast: who had one, and whether it was the narrator's choice.
        var designed: [UUID: VoiceSource] = [:]
        var narratorDesigned: VoiceSource?
        var rolesDesigned: [VoiceRole: VoiceSource] = [:]
        let hadNoVoice = Set(project.characters.filter { $0.voices?[qwen] == nil && $0.voices?[pocket] == nil }.map(\.id))

        for i in project.characters.indices {
            guard var all = project.characters[i].voices, let old = all.removeValue(forKey: qwen) else { continue }
            if all[pocket] == nil {
                if let new = mapped(old) { all[pocket] = new } else { designed[project.characters[i].id] = old.source }
            }
            project.characters[i].voices = all.isEmpty ? nil : all
        }
        if var all = project.settings.narratorVoices, let old = all.removeValue(forKey: qwen) {
            if all[pocket] == nil {
                if let new = mapped(old) { all[pocket] = new } else { narratorDesigned = old.source }
            }
            project.settings.narratorVoices = all.isEmpty ? nil : all
        }
        if var roles = project.settings.roleVoices {
            for (key, var one) in roles {
                guard let old = one.removeValue(forKey: qwen) else { continue }
                if one[pocket] == nil {
                    if let new = mapped(old) { one[pocket] = new } else if let r = VoiceRole(rawValue: key) { rolesDesigned[r] = old.source }
                }
                roles[key] = one.isEmpty ? nil : one
            }
            project.settings.roleVoices = roles.isEmpty ? nil : roles
        }

        guard !designed.isEmpty || narratorDesigned != nil || !rolesDesigned.isEmpty else { return true }
        // Designed voices: what casting picks, keeping every voice already there (mapped or
        // the narrator's) so nobody else's voice moves. The date casting last ran stays.
        let castDate = project.voicesCast
        let stock = VoiceInfo.pocket
        let index = BookIndex(project)
        VoiceCasting.cast(&project, index: index, engine: .pocket, voices: stock, keepExisting: true)
        project.voicesCast = castDate
        for i in project.characters.indices {
            let id = project.characters[i].id
            if hadNoVoice.contains(id) {
                // Casting only stood in for the old designs; characters with no voice keep none.
                project.characters[i].setVoice(nil, for: .pocket)
                continue
            }
            guard let source = designed[id] else { continue }
            if project.characters[i].voice(for: .pocket) == nil {
                // Casting only voices characters with lines: the best fit for the rest.
                let p = VoiceCasting.profile(project.characters[i], project: project, index: index)
                if let best = stock.max(by: { VoiceCasting.score($0, p) < VoiceCasting.score($1, p) }), VoiceCasting.score(best, p) >= 0 {
                    project.characters[i].setVoice(CastVoice(choice: VoiceCasting.ageAdjusted(best, for: p.age), confidence: 0.5, source: source), for: .pocket)
                } else if let first = stock.first {
                    project.characters[i].setVoice(CastVoice(choice: first.choice, confidence: 0.3, source: source), for: .pocket)
                }
            } else if source == .user, var v = project.characters[i].voice(for: .pocket) {
                v.source = .user
                project.characters[i].setVoice(v, for: .pocket)
            }
        }
        if let source = narratorDesigned {
            if project.settings.narratorVoice(for: .pocket) == nil, let first = stock.first {
                project.settings.setNarratorVoice(CastVoice(choice: first.choice, confidence: 0.9, source: source), for: .pocket)
            } else if source == .user, var v = project.settings.narratorVoice(for: .pocket) {
                v.source = .user
                project.settings.setNarratorVoice(v, for: .pocket)
            }
        }
        for (role, source) in rolesDesigned {
            if project.settings.roleVoice(role, for: .pocket) == nil {
                // A role the Casting Style doesn't use now: the stock voice it would get.
                let want: VoiceGender = role == .men ? .male : role == .women ? .female : .neutral
                let pick = stock.first { $0.gender == want && $0.voiceID != PocketVoice.builtInID } ?? stock.last
                if let pick { project.settings.setRoleVoice(CastVoice(choice: pick.choice, confidence: 0.6, source: source), role, for: .pocket) }
            } else if source == .user, var v = project.settings.roleVoice(role, for: .pocket) {
                v.source = .user
                project.settings.setRoleVoice(v, role, for: .pocket)
            }
        }
        return true
    }
}
