import Foundation
import Testing
@testable import InnerVoiceKit

@Suite struct SpeechifyTests {
    @Test func narrationIsEvenAndEscaped() {
        #expect(SpeechifyTTS.ssml("Tom & \"Jerry\" <3", tone: .angry, kind: nil) == "<speak>Tom &amp; &quot;Jerry&quot; &lt;3</speak>")
    }

    @Test func toneBecomesProsody() {
        let s = SpeechifyTTS.ssml("Go!", tone: .excited, kind: .spoken)
        #expect(s == #"<speak><prosody rate="+8%" pitch="+6%" volume="loud">Go!</prosody></speak>"#)
        #expect(SpeechifyTTS.ssml("Hm.", tone: .neutral, kind: .spoken) == "<speak>Hm.</speak>")
        #expect(SpeechifyTTS.ssml("Hm.", tone: nil, kind: .thought).contains(#"volume="soft""#))
    }

    @Test func requestShape() throws {
        let body = try #require(JSONSerialization.jsonObject(with: SpeechifyTTS.requestBody(text: "Hi.", voice: "anna", tone: nil, kind: .spoken)) as? [String: Any])
        #expect(body["voice_id"] as? String == "anna")
        #expect(body["model"] as? String == "simba-3.5")
        #expect(body["output_format"] as? String == "wav_24000")
        #expect(body["input"] as? String == "<speak>Hi.</speak>")
    }

    @Test func voicesSkipPersonalAndReadAccentAndAge() throws {
        let data = Data(#"{"voices":[{"id":"a","display_name":"A","gender":"female","locale":"en-AU","type":"shared","tags":["old","raspy"]},{"id":"b","display_name":"B","gender":"male","locale":"en-US","type":"personal"}],"next_cursor":"x","has_more":true}"#.utf8)
        let page = try SpeechifyTTS.voices(fromResponse: data)
        #expect(page.next == "x")
        #expect(page.voices.count == 1)
        #expect(page.voices[0].accent == "Australian" && page.voices[0].age == .older && page.voices[0].gender == .female)
        #expect(page.voices[0].detail == "Old, Raspy")
    }

    @Test func audioAndErrors() throws {
        let reply = Data(#"{"audio_data":"AAEC","audio_format":"wav","billable_characters_count":3,"speech_marks":{}}"#.utf8)
        let a = try SpeechifyTTS.audio(fromResponse: reply)
        #expect(a.audio == Data([0, 1, 2]) && a.billable == 3)
        #expect(throws: SpeechifyTTS.ResponseError.noAudio) { try SpeechifyTTS.audio(fromResponse: Data("{}".utf8)) }
        #expect(SpeechifyTTS.errorMessage(Data(#"{"error":{"code":"unauthorized","message":"bad"}}"#.utf8))?.code == "unauthorized")
    }

    @Test func permissionIsItsOwn() {
        var s = ProjectSettings()
        s.cloudVoicesAllowed = .now
        s.googleVoicesAllowed = .now
        #expect(!s.allowsCloud(.speechify))
        s.speechifyVoicesAllowed = .now
        #expect(s.allowsCloud(.speechify) && VoiceEngine.speechify.isCloud && VoiceEngine.speechify.cloudCompany == "Speechify")
    }
}
