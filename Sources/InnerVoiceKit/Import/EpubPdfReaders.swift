import AppKit
import Foundation
import PDFKit

enum Simple {
    static func firstAttribute(in data: Data, element: String, attribute: String) -> String? {
        final class D: NSObject, XMLParserDelegate {
            let element: String, attribute: String
            var value: String?
            init(_ e: String, _ a: String) { element = e; attribute = a }
            func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                        attributes: [String: String] = [:]) {
                if value == nil, name.hasSuffix(element) { value = attributes[attribute] }
            }
        }
        let d = D(element, attribute)
        let p = XMLParser(data: data)
        p.delegate = d
        p.parse()
        return d.value
    }
}

// MARK: - PDF

/// Rebuilds paragraphs from a PDF's lines: drops running headers, footers and page
/// numbers, joins lines and pages, and undoes end-of-line hyphenation.
enum PDFReader {
    struct PDFLine {
        var text: String
        var rect: CGRect
        var page: Int
        var fontSize: CGFloat
        var italic: [TextSpan]
        var bold: Bool
    }

    static func read(_ url: URL) throws -> [RawBlock] {
        guard let document = PDFDocument(url: url) else {
            throw ImportError.unreadable(url.lastPathComponent, "The PDF is damaged or locked.")
        }
        if document.isLocked { throw ImportError.unreadable(url.lastPathComponent, "The PDF is locked with a password.") }
        var lines: [PDFLine] = []
        var pageHeights: [CGFloat] = []
        for i in 0..<document.pageCount {
            guard let page = document.page(at: i) else { continue }
            let bounds = page.bounds(for: .cropBox)
            pageHeights.append(bounds.height)
            guard let selection = page.selection(for: bounds) else { continue }
            for lineSel in selection.selectionsByLine() {
                guard let raw = lineSel.string else { continue }
                let text = raw.replacingOccurrences(of: "\u{00AD}", with: "").trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { continue }
                var size: CGFloat = 0
                var italic: [TextSpan] = []
                var bold = true
                if let attr = lineSel.attributedString {
                    let leading = (attr.string.prefix(while: { $0.isWhitespace }) as Substring).utf16.count
                    attr.enumerateAttribute(.font, in: NSRange(location: 0, length: attr.length)) { value, range, _ in
                        guard let font = value as? NSFont else { return }
                        size = max(size, font.pointSize)
                        let name = font.fontName.lowercased()
                        let traits = font.fontDescriptor.symbolicTraits
                        if !(traits.contains(.bold) || name.contains("bold") || name.contains("black") || name.contains("heavy")) { bold = false }
                        if traits.contains(.italic) || name.contains("italic") || name.contains("oblique") {
                            italic.append(TextSpan(location: max(0, range.location - leading), length: range.length))
                        }
                    }
                }
                lines.append(PDFLine(text: text, rect: lineSel.bounds(for: page), page: i, fontSize: size,
                                     italic: AttributedReader.mergeRanges(italic, limit: (text as NSString).length), bold: bold))
            }
        }
        let totalChars = lines.reduce(0) { $0 + $1.text.count }
        if totalChars < 200 {
            if document.pageCount > 0 { throw ImportError.scannedPDF(url.lastPathComponent) }
            throw ImportError.noText(url.lastPathComponent)
        }
        lines = joinSplitLines(lines)
        lines = dropRunningLines(lines, pageCount: document.pageCount, pageHeights: pageHeights)
        return paragraphs(from: lines)
    }

    /// A justified line squeezed out with wide letter spacing comes out of PDFKit as several
    /// pieces on one baseline, each with spaces between its letters ("c o n c e r n e d .").
    /// Each piece started a paragraph of its own (it sits mid-line, like an indent). Puts the
    /// letters back together, then joins pieces on the same baseline into one line.
    static func joinSplitLines(_ lines: [PDFLine]) -> [PDFLine] {
        var out: [PDFLine] = []
        for var line in lines {
            let closed = closeLetterSpacing(line.text)
            if closed != line.text {
                line.text = closed
                line.italic = [] // offsets no longer match; a spaced-out line is rarely italic
            }
            if var prev = out.last, prev.page == line.page {
                let overlap = min(prev.rect.maxY, line.rect.maxY) - max(prev.rect.minY, line.rect.minY)
                let sameBaseline = overlap > min(prev.rect.height, line.rect.height) * 0.6
                if sameBaseline, line.rect.minX >= prev.rect.maxX - 2 {
                    let offset = (prev.text as NSString).length + 1
                    prev.text += " " + line.text
                    prev.italic += line.italic.map { TextSpan(location: $0.location + offset, length: $0.length) }
                    prev.rect = prev.rect.union(line.rect)
                    prev.fontSize = max(prev.fontSize, line.fontSize)
                    prev.bold = prev.bold && line.bold
                    out[out.count - 1] = prev
                    continue
                }
            }
            out.append(line)
        }
        return out
    }

    /// "or c o n c e r n e d ." → "or concerned.": three or more single characters in a row,
    /// each followed by one space, are one spaced-out word.
    static func closeLetterSpacing(_ text: String) -> String {
        let tokens = text.split(separator: " ", omittingEmptySubsequences: false).map(String.init)
        guard tokens.count >= 3 else { return text }
        var result: [String] = []
        var run: [String] = []
        func flush() {
            if run.count >= 3 { result.append(run.joined()) } else { result += run }
            run = []
        }
        for t in tokens {
            if t.count == 1 { run.append(t) } else { flush(); result.append(t) }
        }
        flush()
        return result.joined(separator: " ")
    }

    /// Removes page numbers and lines that repeat near the top or bottom of many pages.
    static func dropRunningLines(_ lines: [PDFLine], pageCount: Int, pageHeights: [CGFloat]) -> [PDFLine] {
        func key(_ s: String) -> String {
            s.lowercased().filter { !$0.isNumber && !$0.isWhitespace }
        }
        func nearEdge(_ l: PDFLine) -> Bool {
            let h = l.page < pageHeights.count ? pageHeights[l.page] : 792
            return l.rect.minY < h * 0.09 || l.rect.maxY > h * 0.91
        }
        var counts: [String: Int] = [:]
        for l in lines where nearEdge(l) { counts[key(l.text), default: 0] += 1 }
        let threshold = max(3, pageCount / 4)
        return lines.filter { l in
            guard nearEdge(l) else { return true }
            let t = l.text.trimmingCharacters(in: .whitespaces)
            if t.range(of: "^(page\\s*)?[0-9ivxlcdm]+(\\s*(of|/)\\s*[0-9]+)?$", options: [.regularExpression, .caseInsensitive]) != nil { return false }
            let k = key(t)
            return k.isEmpty ? false : (counts[k] ?? 0) < threshold
        }
    }

    static func median(_ values: [CGFloat]) -> CGFloat {
        guard !values.isEmpty else { return 0 }
        let s = values.sorted()
        return s[s.count / 2]
    }

    /// The left edge of each page's text block. A printed book mirrors its margins (the text
    /// sits further right on one side of the spread), so one margin for the whole book reads
    /// every indent on the other side as a plain line and runs those paragraphs together.
    /// Indents and centered lines only sit to the right of the edge, so a low percentile of
    /// the page's line starts finds it; a page with few lines uses its side of the spread.
    static func pageLefts(_ lines: [PDFLine]) -> [Int: CGFloat] {
        func edge(_ xs: [CGFloat]) -> CGFloat { let s = xs.sorted(); return s[s.count / 10] }
        var byPage: [Int: [CGFloat]] = [:]
        var bySide: [Int: [CGFloat]] = [:]
        for l in lines {
            byPage[l.page, default: []].append(l.rect.minX)
            bySide[l.page % 2, default: []].append(l.rect.minX)
        }
        let side = bySide.mapValues(edge)
        return byPage.reduce(into: [:]) { result, entry in
            result[entry.key] = entry.value.count >= 4 ? edge(entry.value) : side[entry.key % 2] ?? edge(entry.value)
        }
    }

    static func paragraphs(from pdfLines: [PDFLine]) -> [RawBlock] {
        guard !pdfLines.isEmpty else { return [] }
        // Measure every line from its own page's left edge, so all pages share one text block.
        let lefts = pageLefts(pdfLines)
        let lines = pdfLines.map { l in
            var l = l
            l.rect.origin.x -= lefts[l.page] ?? 0
            return l
        }
        let bodySize = median(lines.map(\.fontSize).filter { $0 > 0 })
        let left = median(lines.map(\.rect.minX))
        let right = median(lines.map(\.rect.maxX))
        let width = max(1, right - left)
        var gaps: [CGFloat] = []
        for i in 1..<lines.count where lines[i].page == lines[i - 1].page {
            let g = lines[i - 1].rect.minY - lines[i].rect.minY
            if g > 0 { gaps.append(g) }
        }
        let lineGap = max(1, median(gaps))

        var blocks: [RawBlock] = []
        var text = ""
        var italic: [TextSpan] = []
        var level: Int?
        var centered = false
        func flush() {
            let t = text.trimmingCharacters(in: .whitespaces)
            if !t.isEmpty {
                blocks.append(RawBlock(text: t, italic: AttributedReader.mergeRanges(italic, limit: (t as NSString).length),
                                       headingLevel: level, isCentered: centered))
            }
            text = ""
            italic = []
            level = nil
            centered = false
        }
        for (i, line) in lines.enumerated() {
            let isHeadingSize = bodySize > 0 && line.fontSize >= bodySize * 1.25
            let lineCenter = line.rect.midX
            let isCentered = abs(lineCenter - (left + right) / 2) < width * 0.06 && line.rect.width < width * 0.7
            var startsNew = i == 0
            if i > 0 {
                let prev = lines[i - 1]
                let samePage = prev.page == line.page
                let gap = samePage ? prev.rect.minY - line.rect.minY : lineGap
                let indented = line.rect.minX > left + max(8, width * 0.025)
                let prevShort = prev.rect.maxX < right - width * 0.12
                let prevHeading = bodySize > 0 && prev.fontSize >= bodySize * 1.25
                startsNew = gap > lineGap * 1.45 || indented || (prevShort && samePage) || prevHeading != isHeadingSize
                    || (isCentered && !samePage)
                // A page that ends mid-sentence carries on.
                if !samePage, !indented, !prevShort, !isHeadingSize { startsNew = false }
                if !samePage, prevShort, let last = prev.text.last, !".!?”\"’:".contains(last) { startsNew = false }
            }
            if startsNew {
                flush()
                if isHeadingSize { level = line.fontSize >= bodySize * 1.6 ? 1 : 2 }
                else if line.bold && line.text.count < 60 { level = 2 }
                centered = isCentered
            }
            var piece = line.text
            if !text.isEmpty {
                if text.hasSuffix("-"), let first = piece.first, first.isLowercase {
                    text.removeLast() // hyphenated across lines
                } else if !text.hasSuffix("—") {
                    text += " "
                }
            }
            let offset = (text as NSString).length
            text += piece
            italic += line.italic.map { TextSpan(location: $0.location + offset, length: $0.length) }
            piece = ""
        }
        flush()
        return blocks
    }
}
