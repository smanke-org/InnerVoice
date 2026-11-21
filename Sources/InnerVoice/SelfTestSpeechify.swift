import AppKit
import InnerVoiceKit

/// Self-test for Speechify (Cloud): nothing goes to Speechify without a key, the narrator's
/// yes and the book's own Speechify permission (separate from ElevenLabs' and Google's);
/// lines go out as SSML with the tone as prosody, and the WAV that comes back plays. Runs
/// against a fake Speechify server (`FakeSpeechify`): test runs have no key.
extension SelfTest {
    static func speechify(_ doc: InnerVoiceDocument, undo: UndoManager) async {
        let sp = SpeechifyVoices.shared
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [FakeSpeechify.self]
        sp.session = URLSession(configuration: config)
        FakeSpeechify.reset()
        check("Speechify is off until it's set up", !sp.isSetUp && !EngineSwitches.shared.isOn(.speechify) && !EngineSwitches.shared.choices.contains(.speechify))
        do {
            try await sp.check(key: "sp-key")
            check("Check Key asks Speechify for the voice list (every page), and sends no book text",
                  FakeSpeechify.checks == 2 && FakeSpeechify.speechRequests.isEmpty, "\(FakeSpeechify.checks) list requests")
        } catch {
            check("Check Key asks Speechify for the voice list", false, error.localizedDescription)
        }
        sp.save(key: "sp-key", consent: true)
        EngineSwitches.shared.set(.speechify, on: true)
        let voices = VoiceCatalog.voices(for: .speechify)
        check("Speechify is offered once it's set up, with its shared voices (not personal ones)",
              EngineSwitches.shared.choices.contains(.speechify) && voices.count == 4 && !voices.contains { $0.voiceID == "mine" },
              voices.map(\.voiceID).joined(separator: ","))
        check("Speechify voices carry gender and accent", voices.contains { $0.gender == .female && $0.accent == "British" })

        // Set up, and the book allows ElevenLabs and Google, but not Speechify.
        doc.setCloudVoicesAllowed(true, undoManager: undo)
        doc.setCloudAllowed(.gemini, true, undoManager: undo)
        doc.setVoiceEngine(.speechify, undoManager: undo)
        check("a book that allowed other companies keeps Speechify chosen but won't play until it allows Speechify",
              doc.project.engineInUse == .speechify && !CloudGate.allows(doc))
        let segs = SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: true)
        let lines = Array(segs.filter { $0.lineID != nil }.prefix(2))
        SpeechRenderer.shared.clearCache()
        let r0 = VoiceResolver(project: doc.project)
        for seg in lines { _ = try? await SpeechRenderer.shared.render(r0.request(for: seg)!) }
        _ = try? await SpeechRenderer.shared.render(SpeechRequest(text: "Test.", choice: VoiceChoice(engine: .speechify, voiceID: "anna"),
                                                                  tone: .neutral, kind: .spoken, cloudAllowed: false))
        check("without the book's Speechify permission, no text is sent to Speechify", FakeSpeechify.speechRequests.isEmpty,
              "\(FakeSpeechify.speechRequests.count) requests")
        doc.setCloudVoicesAllowed(false, undoManager: undo)
        doc.setCloudAllowed(.gemini, false, undoManager: undo)

        // The book allows Speechify.
        doc.setCloudAllowed(.speechify, true, undoManager: undo)
        doc.setVoiceEngine(.speechify, undoManager: undo)
        check("once allowed, the book uses Speechify and allows no other company",
              doc.project.engineInUse == .speechify && doc.project.settings.cloudVoicesAllowed == nil && doc.project.settings.googleVoicesAllowed == nil)
        let speaking = doc.project.characters.filter { (doc.index.stats[$0.id]?.lines ?? 0) > 0 }
        check("casting gives every character a Speechify voice", !speaking.isEmpty && speaking.allSatisfy { $0.voice(for: .speechify)?.choice.engine == .speechify })
        SpeechRenderer.shared.clearCache()
        let r = VoiceResolver(project: doc.project)
        if let seg = segs.first(where: { $0.lineID != nil && $0.tone != .neutral }) ?? lines.first, let req = r.request(for: seg) {
            do {
                let audio = try await SpeechRenderer.shared.render(req)
                let body = FakeSpeechify.speechRequests.last ?? [:]
                let input = body["input"] as? String ?? ""
                check("a line is sent to Speechify as SSML with its words", body["model"] as? String == SpeechifyTTS.model
                      && input.hasPrefix("<speak>") && input.contains(seg.text.prefix(6).replacingOccurrences(of: "&", with: "&amp;"))
                      && (seg.tone == .neutral || input.contains("<prosody")), input)
                check("Speechify's WAV comes back and plays", Double(audio.frameLength) / audio.format.sampleRate > 0.4)
                check("requests carry only the Bearer key", FakeSpeechify.keys.allSatisfy { $0 == "Bearer sp-key" })
                let usage = CloudUsageStore.shared.records.filter { $0.service == .speechify }
                check("Speechify usage counts the billed characters", usage.contains { $0.characters == 42 && $0.requests > 0 })
            } catch {
                check("a line is sent to Speechify", false, error.localizedDescription)
            }
        }
        // Busy: waits and tries again by itself.
        FakeSpeechify.busyOnce = true
        SpeechRenderer.shared.clearCache()
        if let seg = lines.last, let req = r.request(for: seg) {
            do { _ = try await SpeechRenderer.shared.render(req); check("a busy Speechify is tried again", FakeSpeechify.busyServed) } catch {
                check("a busy Speechify is tried again", false, error.localizedDescription)
            }
        }
        // Errors say what to do.
        for (status, word, name) in [(401, "API key", "a wrong Speechify key says so"), (402, "characters left", "a used-up Speechify plan says so")] {
            FakeSpeechify.status = status
            SpeechRenderer.shared.clearCache()
            if let seg = lines.last, let req = r.request(for: seg) {
                do { _ = try await SpeechRenderer.shared.render(req); check(name, false) } catch {
                    check(name, error.localizedDescription.contains(word), error.localizedDescription)
                }
            }
        }
        FakeSpeechify.status = 200
        // Stop sending this book.
        doc.setCloudAllowed(.speechify, false, undoManager: undo)
        let before = FakeSpeechify.speechRequests.count
        SpeechRenderer.shared.clearCache()
        let r2 = VoiceResolver(project: doc.project)
        for seg in lines { _ = try? await SpeechRenderer.shared.render(r2.request(for: seg)!) }
        check("after Stop Sending This Book, nothing more goes to Speechify", FakeSpeechify.speechRequests.count == before && !CloudGate.allows(doc))
        sp.disconnect()
        check("Disconnect turns Speechify off", !sp.isSetUp && !EngineSwitches.shared.choices.contains(.speechify))
        doc.setVoiceEngine(.apple, undoManager: undo)
        sp.session = .shared
        SpeechRenderer.shared.clearCache()
    }
}

/// A stand-in for Speechify's API: two pages of voices for Check Key, and a short WAV for
/// each speech request, or an error when `status` says so.
nonisolated final class FakeSpeechify: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var speechRequests: [[String: Any]] = []
    nonisolated(unsafe) static var checks = 0
    nonisolated(unsafe) static var keys: [String] = []
    nonisolated(unsafe) static var status = 200
    nonisolated(unsafe) static var busyOnce = false
    nonisolated(unsafe) static var busyServed = false

    static func reset() { speechRequests = []; checks = 0; keys = []; status = 200; busyOnce = false; busyServed = false }

    override class func canInit(with request: URLRequest) -> Bool { request.url?.host == "api.speechify.ai" }
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

    override func startLoading() {
        let url = request.url!
        Self.keys.append(request.value(forHTTPHeaderField: "Authorization") ?? "")
        var code = Self.status
        var body = Data()
        var headers = ["Content-Type": "application/json"]
        if url.path.hasSuffix("/voices") {
            Self.checks += 1
            code = 200
            if url.query?.contains("cursor=p2") == true {
                body = Data(#"{"voices":[{"id":"oliver","display_name":"Oliver","gender":"male","locale":"en-GB","type":"shared","tags":["timbre:deep","calm"]},{"id":"kid","display_name":"Pip","gender":"male","locale":"en-US","type":"shared","tags":["child"]}],"next_cursor":null,"has_more":false}"#.utf8)
            } else {
                body = Data(#"{"voices":[{"id":"anna","display_name":"Anna","gender":"female","locale":"en-GB","type":"shared","tags":["warm"],"staff_pick_rank":1},{"id":"george","display_name":"George","gender":"male","locale":"en-US","type":"shared"},{"id":"mine","display_name":"My Clone","gender":"male","locale":"en-US","type":"personal"}],"next_cursor":"p2","has_more":true}"#.utf8)
            }
        } else if url.path.hasSuffix("/audio/speech") {
            if Self.busyOnce {
                Self.busyOnce = false
                Self.busyServed = true
                code = 429
                headers["Retry-After"] = "1"
                body = Data(#"{"error":{"code":"rate_limited","message":"Too many requests"}}"#.utf8)
            } else if code == 200 {
                Self.speechRequests.append(bodyJSON())
                let reply: [String: Any] = ["audio_data": FakeGemini.wav(seconds: 0.6).base64EncodedString(), "audio_format": "wav",
                                            "billable_characters_count": 42, "speech_marks": [:]]
                body = (try? JSONSerialization.data(withJSONObject: reply)) ?? Data()
            } else if code == 401 {
                body = Data(#"{"error":{"code":"unauthorized","message":"Invalid API key"}}"#.utf8)
            } else {
                body = Data(#"{"error":{"code":"payment_required","message":"Character limit reached"}}"#.utf8)
            }
        } else {
            code = 404
        }
        let response = HTTPURLResponse(url: url, statusCode: code, httpVersion: "HTTP/1.1", headerFields: headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
