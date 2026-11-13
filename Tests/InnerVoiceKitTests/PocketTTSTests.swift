import Foundation
import Testing
@testable import InnerVoiceKit

/// Pocket TTS replaced Qwen3-TTS (2026-11-13): old books switch on read, the tones, and
/// decoding engine values this version doesn't know.
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

    @Test func oldEngineReadsAsPocketTTS() throws {
        let p = try Self.oldProject()
        #expect(p.settings.voiceEngine == .pocket)
        // The voices still sit under "qwen" until migrated.
        #expect(LegacyVoices.needsMigration(p))
        #expect(Self.character(p, "Mara Quinn").voices?["qwen"]?.choice.engine == .pocket)
    }

    @Test func oldBooksSwitchToPocketTTS() throws {
        var p = try Self.oldProject()
        #expect(LegacyVoices.migrate(&p))
        #expect(!LegacyVoices.needsMigration(p))
        #expect(p.characters.allSatisfy { $0.voices?["qwen"] == nil })

        // Ready-made → nearest stock voice (Kind Grandmother → Mia, an older woman: lower and
        // slower), the narrator's own speed and pitch kept on top, still the narrator's choice.
        let edda = try #require(Self.character(p, "Edda Voss").voice(for: .pocket))
        #expect(edda.choice.voiceID == "stock:mia" && edda.source == .user)
        #expect(edda.choice.pitch == 0.85 && edda.choice.rate == 0.92)
        #expect(edda.choice.design == nil)

        // Designed → the stock voice casting picks for that character, a woman's for Mara.
        let mara = try #require(Self.character(p, "Mara Quinn").voice(for: .pocket))
        #expect(mara.choice.voiceID.hasPrefix("stock:") && mara.source == .auto)
        #expect(VoiceInfo.pocket.first { $0.voiceID == mara.choice.voiceID }?.gender == .female)

        // A designed voice the narrator chose stays theirs; the Orpheus voice doesn't move.
        let tom = Self.character(p, "Tom Hale")
        let tomVoice = try #require(tom.voice(for: .pocket))
        #expect(tomVoice.source == .user)
        #expect(VoiceInfo.pocket.first { $0.voiceID == tomVoice.choice.voiceID }?.gender == .male)
        #expect(tom.voice(for: .orpheus)?.choice.voiceID == "leo" && tom.voice(for: .orpheus)?.source == .user)

        // A clone keeps its recording, now spoken by Pocket TTS.
        let pip = try #require(Self.character(p, "Pip").voice(for: .pocket))
        #expect(pip.choice == VoiceChoice(engine: .pocket, voiceID: "clone:ABC") && pip.source == .user)

        // The narrator's ready-made voice → the engine's own voice (Alba), still the narrator's choice.
        let narrator = try #require(p.settings.narratorVoice(for: .pocket))
        #expect(narrator.choice.voiceID == PocketVoice.builtInID && narrator.source == .user)
        #expect(PocketVoice.builtInID == "stock:alba")
        #expect(p.settings.narratorVoices?["qwen"] == nil)

        // Roles: ready-made → stock; designed (the narrator's) → a stock voice of that gender, still theirs.
        #expect(p.settings.roleVoice(.men, for: .pocket)?.choice.voiceID == "stock:dan")
        let women = try #require(p.settings.roleVoice(.women, for: .pocket))
        #expect(women.source == .user && VoiceInfo.pocket.first { $0.voiceID == women.choice.voiceID }?.gender == .female)

        // Nobody without a voice got one, and the date casting last ran is kept.
        #expect(Self.character(p, "Edda Voss").voices?.count == 1)
        #expect(p.voicesCast == Date(timeIntervalSinceReferenceDate: 700_000_000))
        #expect(p.characters.filter { $0.name != "Edda Voss" && $0.name != "Mara Quinn" && $0.name != "Tom Hale" && $0.name != "Pip" }
            .allSatisfy { $0.voice(for: .pocket) == nil })

        // Saved and read again, nothing changes and nothing is left to migrate.
        var again = try JSONDecoder().decode(Project.self, from: JSONEncoder().encode(p))
        #expect(!LegacyVoices.migrate(&again))
        #expect(again == p)
        let saved = String(decoding: try JSONEncoder().encode(p), as: UTF8.self)
        #expect(!saved.contains("\"qwen\""))
    }

    @Test func projectsWithoutQwenAreLeftAlone() throws {
        var p = ProjectBuilder.make(from: Self.book(["“Hi,” said Ann."]), fileName: "t.txt")
        p.characters[0].setVoice(CastVoice(choice: VoiceChoice(engine: .orpheus, voiceID: "tara"), confidence: 1, source: .user), for: .orpheus)
        let before = p
        #expect(!LegacyVoices.migrate(&p))
        #expect(p == before)
    }

    @Test func everyQwenPresetHasAStockVoiceOfItsGender() {
        let genders: [String: VoiceGender] = [
            "storyteller": .female, "bright": .female, "soft": .female, "brisk": .female, "dame": .female, "grandmother": .female,
            "lass": .female, "girl": .female, "baritone": .male, "hero": .male, "gentleman": .male, "elder": .male, "sailor": .male,
            "clerk": .male, "boy": .male,
        ]
        #expect(LegacyVoices.presets.count == 16)
        for (preset, gender) in genders {
            let mapped = LegacyVoices.mapped(CastVoice(choice: VoiceChoice(engine: .pocket, voiceID: "preset:" + preset), confidence: 1, source: .auto))
            let info = VoiceInfo.pocket.first { $0.voiceID == mapped?.choice.voiceID }
            #expect(info?.gender == gender, "\(preset)")
        }
        // Children sound higher and a little quicker.
        let girl = LegacyVoices.mapped(CastVoice(choice: VoiceChoice(engine: .pocket, voiceID: "preset:girl"), confidence: 1, source: .auto))
        #expect(PocketVoice.semitones(pitch: girl?.choice.pitch) == 3 && girl?.choice.rate == 1.05)
    }

    @Test func unknownEnginesDoNotStopAProjectOpening() throws {
        let json = #"{"engine":"someday-engine","voiceID":"x"}"#
        let choice = try JSONDecoder().decode(VoiceChoice.self, from: Data(json.utf8))
        // `.mixed` names no voices, so the player falls back to the narrator.
        #expect(choice.engine == .mixed)
        #expect(try JSONDecoder().decode([VoiceEngine].self, from: Data(#"["apple","chatterbox","orpheus","elevenlabs","mixed","qwen"]"#.utf8))
                == [.apple, .pocket, .orpheus, .elevenlabs, .mixed, .pocket])
        // Saved as "pocket"; the unreleased test build's "chatterbox" reads as Pocket TTS.
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
