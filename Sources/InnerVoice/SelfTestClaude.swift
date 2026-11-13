import AppKit
import InnerVoiceKit

/// Self-test for Claude (Cloud): nothing is sent without a key, the narrator's yes and the
/// book's permission; Claude's own features run against a fake Anthropic server (no key
/// exists in test runs, so live calls are never made) and change the book only as asked.
extension SelfTest {
    static func claudeFeatures() async {
        let doc = InnerVoiceDocument(project: SeedData.project())
        let undo = UndoManager()
        let account = ClaudeAccount.shared
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [FakeClaude.self]
        account.session = URLSession(configuration: config)
        FakeClaude.reset()
        let claude = doc.claude

        func wait() async {
            for _ in 0..<500 where claude.isRunning { try? await Task.sleep(for: .milliseconds(20)) }
        }
        func run(_ job: ClaudeFeatures.Job) async {
            claude.finished = nil
            claude.failure = nil
            claude.pending = nil
            claude.run(job, undoManager: undo)
            await wait()
        }

        // Estimates, worked out on the Mac.
        let tagCost = CostEstimator.tagSpeakers(project: doc.project)
        check("a Tag Speakers estimate has a price for Sonnet and tokens only for an unknown model",
              tagCost.inputTokens > 0 && tagCost.dollars(modelID: "claude-sonnet-5-5") != nil && tagCost.phrase(modelID: "claude-new-model").hasSuffix("tokens"),
              tagCost.phrase(modelID: "claude-sonnet-5-5"))
        check("each Claude feature has an estimate", ClaudeFeatures.Job.allCases.filter { $0 != .spellings }.allSatisfy { claude.estimate($0).inputTokens > 0 })

        // Not set up: nothing is sent.
        account.save(key: "", consent: false)
        await run(.cleanup)
        check("without a Claude key, Clean Up Text sends nothing and says why", FakeClaude.requests.isEmpty && claude.failure?.contains("Set up Claude") == true)

        // Set up, but the book hasn't said yes (test runs never ask): nothing is sent.
        account.save(key: "test-key", consent: true)
        await run(.cleanup)
        check("without the book's permission, nothing is sent to Claude", FakeClaude.requests.isEmpty && claude.pending == nil)
        do {
            _ = try ClaudeTextModel(modelID: ClaudeAccount.defaultModelID, bookAllowed: false)
            check("ClaudeTextModel refuses a book that hasn't allowed Claude", false)
        } catch {
            if case .bookNotAllowed? = error as? ClaudeError { check("ClaudeTextModel refuses a book that hasn't allowed Claude", true) } else { check("ClaudeTextModel refuses a book that hasn't allowed Claude", false, error.localizedDescription) }
        }

        doc.setCloudTextAllowed(true, undoManager: undo)

        // Clean Up Text: plant a scanning mistake in a paragraph with a line the narrator assigned.
        guard let target = doc.project.chapters.flatMap(\.paragraphs).first(where: { p in
            p.style == .body && p.text.contains(" the ") && (doc.index.linesByParagraph[p.id]?.isEmpty == false)
        }), let li = doc.index.linesByParagraph[target.id]?.first else {
            check("the sample has a paragraph to clean up", false)
            return
        }
        let lineID = doc.project.lines[li].id
        let speaker = doc.project.characters.first?.id
        doc.change("", undoManager: nil) { p in
            if let i = p.lines.firstIndex(where: { $0.id == lineID }) { p.lines[i].speakerID = speaker; p.lines[i].source = .user }
        }
        let lineText = doc.project.text(of: doc.project.lines[li], index: doc.index)
        doc.change("", undoManager: nil, textChanged: true) { p in
            for c in p.chapters.indices {
                for i in p.chapters[c].paragraphs.indices where p.chapters[c].paragraphs[i].id == target.id {
                    let t = p.chapters[c].paragraphs[i].text as NSString
                    let r = t.range(of: " the ")
                    p.chapters[c].paragraphs[i].text = t.replacingCharacters(in: r, with: " tbe ")
                    // The planted letter swap keeps the length, so lines stay where they were.
                }
            }
        }
        await run(.cleanup)
        let body = FakeClaude.requests.first ?? [:]
        check("Clean Up Text sends the book to Claude, a passage at a time, with the API version and no temperature",
              FakeClaude.requests.count >= 1 && body["model"] as? String == ClaudeAccount.defaultModelID && body["temperature"] == nil
              && FakeClaude.versions.allSatisfy { $0 == ClaudeAccount.apiVersion }, "\(FakeClaude.requests.count) requests")
        var fixes: [CleanupFix] = []
        if case .cleanup(let f)? = claude.pending?.proposals { fixes = f }
        check("Clean Up Text waits for review with the fix it found", fixes.count == 1 && fixes.first?.original.contains("tbe") == true,
              claude.finished ?? claude.failure ?? "")
        check("nothing changes before the fixes are applied", doc.project.paragraph(target.id, index: doc.index)?.text.contains(" tbe ") == true)
        claude.apply(.cleanup(fixes), undoManager: undo)
        claude.pending = nil
        let cleaned = doc.project.paragraph(target.id, index: doc.index)?.text ?? ""
        let kept = doc.project.lines.first { $0.id == lineID }
        check("applying a fix changes the text and keeps the narrator's line", !cleaned.contains(" tbe ") && kept?.source == .user && kept?.speakerID == speaker
              && kept.map { doc.project.text(of: $0, index: doc.index) } == lineText)
        check("every line still fits its paragraph after Clean Up Text", doc.project.lines.allSatisfy { l in
            (doc.project.paragraph(l.paragraphID, index: doc.index).map { ($0.text as NSString).length } ?? 0) >= l.range.end
        })
        undo.undo()
        check("Undo puts the text back in one step", doc.project.paragraph(target.id, index: doc.index)?.text.contains(" tbe ") == true)

        // Chapter Recaps.
        let before = FakeClaude.requests.count
        await run(.recaps)
        var recaps: [ClaudeFeatures.RecapProposal] = []
        if case .recaps(let r)? = claude.pending?.proposals { recaps = r }
        check("Write Chapter Recaps writes one for each chapter after the first",
              recaps.count == doc.project.chapters.count - 1 && FakeClaude.requests.count - before == recaps.count, "\(recaps.count)")
        claude.apply(.recaps(recaps), undoManager: undo)
        claude.pending = nil
        let second = doc.project.chapters[1]
        check("a recap is stored with its chapter", second.recapText?.hasPrefix("Tom met Mara") == true, second.recap ?? "nil")
        let spoken = SpeechScript.segments(chapter: 1, project: doc.project, index: doc.index, includeNarration: true, recaps: true)
        check("Export Audio can say the recap after the chapter title", spoken.contains { $0.text == "Previously…" }
              && !SpeechScript.segments(chapter: 1, project: doc.project, index: doc.index, includeNarration: true).contains { $0.text == "Previously…" })
        doc.change("Edit Recap", undoManager: undo) { RecapPrompt.edit("My own recap.", chapterID: second.id, in: &$0) }
        await run(.recaps)
        if case .recaps(let r)? = claude.pending?.proposals { recaps = r } else { recaps = [] }
        check("a recap the narrator edited is never rewritten", !recaps.contains { $0.chapterID == second.id })
        claude.pending = nil

        // Review off: applied straight away, one Undo step.
        doc.updateSettings("", undoManager: nil) { $0.reviewCloudAI = false }
        doc.change("", undoManager: nil) { p in for c in p.chapters.indices where p.chapters[c].id != second.id { p.chapters[c].recap = nil } }
        await run(.recaps)
        check("with Review Claude's changes off, recaps are kept straight away", claude.pending == nil && doc.project.chapters[2].recapText != nil
              && doc.project.chapters[1].recapText == "My own recap.")
        doc.updateSettings("", undoManager: nil) { $0.reviewCloudAI = nil }

        // Delivery Hints, for ElevenLabs only.
        await run(.delivery)
        var hints: [DeliveryProposal] = []
        if case .delivery(let h)? = claude.pending?.proposals { hints = h }
        check("Suggest Delivery Hints proposes hints to review", !hints.isEmpty && hints.allSatisfy { $0.hint == "barely audible" }, claude.finished ?? "")
        claude.apply(.delivery(hints), undoManager: undo)
        claude.pending = nil
        if let hinted = hints.first, let seg = SpeechScript.segments(chapter: 0, chapters: doc.project.chapters.count, project: doc.project, index: doc.index, includeNarration: true)
            .first(where: { $0.lineID == hinted.lineID }) {
            let r = VoiceResolver(project: doc.project)
            let local = r.request(for: seg)
            check("a delivery hint is never given to a voice on this Mac", local != nil && local?.delivery == nil && local?.choice.engine != .elevenlabs)
            var cloud = local
            cloud?.choice = VoiceChoice(engine: .elevenlabs, voiceID: "v1")
            cloud?.delivery = seg.delivery
            let text = cloud.map(ElevenLabs.text(for:)) ?? ""
            check("ElevenLabs gets the hint as an audio tag with the tone", text.contains("barely audible]") && text.hasPrefix("["), text)
        } else {
            check("a hinted line is spoken", false)
        }

        // Suggest Spellings: only terms and their sentence are sent; the narrator confirms each.
        doc.change("", undoManager: nil) { p in
            PronunciationFinder.refresh(&p, isKnownWord: SpellCheck.isKnown)
            // A name still to do, where it first appears.
            if let name = p.characters.first?.firstName,
               let para = p.chapters.flatMap(\.paragraphs).first(where: { $0.text.contains(name) }),
               !p.pronunciations.contains(where: { $0.term == name && SpellingPrompt.wanted($0) }) {
                p.pronunciations.removeAll { $0.term == name }
                p.pronunciations.append(Pronunciation(term: name, occurrences: 1, firstParagraphID: para.id))
            }
        }
        let terms = doc.project.pronunciations.filter(SpellingPrompt.wanted)
        if !terms.isEmpty {
            let sentBefore = FakeClaude.requests.count
            doc.updateSettings("", undoManager: nil) { $0.reviewCloudAI = false }
            await run(.spellings)
            doc.updateSettings("", undoManager: nil) { $0.reviewCloudAI = nil }
            var spellings: [SpellingProposal] = []
            if case .spellings(let s)? = claude.pending?.proposals { spellings = s }
            let sent = FakeClaude.userText(FakeClaude.requests.last ?? [:])
            check("spellings wait for the narrator even with review off", !spellings.isEmpty && FakeClaude.requests.count > sentBefore)
            check("Suggest Spellings sends terms and sentences, not whole paragraphs", sent.contains("[T1]") && sent.count < 200 * terms.count + 800)
            claude.apply(.spellings(Array(spellings.prefix(1))), undoManager: undo)
            claude.pending = nil
            let used = doc.project.pronunciations.first { $0.id == spellings.first?.pronunciationID }
            check("a confirmed spelling fills Say It As and leaves Confirmed to the narrator", used?.sayItAs == "TEST-word" && used?.confirmed == false)
        } else {
            check("the sample has terms for Suggest Spellings", false)
        }

        // Claude declines a passage: the run goes on and says so.
        FakeClaude.refuse = true
        await run(.cleanup)
        FakeClaude.refuse = false
        check("a refused passage is skipped, and the result says so", claude.failure == nil && claude.finished?.contains("didn’t answer") == true, claude.finished ?? claude.failure ?? "")

        // The book's permission taken back: nothing more is sent.
        doc.setCloudTextAllowed(false, undoManager: undo)
        let sentBefore = FakeClaude.requests.count
        await run(.recaps)
        check("after Stop Sending This Book to Claude, nothing more is sent", FakeClaude.requests.count == sentBefore)
        check("every request went to Anthropic only", FakeClaude.hosts.allSatisfy { $0 == "api.anthropic.com" })

        account.save(key: "", consent: false)
        account.session = .shared
    }
}

/// Anthropic's Messages API, faked: answers each Claude feature by its instructions.
nonisolated final class FakeClaude: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var requests: [[String: Any]] = []
    nonisolated(unsafe) static var versions: [String] = []
    nonisolated(unsafe) static var hosts: [String] = []
    nonisolated(unsafe) static var refuse = false

    static func reset() { requests = []; versions = []; hosts = []; refuse = false }

    static func userText(_ body: [String: Any]) -> String {
        ((body["messages"] as? [[String: Any]]) ?? []).compactMap { $0["content"] as? String }.joined(separator: "\n")
    }

    override class func canInit(with request: URLRequest) -> Bool { request.url?.host == "api.anthropic.com" }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        var data = request.httpBody ?? Data()
        if data.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            var buf = [UInt8](repeating: 0, count: 65536)
            while stream.hasBytesAvailable { let n = stream.read(&buf, maxLength: buf.count); if n <= 0 { break }; data.append(buf, count: n) }
            stream.close()
        }
        let body = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        Self.requests.append(body)
        Self.versions.append(request.value(forHTTPHeaderField: "anthropic-version") ?? "")
        Self.hosts.append(url.host ?? "")
        let system = ((body["system"] as? [[String: Any]])?.first?["text"] as? String) ?? ""
        let user = Self.userText(body)
        var answer = "{}"
        if system == CleanupPrompt.system {
            // The paragraph with the planted "tbe", by its label.
            let re = try! NSRegularExpression(pattern: #"\[(P\d+)\] [^\n]*\btbe\b"#)
            if let m = re.firstMatch(in: user, range: NSRange(user.startIndex..., in: user)), let r = Range(m.range(at: 1), in: user) {
                answer = #"{"fixes":[{"paragraph":"\#(user[r])","find":" tbe ","replace":" the ","issue":"ocr","note":"tbe for the"}]}"#
            } else {
                answer = #"{"fixes":[]}"#
            }
        } else if system == RecapPrompt.system {
            answer = #"{"recap":"Previously… Tom met Mara at the harbor, and they argued about the boat."}"#
        } else if system == DeliveryPrompt.system {
            answer = user.contains("{L1 ") ? #"{"lines":[{"line":"L1","hint":"Barely audible"}]}"# : #"{"lines":[]}"#
        } else if system == SpellingPrompt.system {
            answer = #"{"terms":[{"id":"T1","sayItAs":"TEST-word","note":"test","confidence":70}]}"#
        }
        let reply: [String: Any] = Self.refuse
            ? ["type": "message", "role": "assistant", "content": [], "stop_reason": "refusal"]
            : ["type": "message", "role": "assistant", "content": [["type": "text", "text": answer]], "stop_reason": "end_turn"]
        let out = (try? JSONSerialization.data(withJSONObject: reply)) ?? Data()
        let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: ["content-type": "application/json"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: out)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
