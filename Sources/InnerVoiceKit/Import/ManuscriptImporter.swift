import AppKit
import Foundation
import PDFKit

public enum ImportError: LocalizedError, Equatable {
    case unsupported(String)
    case unreadable(String, String)
    case noText(String)
    case scannedPDF(String)
    case pagesWithoutPreview(String)

    public var errorDescription: String? {
        switch self {
        case .unsupported(let name):
            "InnerVoice can't open “\(name)”. Save it as Word, PDF, RTF, plain text, Markdown, OpenDocument, EPUB or HTML, then try again."
        case .unreadable(let name, let reason):
            "Unable to read “\(name)”. \(reason)"
        case .noText(let name):
            "“\(name)” has no text in it."
        case .scannedPDF(let name):
            "“\(name)” has no text, only pictures of pages. Export it with text, or run it through a text recognition app first."
        case .pagesWithoutPreview(let name):
            "Unable to read “\(name)”. In Pages, choose File › Export To › Word, then open the Word file."
        }
    }
}

/// Reads a manuscript file into chapters and paragraphs.
public enum ManuscriptImporter {
    public static let fileExtensions = ["docx", "doc", "pdf", "rtf", "rtfd", "txt", "text", "md", "markdown",
                                        "odt", "pages", "epub", "html", "htm", "xhtml"]

    public static func read(_ url: URL) throws -> ImportedBook {
        let name = url.lastPathComponent
        let ext = url.pathExtension.lowercased()
        let title = url.deletingPathExtension().lastPathComponent
        let blocks: [RawBlock]
        var bookTitle = title
        var author = ""
        switch ext {
        case "txt", "text": blocks = try PlainTextReader.read(url, markdown: false)
        case "md", "markdown": blocks = try PlainTextReader.read(url, markdown: true)
        case "docx": blocks = try DocxReader.read(url)
        case "odt": blocks = try OdtReader.read(url)
        case "epub":
            let epub = try EpubReader.read(url)
            blocks = epub.blocks
            if !epub.title.isEmpty { bookTitle = epub.title }
            author = epub.author
        case "pdf": blocks = try PDFReader.read(url)
        case "pages": blocks = try PagesReader.read(url)
        case "rtf": blocks = try AttributedReader.read(url, type: .rtf)
        case "rtfd": blocks = try AttributedReader.read(url, type: .rtfd)
        case "doc": blocks = try AttributedReader.read(url, type: .docFormat)
        case "html", "htm", "xhtml": blocks = try AttributedReader.read(url, type: .html)
        default: throw ImportError.unsupported(name)
        }
        let chapters = ChapterDetector.chapters(from: blocks, fallbackTitle: bookTitle)
        guard !chapters.isEmpty else { throw ImportError.noText(name) }
        return ImportedBook(title: bookTitle, author: author, chapters: chapters)
    }
}

// MARK: - Plain text and Markdown

enum PlainTextReader {
    static func read(_ url: URL, markdown: Bool) throws -> [RawBlock] {
        var encoding = String.Encoding.utf8
        let text: String
        do {
            text = try String(contentsOf: url, usedEncoding: &encoding)
        } catch {
            guard let data = try? Data(contentsOf: url), let latin = String(data: data, encoding: .windowsCP1252) else {
                throw ImportError.unreadable(url.lastPathComponent, "The file isn't text.")
            }
            text = latin
        }
        return blocks(from: text, markdown: markdown)
    }

    /// Paragraphs are separated by blank lines when the file has them (lines inside are
    /// hard-wrapped and get joined); otherwise every line is a paragraph.
    static func blocks(from text: String, markdown: Bool) -> [RawBlock] {
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        let lines = normalized.components(separatedBy: "\n")
        let blankCount = lines.filter { $0.trimmingCharacters(in: .whitespaces).isEmpty }.count
        let usesBlankLines = blankCount > lines.count / 6
        var groups: [String] = []
        if usesBlankLines {
            var current: [String] = []
            for line in lines {
                let t = line.trimmingCharacters(in: .whitespaces)
                if t.isEmpty {
                    if !current.isEmpty { groups.append(current.joined(separator: " ")) }
                    current = []
                } else if markdown, t.hasPrefix("#") {
                    if !current.isEmpty { groups.append(current.joined(separator: " ")) }
                    groups.append(t)
                    current = []
                } else {
                    current.append(t)
                }
            }
            if !current.isEmpty { groups.append(current.joined(separator: " ")) }
        } else {
            groups = lines.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        }
        return groups.map { markdown ? markdownBlock($0) : RawBlock(text: $0) }
    }

    static func markdownBlock(_ raw: String) -> RawBlock {
        var text = raw
        var level: Int?
        if text.hasPrefix("#") {
            let hashes = text.prefix(while: { $0 == "#" }).count
            level = min(hashes, 2)
            text = String(text.dropFirst(hashes)).trimmingCharacters(in: .whitespaces)
        }
        var quote = false
        if text.hasPrefix(">") {
            quote = true
            text = String(text.dropFirst()).trimmingCharacters(in: .whitespaces)
        }
        let (plain, italic) = stripEmphasis(text)
        return RawBlock(text: plain, italic: italic, headingLevel: level, isBlockQuote: quote)
    }

    /// Removes *italic*, _italic_ and **bold** markers and returns the italic ranges.
    static func stripEmphasis(_ text: String) -> (String, [TextSpan]) {
        var out = ""
        var italic: [TextSpan] = []
        var italicStart: Int?
        let chars = Array(text)
        var i = 0
        while i < chars.count {
            let c = chars[i]
            if c == "*" || c == "_" {
                let doubled = i + 1 < chars.count && chars[i + 1] == c
                if doubled { i += 2; continue } // bold: drop the markers
                // An underscore inside a word is a character, not emphasis.
                let prevIsLetter = i > 0 && chars[i - 1].isLetter
                let nextIsLetter = i + 1 < chars.count && chars[i + 1].isLetter
                if c == "_" && prevIsLetter && nextIsLetter { out.append(c); i += 1; continue }
                let pos = (out as NSString).length
                if let start = italicStart {
                    if pos > start { italic.append(TextSpan(location: start, length: pos - start)) }
                    italicStart = nil
                } else if nextIsLetter || (i + 1 < chars.count && !chars[i + 1].isWhitespace) {
                    italicStart = pos
                } else {
                    out.append(c)
                }
                i += 1
                continue
            }
            out.append(c)
            i += 1
        }
        return (out, italic)
    }
}

// MARK: - RTF, DOC, HTML (and anything NSAttributedString reads)

enum AttributedReader {
    static func read(_ url: URL, type: NSAttributedString.DocumentType) throws -> [RawBlock] {
        let load = { () throws -> NSAttributedString in
            try NSAttributedString(url: url, options: [.documentType: type], documentAttributes: nil)
        }
        let attributed: NSAttributedString
        do {
            // HTML import uses WebKit, which must run on the main thread.
            if type == .html, !Thread.isMainThread {
                attributed = try DispatchQueue.main.sync { try load() }
            } else {
                attributed = try load()
            }
        } catch {
            throw ImportError.unreadable(url.lastPathComponent, error.localizedDescription)
        }
        return blocks(from: attributed)
    }

    static func blocks(from attributed: NSAttributedString) -> [RawBlock] {
        let string = attributed.string as NSString
        var ranges: [NSRange] = []
        string.enumerateSubstrings(in: NSRange(location: 0, length: string.length), options: .byParagraphs) { _, range, _, _ in
            ranges.append(range)
        }
        // The body size is the most common font size, weighted by text length.
        var sizeWeights: [CGFloat: Int] = [:]
        attributed.enumerateAttribute(.font, in: NSRange(location: 0, length: attributed.length)) { value, range, _ in
            if let font = value as? NSFont { sizeWeights[font.pointSize.rounded(), default: 0] += range.length }
        }
        let bodySize = sizeWeights.max { $0.value < $1.value }?.key ?? 12

        var blocks: [RawBlock] = []
        for range in ranges {
            let raw = string.substring(with: range)
            let trimmedLeading = raw.prefix(while: { $0 == " " || $0 == "\t" }).utf16.count
            let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            var italic: [TextSpan] = []
            var allBold = true
            var maxSize: CGFloat = 0
            attributed.enumerateAttribute(.font, in: range) { value, sub, _ in
                guard let font = value as? NSFont else { return }
                let traits = font.fontDescriptor.symbolicTraits
                maxSize = max(maxSize, font.pointSize)
                let segment = string.substring(with: sub).trimmingCharacters(in: .whitespaces)
                if !traits.contains(.bold), !segment.isEmpty { allBold = false }
                if traits.contains(.italic) {
                    let loc = sub.location - range.location - trimmedLeading
                    let r = TextSpan(location: max(0, loc), length: sub.length + min(0, loc))
                    if r.length > 0 { italic.append(r) }
                }
            }
            italic = mergeRanges(italic, limit: (text as NSString).length)
            let style = attributed.attribute(.paragraphStyle, at: range.location, effectiveRange: nil) as? NSParagraphStyle
            let centered = style?.alignment == .center
            let indented = (style?.headIndent ?? 0) >= 30 && (style?.tailIndent ?? 0) < 0
            var level: Int?
            if maxSize >= bodySize * 1.5 { level = 1 }
            else if maxSize >= bodySize * 1.15 || (allBold && text.count < 60) { level = 2 }
            blocks.append(RawBlock(text: text, italic: italic, headingLevel: level, isBlockQuote: indented, isCentered: centered))
        }
        return blocks
    }

    static func mergeRanges(_ ranges: [TextSpan], limit: Int) -> [TextSpan] {
        let sorted = ranges.map { r -> TextSpan in
            let end = min(r.end, limit)
            return TextSpan(location: min(r.location, limit), length: max(0, end - min(r.location, limit)))
        }.filter { $0.length > 0 }.sorted { $0.location < $1.location }
        var out: [TextSpan] = []
        for r in sorted {
            if let last = out.last, r.location <= last.end + 1 {
                out[out.count - 1].length = max(last.end, r.end) - last.location
            } else {
                out.append(r)
            }
        }
        return out
    }
}

// MARK: - Zip packages

enum Zip {
    /// Unpacks a zip into a fresh temporary folder with ditto.
    static func unpack(_ url: URL) throws -> URL {
        let dest = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-import-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        p.arguments = ["-x", "-k", url.path, dest.path]
        p.standardError = FileHandle.nullDevice
        try p.run()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else {
            throw ImportError.unreadable(url.lastPathComponent, "The file looks damaged.")
        }
        return dest
    }
}

// MARK: - Word (.docx)

/// Reads word/document.xml directly, so heading styles and italics survive.
enum DocxReader {
    static func read(_ url: URL) throws -> [RawBlock] {
        let folder = try Zip.unpack(url)
        defer { try? FileManager.default.removeItem(at: folder) }
        guard let data = try? Data(contentsOf: folder.appending(path: "word/document.xml")) else {
            throw ImportError.unreadable(url.lastPathComponent, "It isn't a Word document.")
        }
        let styles = (try? Data(contentsOf: folder.appending(path: "word/styles.xml"))).map(StyleNames.parse) ?? [:]
        let parser = DocxParser(styleNames: styles)
        let xml = XMLParser(data: data)
        xml.delegate = parser
        guard xml.parse() else { throw ImportError.unreadable(url.lastPathComponent, "The document is damaged.") }
        return parser.blocks
    }

    /// Maps style ids to names ("Heading1" → "heading 1"), since ids are localized.
    final class StyleNames: NSObject, XMLParserDelegate {
        var map: [String: String] = [:]
        private var currentID: String?
        static func parse(_ data: Data) -> [String: String] {
            let s = StyleNames()
            let p = XMLParser(data: data)
            p.delegate = s
            p.parse()
            return s.map
        }
        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                    attributes: [String: String] = [:]) {
            if name == "w:style" { currentID = attributes["w:styleId"] }
            if name == "w:name", let id = currentID, let value = attributes["w:val"] { map[id] = value.lowercased() }
        }
    }

    final class DocxParser: NSObject, XMLParserDelegate {
        var blocks: [RawBlock] = []
        let styleNames: [String: String]
        private var text = ""
        private var italic: [TextSpan] = []
        private var inParagraph = false
        private var inRunProps = false
        private var runItalic = false
        private var paragraphStyle = ""
        private var centered = false
        private var leftIndent = 0
        private var inText = false
        private var inDeleted = 0
        private var inFootnoteRef = false

        init(styleNames: [String: String]) { self.styleNames = styleNames }

        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                    attributes: [String: String] = [:]) {
            switch name {
            case "w:p":
                inParagraph = true
                text = ""
                italic = []
                paragraphStyle = ""
                centered = false
                leftIndent = 0
            case "w:pStyle": paragraphStyle = styleNames[attributes["w:val"] ?? ""] ?? (attributes["w:val"] ?? "").lowercased()
            case "w:jc": centered = attributes["w:val"] == "center"
            case "w:ind": leftIndent = Int(attributes["w:left"] ?? attributes["w:start"] ?? "0") ?? 0
            case "w:r": runItalic = false
            case "w:rPr": inRunProps = true
            case "w:i", "w:iCs":
                if inRunProps { runItalic = !["0", "false", "off"].contains(attributes["w:val"] ?? "") }
            case "w:rStyle":
                let style = (styleNames[attributes["w:val"] ?? ""] ?? attributes["w:val"] ?? "").lowercased()
                if style.contains("emphasis") || style.contains("italic") { runItalic = true }
            case "w:t": inText = true
            case "w:tab": if inParagraph && inDeleted == 0 { text += " " }
            case "w:br", "w:cr": if inParagraph && inDeleted == 0 { text += " " }
            case "w:del", "w:moveFrom": inDeleted += 1
            case "w:footnoteReference", "w:endnoteReference": inFootnoteRef = true
            default: break
            }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            guard inText, inParagraph, inDeleted == 0 else { return }
            let start = (text as NSString).length
            text += string
            if runItalic {
                let len = (string as NSString).length
                if let last = italic.last, last.end == start {
                    italic[italic.count - 1].length += len
                } else {
                    italic.append(TextSpan(location: start, length: len))
                }
            }
        }

        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            switch name {
            case "w:t": inText = false
            case "w:rPr": inRunProps = false
            case "w:del", "w:moveFrom": inDeleted -= 1
            case "w:p":
                inParagraph = false
                let leading = text.prefix(while: { $0.isWhitespace }).utf16.count
                let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !trimmed.isEmpty else { return }
                let shifted = italic.map { TextSpan(location: max(0, $0.location - leading), length: $0.length) }
                var level: Int?
                if paragraphStyle == "title" || paragraphStyle.hasPrefix("heading 1") || paragraphStyle == "heading1" { level = 1 }
                else if paragraphStyle.hasPrefix("heading") || paragraphStyle == "subtitle" { level = 2 }
                let quote = paragraphStyle.contains("quote") || leftIndent >= 1080
                blocks.append(RawBlock(text: trimmed, italic: AttributedReader.mergeRanges(shifted, limit: (trimmed as NSString).length),
                                       headingLevel: level, isBlockQuote: quote, isCentered: centered))
            default: break
            }
        }
    }
}

// MARK: - OpenDocument (.odt)

enum OdtReader {
    static func read(_ url: URL) throws -> [RawBlock] {
        let folder = try Zip.unpack(url)
        defer { try? FileManager.default.removeItem(at: folder) }
        guard let data = try? Data(contentsOf: folder.appending(path: "content.xml")) else {
            throw ImportError.unreadable(url.lastPathComponent, "It isn't an OpenDocument text file.")
        }
        let parser = OdtParser()
        let xml = XMLParser(data: data)
        xml.delegate = parser
        guard xml.parse() else { throw ImportError.unreadable(url.lastPathComponent, "The document is damaged.") }
        return parser.blocks
    }

    final class OdtParser: NSObject, XMLParserDelegate {
        var blocks: [RawBlock] = []
        private var italicStyles: Set<String> = []
        private var centeredStyles: Set<String> = []
        private var currentStyle: String?
        private var depth = 0
        private var text = ""
        private var italic: [TextSpan] = []
        private var spanItalic: [Bool] = []
        private var level: Int?
        private var centered = false
        private var inNote = 0

        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                    attributes: [String: String] = [:]) {
            switch name {
            case "style:style": currentStyle = attributes["style:name"]
            case "style:text-properties":
                if let s = currentStyle, attributes["fo:font-style"] == "italic" { italicStyles.insert(s) }
            case "style:paragraph-properties":
                if let s = currentStyle, attributes["fo:text-align"] == "center" { centeredStyles.insert(s) }
            case "text:p", "text:h":
                depth += 1
                if depth == 1 {
                    text = ""
                    italic = []
                    spanItalic = []
                    let style = attributes["text:style-name"] ?? ""
                    centered = centeredStyles.contains(style)
                    if name == "text:h" {
                        level = Int(attributes["text:outline-level"] ?? "1") == 1 ? 1 : 2
                    } else {
                        level = style.lowercased().contains("title") ? 1 : nil
                    }
                }
            case "text:span": spanItalic.append(italicStyles.contains(attributes["text:style-name"] ?? ""))
            case "text:s": append(String(repeating: " ", count: Int(attributes["text:c"] ?? "1") ?? 1))
            case "text:tab", "text:line-break": append(" ")
            case "text:note": inNote += 1
            default: break
            }
        }

        private func append(_ s: String) {
            guard depth > 0, inNote == 0 else { return }
            let start = (text as NSString).length
            text += s
            if spanItalic.last == true {
                italic.append(TextSpan(location: start, length: (s as NSString).length))
            }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) { append(string) }

        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            switch name {
            case "style:style": currentStyle = nil
            case "text:span": _ = spanItalic.popLast()
            case "text:note": inNote -= 1
            case "text:p", "text:h":
                depth -= 1
                guard depth == 0 else { return }
                let leading = text.prefix(while: { $0.isWhitespace }).utf16.count
                let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !trimmed.isEmpty else { return }
                let shifted = italic.map { TextSpan(location: max(0, $0.location - leading), length: $0.length) }
                blocks.append(RawBlock(text: trimmed, italic: AttributedReader.mergeRanges(shifted, limit: (trimmed as NSString).length),
                                       headingLevel: level, isCentered: centered))
            default: break
            }
        }
    }
}

// MARK: - Pages

enum PagesReader {
    /// Modern Pages files keep their text in a private format. When the document was saved
    /// with a preview, that PDF has the full text.
    static func read(_ url: URL) throws -> [RawBlock] {
        var isDir: ObjCBool = false
        FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir)
        var folder = url
        var cleanup: URL?
        if !isDir.boolValue {
            folder = try Zip.unpack(url)
            cleanup = folder
        }
        defer { if let cleanup { try? FileManager.default.removeItem(at: cleanup) } }
        for candidate in ["QuickLook/Preview.pdf", "preview.pdf"] {
            let pdf = folder.appending(path: candidate)
            if FileManager.default.fileExists(atPath: pdf.path), let blocks = try? PDFReader.read(pdf), blocks.count > 3 {
                return blocks
            }
        }
        throw ImportError.pagesWithoutPreview(url.lastPathComponent)
    }
}
