import Foundation
import Testing
@testable import InnerVoiceKit

@Suite struct OneEngineTests {
    @Test func mixedVoicesFillTheChosenEnginesEmptySlots() {
        var p = Project.empty
        var a = BookCharacter(name: "Ann", colorHex: "#2F7FD0")
        var b = BookCharacter(name: "Bo", colorHex: "#2F7FD0")
        let qa = VoiceChoice(engine: .qwen, voiceID: "preset:dame")
        let ob = VoiceChoice(engine: .orpheus, voiceID: "leo")
        let keep = VoiceChoice(engine: .qwen, voiceID: "preset:lass")
        a.setVoice(CastVoice(choice: qa, confidence: 1, source: .user), for: .mixed)
        b.setVoice(CastVoice(choice: ob, confidence: 1, source: .user), for: .mixed)
        b.setVoice(CastVoice(choice: keep, confidence: 1, source: .user), for: .qwen)
        p.characters = [a, b]
        p.settings.setNarratorVoice(CastVoice(choice: qa, confidence: 1, source: .user), for: .mixed)
        p.settings.voiceEngine = .mixed
        p.adoptMixedVoices(for: .qwen)
        #expect(p.characters[0].voice(for: .qwen)?.choice == qa)
        #expect(p.characters[1].voice(for: .qwen)?.choice == keep)   // its own Qwen voice stays
        #expect(p.settings.narratorVoice(for: .qwen)?.choice == qa)
        #expect(p.characters[1].voice(for: .mixed)?.choice == ob)    // nothing removed
    }
}
