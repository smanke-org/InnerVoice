import Foundation

/// Finds the lines in a book with plain code, before any AI: quoted speech in every common
/// style, dash dialogue, italic thoughts, and block-quoted written text.
public struct DialogueFinder: Sendable {
    public enum QuoteStyle: String, Sendable {
        /// “ ” or " " around speech, ‘ ’ inside.
        case double
        /// ‘ ’ around speech (common in UK books), “ ” inside.
        case single
        /// « » around speech.
        case guillemets
    }

    public struct Found: Hashable, Sendable {
        public var range: TextRange
        public var kind: LineKind
        public var continuesPrevious: Bool
    }

    public var style: QuoteStyle

    public init(style: QuoteStyle) { self.style = style }

    /// Picks the quote style from how quotes open across the book.
    public static func detectStyle(_ paragraphs: [Paragraph]) -> QuoteStyle {
        var double = 0, single = 0, guillemets = 0
        for p in paragraphs.prefix(4000) {
            let scalars = Array(p.text.unicodeScalars)
            for (i, s) in scalars.enumerated() {
                let atStart = i == 0 || CharacterSet.whitespacesAndNewlines.contains(scalars[i - 1]) || "—–-([".unicodeScalars.contains(scalars[i - 1])
                switch s {
                case "“", "„": double += 1
                case "\"": if atStart { double += 1 }
                case "‘": if atStart { single += 1 }
                case "'": if atStart, i + 1 < scalars.count, CharacterSet.uppercaseLetters.contains(scalars[i + 1]) { single += 1 }
                case "«": guillemets += 1
                default: break
                }
            }
        }
        if guillemets > double && guillemets > single { return .guillemets }
        if single > double * 2 && single > 10 { return .single }
        return .double
    }

    /// Finds the lines in one paragraph. `openFromPrevious` is true when the previous
    /// paragraph's quote never closed (a speech that runs across paragraphs).
    /// Returns the lines and whether this paragraph also ends inside an open quote.
    public func find(in paragraph: Paragraph, openFromPrevious: Bool) -> (lines: [Found], endsOpen: Bool) {
        guard paragraph.style != .heading, paragraph.style != .sceneBreak else { return ([], false) }
        let text = paragraph.text as NSString
        let length = text.length
        guard length > 0 else { return ([], false) }

        // Dash dialogue: — Speech, — he said. Speech continues after a second dash.
        let first = text.character(at: 0)
        if first == 0x2014 || first == 0x2015 || (first == 0x2013 && length > 1 && text.character(at: 1) == 0x20) {
            var end = length
            let search = text.range(of: " —", options: [], range: NSRange(location: 1, length: length - 1))
            if search.location != NSNotFound { end = search.location }
            let start = 1 + (text.substring(with: NSRange(location: 1, length: min(2, length - 1))).hasPrefix(" ") ? 1 : 0)
            if end > start {
                return ([Found(range: TextRange(location: start, length: end - start), kind: .spoken, continuesPrevious: false)], false)
            }
        }

        var found: [Found] = []
        var openAt: Int? = nil
        var continues = false
        // A paragraph that starts with an opening mark after an unclosed one continues it.
        let chars = (0..<length).map { text.character(at: $0) }
        var i = 0
        if openFromPrevious, let c = chars.first, isOpen(c, at: 0, chars) {
            continues = true
        }
        while i < length {
            let c = chars[i]
            if let start = openAt {
                if isClose(c, at: i, chars) {
                    if i > start + 1 || i > start {
                        found.append(Found(range: TextRange(location: start + 1, length: i - start - 1), kind: .spoken,
                                           continuesPrevious: continues && found.isEmpty))
                    }
                    openAt = nil
                }
            } else if isOpen(c, at: i, chars) {
                openAt = i
            }
            i += 1
        }
        var endsOpen = false
        if let start = openAt, start + 1 < length {
            found.append(Found(range: TextRange(location: start + 1, length: length - start - 1), kind: .spoken,
                               continuesPrevious: continues && found.isEmpty))
            endsOpen = true
        }
        found = found.map { trimmed($0, text) }.filter { f in
            // "the word ‘yes’" style mentions are too short to be a line unless they end like speech.
            let s = text.substring(with: f.range.ns)
            return Words.count(s) >= 2 || s.hasSuffix("!") || s.hasSuffix("?") || s.hasSuffix(".") || s.hasSuffix(",") || s.hasSuffix("…")
        }

        if found.isEmpty, paragraph.style == .blockQuote {
            return ([Found(range: TextRange(location: 0, length: length), kind: .written, continuesPrevious: false)], false)
        }
        // Italic runs outside quotes, three words or more, read as thoughts.
        for run in paragraph.italic {
            let overlaps = found.contains { NSIntersectionRange($0.range.ns, run.ns).length > 0 }
            guard !overlaps, run.end <= length else { continue }
            let s = text.substring(with: run.ns)
            guard Words.count(s) >= 3 else { continue }
            found.append(trimmed(Found(range: run, kind: .thought, continuesPrevious: false), text))
        }
        found.sort { $0.range.location < $1.range.location }
        return (found, endsOpen)
    }

    private func trimmed(_ f: Found, _ text: NSString) -> Found {
        var r = f.range.ns
        while r.length > 0, let s = UnicodeScalar(text.character(at: r.location)), CharacterSet.whitespacesAndNewlines.contains(s) {
            r.location += 1
            r.length -= 1
        }
        while r.length > 0, let s = UnicodeScalar(text.character(at: r.location + r.length - 1)), CharacterSet.whitespacesAndNewlines.contains(s) {
            r.length -= 1
        }
        var out = f
        out.range = TextRange(r)
        return out
    }

    private func isLetter(_ c: unichar) -> Bool {
        guard let s = UnicodeScalar(c) else { return false }
        return CharacterSet.letters.contains(s) || CharacterSet.decimalDigits.contains(s)
    }

    private func isSpaceOrStart(_ i: Int, _ chars: [unichar]) -> Bool {
        guard i > 0 else { return true }
        let p = chars[i - 1]
        guard let s = UnicodeScalar(p) else { return true }
        return CharacterSet.whitespacesAndNewlines.contains(s) || "—–-([{/".unicodeScalars.contains(s)
    }

    private func isOpen(_ c: unichar, at i: Int, _ chars: [unichar]) -> Bool {
        switch style {
        case .double:
            if c == 0x201C || c == 0x201E { return true } // “ „
            if c == 0x22 { return isSpaceOrStart(i, chars) } // "
            return false
        case .single:
            if c == 0x2018 { return isSpaceOrStart(i, chars) } // ‘
            if c == 0x27 { return isSpaceOrStart(i, chars) && i + 1 < chars.count && isLetter(chars[i + 1]) }
            return false
        case .guillemets:
            return c == 0x00AB // «
        }
    }

    private func isClose(_ c: unichar, at i: Int, _ chars: [unichar]) -> Bool {
        switch style {
        case .double:
            if c == 0x201D { return true } // ”
            if c == 0x22 { return !isSpaceOrStart(i, chars) || i == chars.count - 1 }
            if c == 0x201C, i > 0, !isSpaceOrStart(i, chars) { return true } // a wrongly curled closing mark
            return false
        case .single:
            guard c == 0x2019 || c == 0x27 else { return false }
            // don’t, it’s: an apostrophe between letters is not a closing mark.
            let before = i > 0 && isLetter(chars[i - 1])
            let after = i + 1 < chars.count && isLetter(chars[i + 1])
            if before && after { return false }
            return !isSpaceOrStart(i, chars)
        case .guillemets:
            return c == 0x00BB // »
        }
    }

    /// Finds every line in a book, carrying open quotes across paragraphs.
    public static func findAll(in chapters: [Chapter]) -> [Line] {
        let all = chapters.flatMap(\.paragraphs)
        let finder = DialogueFinder(style: detectStyle(all))
        var lines: [Line] = []
        for chapter in chapters {
            var open = false
            for p in chapter.paragraphs {
                let result = finder.find(in: p, openFromPrevious: open)
                open = result.endsOpen
                lines += result.lines.map {
                    Line(paragraphID: p.id, range: $0.range, kind: $0.kind, continuesPrevious: $0.continuesPrevious)
                }
            }
        }
        // Long runs of italic paragraphs are a stylistic section (a dream, a letter, a flashback), not thoughts.
        var runStart: Int?
        var drop = Set<UUID>()
        let paragraphs = chapters.flatMap(\.paragraphs)
        for (i, p) in paragraphs.enumerated() {
            let wholeItalic = p.italic.count == 1 && p.italic[0].length >= (p.text as NSString).length - 2 && p.text.count > 20
            if wholeItalic {
                if runStart == nil { runStart = i }
            } else {
                if let s = runStart, i - s > 3 { paragraphs[s..<i].forEach { drop.insert($0.id) } }
                runStart = nil
            }
        }
        if let s = runStart, paragraphs.count - s > 3 { paragraphs[s...].forEach { drop.insert($0.id) } }
        return lines.filter { !($0.kind == .thought && drop.contains($0.paragraphID)) }
    }
}
