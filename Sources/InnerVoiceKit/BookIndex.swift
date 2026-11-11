import Foundation

/// Fast lookups over a project: where each paragraph is, which lines it holds, who speaks
/// how much. Rebuilt whenever the project changes (linear in the size of the book).
public struct BookIndex: Sendable {
    public struct Position: Hashable, Sendable, Comparable {
        public var chapter: Int
        public var paragraph: Int
        /// Position in the whole book, for "before" and "after".
        public var global: Int
        public static func < (a: Position, b: Position) -> Bool { a.global < b.global }
    }

    public struct CharacterStats: Hashable, Sendable {
        public var lines = 0
        public var words = 0
        public var firstLine: Position?
        public var chapters: Set<Int> = []
    }

    public let positions: [UUID: Position]
    public let linesByParagraph: [UUID: [Int]]
    public let lineIndexByID: [UUID: Int]
    public let stats: [UUID: CharacterStats]
    public let totalLines: Int

    public init(_ project: Project) {
        var positions: [UUID: Position] = [:]
        var g = 0
        for (c, chapter) in project.chapters.enumerated() {
            for (p, paragraph) in chapter.paragraphs.enumerated() {
                positions[paragraph.id] = Position(chapter: c, paragraph: p, global: g)
                g += 1
            }
        }
        var byParagraph: [UUID: [Int]] = [:]
        var byID: [UUID: Int] = [:]
        var stats: [UUID: CharacterStats] = [:]
        let paragraphText: [UUID: Paragraph] = Dictionary(project.chapters.flatMap(\.paragraphs).map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        for (i, line) in project.lines.enumerated() {
            byParagraph[line.paragraphID, default: []].append(i)
            byID[line.id] = i
            if let speaker = line.speakerID, let pos = positions[line.paragraphID] {
                var s = stats[speaker] ?? CharacterStats()
                s.lines += 1
                if let p = paragraphText[line.paragraphID] { s.words += Words.count(p.substring(line.range)) }
                if s.firstLine == nil || pos < s.firstLine! { s.firstLine = pos }
                s.chapters.insert(pos.chapter)
                stats[speaker] = s
            }
        }
        self.positions = positions
        self.linesByParagraph = byParagraph
        self.lineIndexByID = byID
        self.stats = stats
        self.totalLines = project.lines.count
    }

    /// "Ch 3 ¶12" (1-based), the way references read everywhere in the app.
    public func reference(_ paragraphID: UUID) -> String {
        guard let p = positions[paragraphID] else { return "—" }
        return "Ch \(p.chapter + 1) ¶\(p.paragraph + 1)"
    }

    /// "Chapter 6 · ¶26": the chapter's own title, as its heading reads, then the paragraph
    /// (1-based). `reference` counts chapters from the front matter, so its numbers can be
    /// off from the headings; Review cards use this one.
    public func titledReference(_ paragraphID: UUID, in project: Project) -> String {
        guard let p = positions[paragraphID], project.chapters.indices.contains(p.chapter) else { return "—" }
        return "\(project.chapters[p.chapter].shownTitle) · ¶\(p.paragraph + 1)"
    }

    /// The paragraph's own chapter's paragraph indices from `around` before it to `around`
    /// after it, never past the chapter's first or last paragraph.
    public func contextRange(_ paragraphID: UUID, around: Int, in project: Project) -> ClosedRange<Int>? {
        guard let p = positions[paragraphID], project.chapters.indices.contains(p.chapter) else { return nil }
        let count = project.chapters[p.chapter].paragraphs.count
        let n = max(0, around)
        return max(0, p.paragraph - n)...min(count - 1, p.paragraph + n)
    }

    /// Main characters (over 15% of all lines) start hidden in the index.
    public func showsInIndexByDefault(_ characterID: UUID) -> Bool {
        guard totalLines > 0 else { return true }
        let share = Double(stats[characterID]?.lines ?? 0) / Double(totalLines)
        return share <= 0.15
    }

    public func showsInIndex(_ character: BookCharacter) -> Bool {
        character.showInIndex ?? showsInIndexByDefault(character.id)
    }
}

public extension Project {
    func paragraph(_ id: UUID, index: BookIndex) -> Paragraph? {
        guard let p = index.positions[id] else { return nil }
        return chapters[p.chapter].paragraphs[p.paragraph]
    }

    func text(of line: Line, index: BookIndex) -> String {
        paragraph(line.paragraphID, index: index)?.substring(line.range) ?? ""
    }
}

/// Characters that are probably the same person ("Mara" and "Mara Quinn").
public enum Duplicates {
    public struct Pair: Hashable, Sendable {
        /// The shorter name, to merge into `keep`.
        public var merge: UUID
        public var keep: UUID
    }

    public static func pairs(in project: Project) -> [Pair] {
        func words(_ c: BookCharacter) -> Set<String> {
            Set(c.name.split(separator: " ").map(String.init).filter { !Names.titles.contains($0) })
        }
        var out: [Pair] = []
        for a in project.characters {
            for b in project.characters where a.id != b.id {
                let wa = words(a), wb = words(b)
                // a's name is the first name of b ("Mara" / "Mara Quinn").
                if wa.count == 1, wb.count > 1, let w = wa.first, b.name.split(separator: " ").first.map(String.init) == w {
                    out.append(Pair(merge: a.id, keep: b.id))
                } else if b.aliases.contains(a.name) {
                    out.append(Pair(merge: a.id, keep: b.id))
                }
            }
        }
        return out
    }
}
