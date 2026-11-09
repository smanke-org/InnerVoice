@preconcurrency import AVFoundation
import InnerVoiceKit

/// The voices each engine offers on this Mac.
@MainActor
enum VoiceCatalog {
    /// Apple's English voices, best quality first, one per name. Novelty voices (Bells,
    /// Zarvox…) and Siri's voices (which apps can't use) are left out.
    static func appleVoices() -> [VoiceInfo] {
        var best: [String: AVSpeechSynthesisVoice] = [:]
        for v in AVSpeechSynthesisVoice.speechVoices() where v.language.hasPrefix("en") {
            guard !v.voiceTraits.contains(.isNoveltyVoice), !v.identifier.contains(".siri."),
                  v.name.range(of: #"^Voice \d"#, options: .regularExpression) == nil else { continue }
            let key = v.name + "|" + v.language
            if let old = best[key] {
                let better = v.quality.rawValue > old.quality.rawValue
                    || (v.quality == old.quality && old.identifier.contains("super-compact") && !v.identifier.contains("super-compact"))
                guard better else { continue }
            }
            best[key] = v
        }
        return best.values.map(info).sorted { $0.quality != $1.quality ? $0.quality > $1.quality : $0.name < $1.name }
    }

    static func info(_ v: AVSpeechSynthesisVoice) -> VoiceInfo {
        // Premium 3, Enhanced 2, Apple's ordinary voices 1, and the older Eloquence and
        // classic voices (Flo, Eddy, Fred…) 0: they all report "default", but sound robotic.
        let classic = v.identifier.contains(".eloquence.") || v.identifier.contains("speech.synthesis.voice")
        let quality = v.quality == .premium ? 3 : v.quality == .enhanced ? 2 : classic ? 0 : 1
        let personal = v.voiceTraits.contains(.isPersonalVoice)
        var gender: VoiceGender
        switch v.gender {
        case .female: gender = .female
        case .male: gender = .male
        default: gender = knownGenders[v.name] ?? .neutral
        }
        if let g = knownGenders[v.name], v.gender == .unspecified { gender = g }
        let age: VoiceAge = ["Grandma", "Grandpa"].contains(v.name) ? .older : v.name == "Junior" ? .child : .adult
        var detail = [quality == 3 ? "Premium" : quality == 2 ? "Enhanced" : "", personal ? "Personal Voice" : ""].filter { !$0.isEmpty }
        if classic { detail.append("Classic") }
        return VoiceInfo(engine: .apple, voiceID: v.identifier, name: v.name, gender: gender, age: age,
                         accent: accent(v.language, name: v.name), quality: quality, detail: detail.joined(separator: " · "))
    }

    static func accent(_ language: String, name: String) -> String {
        let l = language.lowercased()
        if l.contains("scotland") || l.contains("gbsct") || name == "Fiona" { return "Scottish" }
        switch l.prefix(5) {
        case "en-us": return "American"
        case "en-gb": return "British"
        case "en-au": return "Australian"
        case "en-ie": return "Irish"
        case "en-in": return "Indian"
        case "en-za": return "South African"
        case "en-nz": return "New Zealand"
        case "en-ca": return "Canadian"
        default: return "English"
        }
    }

    /// Apple leaves the gender of many voices unset; their names say it.
    static let knownGenders: [String: VoiceGender] = {
        let female = ["Ava", "Zoe", "Allison", "Susan", "Samantha", "Nicky", "Joelle", "Noelle", "Serena", "Stephanie", "Kate",
                      "Martha", "Karen", "Catherine", "Moira", "Fiona", "Tessa", "Veena", "Tara", "Isha", "Sangeeta", "Matilda",
                      "Siobhan", "Flo", "Grandma", "Sandy", "Shelley", "Kathy", "Victoria", "Agnes", "Vicki"]
        let male = ["Evan", "Nathan", "Tom", "Alex", "Aaron", "Fred", "Daniel", "Arthur", "Oliver", "Jamie", "Lee", "Gordon",
                    "Rishi", "Aman", "Malcolm", "Eddy", "Grandpa", "Reed", "Rocko", "Ralph", "Junior", "Albert", "Bruce"]
        return Dictionary(uniqueKeysWithValues: female.map { ($0, VoiceGender.female) } + male.map { ($0, VoiceGender.male) })
    }()

    static func voices(for engine: VoiceEngine) -> [VoiceInfo] {
        switch engine {
        case .apple: appleVoices()
        case .orpheus: VoiceInfo.orpheus
        case .qwen: VoiceInfo.qwenPresets
        // Every engine that's on and ready, best first within each.
        case .mixed: VoiceEngine.engines.filter { EngineSwitches.shared.usable($0) }.flatMap { voices(for: $0) }
        }
    }

    /// The voice a choice names, for showing it.
    static func info(for choice: VoiceChoice) -> VoiceInfo? {
        switch choice.engine {
        case .apple: AVSpeechSynthesisVoice(identifier: choice.voiceID).map(info)
        case .orpheus: VoiceInfo.orpheus.first { $0.voiceID == choice.voiceID }
        case .qwen: VoiceInfo.qwenPresets.first { $0.voiceID == choice.voiceID }
        case .mixed: nil
        }
    }

    /// "Ava (Premium)", "Tara", or the start of a Qwen3-TTS design.
    static func name(of choice: VoiceChoice) -> String {
        if choice.isNarrator { return "Same as Narrator" }
        switch choice.engine {
        case .qwen where !choice.voiceID.hasPrefix("preset:"):
            // Short enough for a list row, cut at a word.
            return choice.design.map { d in
                guard d.count > 48 else { return d }
                let head = d.prefix(47)
                let cut = head.lastIndex(of: " ").map { head[..<$0] } ?? head
                return cut.trimmingCharacters(in: CharacterSet(charactersIn: " ,;")) + "…"
            } ?? "Designed voice"
        default:
            guard let v = info(for: choice) else { return "Missing voice" }
            return v.name
        }
    }
}
