import Foundation
import Testing
@testable import InnerVoiceKit

/// Chatterbox replaced Qwen3-TTS (2026-11-13): old books switch on read, the tone dials,
/// and decoding engine values this version doesn't know.
@Suite struct ChatterboxTests {
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

    @Test func oldEngineReadsAsChatterbox() throws {
        let p = try Self.oldProject()
        #expect(p.settings.voiceEngine == .chatterbox)
        // The voices still sit under "qwen" until migrated.
        #expect(LegacyVoices.needsMigration(p))
        #expect(Self.character(p, "Mara Quinn").voices?["qwen"]?.choice.engine == .chatterbox)
    }

    @Test func oldBooksSwitchToChatterbox() throws {
        var p = try Self.oldProject()
        #expect(LegacyVoices.migrate(&p))
        #expect(!LegacyVoices.needsMigration(p))
        #expect(p.characters.allSatisfy { $0.voices?["qwen"] == nil })

        // Ready-made → nearest stock voice (Kind Grandmother → Mia, an older woman: lower and
        // slower), the narrator's own speed and pitch kept on top, still the narrator's choice.
        let edda = try #require(Self.character(p, "Edda Voss").voice(for: .chatterbox))
        #expect(edda.choice.voiceID == "stock:mia" && edda.source == .user)
        #expect(edda.choice.pitch == 0.85 && edda.choice.rate == 0.92)
        #expect(edda.choice.design == nil)

        // Designed → the stock voice casting picks for that character, a woman's for Mara.
        let mara = try #require(Self.character(p, "Mara Quinn").voice(for: .chatterbox))
        #expect(mara.choice.voiceID.hasPrefix("stock:") && mara.source == .auto)
        #expect(VoiceInfo.chatterbox.first { $0.voiceID == mara.choice.voiceID }?.gender == .female)

        // A designed voice the narrator chose stays theirs; the Orpheus voice doesn't move.
        let tom = Self.character(p, "Tom Hale")
        let tomVoice = try #require(tom.voice(for: .chatterbox))
        #expect(tomVoice.source == .user)
        #expect(VoiceInfo.chatterbox.first { $0.voiceID == tomVoice.choice.voiceID }?.gender == .male)
        #expect(tom.voice(for: .orpheus)?.choice.voiceID == "leo" && tom.voice(for: .orpheus)?.source == .user)

        // A clone keeps its recording, now spoken by Chatterbox.
        let pip = try #require(Self.character(p, "Pip").voice(for: .chatterbox))
        #expect(pip.choice == VoiceChoice(engine: .chatterbox, voiceID: "clone:ABC") && pip.source == .user)

        // The narrator's ready-made voice → the engine's own voice, still the narrator's choice.
        let narrator = try #require(p.settings.narratorVoice(for: .chatterbox))
        #expect(narrator.choice.voiceID == ChatterboxVoice.builtInID && narrator.source == .user)
        #expect(p.settings.narratorVoices?["qwen"] == nil)

        // Roles: ready-made → stock; designed (the narrator's) → a stock voice of that gender, still theirs.
        #expect(p.settings.roleVoice(.men, for: .chatterbox)?.choice.voiceID == "stock:dan")
        let women = try #require(p.settings.roleVoice(.women, for: .chatterbox))
        #expect(women.source == .user && VoiceInfo.chatterbox.first { $0.voiceID == women.choice.voiceID }?.gender == .female)

        // Nobody without a voice got one, and the date casting last ran is kept.
        #expect(Self.character(p, "Edda Voss").voices?.count == 1)
        #expect(p.voicesCast == Date(timeIntervalSinceReferenceDate: 700_000_000))
        #expect(p.characters.filter { $0.name != "Edda Voss" && $0.name != "Mara Quinn" && $0.name != "Tom Hale" && $0.name != "Pip" }
            .allSatisfy { $0.voice(for: .chatterbox) == nil })

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
            let mapped = LegacyVoices.mapped(CastVoice(choice: VoiceChoice(engine: .chatterbox, voiceID: "preset:" + preset), confidence: 1, source: .auto))
            let info = VoiceInfo.chatterbox.first { $0.voiceID == mapped?.choice.voiceID }
            #expect(info?.gender == gender, "\(preset)")
        }
        // Children sound higher and a little quicker.
        let girl = LegacyVoices.mapped(CastVoice(choice: VoiceChoice(engine: .chatterbox, voiceID: "preset:girl"), confidence: 1, source: .auto))
        #expect(ChatterboxVoice.semitones(pitch: girl?.choice.pitch) == 3 && girl?.choice.rate == 1.05)
    }

    @Test func unknownEnginesDoNotStopAProjectOpening() throws {
        let json = #"{"engine":"someday-engine","voiceID":"x"}"#
        let choice = try JSONDecoder().decode(VoiceChoice.self, from: Data(json.utf8))
        // `.mixed` names no voices, so the player falls back to the narrator.
        #expect(choice.engine == .mixed)
        #expect(try JSONDecoder().decode([VoiceEngine].self, from: Data(#"["apple","chatterbox","orpheus","elevenlabs","mixed","qwen"]"#.utf8))
                == [.apple, .chatterbox, .orpheus, .elevenlabs, .mixed, .chatterbox])
        #expect(String(decoding: try JSONEncoder().encode(VoiceEngine.chatterbox), as: UTF8.self) == #""chatterbox""#)
    }

    @Test func tonesSetChatterboxsDials() {
        let all = Tone.allCases.map { ($0, ChatterboxDelivery.for($0)) }
        // Within what the model takes steadily.
        #expect(all.allSatisfy { (0.25...0.8).contains($0.1.exaggeration) && (0.3...0.5).contains($0.1.cfgWeight) })
        // Neutral and all narration are the model's own default, an even read.
        #expect(ChatterboxDelivery.for(.neutral) == ChatterboxDelivery(exaggeration: 0.5, cfgWeight: 0.5, volume: 1))
        #expect(Tone.allCases.allSatisfy { ChatterboxDelivery.for($0, narration: true) == .even })
        // Intense tones above an even read, quiet ones below; intense ones slowed with a lower weight.
        func e(_ t: Tone) -> Double { ChatterboxDelivery.for(t).exaggeration }
        #expect(e(.angry) > e(.excited) && e(.excited) > e(.scared) && e(.scared) > e(.sarcastic) && e(.sarcastic) > e(.confused))
        #expect(e(.confused) > e(.neutral) && e(.neutral) > e(.tender) && e(.tender) > e(.sad) && e(.sad) > e(.whispered))
        #expect(ChatterboxDelivery.for(.angry).cfgWeight < ChatterboxDelivery.for(.neutral).cfgWeight)
        // Chatterbox can't whisper: a whispered line is said softly, and only it is quieter.
        #expect(ChatterboxDelivery.for(.whispered).volume < 1)
        #expect(all.filter { $0.1.volume < 1 }.map(\.0) == [.whispered])
    }

    @Test func pitchBecomesWholeSemitonesOfTheClip() {
        #expect(ChatterboxVoice.semitones(pitch: nil) == 0)
        #expect(ChatterboxVoice.semitones(pitch: 1) == 0)
        #expect(ChatterboxVoice.semitones(pitch: 1.19) == 3)
        #expect(ChatterboxVoice.semitones(pitch: 0.89) == -2)
        #expect(ChatterboxVoice.semitones(pitch: 3) == 4)
        #expect(ChatterboxVoice.stockName("stock:tara") == "tara" && ChatterboxVoice.stockName("clone:x") == nil)
        // Clones are never made older or younger by casting: they're a real person's voice.
        let clone = VoiceInfo(engine: .chatterbox, voiceID: "clone:x", name: "Me", gender: .female, age: .adult, accent: "")
        #expect(VoiceCasting.ageAdjusted(clone, for: .child) == clone.choice)
    }
}

@Suite struct ChatterboxRunOnTests {
    static let rate = 24_000.0
    static func tone(_ seconds: Double) -> [Float] { (0..<Int(rate * seconds)).map { Float(sin(Double($0) * 0.05) * 0.3) } }
    static func quiet(_ seconds: Double) -> [Float] { [Float](repeating: 0, count: Int(rate * seconds)) }

    @Test func aRunOnAfterAShortLineIsCut() {
        // "The path was underwater": 2 s said, a pause, 1.5 s made up.
        let audio = Self.tone(2) + Self.quiet(0.7) + Self.tone(1.5)
        let out = ChatterboxRunOn.trim(audio, sampleRate: Self.rate, text: "The path was underwater")
        #expect(abs(Double(out.count) / Self.rate - 2.1) < 0.05)
    }

    @Test func aCommaPauseInALongLineIsKept() {
        // 58 characters (about 4 s): the pause after 2.8 s is the comma.
        let audio = Self.tone(2.8) + Self.quiet(0.5) + Self.tone(1)
        let out = ChatterboxRunOn.trim(audio, sampleRate: Self.rate, text: "I told you the tide would turn before morning, and it did.")
        #expect(out.count == audio.count)
    }

    @Test func shortPausesAndTrailingSilenceStay() {
        let audio = Self.tone(2) + Self.quiet(0.2) + Self.tone(1) + Self.quiet(1)
        #expect(ChatterboxRunOn.trim(audio, sampleRate: Self.rate, text: "Short.") == audio)
    }
}
