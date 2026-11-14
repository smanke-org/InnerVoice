import Foundation
import Testing
@testable import InnerVoiceKit

/// Pocket TTS beside Qwen3-TTS (Qwen3-TTS was set aside 2026-11-13 and came back
/// 2026-11-14): books saved with Qwen3-TTS keep their voices, the tones, and decoding engine
/// values this version doesn't know.
@Suite struct PocketTTSTests {
    static func book(_ paragraphs: [String]) -> ImportedBook {
        ImportedBook(title: "Test", chapters: [Chapter(title: "Chapter 1", paragraphs: paragraphs.map { Paragraph(text: $0) })])
    }

    /// A project as InnerVoice saved it with Qwen3-TTS, written as JSON the way the old app
    /// did (engine "qwen"), so the test reads real old bytes, not today's types.
    static func oldProject() throws -> Project {
        var p = ProjectBuilder.make(from: Self.book([
            "“Morning,” said Edda Voss.", "“Morning,” said Mara Quinn.", "“Tea?” said Tom Hale.",
            "“Please,” Mara said.", "“Two sugars,” said Edda.", "“Coming,” said Pip.",
        ]), fileName: "t.txt")
        for i in p.characters.indices {
            switch p.characters[i].name {
            case "Edda Voss": p.characters[i].facts = [Fact(kind: .gender, text: "Woman"), Fact(kind: .age, text: "About 60")]
            case "Tom Hale": p.characters[i].facts = [Fact(kind: .gender, text: "Man")]
            case "Pip": p.characters[i].facts = [Fact(kind: .gender, text: "Boy"), Fact(kind: .age, text: "Eight")]
            default: break
            }
        }
        p.voicesCast = Date(timeIntervalSinceReferenceDate: 700_000_000)
        var json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(p)) as! [String: Any]
        func voice(_ id: String, design: String? = nil, source: String = "auto", pitch: Double? = nil) -> [String: Any] {
            var choice: [String: Any] = ["engine": "qwen", "voiceID": id]
            if let design { choice["design"] = design }
            if let pitch { choice["pitch"] = pitch }
            return ["choice": choice, "confidence": 0.7, "source": source]
        }
        var characters = json["characters"] as! [[String: Any]]
        for i in characters.indices {
            let orpheus: [String: Any] = ["choice": ["engine": "orpheus", "voiceID": "leo"], "confidence": 1, "source": "user"]
            switch characters[i]["name"] as! String {
            // A ready-made voice the narrator chose, slowed down a little.
            case "Edda Voss": characters[i]["voices"] = ["qwen": voice("preset:grandmother", source: "user", pitch: 0.95)]
            // A designed voice casting made.
            case "Mara Quinn": characters[i]["voices"] = ["qwen": voice("design", design: "A woman in her thirties with a warm voice.")]
            // A designed voice the narrator wrote, and an Orpheus voice that must not move.
            case "Tom Hale": characters[i]["voices"] = ["qwen": voice("design", design: "A gruff old sailor.", source: "user"), "orpheus": orpheus]
            // A voice cloned on this Mac.
            case "Pip": characters[i]["voices"] = ["qwen": voice("clone:ABC", source: "user")]
            default: break
            }
        }
        json["characters"] = characters
        var settings = json["settings"] as! [String: Any]
        settings["voiceEngine"] = "qwen"
        settings["narratorVoices"] = ["qwen": voice("preset:narrator", source: "user")]
        settings["roleVoices"] = ["men": ["qwen": voice("preset:baritone")], "women": ["qwen": voice("design", design: "A woman.", source: "user")]]
        json["settings"] = settings
        return try JSONDecoder().decode(Project.self, from: JSONSerialization.data(withJSONObject: json))
    }

    static func character(_ p: Project, _ name: String) -> BookCharacter { p.characters.first { $0.name == name }! }

    @Test func booksSavedWithQwenKeepTheirQwenVoices() throws {
        let p = try Self.oldProject()
        #expect(p.settings.voiceEngine == .qwen)
        let edda = Self.character(p, "Edda Voss").voice(for: .qwen)
        #expect(edda?.choice.voiceID == "preset:grandmother" && edda?.source == .user && edda?.choice.pitch == 0.95)
        let mara = Self.character(p, "Mara Quinn").voice(for: .qwen)
        #expect(mara?.choice.voiceID == QwenVoice.designID && mara?.choice.design == "A woman in her thirties with a warm voice.")
        #expect(Self.character(p, "Tom Hale").voice(for: .orpheus) != nil)
        #expect(Self.character(p, "Pip").voice(for: .qwen)?.choice.voiceID == "clone:ABC")
        #expect(p.settings.narratorVoice(for: .qwen)?.choice.voiceID == "preset:narrator")
        #expect(p.characters.allSatisfy { $0.voice(for: .pocket) == nil })
        // Saved back unchanged: still "qwen".
        let saved = String(decoding: try JSONEncoder().encode(p), as: UTF8.self)
        #expect(saved.contains(#""voiceEngine":"qwen""#))
    }

    @Test func qwenCastingDesignsDescribedCharactersAndGivesOthersPresets() throws {
        var p = try Self.oldProject()
        for i in p.characters.indices { p.characters[i].voices = nil }
        p.settings.narratorVoices = nil
        let index = BookIndex(p)
        VoiceCasting.cast(&p, index: index, engine: .qwen, voices: VoiceInfo.qwenPresets)
        #expect(p.settings.narratorVoice(for: .qwen)?.choice.voiceID == "preset:narrator")
        let voiced = p.characters.compactMap { $0.voice(for: .qwen) }
        #expect(!voiced.isEmpty)
        #expect(voiced.allSatisfy { $0.choice.engine == .qwen && ($0.choice.voiceID == QwenVoice.designID ? $0.choice.design?.isEmpty == false : $0.choice.voiceID.hasPrefix(QwenVoice.presetPrefix)) })
        #expect(VoiceInfo.qwenPresets.count == 16 && Set(VoiceInfo.qwenPresets.map(\.voiceID)).count == 16)
    }

    @Test func unknownEnginesDoNotStopAProjectOpening() throws {
        let json = #"{"engine":"someday-engine","voiceID":"x"}"#
        let choice = try JSONDecoder().decode(VoiceChoice.self, from: Data(json.utf8))
        // `.mixed` names no voices, so the player falls back to the narrator.
        #expect(choice.engine == .mixed)
        #expect(try JSONDecoder().decode([VoiceEngine].self, from: Data(#"["apple","chatterbox","orpheus","elevenlabs","mixed","qwen"]"#.utf8))
                == [.apple, .chatterbox, .orpheus, .elevenlabs, .mixed, .qwen])
        // "chatterbox" is a real engine again (2026-11-14); the unreleased 0.4 test build's
        // books used the same "stock:"/"clone:" voice IDs, so they speak with it as saved.
        #expect(String(decoding: try JSONEncoder().encode(VoiceEngine.pocket), as: UTF8.self) == #""pocket""#)
    }

    @Test func tonesSetPocketTTSTemperaturePaceAndPitch() {
        let all = Tone.allCases.map { ($0, PocketDelivery.for($0)) }
        // Within what the model says clearly, and small enough steps to keep the voice.
        #expect(all.allSatisfy { (0.5...0.95).contains($0.1.temperature) && abs($0.1.semitones) <= 1 && (0.9...1.1).contains($0.1.rate) })
        // Neutral and all narration are the model's own default, an even read.
        #expect(PocketDelivery.for(.neutral) == PocketDelivery(temperature: 0.7))
        #expect(PocketDelivery.even.temperature == PocketDelivery.defaultTemperature)
        #expect(Tone.allCases.allSatisfy { PocketDelivery.for($0, narration: true) == .even })
        // Intense tones read more freely than an even read, quiet ones more steadily.
        func t(_ x: Tone) -> Double { PocketDelivery.for(x).temperature }
        #expect(t(.angry) > t(.excited) && t(.excited) > t(.scared) && t(.scared) > t(.sarcastic) && t(.sarcastic) > t(.neutral))
        #expect(t(.neutral) > t(.tender) && t(.tender) > t(.sad) && t(.sad) == t(.whispered))
        // Quick when worked up, slow when sad; higher when excited or scared, lower when sad.
        func r(_ x: Tone) -> Double { PocketDelivery.for(x).rate }
        #expect(r(.excited) > 1 && r(.angry) > 1 && r(.scared) > 1 && r(.sad) < r(.tender) && r(.tender) < 1)
        #expect(PocketDelivery.for(.excited).semitones == 1 && PocketDelivery.for(.sad).semitones == -1)
        // Pocket TTS can't whisper: a whispered line is said softly, the quietest of all.
        #expect(all.allSatisfy { $0.1.volume >= PocketDelivery.for(.whispered).volume })
        #expect(PocketDelivery.for(.whispered).volume < PocketDelivery.for(.sad).volume && PocketDelivery.for(.sad).volume < 1)
    }

    @Test func pitchBecomesWholeSemitonesOfTheClip() {
        #expect(PocketVoice.semitones(pitch: nil) == 0)
        #expect(PocketVoice.semitones(pitch: 1) == 0)
        #expect(PocketVoice.semitones(pitch: 1.19) == 3)
        #expect(PocketVoice.semitones(pitch: 0.89) == -2)
        #expect(PocketVoice.semitones(pitch: 3) == 4)
        #expect(PocketVoice.stockName("stock:tara") == "tara" && PocketVoice.stockName("clone:x") == nil)
        // Clones are never made older or younger by casting: they're a real person's voice.
        let clone = VoiceInfo(engine: .pocket, voiceID: "clone:x", name: "Me", gender: .female, age: .adult, accent: "")
        #expect(VoiceCasting.ageAdjusted(clone, for: .child) == clone.choice)
    }
}
