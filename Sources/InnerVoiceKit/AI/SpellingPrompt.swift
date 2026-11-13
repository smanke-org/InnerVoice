import Foundation

/// A Say It As spelling Claude proposes for one pronunciation term.
public struct SpellingProposal: Identifiable, Hashable, Sendable {
    public var id: UUID { pronunciationID }
    public var pronunciationID: UUID
    public var term: String
    public var sayItAs: String
    public var note: String
    /// 0…100, Claude's own.
    public var confidence: Int
    public init(pronunciationID: UUID, term: String, sayItAs: String, note: String = "", confidence: Int = 50) {
        self.pronunciationID = pronunciationID
        self.term = term
        self.sayItAs = sayItAs
        self.note = note
        self.confidence = confidence
    }
}

/// Suggest Spellings: for terms in the pronunciation guide that have no Say It As yet,
/// Claude proposes a respelling. Only each term and the sentence it first appears in are
/// sent. The narrator confirms every one before it's used.
public enum SpellingPrompt {
    public static let system = """
    You help an audiobook narrator pronounce names, places and invented words. For each \
    term, give a respelling that an English speaker and a text-to-speech voice will read the \
    intended way, with hyphens between syllables and the stressed syllable in capitals, like \
    "SHIV-awn" for Siobhan or "her-MY-oh-nee" for Hermione. Use the sentence, the book and \
    the name's origin. Ordinary English words need no entry: leave them out. Say how sure you \
    are, 0 to 100. Answer with JSON only.
    """

    public struct Chunk: Sendable {
        public var labels: [String: UUID]
        public var messages: [ChatMessage]
        public init(labels: [String: UUID], messages: [ChatMessage]) {
            self.labels = labels
            self.messages = messages
        }
    }

    struct Reply: Codable {
        var id: String
        var sayItAs: String
        var note: String?
        var confidence: Int?
    }

    /// Terms still to do: not dismissed, not confirmed, and no Say It As yet.
    public static func wanted(_ p: Pronunciation) -> Bool {
        !p.dismissed && !p.confirmed && p.sayItAs.trimmingCharacters(in: .whitespaces).isEmpty
    }

    /// The sentence a term first appears in (at most 40 words), or "" when it isn't found.
    public static func context(for p: Pronunciation, project: Project, index: BookIndex) -> String {
        guard let pid = p.firstParagraphID, let para = project.paragraph(pid, index: index) else { return "" }
        let sentence = SpeechScript.sentences(para.text).first { $0.localizedCaseInsensitiveContains(p.term) } ?? ""
        let words = sentence.split(whereSeparator: \.isWhitespace)
        return words.count > 40 ? words.prefix(40).joined(separator: " ") + "…" : sentence
    }

    public static func chunks(project: Project, index: BookIndex, perRequest: Int = 60) -> [Chunk] {
        let terms = project.pronunciations.filter(wanted)
        var out: [Chunk] = []
        var start = 0
        while start < terms.count {
            let batch = terms[start..<min(terms.count, start + perRequest)]
            var labels: [String: UUID] = [:]
            var list = ""
            for (k, t) in batch.enumerated() {
                labels["T\(k + 1)"] = t.id
                let ctx = context(for: t, project: project, index: index)
                list += "[T\(k + 1)] \(t.term)" + (ctx.isEmpty ? "" : " — “\(ctx)”") + "\n"
            }
            let byline = project.author.isEmpty ? "" : " by \(project.author)"
            let user = """
            Book: \(project.title)\(byline)

            Terms, each with the sentence it first appears in:
            \(list)
            note says in a few words where the spelling comes from ("Irish name", "invented; follows the book's spelling").
            Reply as {"terms":[{"id":"T1","sayItAs":"SHIV-awn","note":"…","confidence":80}]}
            """
            out.append(Chunk(labels: labels, messages: [ChatMessage(.system, system), ChatMessage(.user, user)]))
            start += perRequest
        }
        return out
    }

    public static func parse(_ text: String, chunk: Chunk, project: Project) -> [SpellingProposal]? {
        struct Wrapper: Codable { var terms: [Reply] }
        var replies: [Reply]
        if let json = JSONText.extract(text), let w = try? JSONDecoder().decode(Wrapper.self, from: Data(json.utf8)) {
            replies = w.terms
        } else {
            replies = JSONText.objects(in: text).compactMap { try? JSONDecoder().decode(Reply.self, from: Data($0.utf8)) }
            if replies.isEmpty { return nil }
        }
        let byID = Dictionary(project.pronunciations.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return replies.compactMap { r in
            let say = r.sayItAs.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let id = chunk.labels[r.id], let p = byID[id], wanted(p), !say.isEmpty, say.count <= 80,
                  say.caseInsensitiveCompare(p.term) != .orderedSame else { return nil }
            return SpellingProposal(pronunciationID: id, term: p.term, sayItAs: say, note: r.note ?? "",
                                    confidence: min(100, max(0, r.confidence ?? 50)))
        }
    }

    /// Uses the spellings the narrator kept. A term filled in meanwhile keeps its own.
    /// Confirmed (checked with the author) stays the narrator's to tick.
    public static func apply(_ kept: [SpellingProposal], to p: inout Project) -> Int {
        var n = 0
        for s in kept {
            guard let i = p.pronunciations.firstIndex(where: { $0.id == s.pronunciationID }),
                  p.pronunciations[i].sayItAs.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
            p.pronunciations[i].sayItAs = s.sayItAs
            if p.pronunciations[i].notes.isEmpty, !s.note.isEmpty { p.pronunciations[i].notes = s.note + " (suggested by Claude)" }
            n += 1
        }
        return n
    }
}
