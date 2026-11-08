import AppKit
import Foundation
import PDFKit

// MARK: - EPUB

enum EpubReader {
    struct Result {
        var blocks: [RawBlock]
        var title: String
        var author: String
    }

    static func read(_ url: URL) throws -> Result {
        let folder = try Zip.unpack(url)
        defer { try? FileManager.default.removeItem(at: folder) }
        let name = url.lastPathComponent
        guard let container = try? Data(contentsOf: folder.appending(path: "META-INF/container.xml")),
              let opfPath = Simple.firstAttribute(in: container, element: "rootfile", attribute: "full-path"),
              let opfData = try? Data(contentsOf: folder.appending(path: opfPath)) else {
            throw ImportError.unreadable(name, "It isn't an EPUB book.")
        }
        let opf = OPF.parse(opfData)
        let base = folder.appending(path: opfPath).deletingLastPathComponent()
        var blocks: [RawBlock] = []
        for idref in opf.spine {
            guard let href = opf.manifest[idref]?.removingPercentEncoding else { continue }
            let file = base.appending(path: href)
            guard let data = try? Data(contentsOf: file) else { continue }
            var fileBlocks = XHTMLBlocks.parse(data)
            if fileBlocks == nil, let attributed = try? NSAttributedString(
                data: data, options: [.documentType: NSAttributedString.DocumentType.html], documentAttributes: nil) {
                fileBlocks = AttributedReader.blocks(from: attributed)
            }
            guard var chunk = fileBlocks, !chunk.isEmpty else { continue }
            // Each spine file usually starts a chapter; a short first line becomes its heading.
            if chunk[0].headingLevel == nil, chunk[0].text.count < 60, chunk.count > 2 {
                chunk[0].headingLevel = 1
            }
            if let i = chunk.firstIndex(where: { $0.headingLevel != nil }), i == 0 { chunk[0].headingLevel = 1 }
            blocks += chunk
        }
        guard !blocks.isEmpty else { throw ImportError.noText(name) }
        return Result(blocks: blocks, title: opf.title, author: opf.author)
    }

    struct OPF {
        var manifest: [String: String] = [:]
        var spine: [String] = []
        var title = ""
        var author = ""

        static func parse(_ data: Data) -> OPF {
            let d = Delegate()
            let p = XMLParser(data: data)
            p.shouldProcessNamespaces = false
            p.delegate = d
            p.parse()
            return d.opf
        }

        final class Delegate: NSObject, XMLParserDelegate {
            var opf = OPF()
            private var field: String?
            private var buffer = ""
            func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                        attributes: [String: String] = [:]) {
                let local = name.split(separator: ":").last.map(String.init) ?? name
                switch local {
                case "item":
                    if let id = attributes["id"], let href = attributes["href"],
                       (attributes["media-type"] ?? "").contains("html") {
                        opf.manifest[id] = href
                    }
                case "itemref": if let id = attributes["idref"], attributes["linear"] != "no" { opf.spine.append(id) }
                case "title", "creator":
                    field = local
                    buffer = ""
                default: break
                }
            }
            func parser(_ parser: XMLParser, foundCharacters string: String) { if field != nil { buffer += string } }
            func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
                let local = name.split(separator: ":").last.map(String.init) ?? name
                guard local == field else { return }
                let value = buffer.trimmingCharacters(in: .whitespacesAndNewlines)
                if local == "title", opf.title.isEmpty { opf.title = value }
                if local == "creator", opf.author.isEmpty { opf.author = value }
                field = nil
            }
        }
    }
}

/// Block-level reading of well-formed XHTML: p, h1–h6, blockquote, li, and leaf divs.
enum XHTMLBlocks {
    private static let entities: [String: String] = [
        "&nbsp;": "\u{00A0}", "&mdash;": "—", "&ndash;": "–", "&hellip;": "…", "&rsquo;": "’", "&lsquo;": "‘",
        "&rdquo;": "”", "&ldquo;": "“", "&eacute;": "é", "&egrave;": "è", "&copy;": "©", "&shy;": "",
        "&thinsp;": "\u{2009}", "&emsp;": " ", "&ensp;": " ", "&laquo;": "«", "&raquo;": "»", "&bull;": "•",
    ]

    static func parse(_ data: Data) -> [RawBlock]? {
        guard var text = String(data: data, encoding: .utf8) else { return nil }
        for (k, v) in entities { text = text.replacingOccurrences(of: k, with: v) }
        // Drop the DOCTYPE; XMLParser would try to load the DTD.
        if let r = text.range(of: "<!DOCTYPE[^>]*>", options: .regularExpression) { text.removeSubrange(r) }
        let d = Delegate()
        let p = XMLParser(data: Data(text.utf8))
        p.delegate = d
        guard p.parse() else { return nil }
        d.flush()
        return d.blocks
    }

    final class Delegate: NSObject, XMLParserDelegate {
        var blocks: [RawBlock] = []
        private var text = ""
        private var italic: [TextRange] = []
        private var italicDepth = 0
        private var italicStart = 0
        private var level: Int?
        private var quoteDepth = 0
        private var skipDepth = 0
        private var italicStack: [Bool] = []
        private static let blockTags: Set<String> = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "div", "blockquote", "br", "tr"]

        func flush() {
            let leading = text.prefix(while: { $0.isWhitespace }).utf16.count
            let trimmed = text.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                let shifted = italic.map { TextRange(location: max(0, $0.location - leading), length: $0.length) }
                blocks.append(RawBlock(text: trimmed, italic: AttributedReader.mergeRanges(shifted, limit: (trimmed as NSString).length),
                                       headingLevel: level, isBlockQuote: quoteDepth > 0))
            }
            text = ""
            italic = []
            level = nil
            if italicDepth > 0 { italicStart = 0 }
        }

        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                    attributes: [String: String] = [:]) {
            let tag = name.lowercased()
            if ["head", "script", "style", "aside", "nav"].contains(tag) || attributes["epub:type"] == "footnote" { skipDepth += 1; return }
            if Self.blockTags.contains(tag) {
                flush()
                if tag.hasPrefix("h"), let n = Int(tag.dropFirst()) { level = n <= 2 ? 1 : 2 }
                if tag == "blockquote" { quoteDepth += 1 }
                let cls = (attributes["class"] ?? "").lowercased()
                if cls.contains("chapter") && (cls.contains("title") || cls.contains("head")) { level = 1 }
            }
            let opensItalic = ["i", "em", "cite"].contains(tag) || (attributes["class"] ?? "").lowercased().contains("italic")
            italicStack.append(opensItalic)
            if opensItalic {
                if italicDepth == 0 { italicStart = (text as NSString).length }
                italicDepth += 1
            }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            guard skipDepth == 0 else { return }
            text += string
        }

        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            let tag = name.lowercased()
            if ["head", "script", "style", "aside", "nav"].contains(tag) { skipDepth = max(0, skipDepth - 1); return }
            if italicStack.popLast() == true, italicDepth > 0 {
                italicDepth -= 1
                if italicDepth == 0 {
                    let end = (text as NSString).length
                    if end > italicStart { italic.append(TextRange(location: italicStart, length: end - italicStart)) }
                }
            }
            if Self.blockTags.contains(tag) {
                flush()
                if tag == "blockquote" { quoteDepth = max(0, quoteDepth - 1) }
            }
        }
    }
}

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
        var italic: [TextRange]
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
                var italic: [TextRange] = []
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
                            italic.append(TextRange(location: max(0, range.location - leading), length: range.length))
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
        lines = dropRunningLines(lines, pageCount: document.pageCount, pageHeights: pageHeights)
        return paragraphs(from: lines)
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

    static func paragraphs(from lines: [PDFLine]) -> [RawBlock] {
        guard !lines.isEmpty else { return [] }
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
        var italic: [TextRange] = []
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
            italic += line.italic.map { TextRange(location: $0.location + offset, length: $0.length) }
            piece = ""
        }
        flush()
        return blocks
    }
}
