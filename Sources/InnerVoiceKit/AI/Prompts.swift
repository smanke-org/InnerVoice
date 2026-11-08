import Foundation

/// A piece of a chapter sent to the model to decide who speaks the marked lines.
public struct AttributionChunk: Sendable {
    public var chapterIndex: Int
    /// "L1" → line id, for the lines the model is asked about.
    public var labels: [String: UUID]
    public var messages: [ChatMessage]
    public var words: Int
}

/// One answer from the model about one line.
public struct AttributionReply: Codable, Hashable, Sendable {
    public var id: String
    public var speaker: String
    public var kind: String?
    public var confidence: Double?
}

/// One fact the model found about a character.
public struct FactReply: Codable, Hashable, Sendable {
    public var kind: String
    public var text: String
    public var paragraph: String?
    public var quote: String?
}

public enum AttributionPrompt {
    public static let system = """
    You help an audiobook narrator prepare a novel. You read a passage and decide who speaks, \
    thinks or writes each marked line, so the narrator knows which voice to use.
    Marked lines start with a tag like {L4} just before the line. Lines that are already known \
    start with the speaker's name, like {Tom}. Use the speech tags ("she said"), who is in the \
    scene, who was addressed, and the turn-taking of the conversation.
    Answer with JSON only.
    """

    /// Lines the model still needs to decide: not the narrator's and not a clear "said Tom".
    public static func needsModel(_ line: Line) -> Bool {
        switch line.source {
        case .user, .ai: false
        case .rule: line.confidence < 0.95
        case .none, .exchange: true
        }
    }

    /// Splits one chapter into passages of about `maxWords`, each with the lines to decide.
    public static func chunks(project: Project, index: BookIndex, chapterIndex: Int,
                              hints: [UUID: RuleAttribution.Hint] = [:], maxWords: Int = 900) -> [AttributionChunk] {
        let chapter = project.chapters[chapterIndex]
        var out: [AttributionChunk] = []
        var start = 0
        let paragraphs = chapter.paragraphs
        while start < paragraphs.count {
            var end = start
            var words = 0
            while end < paragraphs.count, words < maxWords || end == start {
                words += paragraphs[end].wordCount
                end += 1
            }
            let asked = paragraphs[start..<end].flatMap { index.linesByParagraph[$0.id] ?? [] }
                .filter { needsModel(project.lines[$0]) }
            if !asked.isEmpty {
                // Two paragraphs of lead-in, so the turn-taking makes sense.
                let contextStart = max(0, start - 2)
                out.append(build(project: project, index: index, chapterIndex: chapterIndex,
                                 range: contextStart..<end, asked: Set(asked), hints: hints, words: words))
            }
            start = end
        }
        return out
    }

    static func build(project: Project, index: BookIndex, chapterIndex: Int, range: Range<Int>, asked: Set<Int>,
                      hints: [UUID: RuleAttribution.Hint], words: Int) -> AttributionChunk {
        let chapter = project.chapters[chapterIndex]
        var labels: [String: UUID] = [:]
        var order: [String] = []
        var passage = ""
        var hintLines: [String] = []
        for p in chapter.paragraphs[range] {
            let lineIdx = (index.linesByParagraph[p.id] ?? []).sorted { project.lines[$0].range.location > project.lines[$1].range.location }
            let text = NSMutableString(string: p.text)
            // Insert tags from the end so earlier offsets stay valid.
            var inserts: [(Int, String)] = []
            for i in lineIdx {
                let line = project.lines[i]
                var at = line.range.location
                if at > 0, "“\"‘'«—".contains(Character(UnicodeScalar(text.character(at: at - 1)) ?? " ")) { at -= 1 }
                let kind = line.kind == .spoken ? "" : " \(line.kind.rawValue)"
                if asked.contains(i) {
                    inserts.append((at, "{PENDING\(i)\(kind)}"))
                } else if let s = project.character(line.speakerID) {
                    inserts.append((at, "{\(s.name)\(kind)}"))
                }
            }
            for (at, tag) in inserts { text.insert(tag, at: at) }
            passage += (text as String) + "\n\n"
        }
        // Number the asked lines in reading order.
        var n = 0
        let pattern = try! NSRegularExpression(pattern: "\\{PENDING(\\d+)( \\w+)?\\}")
        let ns = passage as NSString
        var result = ""
        var cursor = 0
        for m in pattern.matches(in: passage, range: NSRange(location: 0, length: ns.length)) {
            n += 1
            let label = "L\(n)"
            let i = Int(ns.substring(with: m.range(at: 1)))!
            let line = project.lines[i]
            labels[label] = line.id
            order.append(label)
            if case .pronoun(let p)? = hints[line.id] { hintLines.append("\(label): the tag says “\(p)”") }
            let suffix = m.range(at: 2).location != NSNotFound ? ns.substring(with: m.range(at: 2)) : ""
            result += ns.substring(with: NSRange(location: cursor, length: m.range.location - cursor)) + "{\(label)\(suffix)}"
            cursor = m.range.location + m.range.length
        }
        result += ns.substring(from: cursor)

        var user = "Characters so far: " + castList(project) + "\n"
        if let pov = project.character(project.settings.povCharacterID) {
            user += "The book is told in the first person by \(pov.name): “I” is \(pov.name).\n"
        }
        if !hintLines.isEmpty { user += "Hints: " + hintLines.joined(separator: "; ") + "\n" }
        user += "\nPassage from “\(chapter.title)”:\n\n" + result.trimmingCharacters(in: .whitespacesAndNewlines) + "\n\n"
        user += """
        For each of \(order.joined(separator: ", ")): give the speaker's name exactly as in the character list, \
        or a new name if the passage names someone not in the list, or "?" if the passage does not make it clear. \
        kind is spoken, thought or written. confidence is 0 to 100.
        Reply as {"lines":[{"id":"L1","speaker":"Name","kind":"spoken","confidence":90}]}
        """
        return AttributionChunk(chapterIndex: chapterIndex, labels: labels,
                                messages: [ChatMessage(.system, system), ChatMessage(.user, user)], words: words)
    }

    static func castList(_ project: Project) -> String {
        let cast = project.characters.prefix(40).map { c -> String in
            c.aliases.isEmpty ? c.name : "\(c.name) (also \(c.aliases.joined(separator: ", ")))"
        }
        return cast.isEmpty ? "none yet" : cast.joined(separator: "; ")
    }

    /// llama.cpp grammar that only allows the reply shape.
    public static let grammar = #"""
    root ::= "{" ws "\"lines\"" ws ":" ws "[" ws ( item ( ws "," ws item )* )? ws "]" ws "}"
    item ::= "{" ws "\"id\"" ws ":" ws string ws "," ws "\"speaker\"" ws ":" ws string ws "," ws "\"kind\"" ws ":" ws kind ws "," ws "\"confidence\"" ws ":" ws number ws "}"
    kind ::= "\"spoken\"" | "\"thought\"" | "\"written\""
    string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] )* "\""
    number ::= [0-9] [0-9]? [0-9]?
    ws ::= [ \t\n]*
    """#

    /// Pulls the replies out of the model's text, tolerating chatter around the JSON.
    public static func parse(_ text: String) -> [AttributionReply]? {
        struct Wrapper: Codable { var lines: [AttributionReply] }
        guard let json = JSONText.extract(text) else { return nil }
        return (try? JSONDecoder().decode(Wrapper.self, from: Data(json.utf8)))?.lines
    }

    /// Writes the replies into the project. Returns the lines before the change, for Undo.
    @discardableResult
    public static func apply(_ replies: [AttributionReply], chunk: AttributionChunk, to project: inout Project) -> [Line] {
        var before: [Line] = []
        var lookup: [UUID: Int] = [:]
        for (i, l) in project.lines.enumerated() { lookup[l.id] = i }
        for reply in replies {
            guard let id = chunk.labels[reply.id.trimmingCharacters(in: .whitespaces)], let i = lookup[id],
                  needsModel(project.lines[i]) else { continue }
            before.append(project.lines[i])
            let name = reply.speaker.trimmingCharacters(in: .whitespacesAndNewlines)
            let unknown = ["?", "", "unknown", "none", "n/a", "narrator", "unclear", "nobody", "spoken", "thought", "written"].contains(name.lowercased())
            var speaker: UUID? = nil
            if !unknown { speaker = RuleAttribution.resolve(name, in: &project, create: true) }
            var confidence = min(1, max(0, (reply.confidence ?? 60) / 100))
            let old = project.lines[i]
            // The rules and the model agreeing is worth more than either alone.
            if let s = speaker, old.speakerID == s, old.source != .none { confidence = max(confidence, min(0.97, old.confidence + 0.2)) }
            if let s = speaker, let o = old.speakerID, o != s, old.source == .rule { confidence = min(confidence, 0.55) }
            project.lines[i].speakerID = speaker
            project.lines[i].confidence = speaker == nil ? 0 : confidence
            project.lines[i].source = .ai
            if let k = reply.kind.flatMap(LineKind.init(rawValue:)) { project.lines[i].kind = k }
        }
        return before
    }
}

public enum SummaryPrompt {
    public static let system = """
    You help an audiobook narrator prepare to voice a character. From the passages, list what \
    the book says about the character: age, gender, accent and where they are from, how their \
    voice sounds, appearance, personality, history and relationships. Only use what the text \
    says or clearly shows. Each fact cites the paragraph label and copies the exact words that \
    support it. Answer with JSON only.
    """

    static let descriptive = ["accent", "voice", "years old", "year-old", "age", "born", "grew up", "eyes", "hair",
                              "tall", "short", "wife", "husband", "brother", "sister", "mother", "father", "son",
                              "daughter", "friend", "lisp", "drawl", "brogue", "whisper", "gravel", "husky", "deep",
                              "scottish", "irish", "english", "american", "southern", "french", "german", "italian",
                              "spanish", "russian", "london", "boston", "texas", "old", "young", "girl", "boy",
                              "woman", "man", "laugh", "smile", "scar", "wore", "dressed", "married", "widow"]

    public struct Chunk: Sendable {
        public var labels: [String: UUID]
        public var messages: [ChatMessage]
    }

    /// Picks the paragraphs that say most about a character: where they speak, where
    /// they are named, especially near descriptive words, early ones first.
    public static func chunks(for character: BookCharacter, project: Project, index: BookIndex,
                              maxParagraphs: Int = 48, maxWords: Int = 1100) -> [Chunk] {
        let names = character.allNames
        var scored: [(UUID, Int, Double)] = []
        let speaking = Set(project.lines.filter { $0.speakerID == character.id }.map(\.paragraphID))
        for chapter in project.chapters {
            for p in chapter.paragraphs {
                let named = names.contains { RuleAttribution.containsWord(p.text, $0) }
                guard named || speaking.contains(p.id) else { continue }
                let lower = p.text.lowercased()
                let hits = descriptive.filter { lower.contains($0) }.count
                let g = index.positions[p.id]?.global ?? 0
                scored.append((p.id, g, Double(hits * 3 + (named ? 2 : 0)) - Double(g) / 2000))
            }
        }
        let picked = scored.sorted { $0.2 > $1.2 }.prefix(maxParagraphs).sorted { $0.1 < $1.1 }
        var out: [Chunk] = []
        var labels: [String: UUID] = [:]
        var passage = ""
        var words = 0
        var n = 0
        func flush() {
            guard !labels.isEmpty else { return }
            let aliases = character.aliases.isEmpty ? "" : " (also called \(character.aliases.joined(separator: ", ")))"
            let user = """
            Character: \(character.name)\(aliases)

            Passages:

            \(passage.trimmingCharacters(in: .whitespacesAndNewlines))

            kind is one of age, gender, accent, voice, appearance, personality, history, relationships, other. \
            text is a short note for the narrator, like "About 60" or "Glaswegian accent". \
            Leave out anything the passages don't support. If there is nothing, reply {"facts":[]}.
            Reply as {"facts":[{"kind":"age","text":"…","paragraph":"P1","quote":"exact words"}]}
            """
            out.append(Chunk(labels: labels, messages: [ChatMessage(.system, system), ChatMessage(.user, user)]))
            labels = [:]
            passage = ""
            words = 0
        }
        for (id, _, _) in picked {
            guard let p = project.paragraph(id, index: index) else { continue }
            n += 1
            labels["P\(n)"] = id
            passage += "[P\(n)] \(p.text)\n\n"
            words += p.wordCount
            if words >= maxWords { flush() }
        }
        flush()
        return out
    }

    public static let grammar = #"""
    root ::= "{" ws "\"facts\"" ws ":" ws "[" ws ( item ( ws "," ws item )* )? ws "]" ws "}"
    item ::= "{" ws "\"kind\"" ws ":" ws kind ws "," ws "\"text\"" ws ":" ws string ws "," ws "\"paragraph\"" ws ":" ws string ws "," ws "\"quote\"" ws ":" ws string ws "}"
    kind ::= "\"age\"" | "\"gender\"" | "\"accent\"" | "\"voice\"" | "\"appearance\"" | "\"personality\"" | "\"history\"" | "\"relationships\"" | "\"other\""
    string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] )* "\""
    ws ::= [ \t\n]*
    """#

    public static func parse(_ text: String) -> [FactReply]? {
        struct Wrapper: Codable { var facts: [FactReply] }
        guard let json = JSONText.extract(text) else { return nil }
        return (try? JSONDecoder().decode(Wrapper.self, from: Data(json.utf8)))?.facts
    }

    /// Adds supported facts to the character. Facts whose quote is not in the cited
    /// paragraph are dropped (the model made them up); near-duplicates are skipped; facts
    /// the narrator edited are never touched. Returns how many were added.
    @discardableResult
    public static func merge(_ replies: [FactReply], chunk: Chunk, characterID: UUID, into project: inout Project, index: BookIndex) -> Int {
        guard let ci = project.characterIndex(characterID) else { return 0 }
        var added = 0
        for reply in replies {
            let text = reply.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty, text.count < 240 else { continue }
            let kind = FactKind(rawValue: reply.kind.lowercased()) ?? .other
            var source: UUID? = reply.paragraph.flatMap { chunk.labels[$0.trimmingCharacters(in: .whitespaces)] }
            let quote = (reply.quote ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if let id = source, let p = project.paragraph(id, index: index) {
                if !quote.isEmpty, !TextMatch.supports(paragraph: p.text, quote: quote) {
                    // Maybe the model cited the wrong label; look in the other passages.
                    source = chunk.labels.values.first { id in
                        project.paragraph(id, index: index).map { TextMatch.supports(paragraph: $0.text, quote: quote) } ?? false
                    }
                    if source == nil { continue }
                }
            } else {
                continue
            }
            let existing = project.characters[ci].facts.filter { $0.kind == kind }
            if existing.contains(where: { TextMatch.similarity($0.text, text) >= 0.6 }) { continue }
            if existing.count >= 6 { continue }
            project.characters[ci].facts.append(Fact(kind: kind, text: text, sourceParagraphID: source, quote: quote, fromAI: true))
            added += 1
        }
        return added
    }
}

/// Finding the JSON object in a model's reply.
public enum JSONText {
    public static func extract(_ text: String) -> String? {
        var t = text
        if let r = t.range(of: "</think>") { t = String(t[r.upperBound...]) }
        guard let start = t.firstIndex(of: "{") else { return nil }
        var depth = 0
        var inString = false
        var escaped = false
        var i = start
        while i < t.endIndex {
            let c = t[i]
            if inString {
                if escaped { escaped = false } else if c == "\\" { escaped = true } else if c == "\"" { inString = false }
            } else if c == "\"" {
                inString = true
            } else if c == "{" {
                depth += 1
            } else if c == "}" {
                depth -= 1
                if depth == 0 { return String(t[start...i]) }
            }
            i = t.index(after: i)
        }
        return nil
    }
}

/// Loose text comparison for quotes and duplicates.
public enum TextMatch {
    public static func normalize(_ s: String) -> String {
        var out = s.lowercased()
        for (a, b) in [("’", "'"), ("‘", "'"), ("“", "\""), ("”", "\""), ("—", " "), ("–", " "), ("…", "...")] {
            out = out.replacingOccurrences(of: a, with: b)
        }
        return out.components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.joined(separator: " ")
    }

    static func words(_ s: String) -> [String] {
        normalize(s).components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
    }

    /// True when the paragraph contains the quote, or nearly all of its words.
    public static func supports(paragraph: String, quote: String) -> Bool {
        let p = normalize(paragraph), q = normalize(quote)
        if p.contains(q) { return true }
        let qw = words(quote)
        guard qw.count >= 2 else { return false }
        let pw = Set(words(paragraph))
        return Double(qw.filter { pw.contains($0) }.count) / Double(qw.count) >= 0.8
    }

    /// Dice similarity of the two texts' word sets, 0…1.
    public static func similarity(_ a: String, _ b: String) -> Double {
        let wa = Set(words(a)), wb = Set(words(b))
        guard !wa.isEmpty || !wb.isEmpty else { return 1 }
        return 2 * Double(wa.intersection(wb).count) / Double(wa.count + wb.count)
    }
}
