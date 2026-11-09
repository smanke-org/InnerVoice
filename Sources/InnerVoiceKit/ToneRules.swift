import Foundation

/// Reads a line's tone from the words around it, before the AI: "she snapped" is angry,
/// "he whispered" is a whisper, "she sobbed" is sad. A clear speech tag is fairly sure;
/// an exclamation mark alone is only a guess for the AI to check; a line with no clue is
/// probably said plainly.
public enum ToneRules {
    /// A clue word and the tone it means. Matched as a whole word (or the start of one,
    /// for stems ending in "*"), in the speech tag after the line or the narration just
    /// before it.
    static let cues: [(Tone, [String])] = [
        (.whispered, ["whispered", "whispering", "whisper", "murmured", "murmuring", "breathed", "muttered", "mumbled",
                      "under her breath", "under his breath", "under their breath", "hushed", "barely audible"]),
        (.angry, ["snapped", "snarled", "growled", "barked", "yelled", "shouted", "roared", "bellowed", "spat", "fumed",
                  "raged", "seethed", "thundered", "hissed", "angrily", "furiously", "fiercely", "sharply", "glared",
                  "scowled", "slammed", "stormed", "bristled", "through gritted teeth", "through clenched teeth"]),
        (.sad, ["sobbed", "sobbing", "wept", "weeping", "sniffled", "sniffed", "whimpered", "sighed", "sadly", "mournfully",
                "tearfully", "miserably", "forlornly", "choked", "voice broke", "voice cracked", "her eyes filled", "his eyes filled", "wiped her eyes", "wiped his eyes"]),
        (.scared, ["stammered", "stuttered", "trembled", "trembling", "quavered", "shrieked", "screamed", "gasped",
                   "nervously", "fearfully", "frightened", "terrified", "panicked", "shakily", "voice shaking", "voice shook"]),
        (.excited, ["exclaimed", "squealed", "gushed", "whooped", "cheered", "beamed", "excitedly", "eagerly", "breathlessly",
                    "delighted", "thrilled", "giggled", "laughed", "crowed", "bounced"]),
        (.confused, ["puzzled", "confused", "bewildered", "baffled", "frowned", "blinked", "uncertainly", "hesitantly",
                     "hesitated", "wondered", "perplexed", "tilted her head", "tilted his head", "scratched his head", "scratched her head"]),
        (.tender, ["gently", "tenderly", "softly", "kindly", "warmly", "soothed", "soothingly", "cooed", "lovingly", "smiled"]),
        (.sarcastic, ["drawled", "sneered", "scoffed", "smirked", "mocked", "snorted", "dryly", "drily", "sarcastically",
                      "wryly", "rolled her eyes", "rolled his eyes", "rolled their eyes", "deadpan"]),
    ]

    public static let sureCue = 0.85
    /// "Said plainly" when nothing says otherwise: above the Unsure level, below what the
    /// AI is asked to look at again.
    public static let plain = 0.65
    public static let punctuationGuess = 0.45

    /// The tone the words around a line suggest, with how sure that is.
    public static func tone(for line: Line, in paragraph: Paragraph, linesInParagraph: [Line]) -> (Tone, Double) {
        let ns = paragraph.text as NSString
        let lineText = paragraph.substring(line.range)
        // The narration after the line, up to the next line or the end of the sentence,
        // then the narration just before it ("Her voice shook. “I can’t.”").
        let later = linesInParagraph.filter { $0.range.location > line.range.location }.map(\.range.location).min() ?? ns.length
        let afterRange = NSRange(location: min(line.range.end, ns.length), length: max(0, min(later, line.range.end + 90, ns.length) - line.range.end))
        let earlier = linesInParagraph.filter { $0.range.end <= line.range.location }.map(\.range.end).max() ?? 0
        let beforeStart = max(earlier, line.range.location - 90)
        let beforeRange = NSRange(location: beforeStart, length: max(0, line.range.location - beforeStart))
        let after = ns.substring(with: afterRange).lowercased()
        let before = ns.substring(with: beforeRange).lowercased()

        if let t = cue(in: after) ?? cue(in: lastSentence(before)) { return (t, sureCue) }

        let trimmed = lineText.trimmingCharacters(in: .whitespacesAndNewlines)
        if line.kind == .written { return (.neutral, 0.8) }
        if trimmed.contains("?!") || trimmed.contains("!?") { return (.confused, punctuationGuess) }
        if trimmed.contains("!") { return (.excited, punctuationGuess) }
        if line.kind == .thought { return (.neutral, 0.7) }
        return (.neutral, plain)
    }

    static func cue(in text: String) -> Tone? {
        guard !text.isEmpty else { return nil }
        var best: (Tone, String.Index)?
        for (tone, words) in cues {
            for w in words {
                guard let r = text.range(of: "\\b" + NSRegularExpression.escapedPattern(for: w) + "\\b", options: .regularExpression) else { continue }
                // The first clue in the tag wins ("she whispered angrily": a whisper).
                if best == nil || r.lowerBound < best!.1 { best = (tone, r.lowerBound) }
            }
        }
        return best?.0
    }

    static func lastSentence(_ s: String) -> String {
        let trimmed = s.trimmingCharacters(in: .whitespaces.union(CharacterSet(charactersIn: "“\"‘'")))
        guard let r = trimmed.dropLast().lastIndex(where: { ".!?".contains($0) }) else { return trimmed }
        return String(trimmed[trimmed.index(after: r)...])
    }

    /// Sets the tone of every line the narrator and the AI haven't decided. Returns how
    /// many lines changed.
    @discardableResult
    public static func apply(to project: inout Project, index: BookIndex? = nil) -> Int {
        let index = index ?? BookIndex(project)
        var changed = 0
        for (pid, lineIdx) in index.linesByParagraph {
            guard let paragraph = project.paragraph(pid, index: index) else { continue }
            let lines = lineIdx.map { project.lines[$0] }
            for i in lineIdx {
                let line = project.lines[i]
                guard line.toneSource == nil || line.toneSource == .rule else { continue }
                let (t, c) = tone(for: line, in: paragraph, linesInParagraph: lines)
                if line.tone != t || line.toneConfidence != c || line.toneSource != .rule {
                    project.lines[i].tone = t
                    project.lines[i].toneConfidence = c
                    project.lines[i].toneSource = .rule
                    changed += 1
                }
            }
        }
        return changed
    }
}

/// Asking the AI how each line is said, with the paragraphs around it.
public enum TonePrompt {
    public static let system = """
    You help an audiobook narrator prepare a novel. For each marked line, decide how the \
    character says it, from the words, the speech tag ("she snapped"), what just happened \
    and how the scene feels. Most lines are said plainly: use neutral unless the passage \
    gives a reason. Answer with JSON only.
    """

    /// Lines the AI should read: not set by the narrator or the AI, and not a clear tag.
    public static func needsModel(_ line: Line) -> Bool {
        guard line.kind != .written else { return false }
        switch line.toneSource {
        case .user?, .ai?: return false
        case .rule?: return (line.toneConfidence ?? 0) < ToneRules.sureCue
        case nil: return true
        }
    }

    public struct Chunk: Sendable {
        public var labels: [String: UUID]
        public var messages: [ChatMessage]
        /// The labels in reading order.
        public var order: [String] = []

        /// A grammar that needs one answer for every label, in order: the model can't
        /// reply with an empty list (Gemma 4 31B did, for every passage).
        public var grammar: String {
            let items = order.map { "\"{\" ws \"\\\"id\\\"\" ws \":\" ws \"\\\"\($0)\\\"\" ws \",\" ws rest" }
            return #"root ::= "{" ws "\"lines\"" ws ":" ws "[" ws "# + items.joined(separator: #" ws "," ws "#) + #" ws "]" ws "}""# + "\n" + TonePrompt.grammarTail
        }
    }

    public static func chunks(project: Project, index: BookIndex, chapterIndex: Int, maxWords: Int = 900) -> [Chunk] {
        let paragraphs = project.chapters[chapterIndex].paragraphs
        var out: [Chunk] = []
        var start = 0
        while start < paragraphs.count {
            var end = start, words = 0
            while end < paragraphs.count, words < maxWords || end == start {
                words += paragraphs[end].wordCount
                end += 1
            }
            let asked = Set(paragraphs[start..<end].flatMap { index.linesByParagraph[$0.id] ?? [] }.filter { needsModel(project.lines[$0]) })
            if !asked.isEmpty {
                out.append(build(project: project, index: index, paragraphs: Array(paragraphs[max(0, start - 1)..<end]), asked: asked,
                                 title: project.chapters[chapterIndex].title))
            }
            start = end
        }
        return out
    }

    static func build(project: Project, index: BookIndex, paragraphs: [Paragraph], asked: Set<Int>, title: String) -> Chunk {
        var labels: [String: UUID] = [:]
        var order: [String] = []
        var passage = ""
        var n = 0
        for p in paragraphs {
            let idx = (index.linesByParagraph[p.id] ?? []).sorted { project.lines[$0].range.location < project.lines[$1].range.location }
            let text = NSMutableString(string: p.text)
            var inserts: [(Int, String)] = []
            for i in idx {
                let line = project.lines[i]
                let who = project.character(line.speakerID)?.name ?? "someone"
                let kind = line.kind == .spoken ? "" : " \(line.kind.rawValue)"
                if asked.contains(i) {
                    n += 1
                    let label = "T\(n)"
                    labels[label] = line.id
                    order.append(label)
                    inserts.append((line.range.location, "{\(label): \(who)\(kind)}"))
                } else {
                    inserts.append((line.range.location, "{\(who)\(kind)}"))
                }
            }
            for (at, tag) in inserts.sorted(by: { $0.0 > $1.0 }) {
                var at = at
                if at > 0, "“\"‘'«—".contains(Character(UnicodeScalar(text.character(at: at - 1)) ?? " ")) { at -= 1 }
                text.insert(tag, at: at)
            }
            passage += (text as String) + "\n\n"
        }
        let tones = Tone.allCases.map(\.rawValue).joined(separator: ", ")
        let user = """
        Passage from “\(title)”:

        \(passage.trimmingCharacters(in: .whitespacesAndNewlines))

        For each of \(order.joined(separator: ", ")): the tone is one of \(tones). \
        confidence is 0 to 100; give under 50 when the passage doesn't make it clear.
        Reply as {"lines":[{"id":"T1","tone":"neutral","confidence":80}]}
        """
        return Chunk(labels: labels, messages: [ChatMessage(.system, system), ChatMessage(.user, user)], order: order)
    }

    /// What follows each answer's id.
    static let grammarTail = #"""
    rest ::= "\"tone\"" ws ":" ws tone ws "," ws "\"confidence\"" ws ":" ws number ws "}"
    tone ::= "\"neutral\"" | "\"excited\"" | "\"confused\"" | "\"sad\"" | "\"angry\"" | "\"whispered\"" | "\"scared\"" | "\"tender\"" | "\"sarcastic\""
    number ::= [0-9] [0-9]? [0-9]?
    ws ::= [ ]?
    """#

    public static let grammar = #"""
    root ::= "{" ws "\"lines\"" ws ":" ws "[" ws ( item ( ws "," ws item )* )? ws "]" ws "}"
    item ::= "{" ws "\"id\"" ws ":" ws string ws "," ws "\"tone\"" ws ":" ws tone ws "," ws "\"confidence\"" ws ":" ws number ws "}"
    tone ::= "\"neutral\"" | "\"excited\"" | "\"confused\"" | "\"sad\"" | "\"angry\"" | "\"whispered\"" | "\"scared\"" | "\"tender\"" | "\"sarcastic\""
    string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] )* "\""
    number ::= [0-9] [0-9]? [0-9]?
    ws ::= [ ]?
    """#

    public struct Reply: Codable, Hashable, Sendable {
        public var id: String
        public var tone: String
        public var confidence: Double?
    }

    public static func parse(_ text: String) -> [Reply]? {
        struct Wrapper: Codable { var lines: [Reply] }
        if let json = JSONText.extract(text), let whole = try? JSONDecoder().decode(Wrapper.self, from: Data(json.utf8)) {
            return whole.lines
        }
        let partial = JSONText.objects(in: text).compactMap { try? JSONDecoder().decode(Reply.self, from: Data($0.utf8)) }
        return partial.isEmpty ? nil : partial
    }

    /// Writes the tones into the project. Returns the lines before the change, for Undo.
    @discardableResult
    public static func apply(_ replies: [Reply], chunk: Chunk, to project: inout Project) -> [Line] {
        var lookup: [UUID: Int] = [:]
        for (i, l) in project.lines.enumerated() { lookup[l.id] = i }
        var before: [Line] = []
        for reply in replies {
            // The label alone: replies sometimes copy the speaker from the tag ("T1: Mara").
            let label = reply.id.range(of: #"T\d+"#, options: .regularExpression).map { String(reply.id[$0]) } ?? reply.id
            guard let id = chunk.labels[label], let i = lookup[id],
                  needsModel(project.lines[i]), let tone = Tone(rawValue: reply.tone.lowercased()) else { continue }
            let old = project.lines[i]
            before.append(old)
            var c = min(1, max(0, (reply.confidence ?? 60) / 100))
            // The words and the AI agreeing is worth more than either alone; an exclamation
            // mark the AI reads differently was only ever a guess.
            if old.tone == tone, old.toneSource == .rule { c = max(c, min(0.97, (old.toneConfidence ?? 0) + 0.2)) }
            project.lines[i].tone = tone
            project.lines[i].toneConfidence = c
            project.lines[i].toneSource = .ai
        }
        return before
    }
}
