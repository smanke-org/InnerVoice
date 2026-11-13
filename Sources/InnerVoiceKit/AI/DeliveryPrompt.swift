import Foundation

/// A delivery hint Claude proposes for one line.
public struct DeliveryProposal: Identifiable, Hashable, Sendable {
    public var id: UUID { lineID }
    public var lineID: UUID
    public var hint: String
    public var before: String?
    public init(lineID: UUID, hint: String, before: String? = nil) {
        self.lineID = lineID
        self.hint = hint
        self.before = before
    }
}

/// Delivery Hints: how a line is performed beyond its tone ("barely audible", "through
/// gritted teeth", "laughing"). They reach ElevenLabs only, as an audio tag in square
/// brackets before the line (eleven_v4 takes free-form tags); no other engine is sent them.
public enum DeliveryPrompt {
    public static let system = """
    You help an audiobook producer direct AI voice actors. For each marked line, decide \
    whether it needs a performance direction beyond its tone, from what the passage says or \
    clearly shows: how loud (whispers, shouts, barely audible), the pace (slowly, rushed, \
    hesitant), a reaction (laughing, sighs, crying, out of breath), or the manner (through \
    gritted teeth, mocking, pleading). A direction is one to four plain words, lowercase. \
    Leave out lines that need nothing beyond their tone; most lines need nothing. Answer \
    with JSON only.
    """

    public struct Chunk: Sendable {
        public var labels: [String: UUID]
        public var messages: [ChatMessage]
        public init(labels: [String: UUID], messages: [ChatMessage]) {
            self.labels = labels
            self.messages = messages
        }
    }

    struct Reply: Codable { var line: String; var hint: String }

    /// Lines Claude may give a hint: spoken or thought lines with a speaker, whose hint isn't
    /// the narrator's own.
    public static func asks(_ line: Line) -> Bool {
        line.speakerID != nil && line.deliverySource != .user && line.kind != .written
    }

    /// The whole book, or `chapters`, in passages of about `maxWords`. Each asked line is
    /// marked in the text with its label, speaker and tone: {L3 Mara, sad}.
    public static func chunks(project: Project, index: BookIndex, chapters: [Int]? = nil, maxWords: Int = 2500) -> [Chunk] {
        let picked = (chapters ?? Array(project.chapters.indices)).filter { project.chapters.indices.contains($0) }
        var out: [Chunk] = []
        var labels: [String: UUID] = [:]
        var passage = ""
        var words = 0
        var n = 0
        func flush() {
            if !labels.isEmpty {
                let user = """
                Passage:

                \(passage.trimmingCharacters(in: .whitespacesAndNewlines))

                Reply as {"lines":[{"line":"L1","hint":"barely audible"}]}, only for lines that need a direction, or {"lines":[]}.
                """
                out.append(Chunk(labels: labels, messages: [ChatMessage(.system, system), ChatMessage(.user, user)]))
            }
            labels = [:]
            passage = ""
            words = 0
            n = 0
        }
        for c in picked {
            for p in project.chapters[c].paragraphs where p.style == .body || p.style == .blockQuote {
                let lines = (index.linesByParagraph[p.id] ?? []).map { project.lines[$0] }.sorted { $0.range.location < $1.range.location }
                let text = NSMutableString(string: p.text)
                var inserts: [(Int, String)] = []
                for line in lines where asks(line) && line.range.end <= text.length {
                    n += 1
                    labels["L\(n)"] = line.id
                    let who = project.character(line.speakerID)?.name ?? "?"
                    let tone = line.tone.map { $0 == .neutral ? "" : ", \($0.rawValue)" } ?? ""
                    // Before the opening quote mark, so the quote reads as it does in the book.
                    var at = line.range.location
                    if at > 0, "“\"‘'«—".contains(Character(UnicodeScalar(text.character(at: at - 1)) ?? " ")) { at -= 1 }
                    inserts.append((at, "{L\(n) \(who)\(tone)}"))
                }
                for (at, tag) in inserts.reversed() { text.insert(tag, at: at) }
                passage += (text as String) + "\n\n"
                words += p.wordCount
                if words >= maxWords { flush() }
            }
        }
        flush()
        return out
    }

    public static func parse(_ text: String) -> [(label: String, hint: String)]? {
        struct Wrapper: Codable { var lines: [Reply] }
        if let json = JSONText.extract(text), let w = try? JSONDecoder().decode(Wrapper.self, from: Data(json.utf8)) {
            return w.lines.map { ($0.line, $0.hint) }
        }
        let partial = JSONText.objects(in: text).compactMap { try? JSONDecoder().decode(Reply.self, from: Data($0.utf8)) }
        return partial.isEmpty ? nil : partial.map { ($0.line, $0.hint) }
    }

    /// A hint as ElevenLabs is sent it: plain words, no brackets or braces (they would be
    /// read aloud or end the tag early), at most six words. nil when nothing is left.
    public static func clean(_ hint: String) -> String? {
        let allowed = CharacterSet.letters.union(.whitespaces).union(CharacterSet(charactersIn: ",'’-"))
        let kept = String(String.UnicodeScalarView(hint.unicodeScalars.filter { allowed.contains($0) }))
        let words = kept.split(whereSeparator: \.isWhitespace).prefix(6)
        let out = words.joined(separator: " ").trimmingCharacters(in: CharacterSet(charactersIn: " ,-"))
        return out.isEmpty ? nil : String(out.prefix(60))
    }

    /// Proposals from a reply: known labels, cleaned hints that say more than the tone, and
    /// never a line whose hint is the narrator's.
    public static func proposals(_ replies: [(label: String, hint: String)], chunk: Chunk, project: Project, index: BookIndex) -> [DeliveryProposal] {
        var out: [DeliveryProposal] = []
        for r in replies {
            guard let id = chunk.labels[r.label], let i = index.lineIndexByID[id], let hint = clean(r.hint.lowercased()) else { continue }
            let line = project.lines[i]
            guard asks(line), hint != line.delivery else { continue }
            if let t = line.tone, hint == t.rawValue || "[\(hint)]" == t.audioTag { continue }
            out.append(DeliveryProposal(lineID: id, hint: hint, before: line.delivery))
        }
        return out
    }

    /// Stores the hints (the AI's), never over the narrator's own.
    public static func apply(_ proposals: [DeliveryProposal], to p: inout Project) {
        let byLine = Dictionary(proposals.map { ($0.lineID, $0.hint) }, uniquingKeysWith: { a, _ in a })
        for i in p.lines.indices {
            guard let hint = byLine[p.lines[i].id], p.lines[i].deliverySource != .user else { continue }
            p.lines[i].delivery = hint
            p.lines[i].deliverySource = .ai
        }
    }

    /// The narrator's own hint; empty clears it (and Claude may suggest one again).
    public static func set(_ hint: String, for ids: [UUID], in p: inout Project) {
        let ids = Set(ids)
        let cleaned = clean(hint)
        for i in p.lines.indices where ids.contains(p.lines[i].id) {
            p.lines[i].delivery = cleaned
            p.lines[i].deliverySource = cleaned == nil ? nil : .user
        }
    }

    /// The audio tag ElevenLabs gets for a line: tone and hint in one bracket
    /// ("[sad, barely audible]"), since stacked tags can blur the performance.
    public static func audioTag(tone: Tone?, delivery: String?) -> String? {
        let toneWord = tone?.audioTag.map { String($0.dropFirst().dropLast()) }
        let parts = [toneWord, delivery.flatMap(clean)].compactMap { $0 }
        return parts.isEmpty ? nil : "[" + parts.joined(separator: ", ") + "]"
    }
}
