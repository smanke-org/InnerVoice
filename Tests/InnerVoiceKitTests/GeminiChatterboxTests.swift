import Foundation
import Testing
@testable import InnerVoiceKit

struct GeminiTTSTests {
    @Test func thirtyVoicesWithGenders() {
        #expect(GeminiTTS.voices.count == 30)
        #expect(Set(GeminiTTS.voices.map(\.voiceID)).count == 30)
        #expect(GeminiTTS.voices.allSatisfy { $0.engine == .gemini && $0.gender != .neutral })
        #expect(GeminiTTS.voices.filter { $0.gender == .female }.count == 14)
    }

    @Test func requestBodyMatchesTheInteractionsAPI() throws {
        let data = GeminiTTS.requestBody(text: "Not here.", voice: "Kore", style: "in a quiet whisper", model: .flash)
        let root = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(root["model"] as? String == "gemini-3.8-flash-tts")
        #expect((root["response_format"] as? [String: Any])?["type"] as? String == "audio")
        #expect(root["store"] as? Bool == false)
        let speech = ((root["generation_config"] as? [String: Any])?["speech_config"] as? [[String: Any]])?.first
        #expect(speech?["voice"] as? String == "Kore")
        let input = try #require((root["input"] as? [[String: Any]])?.first)
        #expect(input["type"] as? String == "user_input")
        let content = try #require((input["content"] as? [[String: Any]])?.first)
        #expect(content["text"] as? String == "Not here.")
        let note = (content["annotations"] as? [[String: Any]])?.first
        #expect(note?["type"] as? String == "speech_metadata")
        #expect(note?["style"] as? String == "in a quiet whisper")
        // No style: no annotations at all.
        let plain = try #require(try JSONSerialization.jsonObject(with: GeminiTTS.requestBody(text: "x", voice: "Puck", style: nil, model: .flashLite)) as? [String: Any])
        let plainContent = ((plain["input"] as? [[String: Any]])?.first?["content"] as? [[String: Any]])?.first
        #expect(plainContent?["annotations"] == nil)
        #expect(plain["model"] as? String == "gemini-3.8-flash-lite-tts")
    }

    @Test func styleFromToneThoughtAndHint() {
        #expect(GeminiTTS.style(tone: .neutral, kind: .spoken, delivery: nil) == nil)
        #expect(GeminiTTS.style(tone: .whispered, kind: .spoken, delivery: nil) == "in a quiet whisper")
        #expect(GeminiTTS.style(tone: .neutral, kind: .thought, delivery: nil) == "quietly, as a private thought")
        #expect(GeminiTTS.style(tone: .sad, kind: .spoken, delivery: "barely audible") == "sad and subdued, close to tears, barely audible")
        #expect(GeminiTTS.tag(.sigh) == "<sigh>")
    }

    private func wav(_ samples: [Int16]) -> Data {
        var d = Data("RIFF".utf8)
        func u32(_ v: Int) { d.append(contentsOf: withUnsafeBytes(of: UInt32(v).littleEndian, Array.init)) }
        func u16(_ v: Int) { d.append(contentsOf: withUnsafeBytes(of: UInt16(v).littleEndian, Array.init)) }
        u32(36 + samples.count * 2)
        d.append(Data("WAVEfmt ".utf8)); u32(16); u16(1); u16(1); u32(24_000); u32(48_000); u16(2); u16(16)
        // An odd-sized extra chunk before the data, padded, as some encoders write.
        d.append(Data("LIST".utf8)); u32(3); d.append(contentsOf: [1, 2, 3, 0])
        d.append(Data("data".utf8)); u32(samples.count * 2)
        for s in samples { d.append(contentsOf: withUnsafeBytes(of: s.littleEndian, Array.init)) }
        return d
    }

    @Test func audioFromTheReplyAndWAVSamples() throws {
        let w = wav([1, -2, 3])
        let reply: [String: Any] = ["steps": [
            ["type": "user_input", "content": [["type": "text", "text": "Not here."]]],
            ["type": "model_output", "content": [["type": "audio", "mime_type": "audio/wav", "data": w.base64EncodedString()]]],
        ]]
        let audio = try GeminiTTS.audio(fromResponse: try JSONSerialization.data(withJSONObject: reply))
        #expect(audio == w)
        let pcm = GeminiTTS.pcm16(fromWAVOrRaw: audio)
        #expect(pcm.count == 6)
        #expect(pcm.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 2, as: Int16.self) } == -2)
        // Raw PCM passes through.
        #expect(GeminiTTS.pcm16(fromWAVOrRaw: Data([1, 0, 2, 0])) == Data([1, 0, 2, 0]))
    }

    @Test func aReplyWithoutAudioSaysWhy() throws {
        let reply: [String: Any] = ["steps": [["type": "model_output", "content": [["type": "text", "text": "I can’t read that."]]]]]
        #expect(throws: GeminiTTS.ResponseError.noAudio("I can’t read that.")) {
            try GeminiTTS.audio(fromResponse: try JSONSerialization.data(withJSONObject: reply))
        }
        let err = Data(#"{"error":{"code":400,"message":"API key not valid.","status":"INVALID_ARGUMENT"}}"#.utf8)
        #expect(GeminiTTS.errorMessage(err)?.message == "API key not valid.")
    }

    /// A per-minute limit is "busy" (wait as long as Google says); a daily quota isn't.
    @Test func rateLimitsWaitDailyQuotaStops() {
        let perMinute = Data(#"{"error":{"code":429,"message":"You exceeded your current quota. Quota exceeded for metric: generate_requests_per_model, limit: 10","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generativelanguage.googleapis.com/generate_requests_per_model","quotaId":"GenerateRequestsPerMinutePerProjectPerModel","quotaValue":"10"}]},{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"27.5s"}]}}"#.utf8)
        #expect(GeminiTTS.retryDelay(perMinute) == 27.5)
        #expect(GeminiTTS.perMinuteLimit(perMinute) == 10)
        #expect(!GeminiTTS.isOutOfQuota(perMinute))
        let daily = Data(#"{"error":{"code":429,"message":"Quota exceeded.","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel"}]},{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"3600s"}]}}"#.utf8)
        #expect(GeminiTTS.isOutOfQuota(daily))
        #expect(GeminiTTS.perMinuteLimit(daily) == nil)
        let bare = Data(#"{"error":{"code":429,"message":"Resource has been exhausted (e.g. check quota).","status":"RESOURCE_EXHAUSTED"}}"#.utf8)
        #expect(GeminiTTS.isOutOfQuota(bare) && GeminiTTS.retryDelay(bare) == nil)
        #expect(!GeminiTTS.isOutOfQuota(Data(#"{"error":{"code":503,"message":"The model is overloaded.","status":"UNAVAILABLE"}}"#.utf8)))
    }

    @Test func costsPerSecondAndThePriceRise() {
        let late2026 = DateComponents(calendar: Calendar(identifier: .gregorian), timeZone: TimeZone(identifier: "UTC"), year: 2026, month: 12, day: 1).date!
        let early2027 = DateComponents(calendar: Calendar(identifier: .gregorian), timeZone: TimeZone(identifier: "UTC"), year: 2027, month: 1, day: 2).date!
        #expect(abs(GeminiTTS.Model.flash.dollarsPerSecond(on: late2026) - 0.000225) < 1e-9)
        #expect(abs(GeminiTTS.Model.flash.dollarsPerSecond(on: early2027) - 0.00045) < 1e-9)
        #expect(abs(GeminiTTS.Model.flashLite.dollarsPerSecond(on: late2026) - 0.00015) < 1e-9)
        // A 10-hour book with Flash in 2026: about $8.
        #expect(abs(36_000 * GeminiTTS.Model.flash.dollarsPerSecond(on: late2026) - 8.1) < 0.01)
    }

    @Test func usageRecordsCostAndOldRecordsStillRead() throws {
        var r = CloudUsageRecord(day: "2026-11-14", book: "b", service: .google, model: "gemini-3.8-flash-tts")
        r.audioSeconds = 100
        #expect(r.cost != nil)
        var t = r
        t.add(r)
        #expect(t.audioSeconds == 200)
        let old = #"{"day":"2026-11-01","book":"b","service":"elevenlabs","model":"eleven_v4","inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"characters":12,"requests":1}"#
        let decoded = try JSONDecoder().decode(CloudUsageRecord.self, from: Data(old.utf8))
        #expect(decoded.audioSeconds == nil && decoded.characters == 12)
    }

    @Test func eachCompanyHasItsOwnYes() {
        var s = ProjectSettings()
        #expect(!s.allowsCloud(.elevenlabs) && !s.allowsCloud(.gemini) && s.allowsCloud(.chatterbox))
        s.cloudVoicesAllowed = .now
        #expect(s.allowsCloud(.elevenlabs) && !s.allowsCloud(.gemini))
        s.googleVoicesAllowed = .now
        #expect(s.allowsCloud(.gemini) && s.allowsAnyCloudVoices)
        #expect(VoiceEngine.gemini.isCloud && VoiceEngine.gemini.cloudCompany == "Google")
        #expect(!VoiceEngine.chatterbox.isCloud && VoiceEngine.chatterbox.hasChineseOrigin)
    }
}

struct ChatterboxTests {
    @Test func tenVoicesSharingPocketClips() {
        #expect(VoiceInfo.chatterbox.count == VoiceInfo.pocket.count + 1)
        #expect(VoiceInfo.chatterbox.first?.voiceID == ChatterboxVoice.builtInID)
        #expect(VoiceInfo.chatterbox.allSatisfy { $0.engine == .chatterbox && $0.voiceID.hasPrefix(PocketVoice.stockPrefix) })
    }

    @Test func tonesStayInTheSteadyRange() {
        for tone in Tone.allCases {
            let d = ChatterboxDelivery.for(tone)
            #expect(d.exaggeration <= 0.8 && d.exaggeration >= 0.3)
        }
        #expect(ChatterboxDelivery.for(.angry, narration: true) == .even)
    }

    @Test func runOnAfterAShortLineIsCut() {
        let rate = 24_000.0
        let tone = (0..<Int(rate * 1.8)).map { Float(sin(Double($0) * 0.05)) * 0.5 }
        let gap = [Float](repeating: 0, count: Int(rate * 0.6))
        let more = (0..<Int(rate * 1.0)).map { Float(sin(Double($0) * 0.07)) * 0.5 }
        let out = ChatterboxRunOn.trim(tone + gap + more, sampleRate: rate, text: "The path was underwater.")
        #expect(out.count < tone.count + gap.count)
        #expect(out.count >= tone.count)
    }

    @Test func ageNudgesApplyToStockVoicesNotClones() {
        let stock = VoiceInfo.chatterbox[1]
        #expect(VoiceCasting.ageAdjusted(stock, for: .child).pitch == 1.19)
        let clone = VoiceInfo(engine: .chatterbox, voiceID: "clone:abc", name: "Me", gender: .female, accent: "")
        #expect(VoiceCasting.ageAdjusted(clone, for: .child).pitch == nil)
    }
}
