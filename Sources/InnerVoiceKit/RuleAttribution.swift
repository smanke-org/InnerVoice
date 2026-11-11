import Foundation

/// Character colors made for highlighting text. Red is never used: it means Who's Speaking?
/// Three families: masculine (blues and greens) for men, feminine (pinks and purples) for
/// women, and neutral (warm colors) for anyone the book hasn't said yet.
public enum CharacterPalette {
    public enum Family: String, CaseIterable, Sendable {
        case masculine, feminine, neutral

        public var title: String {
            switch self {
            case .masculine: "Masculine"
            case .feminine: "Feminine"
            case .neutral: "Neutral"
            }
        }

        public init(_ gender: VoiceGender?) {
            switch gender {
            case .male?: self = .masculine
            case .female?: self = .feminine
            default: self = .neutral
            }
        }
    }

    public struct Swatch: Hashable, Sendable, Identifiable {
        public var name: String
        public var hex: String
        public var family: Family
        public var id: String { hex }
    }

    /// Each family in the order new characters get them: neighbors are the easiest to tell apart.
    public static let swatches: [Swatch] = [
        ("Blue", "#3B82F6"), ("Emerald", "#10B981"), ("Indigo", "#6366F1"), ("Teal", "#14B8A6"),
        ("Sky", "#0EA5E9"), ("Green", "#22C55E"), ("Slate", "#64748B"), ("Cyan", "#06B6D4"),
    ].map { Swatch(name: $0.0, hex: $0.1, family: .masculine) } + [
        // Pinks come later: highlighted, they sit closest to the red of Who's Speaking?
        ("Fuchsia", "#D946EF"), ("Violet", "#8B5CF6"), ("Lavender", "#A78BFA"), ("Orchid", "#C026D3"),
        ("Pink", "#EC4899"), ("Purple", "#A855F7"), ("Lilac", "#E879F9"), ("Blush", "#F472B6"),
    ].map { Swatch(name: $0.0, hex: $0.1, family: .feminine) } + [
        ("Amber", "#F59E0B"), ("Lime", "#84CC16"), ("Orange", "#F97316"), ("Brown", "#A16207"),
        ("Gold", "#CA8A04"), ("Olive", "#65A30D"),
    ].map { Swatch(name: $0.0, hex: $0.1, family: .neutral) }

    public static func swatches(_ family: Family) -> [Swatch] { swatches.filter { $0.family == family } }

    /// The family a palette color belongs to; nil for a color picked with Other….
    public static func family(of hex: String) -> Family? {
        swatches.first { $0.hex.caseInsensitiveCompare(hex) == .orderedSame }?.family
    }

    /// The color for Who's Speaking? and Unsure lines.
    public static let attentionHex = "#EF4444"

    /// The first color in the family no character uses yet, or the family's least used one.
    public static func next(used: [String], family: Family = .neutral) -> String {
        next(counts: Dictionary(used.map { ($0.uppercased(), 1) }, uniquingKeysWith: +), family: family)
    }

    static func next(counts: [String: Int], family: Family) -> String {
        let pool = swatches(family)
        if let free = pool.first(where: { counts[$0.hex.uppercased()] == nil }) { return free.hex }
        return pool.min { (counts[$0.hex.uppercased()] ?? 0) < (counts[$1.hex.uppercased()] ?? 0) }!.hex
    }

    /// The color a new character starts with: a title ("Mrs.", "Sir") already says which
    /// family; anyone else starts neutral until the book says more.
    public static func forNew(named name: String, used: [String]) -> String {
        next(used: used, family: Family(VoiceCasting.titleGender(name)))
    }

    /// The family for each character on Automatic. Finding gender reads the whole book for
    /// pronouns (over a second for a novel's cast the first time), so the app runs this off
    /// the main thread and hands the result to `recolorAutomatic`.
    public static func families(_ p: Project, index: BookIndex) -> [UUID: Family] {
        var result: [UUID: Family] = [:]
        for c in p.characters where c.colorIsAutomatic {
            result[c.id] = Family(VoiceCasting.profile(c, project: p, index: index).gender)
        }
        return result
    }

    /// Gives every character on Automatic a color from their gender's family: main characters
    /// first, so they get the most distinct colors. A character whose color already fits keeps
    /// it, so colors don't shuffle each time; colors the narrator chose are never changed.
    /// `only` recolors one character and leaves everyone else as they are. `families` comes
    /// from `families(_:index:)` run off the main thread; characters it doesn't list keep
    /// their colors. Returns how many characters changed color.
    @discardableResult
    public static func recolorAutomatic(_ p: inout Project, index: BookIndex, families known: [UUID: Family]? = nil, only: UUID? = nil) -> Int {
        let moving = p.characters.indices.filter { i in
            let c = p.characters[i]
            return c.colorIsAutomatic && (only == nil || c.id == only) && (known == nil || known?[c.id] != nil)
        }
        guard !moving.isEmpty else { return 0 }
        var counts: [String: Int] = [:]
        for (i, c) in p.characters.enumerated() where !moving.contains(i) { counts[c.colorHex.uppercased(), default: 0] += 1 }
        let count = Dictionary(uniqueKeysWithValues: moving.map { ($0, index.stats[p.characters[$0].id]?.lines ?? 0) })
        let names = Dictionary(uniqueKeysWithValues: moving.map { ($0, p.characters[$0].name) })
        let order = moving.sorted { count[$0]! != count[$1]! ? count[$0]! > count[$1]! : names[$0]! < names[$1]! }
        var family: [Int: Family] = [:]
        for i in order { family[i] = known?[p.characters[i].id] ?? Family(VoiceCasting.profile(p.characters[i], project: p, index: index).gender) }
        // Keep every color that already fits and nobody earlier holds.
        var placed: Set<Int> = []
        for i in order {
            let hex = p.characters[i].colorHex.uppercased()
            if Self.family(of: hex) == family[i], counts[hex] == nil {
                counts[hex] = 1
                placed.insert(i)
            }
        }
        var changed = 0
        for i in order where !placed.contains(i) {
            let hex = next(counts: counts, family: family[i]!)
            counts[hex.uppercased(), default: 0] += 1
            if p.characters[i].colorHex.caseInsensitiveCompare(hex) != .orderedSame {
                p.characters[i].colorHex = hex
                changed += 1
            }
        }
        return changed
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

    static let afterVerbName = try! NSRegularExpression(pattern: "^[\\s,.;:—–!?…”“\"’‘«»]*" + verbGroup + "\\s+" + nameGroup)
    static let afterNameVerb = try! NSRegularExpression(pattern: "^[\\s,.;:—–!?…”“\"’‘«»]*" + nameGroup + "\\s+(?:\\p{Ll}+ly\\s+)?" + verbGroup + "\\b")
    static let afterPronoun = try! NSRegularExpression(pattern: "^[\\s,.;:—–!?…”“\"’‘«»]*(?:(I|he|she)\\s+(?:\\p{Ll}+ly\\s+)?" + verbGroup + "\\b|" + verbGroup + "\\s+(I|he|she)\\b)", options: [.caseInsensitive])
    static let beforeNameVerb = try! NSRegularExpression(pattern: nameGroup + "\\s+(?:\\p{Ll}+ly\\s+)?" + verbGroup + "[^.!?\"“”]{0,40}[,:]\\s*[“\"‘'«]?\\s*$")
    static let beforePronoun = try! NSRegularExpression(pattern: "\\b(I|he|she)\\s+(?:\\p{Ll}+ly\\s+)?" + verbGroup + "[^.!?\"“”]{0,40}[,:]\\s*[“\"‘'«]?\\s*$", options: [.caseInsensitive])

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
                    } else if named.count > 1, let first = spoken.first,
                              let subject = sentenceSubject(before: project.lines[first].range.location, in: paragraph,
                                                            lines: indices.map { project.lines[$0] }, candidates: named) {
                        // 4b. Several people named: the one doing something in the sentence just
                        // before the line probably speaks ("Cyrus flipped the pages and passed
                        // the book to Barnabe. 'You can,' he said"). A guess, so it stays Unsure.
                        for i in spoken { set(i, subject, unsureGuess, .rule) }
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

    /// The confidence of a guess the rules can't be sure of: below the Unsure line, so the
    /// narrator still checks it, but the name is there.
    public static let unsureGuess = 0.5

    /// The character who starts the last sentence of narration before `location` in the
    /// paragraph (the sentence's subject): "Cyrus flipped …" is Cyrus.
    static func sentenceSubject(before location: Int, in paragraph: Paragraph, lines: [Line], candidates: [BookCharacter]) -> UUID? {
        let ns = paragraph.text as NSString
        let end = min(location, ns.length)
        // Only narration counts, not words inside earlier quotes.
        var narration = ""
        var cursor = 0
        for l in lines.sorted(by: { $0.range.location < $1.range.location }) where l.range.location < end {
            if l.range.location > cursor { narration += ns.substring(with: NSRange(location: cursor, length: l.range.location - cursor)) }
            narration += " . "
            cursor = max(cursor, l.range.end)
        }
        if end > cursor { narration += ns.substring(with: NSRange(location: cursor, length: end - cursor)) }
        let sentences = narration.components(separatedBy: CharacterSet(charactersIn: ".!?…"))
            .map { $0.trimmingCharacters(in: CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn: "“”\"‘’'«»—–,;:"))) }
            .filter { Words.count($0) >= 2 }
        guard let last = sentences.last else { return nil }
        let names = candidates.flatMap { c in c.allNames.map { (c.id, $0) } }.sorted { $0.1.count > $1.1.count }
        for (id, name) in names where last.hasPrefix(name + " ") {
            let rest = last.dropFirst(name.count + 1)
            if let next = rest.first, next.isLowercase { return id }
        }
        return nil
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
        guard !word.isEmpty, text.contains(word) else { return false }
        // Only check word boundaries when the plain search found it.
        var search = text.startIndex..<text.endIndex
        while let r = text.range(of: word, range: search) {
            let before = r.lowerBound > text.startIndex ? text[text.index(before: r.lowerBound)] : " "
            let after = r.upperBound < text.endIndex ? text[r.upperBound] : " "
            if !before.isLetter && !after.isLetter { return true }
            search = r.upperBound..<text.endIndex
        }
        return false
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
        let character = BookCharacter(name: name, colorHex: CharacterPalette.forNew(named: name, used: project.characters.map(\.colorHex)))
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
