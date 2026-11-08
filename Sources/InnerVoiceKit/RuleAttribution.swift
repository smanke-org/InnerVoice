import Foundation

/// Character colors made for highlighting text. Red is never used: it means Who's Speaking?
public enum CharacterPalette {
    public struct Swatch: Hashable, Sendable, Identifiable {
        public var name: String
        public var hex: String
        public var id: String { hex }
    }

    public static let swatches: [Swatch] = [
        Swatch(name: "Amber", hex: "#F59E0B"), Swatch(name: "Sky", hex: "#0EA5E9"),
        Swatch(name: "Emerald", hex: "#10B981"), Swatch(name: "Violet", hex: "#8B5CF6"),
        Swatch(name: "Orange", hex: "#F97316"), Swatch(name: "Teal", hex: "#14B8A6"),
        Swatch(name: "Indigo", hex: "#6366F1"), Swatch(name: "Lime", hex: "#84CC16"),
        Swatch(name: "Fuchsia", hex: "#D946EF"), Swatch(name: "Cyan", hex: "#06B6D4"),
        Swatch(name: "Gold", hex: "#CA8A04"), Swatch(name: "Blue", hex: "#3B82F6"),
        Swatch(name: "Green", hex: "#22C55E"), Swatch(name: "Purple", hex: "#A855F7"),
        Swatch(name: "Brown", hex: "#A16207"), Swatch(name: "Pink", hex: "#EC4899"),
        Swatch(name: "Slate", hex: "#64748B"), Swatch(name: "Olive", hex: "#65A30D"),
    ]

    /// The color for Who's Speaking? and Unsure lines.
    public static let attentionHex = "#EF4444"

    /// The first swatch no character uses yet, or the least used one.
    public static func next(used: [String]) -> String {
        let counts = Dictionary(used.map { ($0.uppercased(), 1) }, uniquingKeysWith: +)
        if let free = swatches.first(where: { counts[$0.hex.uppercased()] == nil }) { return free.hex }
        return swatches.min { (counts[$0.hex.uppercased()] ?? 0) < (counts[$1.hex.uppercased()] ?? 0) }!.hex
    }

    /// Reddish colors are too close to Who's Speaking? to tell apart.
    public static func isTooRed(_ hex: String) -> Bool {
        guard let (r, g, b) = rgb(hex) else { return false }
        let maxC = max(r, g, b), minC = min(r, g, b)
        guard maxC - minC > 0.25, maxC == r else { return false }
        // Hue near 0°: red channel dominant, green and blue both low and close together.
        let hue = (g - b) / (maxC - minC) * 60
        return hue > -20 && hue < 15
    }

    public static func rgb(_ hex: String) -> (Double, Double, Double)? {
        let h = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        guard h.count == 6, let v = UInt32(h, radix: 16) else { return nil }
        return (Double((v >> 16) & 0xFF) / 255, Double((v >> 8) & 0xFF) / 255, Double(v & 0xFF) / 255)
    }
}

/// Assigns speakers with plain rules: "said Tom", "Tom asked", a quote that carries on,
/// one speaker per paragraph, a named character's action beat, and a two-person
/// back-and-forth. Never changes lines the narrator or the AI assigned.
public enum RuleAttribution {
    static let verbs = [
        "said", "says", "asked", "asks", "replied", "replies", "answered", "whispered", "shouted", "yelled", "cried",
        "called", "muttered", "murmured", "snapped", "added", "continued", "began", "told", "explained", "insisted",
        "demanded", "admitted", "agreed", "argued", "laughed", "sighed", "growled", "hissed", "breathed", "repeated",
        "offered", "suggested", "warned", "wondered", "mumbled", "screamed", "exclaimed", "protested", "pleaded",
        "stammered", "retorted", "chuckled", "grumbled", "drawled", "barked", "sobbed", "gasped", "snarled", "teased",
        "joked", "announced", "declared", "observed", "remarked", "noted", "whimpered", "croaked", "rasped", "purred",
        "thought", "wrote", "read", "sang", "promised", "agreed", "corrected", "interrupted", "conceded", "confirmed",
    ]
    static let verbGroup = "(?:" + verbs.joined(separator: "|") + ")"
    static let titleGroup = "(?:(?:" + Names.titles.map { NSRegularExpression.escapedPattern(for: $0) }.joined(separator: "|") + ")\\s+)?"
    static let nameGroup = "(" + titleGroup + "\\p{Lu}[\\p{L}’'\\-]+(?:\\s+\\p{Lu}[\\p{L}’'\\-]+){0,2})"

    static let afterVerbName = try! NSRegularExpression(pattern: "^[\\s,.;:—–!?…]*" + verbGroup + "\\s+" + nameGroup)
    static let afterNameVerb = try! NSRegularExpression(pattern: "^[\\s,.;:—–!?…]*" + nameGroup + "\\s+(?:\\p{Ll}+ly\\s+)?" + verbGroup + "\\b")
    static let afterPronoun = try! NSRegularExpression(pattern: "^[\\s,.;:—–!?…]*(?:(I|he|she)\\s+(?:\\p{Ll}+ly\\s+)?" + verbGroup + "\\b|" + verbGroup + "\\s+(I|he|she)\\b)", options: [.caseInsensitive])
    static let beforeNameVerb = try! NSRegularExpression(pattern: nameGroup + "\\s+(?:\\p{Ll}+ly\\s+)?" + verbGroup + "[^.!?\"“”]{0,40}[,:]\\s*$")
    static let beforePronoun = try! NSRegularExpression(pattern: "\\b(I|he|she)\\s+(?:\\p{Ll}+ly\\s+)?" + verbGroup + "[^.!?\"“”]{0,40}[,:]\\s*$", options: [.caseInsensitive])

    /// A hint the rules found but could not settle: a pronoun.
    public enum Hint: Hashable, Sendable { case pronoun(String) }

    /// Runs every rule over the lines that have no assignment yet. Returns pronoun hints for the AI.
    @discardableResult
    public static func apply(to project: inout Project) -> [UUID: Hint] {
        var hints: [UUID: Hint] = [:]
        let index = BookIndex(project)
        var byParagraph = index.linesByParagraph

        func set(_ i: Int, _ speaker: UUID, _ confidence: Double, _ source: AssignmentSource) {
            guard project.lines[i].source == .none else { return }
            project.lines[i].speakerID = speaker
            project.lines[i].confidence = confidence
            project.lines[i].source = source
        }

        // 1. Speech tags next to the line.
        for chapter in project.chapters {
            for paragraph in chapter.paragraphs {
                guard let indices = byParagraph[paragraph.id] else { continue }
                let text = paragraph.text as NSString
                for (n, i) in indices.enumerated() where project.lines[i].kind != .written {
                    let line = project.lines[i]
                    let afterStart = line.range.end
                    let afterEnd = n + 1 < indices.count ? project.lines[indices[n + 1]].range.location : text.length
                    let beforeStart = n > 0 ? project.lines[indices[n - 1]].range.end : 0
                    let after = afterEnd > afterStart ? text.substring(with: NSRange(location: afterStart, length: afterEnd - afterStart)) : ""
                    let before = line.range.location > beforeStart ? text.substring(with: NSRange(location: beforeStart, length: line.range.location - beforeStart)) : ""
                    if let name = match(afterVerbName, after) ?? match(afterNameVerb, after) ?? match(beforeNameVerb, before),
                       let speaker = resolve(name, in: &project, create: true) {
                        set(i, speaker, 0.95, .rule)
                    } else if let pronoun = match(afterPronoun, after) ?? match(beforePronoun, before) {
                        if pronoun.lowercased() == "i", let pov = project.settings.povCharacterID {
                            set(i, pov, 0.9, .rule)
                        } else {
                            hints[line.id] = .pronoun(pronoun.lowercased())
                        }
                    }
                }
            }
        }

        byParagraph = BookIndex(project).linesByParagraph
        for chapter in project.chapters {
            var previousParagraphSpeaker: UUID?
            var history: [UUID?] = [] // speaker of each recent dialogue paragraph, nil for narration
            for paragraph in chapter.paragraphs {
                guard let indices = byParagraph[paragraph.id], !indices.isEmpty else {
                    if Words.count(paragraph.text) > 0 { history.append(nil) }
                    previousParagraphSpeaker = nil
                    continue
                }
                // 2. A quote that carries on from the last paragraph keeps its speaker.
                for i in indices where project.lines[i].continuesPrevious {
                    if let s = previousParagraphSpeaker { set(i, s, 0.9, .rule) }
                }
                // 3. One speaker per paragraph.
                let spoken = indices.filter { project.lines[$0].kind == .spoken }
                if let known = spoken.compactMap({ project.lines[$0].speakerID }).first {
                    for i in spoken { set(i, known, 0.85, .rule) }
                }
                // 4. An action beat: the only character named in the narration is the speaker.
                if spoken.contains(where: { project.lines[$0].speakerID == nil }) {
                    let narration = narrationText(paragraph, lines: indices.map { project.lines[$0] })
                    let named = project.characters.filter { c in c.allNames.contains { containsWord(narration, $0) } }
                    if named.count == 1 {
                        for i in spoken { set(i, named[0].id, 0.7, .rule) }
                    }
                }
                // 5. A back-and-forth between two people with no tags alternates.
                let dialogueOnly = Words.count(narrationText(paragraph, lines: indices.map { project.lines[$0] })) <= 3
                if dialogueOnly, spoken.allSatisfy({ project.lines[$0].speakerID == nil }), history.count >= 2,
                   let a = history[history.count - 2], let b = history[history.count - 1], a != b {
                    for i in spoken { set(i, a, 0.65, .exchange) }
                }
                let speaker = spoken.compactMap { project.lines[$0].speakerID }.first
                history.append(dialogueOnly || speaker != nil ? speaker : nil)
                if history.count > 6 { history.removeFirst() }
                previousParagraphSpeaker = indices.last.flatMap { project.lines[$0].speakerID }
            }
        }
        return hints
    }

    static func match(_ regex: NSRegularExpression, _ text: String) -> String? {
        let ns = text as NSString
        guard let m = regex.firstMatch(in: text, range: NSRange(location: 0, length: ns.length)) else { return nil }
        for g in 1..<m.numberOfRanges where m.range(at: g).location != NSNotFound {
            return ns.substring(with: m.range(at: g))
        }
        return nil
    }

    /// Text in a paragraph that is not inside any line.
    public static func narrationText(_ paragraph: Paragraph, lines: [Line]) -> String {
        let text = paragraph.text as NSString
        var out = ""
        var cursor = 0
        for line in lines.sorted(by: { $0.range.location < $1.range.location }) {
            if line.range.location > cursor {
                out += text.substring(with: NSRange(location: cursor, length: line.range.location - cursor)) + " "
            }
            cursor = max(cursor, line.range.end)
        }
        if cursor < text.length { out += text.substring(from: cursor) }
        return out.trimmingCharacters(in: CharacterSet(charactersIn: " “”\"‘’'«»—–"))
    }

    static func containsWord(_ text: String, _ word: String) -> Bool {
        guard !word.isEmpty else { return false }
        let pattern = "(?<![\\p{L}])" + NSRegularExpression.escapedPattern(for: word) + "(?![\\p{L}])"
        return text.range(of: pattern, options: .regularExpression) != nil
    }

    /// Finds the character a name refers to, or adds one.
    public static func resolve(_ rawName: String, in project: inout Project, create: Bool) -> UUID? {
        let cleaned = rawName.trimmingCharacters(in: CharacterSet(charactersIn: " ,.’'"))
            .replacingOccurrences(of: "’s$", with: "", options: .regularExpression)
        // "Then Tom" is Tom.
        let words = Array(cleaned.split(separator: " ").map(String.init).drop { Names.notNames.contains($0) })
        guard !words.isEmpty else { return nil }
        let name = words.joined(separator: " ")
        // "Tom Hale" matches Tom Hale; "Tom" matches Tom Hale; "Hale" matches Tom Hale.
        if let c = project.characters.first(where: { $0.name == name || $0.aliases.contains(name) }) { return c.id }
        if let c = project.characters.first(where: { $0.allNames.contains(name) }) { return c.id }
        if words.count > 1, let c = project.characters.first(where: { c in words.contains { w in !Names.titles.contains(w) && c.allNames.contains(w) } }) {
            return c.id
        }
        guard create, name.count >= 2, !words.allSatisfy({ Names.titles.contains($0) }) else { return nil }
        let character = BookCharacter(name: name, colorHex: CharacterPalette.next(used: project.characters.map(\.colorHex)))
        project.characters.append(character)
        return character.id
    }
}

/// Fixing one line of a back-and-forth usually means the rest of it is off by one.
public enum Exchange {
    public struct Proposal: Hashable, Sendable {
        public var lineID: UUID
        public var speakerID: UUID
        public init(lineID: UUID, speakerID: UUID) {
            self.lineID = lineID
            self.speakerID = speakerID
        }
    }

    /// After the narrator gives the line `lineID` to a new speaker, proposes the alternating
    /// speakers for the dialogue paragraphs that follow, up to narration or a line the
    /// narrator assigned. `previousSpeaker` is the speaker the line had before.
    public static func proposals(in project: Project, after lineID: UUID, previousSpeaker: UUID?) -> [Proposal] {
        let index = BookIndex(project)
        guard let li = index.lineIndexByID[lineID], let newSpeaker = project.lines[li].speakerID,
              let pos = index.positions[project.lines[li].paragraphID] else { return [] }
        let chapter = project.chapters[pos.chapter]
        // The other person: the speaker of the paragraph before, or the line's old speaker.
        var other: UUID? = nil
        if pos.paragraph > 0, let prev = index.linesByParagraph[chapter.paragraphs[pos.paragraph - 1].id]?.first,
           let s = project.lines[prev].speakerID, s != newSpeaker {
            other = s
        }
        if other == nil, let p = previousSpeaker, p != newSpeaker { other = p }
        guard let otherSpeaker = other else { return [] }
        var out: [Proposal] = []
        var expectOther = true
        for p in chapter.paragraphs.dropFirst(pos.paragraph + 1) {
            guard let indices = index.linesByParagraph[p.id], !indices.isEmpty else { break }
            let lines = indices.map { project.lines[$0] }.filter { $0.kind == .spoken }
            guard !lines.isEmpty else { break }
            let narration = RuleAttribution.narrationText(p, lines: lines)
            if Words.count(narration) > 3 || lines.contains(where: { $0.source == .user || $0.source == .rule && $0.confidence >= 0.95 }) { break }
            let expected = expectOther ? otherSpeaker : newSpeaker
            for line in lines where line.speakerID != expected {
                out.append(Proposal(lineID: line.id, speakerID: expected))
            }
            expectOther.toggle()
        }
        return out
    }
}
