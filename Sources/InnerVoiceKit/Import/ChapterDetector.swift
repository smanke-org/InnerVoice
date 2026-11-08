import Foundation

/// One block of text as a reader found it, before chapters are formed.
public struct RawBlock: Hashable, Sendable {
    public var text: String
    public var italic: [TextSpan]
    /// 1 = title or chapter heading, 2 = section; nil for body text.
    public var headingLevel: Int?
    public var isBlockQuote: Bool
    public var isCentered: Bool

    public init(text: String, italic: [TextSpan] = [], headingLevel: Int? = nil,
                isBlockQuote: Bool = false, isCentered: Bool = false) {
        self.text = text
        self.italic = italic
        self.headingLevel = headingLevel
        self.isBlockQuote = isBlockQuote
        self.isCentered = isCentered
    }
}

/// A book as read from a manuscript file.
public struct ImportedBook: Sendable {
    public var title: String
    public var author: String
    public var chapters: [Chapter]

    public init(title: String, author: String = "", chapters: [Chapter]) {
        self.title = title
        self.author = author
        self.chapters = chapters
    }

    public var paragraphCount: Int { chapters.reduce(0) { $0 + $1.paragraphs.count } }
}

/// Splits a run of blocks into chapters at headings.
public enum ChapterDetector {
    private static let numberWords = "one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred"
    private static let headingPattern: NSRegularExpression = {
        let n = "(?:\\d+|[ivxlcdm]+|(?:\(numberWords))(?:[- ](?:\(numberWords)))*)"
        let pattern = "^(?:(?:chapter|ch\\.|book|part|act|volume)\\s+\(n)\\b.*|(?:prologue|epilogue|interlude|introduction|preface|foreword|afterword|acknowledg(?:e)?ments|author'?s note|author’s note|dedication|contents|table of contents|about the author)\\b.*|\(n)\\.?)$"
        return try! NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }()

    /// True for "Chapter 12", "CHAPTER TWELVE", "Prologue", "Part II", "7".
    public static func looksLikeHeading(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty, t.count <= 80 else { return false }
        // A heading never ends like a sentence of dialogue.
        if t.hasSuffix("”") || t.hasSuffix("\"") { return false }
        return headingPattern.firstMatch(in: t, range: NSRange(location: 0, length: (t as NSString).length)) != nil
    }

    public static func isSceneBreak(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty, t.count <= 12 else { return false }
        return t.unicodeScalars.allSatisfy { "*#~•·-—–_=+◆◇❖⁂ ".unicodeScalars.contains($0) }
    }

    /// Builds chapters. Explicit level-1 headings win; otherwise heading-like lines split.
    /// Text before the first heading becomes "Opening" (or the title page is dropped when tiny).
    public static func chapters(from blocks: [RawBlock], fallbackTitle: String) -> [Chapter] {
        let clean = blocks.map { b -> RawBlock in
            var b = b
            b.text = b.text.trimmingCharacters(in: .whitespacesAndNewlines)
            return b
        }.filter { !$0.text.isEmpty }
        let explicit = clean.filter { $0.headingLevel == 1 }.count
        func isHeading(_ b: RawBlock) -> Bool {
            if b.headingLevel == 1 { return true }
            if b.text.count > 80 { return false }
            if explicit >= 3 { return looksLikeHeading(b.text) && (b.headingLevel != nil || b.isCentered) }
            return looksLikeHeading(b.text) || (b.headingLevel == 2 && explicit == 0 && b.text.count < 60)
        }

        var chapters: [Chapter] = []
        var current = Chapter(title: "Opening", paragraphs: [])
        var pendingSubtitle = false
        for block in clean {
            if isHeading(block) {
                if current.paragraphs.isEmpty, pendingSubtitle {
                    // Two headings in a row ("Part One", then "Chapter 1") make one title.
                    current.title += " · " + titleCase(block.text)
                } else {
                    if !current.paragraphs.isEmpty { chapters.append(current) }
                    current = Chapter(title: titleCase(block.text), paragraphs: [])
                }
                pendingSubtitle = true
                continue
            }
            // "Chapter 3" followed by a short heading "The Storm" reads as one title.
            if pendingSubtitle, current.paragraphs.isEmpty, block.headingLevel != nil, block.text.count < 60,
               !block.text.contains("“"), !block.text.contains("\"") {
                current.title += ": " + block.text
                pendingSubtitle = false
                continue
            }
            pendingSubtitle = false
            let style: Paragraph.Style = isSceneBreak(block.text) ? .sceneBreak : (block.isBlockQuote ? .blockQuote : .body)
            current.paragraphs.append(Paragraph(text: block.text, style: style, italic: block.italic))
        }
        if !current.paragraphs.isEmpty { chapters.append(current) }
        // A title page (a few short lines before Chapter 1) is not worth a chapter of its own.
        if chapters.count > 1, chapters[0].title == "Opening", chapters[0].wordCount < 40,
           chapters[0].paragraphs.allSatisfy({ !$0.text.contains("“") && !$0.text.contains("\"") }) {
            chapters.removeFirst()
        }
        if chapters.isEmpty { return [] }
        if chapters.count == 1, chapters[0].title == "Opening" { chapters[0].title = fallbackTitle }
        return chapters
    }

    /// "CHAPTER ONE" reads better as "Chapter One"; mixed case is kept.
    static func titleCase(_ s: String) -> String {
        let letters = s.filter(\.isLetter)
        guard !letters.isEmpty, letters == letters.uppercased() else { return s }
        return s.lowercased().split(separator: " ", omittingEmptySubsequences: false).map { word -> String in
            let w = String(word)
            if ["i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii"].contains(w) { return w.uppercased() }
            return w.prefix(1).uppercased() + w.dropFirst()
        }.joined(separator: " ")
    }
}
