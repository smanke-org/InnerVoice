import AppKit
import InnerVoiceKit

/// Self-test for Gemini TTS (Cloud): nothing goes to Google without a key, the narrator's
/// yes and the book's own Google permission (separate from ElevenLabs'); lines go out in
/// the Interactions API's shape and the WAV that comes back plays. Runs against a fake
/// Google server (`FakeGemini`): test runs have no key, so live calls are never made.
extension SelfTest {
    static func gemini(_ doc: InnerVoiceDocument, undo: UndoManager) async {
        let gemini = GeminiVoices.shared
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [FakeGemini.self]
        gemini.session = URLSession(configuration: config)
        FakeGemini.reset()
        check("Gemini TTS is off until it's set up", !gemini.isSetUp && !EngineSwitches.shared.isOn(.gemini) && !EngineSwitches.shared.choices.contains(.gemini))
        do {
            try await gemini.check(key: "test-key")
            check("Check Key asks Google for the voice list, and sends no book text", FakeGemini.checks == 1 && FakeGemini.speechRequests.isEmpty)
        } catch {
            check("Check Key asks Google for the voice list", false, error.localizedDescription)
        }
        gemini.save(key: "test-key", consent: true)
        EngineSwitches.shared.set(.gemini, on: true)
        check("Gemini TTS is offered once it's set up, with its 30 voices", EngineSwitches.shared.choices.contains(.gemini) && VoiceCatalog.voices(for: .gemini).count == 30)

        // Set up, and the book allows ElevenLabs, but not Google: nothing may go to Google.
        doc.setCloudVoicesAllowed(true, undoManager: undo)
        doc.setVoiceEngine(.gemini, undoManager: undo)
        check("a book that allowed only ElevenLabs keeps Gemini TTS chosen but won't play until it allows Google", doc.project.engineInUse == .gemini && !CloudGate.allows(doc))
        let segs = SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: true)
        let lines = Array(segs.filter { $0.lineID != nil }.prefix(2))
        SpeechRenderer.shared.clearCache()
        let r0 = VoiceResolver(project: doc.project)
        for seg in lines { _ = try? await SpeechRenderer.shared.render(r0.request(for: seg)!) }
        let kore = VoiceChoice(engine: .gemini, voiceID: "Kore")
        _ = try? await SpeechRenderer.shared.render(SpeechRequest(text: "Test.", choice: kore, tone: .neutral, kind: .spoken, cloudAllowed: false))
        let mixed = VoiceResolver(project: doc.project, engine: .mixed)
        check("without the book's Google permission, no text is sent to Google", FakeGemini.speechRequests.isEmpty
              && !mixed.usable.contains(.gemini), "\(FakeGemini.speechRequests.count) requests")
        doc.setCloudVoicesAllowed(false, undoManager: undo)

        // The book allows Google.
        doc.setCloudAllowed(.gemini, true, undoManager: undo)
        doc.setVoiceEngine(.gemini, undoManager: undo)
        check("once allowed, the book uses Gemini TTS", doc.project.engineInUse == .gemini && doc.project.settings.cloudVoicesAllowed == nil)
        let speaking = doc.project.characters.filter { (doc.index.stats[$0.id]?.lines ?? 0) > 0 }
        check("casting gives every character a Gemini voice", !speaking.isEmpty && speaking.allSatisfy { $0.voice(for: .gemini)?.choice.engine == .gemini })
        SpeechRenderer.shared.clearCache()
        let r = VoiceResolver(project: doc.project)
        if let seg = segs.first(where: { $0.lineID != nil && $0.tone != .neutral }) ?? lines.first, let req = r.request(for: seg) {
            do {
                let audio = try await SpeechRenderer.shared.render(req)
                let body = FakeGemini.speechRequests.last ?? [:]
                let content = ((body["input"] as? [[String: Any]])?.first?["content"] as? [[String: Any]])?.first ?? [:]
                let style = ((content["annotations"] as? [[String: Any]])?.first?["style"] as? String) ?? ""
                check("a line is sent to Gemini TTS with its tone as a direction", body["model"] as? String == GeminiTTS.Model.flash.rawValue
                      && (content["text"] as? String ?? "").contains(seg.text.prefix(8))
                      && (seg.tone == .neutral || style.contains(seg.tone.direction)), style)
                check("Gemini's WAV comes back and plays", Double(audio.frameLength) / audio.format.sampleRate > 0.4)
                check("the request names no book anywhere but the text", FakeGemini.keys.allSatisfy { $0 == "test-key" } && body["store"] as? Bool == false)
                let usage = CloudUsageStore.shared.records.filter { $0.service == .google }
                check("Gemini usage is counted in seconds with a cost", usage.contains { ($0.audioSeconds ?? 0) > 0.4 && $0.cost != nil })
            } catch {
                check("a line is sent to Gemini TTS", false, error.localizedDescription)
            }
        }
        // Errors say what to do.
        FakeGemini.status = 400
        SpeechRenderer.shared.clearCache()
        if let seg = lines.last, let req = r.request(for: seg) {
            do { _ = try await SpeechRenderer.shared.render(req); check("a wrong Gemini key says so", false) } catch {
                check("a wrong Gemini key says so", error.localizedDescription.contains("API key"), error.localizedDescription)
            }
        }
        FakeGemini.status = 429
        SpeechRenderer.shared.clearCache()
        if let seg = lines.last, let req = r.request(for: seg) {
            do { _ = try await SpeechRenderer.shared.render(req); check("a used-up Gemini quota says so", false) } catch {
                check("a used-up Gemini quota says so", error.localizedDescription.contains("quota"), error.localizedDescription)
            }
        }
        FakeGemini.status = 200
        // Stop sending this book.
        doc.setCloudAllowed(.gemini, false, undoManager: undo)
        let before = FakeGemini.speechRequests.count
        SpeechRenderer.shared.clearCache()
        let r2 = VoiceResolver(project: doc.project)
        for seg in lines { _ = try? await SpeechRenderer.shared.render(r2.request(for: seg)!) }
        check("after Stop Sending This Book, nothing more goes to Google", FakeGemini.speechRequests.count == before && !CloudGate.allows(doc))
        gemini.disconnect()
        check("Disconnect turns Gemini TTS off", !gemini.isSetUp && !EngineSwitches.shared.choices.contains(.gemini))
        doc.setVoiceEngine(.apple, undoManager: undo)
        gemini.session = .shared
        SpeechRenderer.shared.clearCache()
    }
}

/// A stand-in for Google's Gemini API: the voice list for Check Key, and a short WAV for
/// each speech request (0.6 s of a tone), or an error when `status` says so.
nonisolated final class FakeGemini: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var speechRequests: [[String: Any]] = []
    nonisolated(unsafe) static var checks = 0
    nonisolated(unsafe) static var keys: [String] = []
    nonisolated(unsafe) static var status = 200

    static func reset() { speechRequests = []; checks = 0; keys = []; status = 200 }

    override class func canInit(with request: URLRequest) -> Bool { request.url?.host == "generativelanguage.googleapis.com" }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    private func bodyJSON() -> [String: Any] {
        var data = request.httpBody ?? Data()
        if data.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            var buf = [UInt8](repeating: 0, count: 65536)
            while stream.hasBytesAvailable { let n = stream.read(&buf, maxLength: buf.count); if n <= 0 { break }; data.append(buf, count: n) }
            stream.close()
        }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
    }

    static func wav(seconds: Double) -> Data {
        let n = Int(24_000 * seconds)
        var d = Data("RIFF".utf8)
        func u32(_ v: Int) { d.append(contentsOf: withUnsafeBytes(of: UInt32(v).littleEndian, Array.init)) }
        func u16(_ v: Int) { d.append(contentsOf: withUnsafeBytes(of: UInt16(v).littleEndian, Array.init)) }
        u32(36 + n * 2)
        d.append(Data("WAVEfmt ".utf8)); u32(16); u16(1); u16(1); u32(24_000); u32(48_000); u16(2); u16(16)
        d.append(Data("data".utf8)); u32(n * 2)
        for i in 0..<n { d.append(contentsOf: withUnsafeBytes(of: Int16(sin(Double(i) * 0.06) * 8000).littleEndian, Array.init)) }
        return d
    }

    override func startLoading() {
        let url = request.url!
        Self.keys.append(request.value(forHTTPHeaderField: "x-goog-api-key") ?? "")
        var code = Self.status
        var body = Data()
        if url.path.hasSuffix("/voices") {
            Self.checks += 1
            code = 200
            body = Data(#"{"voices":[{"id":"Kore","display_name":"Kore","gender":"female"}]}"#.utf8)
        } else if url.path.hasSuffix("/interactions") {
            if code == 200 {
                Self.speechRequests.append(bodyJSON())
                let reply: [String: Any] = ["steps": [["type": "model_output", "content": [
                    ["type": "audio", "mime_type": "audio/wav", "data": Self.wav(seconds: 0.6).base64EncodedString()]]]]]
                body = (try? JSONSerialization.data(withJSONObject: reply)) ?? Data()
            } else if code == 400 {
                body = Data(#"{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}"#.utf8)
            } else {
                body = Data(#"{"error":{"code":429,"message":"You exceeded your current quota.","status":"RESOURCE_EXHAUSTED"}}"#.utf8)
            }
        } else {
            code = 404
        }
        let response = HTTPURLResponse(url: url, statusCode: code, httpVersion: "HTTP/1.1", headerFields: ["Content-Type": "application/json"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
