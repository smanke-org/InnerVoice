import Foundation
import Testing
@testable import InnerVoiceKit

@Suite struct ServiceCatalogTests {
    let day0 = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func firstRefreshMarksNothingNewThenNewOnesShow() {
        var st = CatalogState()
        st.record(models: ["eleven_v4", "eleven_v3"], voices: ["a", "b"], at: day0)
        #expect(st.newSummary == nil)
        st.record(models: ["eleven_v5", "eleven_v4", "eleven_v3"], voices: ["a", "b", "c", "d"], at: day0.addingTimeInterval(90_000))
        #expect(st.newModels == ["eleven_v5"])
        #expect(st.newVoices == ["c", "d"])
        #expect(st.newSummary == "1 new model, 2 new voices")
        // A list that wasn't fetched stays as it was; new ones stay new until seen.
        st.record(models: nil, voices: ["a", "b", "c", "d"], at: day0.addingTimeInterval(200_000))
        #expect(st.newModels == ["eleven_v5"] && st.newVoices == ["c", "d"])
        st.markSeen()
        #expect(st.newSummary == nil)
    }

    @Test func retiredModelAndSuggestion() {
        var st = CatalogState()
        #expect(!st.isRetired("eleven_v3")) // never refreshed: unknown, not retired
        st.record(models: ["eleven_v5", "eleven_flash_v2_5"], voices: nil, at: day0)
        #expect(st.isRetired("eleven_v3"))
        #expect(!st.isRetired("eleven_v5"))
        #expect(ServiceCatalog.suggestion(for: "eleven_v3", in: st.models) == "eleven_v5")
        #expect(ServiceCatalog.suggestion(for: "gemini-3.8-flash-lite-tts", in: ["gemini-4.0-flash-tts", "gemini-3.8-flash-lite-tts-2"]) == "gemini-3.8-flash-lite-tts-2")
    }

    @Test func dueOncePerDay() {
        var st = CatalogState()
        #expect(ServiceCatalog.isDue(nil, now: day0))
        st.record(models: ["x"], voices: nil, at: day0)
        #expect(!ServiceCatalog.isDue(st, now: day0.addingTimeInterval(3600)))
        #expect(ServiceCatalog.isDue(st, now: day0.addingTimeInterval(86_400)))
    }

    @Test func geminiModelListKeepsOnlyTTS() {
        let json = #"{"models":[{"name":"models/gemini-3.8-flash"},{"name":"models/gemini-3.8-flash-tts"},{"name":"models/gemini-3.8-flash-lite-tts"},{"name":"models/gemini-4.0-pro-tts-preview"}]}"#
        #expect(ServiceCatalog.geminiTTSModels(Data(json.utf8)) == ["gemini-3.8-flash-tts", "gemini-3.8-flash-lite-tts", "gemini-4.0-pro-tts-preview"])
        #expect(GeminiTTS.Model.pricing(for: "gemini-4.0-flash-lite-tts") == .flashLite)
        #expect(GeminiTTS.Model.pricing(for: "gemini-4.0-pro-tts-preview") == .flash)
        let body = String(decoding: GeminiTTS.requestBody(text: "Hi", voice: "Kore", style: nil, modelID: "gemini-4.0-pro-tts-preview"), as: UTF8.self)
        #expect(body.contains(#""model":"gemini-4.0-pro-tts-preview""#))
    }
}
