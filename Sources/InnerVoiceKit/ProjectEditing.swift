import Foundation

// MARK: Speaker lists

extension Project {
    /// Every speaker list in the same order: pinned characters first, then everyone
    /// else, each part A to Z by first name (then by full name).
    public var speakerOrder: (pinned: [BookCharacter], others: [BookCharacter]) {
        let sorted = characters.sorted(by: Self.byFirstName)
        return (sorted.filter(\.isPinned), sorted.filter { !$0.isPinned })
    }

    public static func byFirstName(_ a: BookCharacter, _ b: BookCharacter) -> Bool {
        let first = a.firstName.localizedStandardCompare(b.firstName)
        if first != .orderedSame { return first == .orderedAscending }
        return a.name.localizedStandardCompare(b.name) == .orderedAscending
    }
}

// MARK: Deleting text

extension Project {
    /// Deletes text the narrator doesn't read (an ISBN, an ad for another book): `cuts`
    /// are ranges inside paragraphs, `chapters` are whole chapters, heading included.
    ///
    /// Lines inside a cut go, lines that overlap one are trimmed, and later lines in the
    /// paragraph move up. A paragraph left with nothing but spaces goes too, along with
    /// its recorded mark and retakes; summary facts and pronunciations that pointed at it
    /// keep their text but lose the link. Only the project's copy changes: the manuscript
    /// file it came from is never touched.
    ///
    /// `headings` are ranges inside chapter titles (front matter often imports as a
    /// heading: "Copyright", an ISBN). A heading with nothing left isn't a chapter: its
    /// text joins the chapter before it, or, for the first chapter, stays with no heading.
    public mutating func deleteText(cuts: [UUID: [TextSpan]], chapters doomed: Set<UUID> = [], headings: [UUID: TextSpan] = [:]) {
        var removedParagraphs = Set<UUID>()
        var lineChanges: [UUID: [TextSpan]] = [:]   // paragraph → the cuts as applied, last first

        for c in chapters.indices {
            if doomed.contains(chapters[c].id) {
                removedParagraphs.formUnion(chapters[c].paragraphs.map(\.id))
                continue
            }
            for p in chapters[c].paragraphs.indices {
                let id = chapters[c].paragraphs[p].id
                guard let spans = cuts[id], !spans.isEmpty else { continue }
                var paragraph = chapters[c].paragraphs[p]
                var applied: [TextSpan] = []
                // Back to front, so earlier cuts keep their places.
                for span in spans.sorted(by: { $0.location > $1.location }) {
                    let ns = paragraph.text as NSString
                    var r = NSIntersectionRange(span.ns, NSRange(location: 0, length: ns.length))
                    guard r.length > 0 else { continue }
                    r = Self.absorbSpace(around: r, in: ns)
                    paragraph.text = ns.replacingCharacters(in: r, with: "")
                    let cut = TextSpan(r)
                    paragraph.italic = paragraph.italic.compactMap { Self.span($0, after: cut) }
                    applied.append(cut)
                }
                if paragraph.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    removedParagraphs.insert(id)
                } else {
                    chapters[c].paragraphs[p] = paragraph
                    lineChanges[id] = applied
                }
            }
            chapters[c].paragraphs.removeAll { removedParagraphs.contains($0.id) }
        }
        chapters.removeAll { doomed.contains($0.id) }
        cutHeadings(headings)

        lines = lines.compactMap { line in
            if removedParagraphs.contains(line.paragraphID) { return nil }
            guard let applied = lineChanges[line.paragraphID] else { return line }
            var l = line
            for cut in applied {
                guard let moved = Self.span(l.range, after: cut) else { return nil }
                l.range = moved
            }
            return l
        }

        guard !removedParagraphs.isEmpty else { return }
        recorded.subtract(removedParagraphs)
        retakes.removeAll { removedParagraphs.contains($0.paragraphID) }
        for i in characters.indices {
            for f in characters[i].facts.indices where characters[i].facts[f].sourceParagraphID.map(removedParagraphs.contains) == true {
                characters[i].facts[f].sourceParagraphID = nil
            }
        }
        for i in pronunciations.indices where pronunciations[i].firstParagraphID.map(removedParagraphs.contains) == true {
            pronunciations[i].firstParagraphID = nil
        }
        for i in revisions.indices {
            revisions[i].added.removeAll { removedParagraphs.contains($0) }
            revisions[i].changed.removeAll { removedParagraphs.contains($0) }
        }
    }

    /// Takes text out of chapter headings; an emptied heading's chapter joins the one before.
    mutating func cutHeadings(_ headings: [UUID: TextSpan]) {
        for c in chapters.indices.reversed() {
            guard let span = headings[chapters[c].id] else { continue }
            let ns = chapters[c].title as NSString
            let r = NSIntersectionRange(span.ns, NSRange(location: 0, length: ns.length))
            guard r.length > 0 else { continue }
            let title = ns.replacingCharacters(in: Self.absorbSpace(around: r, in: ns), with: "")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !title.isEmpty {
                chapters[c].title = title
            } else if c > 0 {
                chapters[c - 1].paragraphs += chapters[c].paragraphs
                chapters.remove(at: c)
            } else if chapters[c].paragraphs.isEmpty {
                chapters.remove(at: c)
            } else {
                chapters[c].title = ""
            }
        }
    }

    /// Where a span ends up once `cut` is taken out of its paragraph: moved up when it
    /// comes after, trimmed when it overlaps, nil when nothing of it is left.
    static func span(_ s: TextSpan, after cut: TextSpan) -> TextSpan? {
        if s.end <= cut.location { return s }
        if s.location >= cut.end { return TextSpan(location: s.location - cut.length, length: s.length) }
        let before = max(0, cut.location - s.location)
        let afterPart = max(0, s.end - cut.end)
        let length = before + afterPart
        guard length > 0 else { return nil }
        return TextSpan(location: min(s.location, cut.location), length: length)
    }

    /// One space between the words either side of a cut, not two; none at either end.
    static func absorbSpace(around r: NSRange, in ns: NSString) -> NSRange {
        func isSpace(_ i: Int) -> Bool {
            guard i >= 0, i < ns.length, let scalar = Unicode.Scalar(ns.character(at: i)) else { return false }
            return CharacterSet.whitespaces.contains(scalar)
        }
        var r = r
        if r.location == 0 {
            while isSpace(NSMaxRange(r)) { r.length += 1 }
        } else if NSMaxRange(r) == ns.length {
            while isSpace(r.location - 1) { r.location -= 1; r.length += 1 }
        } else if isSpace(r.location - 1), isSpace(NSMaxRange(r)) {
            r.length += 1
        }
        return r
    }
}

extension Chapter {
    /// The title to show in lists: a chapter whose heading was deleted is the book's opening.
    public var shownTitle: String { title.isEmpty ? "Opening" : title }
}
