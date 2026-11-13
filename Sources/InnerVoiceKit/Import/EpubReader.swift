import AppKit
import Foundation

// MARK: - EPUB

/// Reads an EPUB 2 or 3 book. Chapters come from the book's own table of contents (the
/// EPUB 3 nav document or the EPUB 2 toc.ncx), fragment anchors included, so a book kept
/// in one big file still splits where its contents say. Without a usable table of
/// contents the blocks go through `ChapterDetector` as before. Footnotes, endnotes and the
/// note markers in the text are left out: they aren't read aloud.
enum EpubReader {
    struct Result {
        var blocks: [RawBlock]
        var title: String
        var author: String
        /// Chapters from the table of contents; nil when it has none worth using.
        var chapters: [Chapter]?
    }

    /// One spine file as read.
    struct Document {
        var path: String
        var blocks: [RawBlock]
        /// Element id → index of the block it starts (or sits in).
        var anchors: [String: Int]
        /// epub:type (and doc- role) words on the html, body and opening section.
        var types: Set<String>
    }

    /// A table of contents entry, in contents order.
    struct TOCEntry: Equatable {
        var title: String
        var path: String
        var fragment: String?
        var parent: Int?
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
        let opfURL = folder.appending(path: opfPath).standardizedFileURL
        let base = opfURL.deletingLastPathComponent()
        func path(_ href: String) -> String {
            URL(fileURLWithPath: href.removingPercentEncoding ?? href, relativeTo: base).standardizedFileURL.path
        }

        var documents: [Document] = []
        var blocks: [RawBlock] = []
        for idref in opf.spine {
            guard let item = opf.manifest[idref], item.mediaType.contains("html") else { continue }
            let file = URL(fileURLWithPath: path(item.href))
            guard let data = try? Data(contentsOf: file) else { continue }
            var doc = XHTMLBlocks.document(data).map { Document(path: file.path, blocks: $0.blocks, anchors: $0.anchors, types: $0.types) }
            if doc == nil, let attributed = try? NSAttributedString(
                data: data, options: [.documentType: NSAttributedString.DocumentType.html], documentAttributes: nil) {
                doc = Document(path: file.path, blocks: AttributedReader.blocks(from: attributed), anchors: [:], types: [])
            }
            guard let doc else { continue }
            documents.append(doc)
            var chunk = doc.blocks
            guard !chunk.isEmpty else { continue }
            // Each spine file usually starts a chapter; a short first line becomes its heading.
            if chunk[0].headingLevel == nil, chunk[0].text.count < 60, chunk.count > 2 {
                chunk[0].headingLevel = 1
            }
            if let i = chunk.firstIndex(where: { $0.headingLevel != nil }), i == 0 { chunk[0].headingLevel = 1 }
            blocks += chunk
        }
        guard !blocks.isEmpty else { throw ImportError.noText(name) }

        // The table of contents: EPUB 3's nav document first, then EPUB 2's NCX.
        var toc: [TOCEntry] = []
        var skipped: Set<String> = Set(opf.guide.filter { Self.unread.contains($0.type) }.map { path($0.href.components(separatedBy: "#")[0]) })
        if let nav = opf.manifest.values.first(where: { $0.properties.contains("nav") }),
           let data = try? Data(contentsOf: URL(fileURLWithPath: path(nav.href))) {
            let parsed = NavReader.parse(data, at: URL(fileURLWithPath: path(nav.href)))
            toc = parsed.toc
            skipped.formUnion(parsed.landmarks.filter { Self.unread.contains($0.type) }.map(\.path))
            skipped.insert(path(nav.href))
        }
        if toc.count < 2, let ncx = (opf.tocID.flatMap { opf.manifest[$0] } ?? opf.manifest.values.first { $0.mediaType.contains("dtbncx") }),
           let data = try? Data(contentsOf: URL(fileURLWithPath: path(ncx.href))) {
            let entries = NCXReader.parse(data, at: URL(fileURLWithPath: path(ncx.href)))
            if entries.count >= 2 { toc = entries }
        }
        let fromTOC = chaptersFromTOC(documents: documents, toc: toc, skipped: skipped)
        return Result(blocks: blocks, title: opf.title, author: opf.author, chapters: fromTOC)
    }

    /// Landmark and guide types that are never read aloud.
    static let unread: Set<String> = ["toc", "copyright-page", "copyright", "loi", "lot"]
    /// Contents titles left out: the contents page itself, the copyright page, the notes.
    static let unreadTitles: Set<String> = ["contents", "table of contents", "copyright", "copyright page",
                                            "notes", "endnotes", "footnotes"]
    /// Whole files left out by their epub:type.
    static let unreadTypes: Set<String> = ["toc", "copyright-page", "footnotes", "endnotes", "rearnotes",
                                           "doc-toc", "doc-endnotes", "landmarks"]

    /// Builds chapters from the table of contents, or nil when it isn't usable (fewer than
    /// two chapter starts found in the text).
    static func chaptersFromTOC(documents: [Document], toc: [TOCEntry], skipped: Set<String>) -> [Chapter]? {
        let fileIndex = Dictionary(documents.enumerated().map { ($1.path, $0) }, uniquingKeysWith: { a, _ in a })
        // An entry starts a chapter unless it points inside a file one of its parents already
        // starts (a section of that chapter, not a chapter of its own).
        struct Start { var file: Int; var block: Int; var title: String }
        var starts: [Start] = []
        for entry in toc {
            guard let f = fileIndex[entry.path] else { continue }
            var parent = entry.parent
            var inside = false
            while let p = parent {
                if toc[p].path == entry.path { inside = entry.fragment != nil }
                parent = toc[p].parent
            }
            if inside { continue }
            var block = 0
            if let fragment = entry.fragment, !fragment.isEmpty {
                guard let b = documents[f].anchors[fragment] else { continue }
                block = b
            }
            let title = entry.title.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            starts.append(Start(file: f, block: block, title: title))
        }
        // Reading order wins; two entries at one place (a part and its first chapter) share a title.
        starts.sort { ($0.file, $0.block) < ($1.file, $1.block) }
        var merged: [Start] = []
        for s in starts {
            if let last = merged.last, last.file == s.file, last.block == s.block {
                if !s.title.isEmpty, s.title != last.title { merged[merged.count - 1].title += " · " + s.title }
            } else {
                merged.append(s)
            }
        }
        guard merged.count >= 2 else { return nil }

        enum Group { case toc(String, [RawBlock]), detect([RawBlock]) }
        var groups: [Group] = []
        func append(_ blocks: [RawBlock]) {
            guard !blocks.isEmpty else { return }
            switch groups.last {
            case .toc(let t, let b)?: groups[groups.count - 1] = .toc(t, b + blocks)
            case .detect(let b)?: groups[groups.count - 1] = .detect(b + blocks)
            case nil: groups.append(.detect(blocks))
            }
        }
        let startsByFile = Dictionary(grouping: merged, by: \.file)
        for (f, doc) in documents.enumerated() {
            // The contents page, the copyright page, a notes file: left out whole.
            if skipped.contains(doc.path) || !doc.types.isDisjoint(with: unreadTypes) { continue }
            guard let here = startsByFile[f]?.sorted(by: { $0.block < $1.block }) else {
                // A file the contents don't list: its own chapter when it opens with a
                // heading, otherwise more of the chapter before (a chapter split in two files).
                if let first = doc.blocks.first, first.headingLevel != nil, !groups.isEmpty {
                    var chunk = doc.blocks
                    chunk[0].headingLevel = 1
                    groups.append(.detect(chunk))
                } else {
                    append(doc.blocks)
                }
                continue
            }
            append(Array(doc.blocks.prefix(here[0].block)))
            for (k, s) in here.enumerated() {
                let end = k + 1 < here.count ? here[k + 1].block : doc.blocks.count
                let body = Array(doc.blocks[min(s.block, end)..<end])
                groups.append(.toc(s.title, body))
            }
        }

        var chapters: [Chapter] = []
        var pendingTitle: String?
        for (g, group) in groups.enumerated() {
            switch group {
            case .detect(let blocks):
                var found = ChapterDetector.chapters(from: blocks, fallbackTitle: "Opening")
                // Before the first chapter: a title page or a cover isn't worth a chapter.
                if g == 0 {
                    found.removeAll { $0.wordCount < 40 && $0.paragraphs.allSatisfy { !$0.text.contains("“") && !$0.text.contains("\"") } }
                }
                chapters += found
            case .toc(let title, let blocks):
                if unreadTitles.contains(title.lowercased()) { continue }
                let made = tocChapters(title: title, blocks: blocks)
                guard !made.isEmpty else {
                    // A part title page: its title goes before the next chapter's.
                    if !title.isEmpty { pendingTitle = pendingTitle.map { $0 + " · " + title } ?? title }
                    continue
                }
                var first = made
                if let p = pendingTitle { first[0].title = first[0].title.isEmpty ? p : p + " · " + first[0].title }
                pendingTitle = nil
                chapters += first
            }
        }
        for i in chapters.indices where chapters[i].title.isEmpty { chapters[i].title = i == 0 ? "Opening" : "Untitled" }
        return chapters.count >= 2 ? chapters : nil
    }

    /// One contents entry's text as a chapter: the heading lines that repeat its title are
    /// dropped; a "Chapter 7" heading the contents left out still starts a chapter.
    static func tocChapters(title: String, blocks: [RawBlock]) -> [Chapter] {
        var body = blocks.map { b -> RawBlock in
            var b = b
            b.text = b.text.trimmingCharacters(in: .whitespacesAndNewlines)
            return b
        }.filter { !$0.text.isEmpty }
        let key = normalized(title)
        var dropped = 0
        while dropped < 3, let b = body.first, b.text.count < 100,
              b.headingLevel != nil || (!normalized(b.text).isEmpty && key.contains(normalized(b.text))) {
            body.removeFirst()
            dropped += 1
        }
        var chapters: [Chapter] = []
        var current = Chapter(title: title, paragraphs: [])
        var subtitleOpen = false
        for block in body {
            if block.headingLevel == 1, ChapterDetector.looksLikeHeading(block.text), !current.paragraphs.isEmpty {
                chapters.append(current)
                current = Chapter(title: ChapterDetector.titleCase(block.text), paragraphs: [])
                subtitleOpen = true
                continue
            }
            if subtitleOpen, current.paragraphs.isEmpty, block.headingLevel != nil, block.text.count < 60 {
                current.title += ": " + block.text
                subtitleOpen = false
                continue
            }
            subtitleOpen = false
            let style: Paragraph.Style = ChapterDetector.isSceneBreak(block.text) ? .sceneBreak : (block.isBlockQuote ? .blockQuote : .body)
            current.paragraphs.append(Paragraph(text: block.text, style: style, italic: block.italic))
        }
        if !current.paragraphs.isEmpty { chapters.append(current) }
        return chapters
    }

    /// Lowercased letters and digits only, for comparing a heading with its contents title.
    static func normalized(_ s: String) -> String {
        String(s.lowercased().unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) }.map(Character.init))
    }

    /// "../Text/ch1.xhtml#p3" read from the file at `doc` → (absolute path, "p3").
    static func resolve(_ href: String, from doc: URL) -> (path: String, fragment: String?) {
        let parts = href.split(separator: "#", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
        let file = parts[0].removingPercentEncoding ?? parts[0]
        let fragment = parts.count > 1 ? (parts[1].removingPercentEncoding ?? parts[1]) : nil
        let path = file.isEmpty ? doc.standardizedFileURL.path
            : URL(fileURLWithPath: file, relativeTo: doc.deletingLastPathComponent()).standardizedFileURL.path
        return (path, fragment)
    }

    struct OPF {
        struct Item { var href: String; var mediaType: String; var properties: String }
        var manifest: [String: Item] = [:]
        var spine: [String] = []
        /// The spine's toc attribute (EPUB 2: the NCX's manifest id).
        var tocID: String?
        /// EPUB 2 guide references: (type, href).
        var guide: [(type: String, href: String)] = []
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
                    if let id = attributes["id"], let href = attributes["href"] {
                        opf.manifest[id] = Item(href: href, mediaType: attributes["media-type"] ?? "", properties: attributes["properties"] ?? "")
                    }
                case "spine": opf.tocID = attributes["toc"]
                case "itemref": if let id = attributes["idref"], attributes["linear"] != "no" { opf.spine.append(id) }
                case "reference":
                    if let type = attributes["type"], let href = attributes["href"] { opf.guide.append((type.lowercased(), href)) }
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

    /// The EPUB 3 nav document: the toc nav's links (nested lists give parents) and the
    /// landmarks nav's typed links.
    enum NavReader {
        static func parse(_ data: Data, at url: URL) -> (toc: [TOCEntry], landmarks: [(type: String, path: String)]) {
            guard let prepared = XHTMLBlocks.prepare(data) else { return ([], []) }
            let d = Delegate(url: url)
            let p = XMLParser(data: prepared)
            p.delegate = d
            p.parse()
            return (d.toc, d.landmarks)
        }

        final class Delegate: NSObject, XMLParserDelegate {
            let url: URL
            var toc: [TOCEntry] = []
            var landmarks: [(type: String, path: String)] = []
            private enum Kind { case toc, landmarks, other }
            private var nav: Kind?
            private var navDepth = 0
            private var depth = 0
            /// The last entry at each list depth, for parents.
            private var lastAt: [Int: Int] = [:]
            private var link: (href: String, type: String)?
            private var buffer = ""
            init(url: URL) { self.url = url }

            func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                        attributes: [String: String] = [:]) {
                let tag = name.lowercased()
                if tag == "nav" {
                    navDepth += 1
                    if navDepth == 1 {
                        let types = XHTMLBlocks.types(attributes)
                        nav = types.contains("toc") || types.contains("doc-toc") ? .toc : types.contains("landmarks") ? .landmarks : .other
                        depth = 0
                        lastAt = [:]
                    }
                    return
                }
                guard nav != nil else { return }
                if tag == "ol" || tag == "ul" { depth += 1 }
                // A list item without a link of its own (a part shown as plain text) is no parent.
                if tag == "li" { lastAt[depth] = nil }
                if tag == "a", let href = attributes["href"] {
                    link = (href, XHTMLBlocks.types(attributes).first ?? "")
                    buffer = ""
                }
            }
            func parser(_ parser: XMLParser, foundCharacters string: String) { if link != nil { buffer += string } }
            func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
                let tag = name.lowercased()
                if tag == "nav" {
                    navDepth = max(0, navDepth - 1)
                    if navDepth == 0 { nav = nil }
                    return
                }
                guard let kind = nav else { return }
                if tag == "ol" || tag == "ul" {
                    lastAt[depth] = nil
                    depth = max(0, depth - 1)
                }
                if tag == "a", let l = link {
                    let target = EpubReader.resolve(l.href, from: url)
                    switch kind {
                    case .toc:
                        toc.append(TOCEntry(title: buffer.trimmingCharacters(in: .whitespacesAndNewlines), path: target.path,
                                            fragment: target.fragment, parent: lastAt[depth - 1]))
                        lastAt[depth] = toc.count - 1
                    case .landmarks: landmarks.append((l.type, target.path))
                    case .other: break
                    }
                    link = nil
                }
            }
        }
    }

    /// The EPUB 2 toc.ncx: nested navPoints, each a label and a content src.
    enum NCXReader {
        static func parse(_ data: Data, at url: URL) -> [TOCEntry] {
            let d = Delegate(url: url)
            let p = XMLParser(data: XHTMLBlocks.prepare(data) ?? data)
            p.delegate = d
            p.parse()
            return d.entries
        }

        final class Delegate: NSObject, XMLParserDelegate {
            let url: URL
            var entries: [TOCEntry] = []
            /// For each open navPoint: its entry, once its content is read.
            private var open: [Int?] = []
            private var label = ""
            private var inLabel = false
            private var inMap = false
            init(url: URL) { self.url = url }

            func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                        attributes: [String: String] = [:]) {
                let tag = (name.split(separator: ":").last.map(String.init) ?? name).lowercased()
                switch tag {
                case "navmap": inMap = true
                case "navpoint" where inMap:
                    open.append(nil)
                    label = ""
                case "navlabel" where inMap: inLabel = true
                case "content" where inMap:
                    guard let src = attributes["src"], !open.isEmpty, open[open.count - 1] == nil else { return }
                    let target = EpubReader.resolve(src, from: url)
                    let parent = open.dropLast().last(where: { $0 != nil }) ?? nil
                    entries.append(TOCEntry(title: label.trimmingCharacters(in: .whitespacesAndNewlines), path: target.path,
                                            fragment: target.fragment, parent: parent))
                    open[open.count - 1] = entries.count - 1
                default: break
                }
            }
            func parser(_ parser: XMLParser, foundCharacters string: String) { if inLabel { label += string } }
            func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
                let tag = (name.split(separator: ":").last.map(String.init) ?? name).lowercased()
                switch tag {
                case "navmap": inMap = false
                case "navpoint": if !open.isEmpty { open.removeLast() }
                case "navlabel": inLabel = false
                default: break
                }
            }
        }
    }
}

/// Block-level reading of well-formed XHTML: p, h1–h6, blockquote, li, and leaf divs.
/// Footnotes and endnotes (epub:type, doc- roles, note classes, asides) and the note
/// markers that link to them are left out.
enum XHTMLBlocks {
    private static let entities: [String: String] = {
        var map: [String: String] = [
            "nbsp": "\u{00A0}", "mdash": "—", "ndash": "–", "hellip": "…", "rsquo": "’", "lsquo": "‘",
            "rdquo": "”", "ldquo": "“", "sbquo": "‚", "bdquo": "„", "shy": "", "thinsp": "\u{2009}", "emsp": " ",
            "ensp": " ", "bull": "•", "dagger": "†", "Dagger": "‡", "prime": "′", "Prime": "″", "trade": "™",
            "oelig": "œ", "OElig": "Œ", "scaron": "š", "Scaron": "Š", "zwnj": "\u{200C}", "zwj": "\u{200D}",
            "lsaquo": "‹", "rsaquo": "›", "euro": "€", "minus": "−", "times": "×", "divide": "÷",
        ]
        // Latin-1, 160–255, in order.
        let latin1 = "nbsp iexcl cent pound curren yen brvbar sect uml copy ordf laquo not shy reg macr deg plusmn sup2 sup3 acute micro para middot cedil sup1 ordm raquo frac14 frac12 frac34 iquest Agrave Aacute Acirc Atilde Auml Aring AElig Ccedil Egrave Eacute Ecirc Euml Igrave Iacute Icirc Iuml ETH Ntilde Ograve Oacute Ocirc Otilde Ouml times Oslash Ugrave Uacute Ucirc Uuml Yacute THORN szlig agrave aacute acirc atilde auml aring aelig ccedil egrave eacute ecirc euml igrave iacute icirc iuml eth ntilde ograve oacute ocirc otilde ouml divide oslash ugrave uacute ucirc uuml yacute thorn yuml"
        for (i, name) in latin1.split(separator: " ").enumerated() where map[String(name)] == nil {
            map[String(name)] = String(UnicodeScalar(UInt8(160 + i)))
        }
        return map
    }()

    /// Named entities XML doesn't know become characters, and the DOCTYPE goes (XMLParser
    /// would try to load the DTD).
    static func prepare(_ data: Data) -> Data? {
        guard var text = String(data: data, encoding: .utf8) else { return nil }
        if text.contains("&") {
            let regex = try! NSRegularExpression(pattern: "&([A-Za-z][A-Za-z0-9]*);")
            let ns = text as NSString
            var out = ""
            var cursor = 0
            for m in regex.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
                let name = ns.substring(with: m.range(at: 1))
                guard let value = entities[name] else { continue }
                out += ns.substring(with: NSRange(location: cursor, length: m.range.location - cursor)) + value
                cursor = NSMaxRange(m.range)
            }
            out += ns.substring(from: cursor)
            text = out
        }
        if let r = text.range(of: "<!DOCTYPE[^>]*>", options: .regularExpression) { text.removeSubrange(r) }
        return Data(text.utf8)
    }

    static func parse(_ data: Data) -> [RawBlock]? { document(data)?.blocks }

    static func document(_ data: Data) -> (blocks: [RawBlock], anchors: [String: Int], types: Set<String>)? {
        guard let prepared = prepare(data) else { return nil }
        let d = Delegate()
        let p = XMLParser(data: prepared)
        p.delegate = d
        guard p.parse() else { return nil }
        d.flush()
        return (d.blocks, d.anchors, d.types)
    }

    /// The epub:type words and doc- roles on an element.
    static func types(_ attributes: [String: String]) -> Set<String> {
        let words = ((attributes["epub:type"] ?? "") + " " + (attributes["role"] ?? "")).lowercased()
        return Set(words.split(whereSeparator: \.isWhitespace).map { String($0.split(separator: ":").last ?? $0) })
    }

    /// Notes, note markers and their back links: never read aloud.
    static let noteTypes: Set<String> = ["footnote", "footnotes", "endnote", "endnotes", "rearnote", "rearnotes", "note",
                                         "noteref", "annoref", "doc-footnote", "doc-endnote", "doc-endnotes",
                                         "doc-noteref", "doc-backlink"]
    static let noteClasses: Set<String> = ["footnote", "footnotes", "endnote", "endnotes", "rearnote", "rearnotes",
                                           "footnote-ref", "noteref", "fnref"]
    /// What a note marker says: "12", "[3]", "*", "†", a single small letter.
    private static let markerPattern = try! NSRegularExpression(pattern: "^[\\[(]?(?:\\d{1,4}|[*†‡§¶]{1,3}|[a-z])[\\])]?$")

    static func isNoteMarker(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return !t.isEmpty && markerPattern.firstMatch(in: t, range: NSRange(location: 0, length: (t as NSString).length)) != nil
    }

    final class Delegate: NSObject, XMLParserDelegate {
        var blocks: [RawBlock] = []
        var anchors: [String: Int] = [:]
        var types: Set<String> = []
        private var text = ""
        private var italic: [TextSpan] = []
        private var italicDepth = 0
        private var italicStart = 0
        private var level: Int?
        private var quoteDepth = 0
        private var skipDepth = 0
        private var supCount = 0
        private struct Frame {
            var italic = false
            var quote = false
            var block = false
            /// A link that may be a note marker: where its text starts and how many
            /// superscripts there were when it opened.
            var link: (start: Int, sups: Int, notey: Bool)?
            var sup = false
        }
        private var stack: [Frame] = []
        private static let blockTags: Set<String> = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "div", "blockquote", "br", "tr"]
        private static let inlineTags: Set<String> = ["a", "span", "i", "em", "b", "strong", "sup", "sub", "small", "cite", "u", "abbr", "code", "q", "mark", "img"]
        private static let skipTags: Set<String> = ["head", "script", "style", "aside", "nav"]

        func flush() {
            let leading = text.prefix(while: { $0.isWhitespace }).utf16.count
            let trimmed = text.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                let shifted = italic.map { TextSpan(location: max(0, $0.location - leading), length: $0.length) }
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
            if skipDepth > 0 { skipDepth += 1; return }
            let kinds = XHTMLBlocks.types(attributes)
            let classes = Set((attributes["class"] ?? "").lowercased().split(whereSeparator: \.isWhitespace).map(String.init))
            if ["html", "body"].contains(tag) || (blocks.isEmpty && text.allSatisfy(\.isWhitespace) && ["section", "div", "article"].contains(tag)) {
                types.formUnion(kinds)
            }
            if Self.skipTags.contains(tag) || !kinds.isDisjoint(with: XHTMLBlocks.noteTypes) || !classes.isDisjoint(with: XHTMLBlocks.noteClasses) {
                skipDepth = 1
                return
            }
            var frame = Frame()
            if Self.blockTags.contains(tag) {
                flush()
                frame.block = true
                if tag.hasPrefix("h"), let n = Int(tag.dropFirst()) { level = n <= 2 ? 1 : 2 }
                if tag == "blockquote" { quoteDepth += 1; frame.quote = true }
                let cls = (attributes["class"] ?? "").lowercased()
                if cls.contains("chapter") && (cls.contains("title") || cls.contains("head")) { level = 1 }
            }
            if let id = attributes["id"] ?? (tag == "a" ? attributes["name"] : nil), !id.isEmpty {
                if !frame.block, !Self.inlineTags.contains(tag) { flush() }
                if anchors[id] == nil { anchors[id] = blocks.count }
            }
            if tag == "sup" { supCount += 1; frame.sup = true }
            if tag == "a", let href = attributes["href"], href.contains("#") {
                let target = (href + " " + (attributes["id"] ?? "")).lowercased()
                let notey = ["note", "fn", "ftn"].contains { target.contains($0) }
                frame.link = ((text as NSString).length, supCount, notey)
            }
            frame.italic = ["i", "em", "cite"].contains(tag) || classes.contains { $0.contains("italic") }
            if frame.italic {
                if italicDepth == 0 { italicStart = (text as NSString).length }
                italicDepth += 1
            }
            stack.append(frame)
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            guard skipDepth == 0 else { return }
            text += string
        }

        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            if skipDepth > 0 { skipDepth -= 1; return }
            guard let frame = stack.popLast() else { return }
            if frame.italic, italicDepth > 0 {
                italicDepth -= 1
                if italicDepth == 0 {
                    let end = (text as NSString).length
                    if end > italicStart { italic.append(TextSpan(location: italicStart, length: end - italicStart)) }
                }
            }
            // A link that only says "12" or "*", in or around a superscript or pointing at a
            // note, is a note marker: take it out of the text.
            if let link = frame.link {
                let ns = text as NSString
                let start = min(link.start, ns.length)
                let inSup = stack.contains { $0.sup } || supCount > link.sups
                if inSup || link.notey, XHTMLBlocks.isNoteMarker(ns.substring(from: start)) {
                    text = ns.substring(to: start)
                    italic = italic.compactMap { span in
                        guard span.location < start else { return nil }
                        return TextSpan(location: span.location, length: min(span.length, start - span.location))
                    }
                    if italicDepth > 0, italicStart > start { italicStart = start }
                }
            }
            if frame.block {
                flush()
                if frame.quote { quoteDepth = max(0, quoteDepth - 1) }
            }
        }
    }
}
