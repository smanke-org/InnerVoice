import Foundation

/// A problem left in the text by importing (a scan, a PDF, an ebook).
public enum CleanupIssue: String, Codable, CaseIterable, Sendable {
    case ocr, hyphenation, pageNumber = "page-number", header, footnote, spacing, other

    public var title: String {
        switch self {
        case .ocr: "Scanning mistake"
        case .hyphenation: "Broken hyphenation"
        case .pageNumber: "Page number"
        case .header: "Page header"
        case .footnote: "Footnote marker"
        case .spacing: "Letter spacing"
        case .other: "Other"
        }
    }
}

/// One proposed fix: replace `original` (exact text in the paragraph) with `replacement`.
public struct CleanupFix: Identifiable, Hashable, Sendable {
    public var id: UUID
    public var paragraphID: UUID
    public var original: String
    public var replacement: String
    public var issue: CleanupIssue
    public var note: String

    public init(id: UUID = UUID(), paragraphID: UUID, original: String, replacement: String,
                issue: CleanupIssue = .other, note: String = "") {
        self.id = id
        self.paragraphID = paragraphID
        self.original = original
        self.replacement = replacement
        self.issue = issue
        self.note = note
    }
}

/// Clean Up Text: Claude reads the book a passage at a time and proposes small fixes for
/// import problems. Only the project changes; the imported file never does.
public enum CleanupPrompt {
    public static let system = """
    You proofread a manuscript that was imported from a scan, a PDF or an ebook, so an \
    audiobook narrator can read it aloud. Find only problems the import caused: scanning \
    (OCR) mistakes such as "tbe" for "the" or "rn" read as "m", words broken by a hyphen at a \
    line end ("con- tinue"), page numbers and running page headers or footers left in the \
    text, footnote markers glued to words ("night.12"), and letters spread apart ("T H E"). \
    Never change the author's wording, spelling, dialect, grammar, punctuation style or \
    names, and never rewrite a sentence. If a passage has no import problems, return no fixes. \
    Answer with JSON only.
    """

    public struct Chunk: Sendable {
        public var labels: [String: UUID]
        public var messages: [ChatMessage]
        public init(labels: [String: UUID], messages: [ChatMessage]) {
            self.labels = labels
            self.messages = messages
        }
    }

    public struct Reply: Codable, Hashable, Sendable {
        public var paragraph: String
        public var find: String
        public var replace: String
        public var issue: String?
        public var note: String?
    }

    /// The paragraphs Claude reads: body text and block quotes (headings are chapter titles).
    static func readable(_ p: Paragraph) -> Bool {
        (p.style == .body || p.style == .blockQuote) && !p.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// The whole book, or `chapters`, in passages of about `maxWords`.
    public static func chunks(project: Project, chapters: [Int]? = nil, maxWords: Int = 2500) -> [Chunk] {
        let picked = (chapters ?? Array(project.chapters.indices)).filter { project.chapters.indices.contains($0) }
        var out: [Chunk] = []
        var labels: [String: UUID] = [:]
        var passage = ""
        var words = 0
        func flush() {
            guard !labels.isEmpty else { return }
            let user = """
            Passage (each paragraph starts with its label):

            \(passage.trimmingCharacters(in: .whitespacesAndNewlines))

            For each problem, "find" is the exact text in that paragraph, copied character for \
            character, with a few words around the problem so it is found only once; "replace" \
            is the same text fixed. To remove a page number or header that fills a whole \
            paragraph, "find" is the whole paragraph and "replace" is "". issue is one of ocr, \
            hyphenation, page-number, header, footnote, spacing, other. note says what is wrong \
            in a few words.
            Reply as {"fixes":[{"paragraph":"P1","find":"…","replace":"…","issue":"ocr","note":"…"}]}, or {"fixes":[]}.
            """
            out.append(Chunk(labels: labels, messages: [ChatMessage(.system, system), ChatMessage(.user, user)]))
            labels = [:]
            passage = ""
            words = 0
        }
        var n = 0
        for c in picked {
            for p in project.chapters[c].paragraphs where readable(p) {
                n += 1
                labels["P\(n)"] = p.id
                passage += "[P\(n)] \(p.text)\n\n"
                words += p.wordCount
                if words >= maxWords { flush() }
            }
        }
        flush()
        return out
    }

    public static func parse(_ text: String) -> [Reply]? {
        struct Wrapper: Codable { var fixes: [Reply] }
        if let json = JSONText.extract(text), let whole = try? JSONDecoder().decode(Wrapper.self, from: Data(json.utf8)) {
            return whole.fixes
        }
        let partial = JSONText.objects(in: text).compactMap { try? JSONDecoder().decode(Reply.self, from: Data($0.utf8)) }
        return partial.isEmpty ? nil : partial
    }

    /// Fixes that can be trusted: the text is really in the cited paragraph, the change is
    /// small (a repair, not a rewrite), and it stays one paragraph.
    public static func fixes(from replies: [Reply], chunk: Chunk, project: Project, index: BookIndex) -> [CleanupFix] {
        var out: [CleanupFix] = []
        for r in replies {
            guard let pid = chunk.labels[r.paragraph], let p = project.paragraph(pid, index: index) else { continue }
            guard !r.find.isEmpty, r.find != r.replace, p.text.contains(r.find),
                  !r.replace.contains("\n"), r.find.count <= 600 else { continue }
            let issue = CleanupIssue(rawValue: (r.issue ?? "").lowercased()) ?? .other
            guard isRepair(r.find, r.replace, wholeParagraph: r.find == p.text) else { continue }
            guard !out.contains(where: { $0.paragraphID == pid && $0.original == r.find }) else { continue }
            out.append(CleanupFix(paragraphID: pid, original: r.find, replacement: r.replace, issue: issue, note: r.note ?? ""))
        }
        return out
    }

    /// A repair changes a few letters, joins or splits words, or removes stray numbers and
    /// short headers. A rewrite is refused.
    static func isRepair(_ find: String, _ replace: String, wholeParagraph: Bool) -> Bool {
        if replace.isEmpty {
            // Removing: a whole short paragraph (a page number or header), or a few characters.
            let words = Words.count(find)
            return wholeParagraph ? words <= 12 : find.count <= 40
        }
        let a = Array(find.lowercased().filter { !$0.isWhitespace })
        let b = Array(replace.lowercased().filter { !$0.isWhitespace })
        let allowed = max(3, min(a.count, b.count) / 5)
        return editDistance(a, b, limit: allowed) <= allowed
    }

    /// Levenshtein distance, giving up past `limit`.
    static func editDistance(_ a: [Character], _ b: [Character], limit: Int) -> Int {
        if abs(a.count - b.count) > limit { return limit + 1 }
        if a.isEmpty || b.isEmpty { return max(a.count, b.count) }
        var prev = Array(0...b.count)
        var cur = [Int](repeating: 0, count: b.count + 1)
        for i in 1...a.count {
            cur[0] = i
            var best = cur[0]
            for j in 1...b.count {
                cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
                best = min(best, cur[j])
            }
            if best > limit { return limit + 1 }
            swap(&prev, &cur)
        }
        return prev[b.count]
    }
}

public enum TextCleanup {
    /// The fix in its sentence, before and after, for the review list.
    public static func preview(_ fix: CleanupFix, in paragraph: String, around: Int = 40) -> (before: String, after: String) {
        let ns = paragraph as NSString
        let r = ns.range(of: fix.original)
        guard r.location != NSNotFound else { return (fix.original, fix.replacement) }
        let start = max(0, r.location - around)
        let end = min(ns.length, NSMaxRange(r) + around)
        let lead = (start > 0 ? "…" : "") + ns.substring(with: NSRange(location: start, length: r.location - start))
        let tail = ns.substring(with: NSRange(location: NSMaxRange(r), length: end - NSMaxRange(r))) + (end < ns.length ? "…" : "")
        return (lead + fix.original + tail, lead + fix.replacement + tail)
    }

    /// Applies the fixes to the project's text and returns the ids of those applied. Lines in
    /// edited paragraphs are found again, keeping each line's speaker, tone and delivery where
    /// its words match. A paragraph whose fixes would lose a line the narrator assigned keeps
    /// its old text (none of its fixes apply). A paragraph a fix empties (a page number) is
    /// removed, unless it is all its chapter has.
    @discardableResult
    public static func apply(_ fixes: [CleanupFix], to p: inout Project) -> [UUID] {
        let byParagraph = Dictionary(grouping: fixes, by: \.paragraphID)
        guard !byParagraph.isEmpty else { return [] }
        let finder = DialogueFinder(style: DialogueFinder.detectStyle(p.chapters.flatMap(\.paragraphs)))
        let linesByParagraph = Dictionary(grouping: p.lines, by: \.paragraphID)
        var applied: [UUID] = []
        var newLines: [UUID: [Line]] = [:]   // edited paragraph → its lines now
        var removed = Set<UUID>()

        for c in p.chapters.indices where p.chapters[c].paragraphs.contains(where: { byParagraph[$0.id] != nil }) {
            var open = false
            var removedHere = 0
            for i in p.chapters[c].paragraphs.indices {
                let old = p.chapters[c].paragraphs[i]
                guard let fs = byParagraph[old.id], old.style != .heading, old.style != .sceneBreak else {
                    open = finder.find(in: old, openFromPrevious: open).endsOpen
                    continue
                }
                var edited = old
                var done: [UUID] = []
                for f in fs {
                    let ns = edited.text as NSString
                    let r = ns.range(of: f.original)
                    guard r.location != NSNotFound else { continue }
                    edited.text = ns.replacingCharacters(in: r, with: f.replacement)
                    edited.italic = shift(edited.italic, replacing: r, with: (f.replacement as NSString).length)
                    done.append(f.id)
                }
                guard !done.isEmpty else {
                    open = finder.find(in: old, openFromPrevious: open).endsOpen
                    continue
                }
                let oldLines = linesByParagraph[old.id] ?? []
                if edited.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    // A page number or header on its own: the paragraph goes, unless it holds the
                    // narrator's line or is all the chapter has.
                    guard !oldLines.contains(where: { $0.source == .user }), p.chapters[c].paragraphs.count - removedHere > 1 else {
                        open = finder.find(in: old, openFromPrevious: open).endsOpen
                        continue
                    }
                    removed.insert(old.id)
                    removedHere += 1
                    applied += done
                    continue
                }
                let result = finder.find(in: edited, openFromPrevious: open)
                guard let lines = match(result.lines, in: edited, old: oldLines, oldText: old) else {
                    open = finder.find(in: old, openFromPrevious: open).endsOpen
                    continue
                }
                open = result.endsOpen
                p.chapters[c].paragraphs[i] = edited
                newLines[old.id] = lines
                applied += done
            }
        }
        guard !applied.isEmpty else { return [] }
        if !removed.isEmpty {
            for c in p.chapters.indices { p.chapters[c].paragraphs.removeAll { removed.contains($0.id) } }
            p.recorded.subtract(removed)
            p.retakes.removeAll { removed.contains($0.paragraphID) }
            for c in p.characters.indices {
                for f in p.characters[c].facts.indices where p.characters[c].facts[f].sourceParagraphID.map(removed.contains) == true {
                    p.characters[c].facts[f].sourceParagraphID = nil
                }
            }
            for i in p.pronunciations.indices where p.pronunciations[i].firstParagraphID.map(removed.contains) == true {
                p.pronunciations[i].firstParagraphID = nil
            }
        }
        // Lines: the edited paragraphs' new ones replace their old ones, in book order.
        var order: [UUID: Int] = [:]
        var g = 0
        for c in p.chapters { for para in c.paragraphs { order[para.id] = g; g += 1 } }
        var lines = p.lines.filter { newLines[$0.paragraphID] == nil && !removed.contains($0.paragraphID) }
        lines += newLines.values.flatMap { $0 }
        lines = lines.enumerated().sorted { a, b in
            let pa = order[a.element.paragraphID] ?? Int.max, pb = order[b.element.paragraphID] ?? Int.max
            if pa != pb { return pa < pb }
            if a.element.range.location != b.element.range.location { return a.element.range.location < b.element.range.location }
            return a.offset < b.offset
        }.map(\.element)
        p.lines = lines
        // New lines (a quote the scan had broken) get the rules' reading; settled ones keep theirs.
        RuleAttribution.apply(to: &p)
        ToneRules.apply(to: &p)
        return applied
    }

    /// Lines found in an edited paragraph, each keeping the old line with the same words (its
    /// id, speaker, tone and delivery). nil when a line the narrator assigned has no match.
    static func match(_ found: [DialogueFinder.Found], in paragraph: Paragraph, old: [Line], oldText: Paragraph) -> [Line]? {
        var used = Set<UUID>()
        var out: [Line] = []
        for f in found {
            var line = Line(paragraphID: paragraph.id, range: f.range, kind: f.kind, continuesPrevious: f.continuesPrevious)
            let words = paragraph.substring(f.range)
            var best: (Line, Double)? = nil
            for ol in old where !used.contains(ol.id) {
                let s = ol.range == f.range && oldText.substring(ol.range) == words ? 1 : TextMatch.similarity(oldText.substring(ol.range), words)
                if s >= 0.6, s > (best?.1 ?? 0) { best = (ol, s) }
            }
            if let (ol, _) = best {
                used.insert(ol.id)
                line = ol
                line.range = f.range
                line.continuesPrevious = f.continuesPrevious
            }
            out.append(line)
        }
        if old.contains(where: { $0.source == .user && !used.contains($0.id) }) { return nil }
        return out
    }

    /// Italic runs after `range` is replaced by text `length` long.
    static func shift(_ spans: [TextSpan], replacing range: NSRange, with length: Int) -> [TextSpan] {
        let delta = length - range.length
        let end = NSMaxRange(range)
        return spans.compactMap { s in
            if s.end <= range.location { return s }
            if s.location >= end { return TextSpan(location: s.location + delta, length: s.length) }
            let start = min(s.location, range.location)
            let newEnd = s.end > end ? s.end + delta : range.location + length
            return newEnd > start ? TextSpan(location: start, length: newEnd - start) : nil
        }
    }
}
