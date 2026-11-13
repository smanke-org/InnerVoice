import CryptoKit
import Foundation
import NaturalLanguage

/// EPUB with Audio: an EPUB 3 book whose text plays in the book's voices and highlights as
/// it's read (Media Overlays). Each chapter is an XHTML page, an audio file and a SMIL file
/// that ties every sentence or line (a fragment of the page) to its clip of the audio.
///
///   mimetype                      (first, stored)
///   META-INF/container.xml
///   OEBPS/content.opf             (media:duration per chapter and in all)
///   OEBPS/nav.xhtml, style.css
///   OEBPS/text/c001.xhtml         (media-overlay="mo-c001")
///   OEBPS/smil/c001.smil
///   OEBPS/audio/c001.m4a
public enum ReadAlongEpub {
    /// One stretch of text and when the audio reads it, in seconds from the start of its chapter's audio.
    public struct Clip: Equatable, Sendable {
        public var fragment: String
        public var begin: Double
        public var end: Double
        public init(fragment: String, begin: Double, end: Double) {
            self.fragment = fragment
            self.begin = begin
            self.end = end
        }
    }

    /// A chapter's page and which fragment each segment reads.
    public struct Page: Sendable {
        public var xhtml: String
        /// Segment id → the id of the span holding its words.
        public var fragments: [String: String]
    }

    public struct ChapterFile: Sendable {
        public var title: String
        public var xhtml: String
        /// The chapter's audio (M4A), or nil when nothing in it is read.
        public var audio: URL?
        /// Seconds of audio in the file.
        public var duration: Double
        public var clips: [Clip]
        public init(title: String, xhtml: String, audio: URL?, duration: Double, clips: [Clip]) {
            self.title = title
            self.xhtml = xhtml
            self.audio = audio
            self.duration = duration
            self.clips = clips
        }
    }

    public struct Book: Sendable {
        public var title: String
        public var author: String
        public var language: String
        public var identifier: String
        public var modified: Date
        public var chapters: [ChapterFile]
        public init(title: String, author: String, language: String, identifier: String, modified: Date = Date(), chapters: [ChapterFile]) {
            self.title = title
            self.author = author
            self.language = language
            self.identifier = identifier
            self.modified = modified
            self.chapters = chapters
        }
    }

    /// A file inside the book: written from memory or copied from disk.
    public enum Content: Sendable {
        case text(String)
        case file(URL)
    }

    // MARK: Pages

    /// The chapter as XHTML: its title, then each paragraph, with every stretch the voices
    /// read (one or more segments with the same `source`) in a span of its own.
    public static func page(chapter: Chapter, number: Int, segments: [SpeechSegment], language: String) -> Page {
        let byParagraph = Dictionary(grouping: segments, by: \.paragraphID)
        var fragments: [String: String] = [:]
        var count = 0
        var body = "<h1>\(escape(chapter.title))</h1>\n"
        for paragraph in chapter.paragraphs {
            let ns = paragraph.text as NSString
            let whole = NSRange(location: 0, length: ns.length)
            // The spans, in order, never overlapping.
            var spans: [(range: NSRange, id: String)] = []
            for seg in byParagraph[paragraph.id] ?? [] {
                guard let raw = seg.source else { continue }
                let r = NSIntersectionRange(raw, whole)
                guard r.length > 0 else { continue }
                if let last = spans.last, last.range == r || r.location < NSMaxRange(last.range) {
                    fragments[seg.id] = last.id
                    continue
                }
                count += 1
                let id = "c\(number)-\(count)"
                spans.append((r, id))
                fragments[seg.id] = id
            }
            var html = ""
            var cursor = 0
            for span in spans {
                html += inline(ns, NSRange(location: cursor, length: span.range.location - cursor), italic: paragraph.italic)
                html += "<span id=\"\(span.id)\">" + inline(ns, span.range, italic: paragraph.italic) + "</span>"
                cursor = NSMaxRange(span.range)
            }
            html += inline(ns, NSRange(location: cursor, length: ns.length - cursor), italic: paragraph.italic)
            switch paragraph.style {
            case .heading: body += "<h2>\(html)</h2>\n"
            case .sceneBreak: body += "<p class=\"break\">\(html)</p>\n"
            case .blockQuote: body += "<blockquote><p>\(html)</p></blockquote>\n"
            case .body: body += "<p>\(html)</p>\n"
            }
        }
        let lang = escape(language)
        let xhtml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="\(lang)" lang="\(lang)">
        <head>
        <meta charset="UTF-8"/>
        <title>\(escape(chapter.title))</title>
        <link rel="stylesheet" type="text/css" href="../style.css"/>
        </head>
        <body>
        <section epub:type="chapter" id="chapter-\(number)">
        \(body)</section>
        </body>
        </html>

        """
        return Page(xhtml: xhtml, fragments: fragments)
    }

    /// Text in `range`, escaped, with its italic parts in <i>.
    static func inline(_ ns: NSString, _ range: NSRange, italic: [TextSpan]) -> String {
        guard range.length > 0 else { return "" }
        var cuts = Set([range.location, NSMaxRange(range)])
        for span in italic {
            for edge in [span.location, span.location + span.length] where edge > range.location && edge < NSMaxRange(range) {
                cuts.insert(edge)
            }
        }
        let points = cuts.sorted()
        var out = ""
        for (a, b) in zip(points, points.dropFirst()) {
            let text = escape(ns.substring(with: NSRange(location: a, length: b - a)))
            let isItalic = italic.contains { $0.location <= a && $0.location + $0.length >= b }
            out += isItalic ? "<i>\(text)</i>" : text
        }
        return out
    }

    /// Clips from each segment's place in the audio: a fragment runs from its first
    /// segment's start to its last one's end. In reading order.
    public static func clips(fragments: [String: String], timing: [String: (begin: Double, end: Double)], duration: Double) -> [Clip] {
        var byFragment: [String: Clip] = [:]
        for (seg, fragment) in fragments {
            guard let t = timing[seg] else { continue }
            if var c = byFragment[fragment] {
                c.begin = min(c.begin, t.begin)
                c.end = max(c.end, t.end)
                byFragment[fragment] = c
            } else {
                byFragment[fragment] = Clip(fragment: fragment, begin: t.begin, end: t.end)
            }
        }
        return byFragment.values
            .map { Clip(fragment: $0.fragment, begin: max(0, $0.begin), end: min(duration, $0.end)) }
            .filter { $0.end - $0.begin >= 0.001 }
            .sorted { $0.begin < $1.begin }
    }

    // MARK: Package

    /// Every file of the book, in the order they're written (mimetype first).
    public static func files(_ book: Book) -> [(path: String, content: Content)] {
        var out: [(String, Content)] = [
            ("mimetype", .text("application/epub+zip")),
            ("META-INF/container.xml", .text("""
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
            <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>

            """)),
            ("OEBPS/content.opf", .text(package(book))),
            ("OEBPS/nav.xhtml", .text(nav(book))),
            ("OEBPS/style.css", .text(style)),
        ]
        for (i, chapter) in book.chapters.enumerated() {
            let n = name(i)
            out.append(("OEBPS/text/\(n).xhtml", .text(chapter.xhtml)))
            if let audio = chapter.audio, !chapter.clips.isEmpty {
                out.append(("OEBPS/smil/\(n).smil", .text(smil(chapter, index: i))))
                out.append(("OEBPS/audio/\(n).m4a", .file(audio)))
            }
        }
        return out
    }

    /// Writes the book to `url` (replacing a file there).
    public static func write(_ book: Book, to url: URL) throws {
        try EpubZip.write(files(book), to: url)
    }

    static func name(_ i: Int) -> String { String(format: "c%03d", i + 1) }

    static func hasOverlay(_ c: ChapterFile) -> Bool { c.audio != nil && !c.clips.isEmpty }

    static func package(_ book: Book) -> String {
        let modified: String = {
            let f = ISO8601DateFormatter()
            f.formatOptions = [.withInternetDateTime]
            f.timeZone = TimeZone(identifier: "UTC")
            return f.string(from: book.modified)
        }()
        var meta = ""
        var manifest = """
        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
        <item id="css" href="style.css" media-type="text/css"/>

        """
        var spine = ""
        // Durations in whole milliseconds, so the parts add up to the total exactly.
        var total = 0
        for (i, c) in book.chapters.enumerated() {
            let n = name(i)
            if hasOverlay(c), let _ = c.audio {
                let ms = Int((c.duration * 1000).rounded())
                total += ms
                meta += "<meta property=\"media:duration\" refines=\"#mo-\(n)\">\(clock(ms: ms))</meta>\n"
                manifest += "<item id=\"\(n)\" href=\"text/\(n).xhtml\" media-type=\"application/xhtml+xml\" media-overlay=\"mo-\(n)\"/>\n"
                manifest += "<item id=\"mo-\(n)\" href=\"smil/\(n).smil\" media-type=\"application/smil+xml\"/>\n"
                manifest += "<item id=\"audio-\(n)\" href=\"audio/\(n).m4a\" media-type=\"audio/mp4\"/>\n"
            } else {
                manifest += "<item id=\"\(n)\" href=\"text/\(n).xhtml\" media-type=\"application/xhtml+xml\"/>\n"
            }
            spine += "<itemref idref=\"\(n)\"/>\n"
        }
        if total > 0 {
            meta += "<meta property=\"media:duration\">\(clock(ms: total))</meta>\n"
            meta += "<meta property=\"media:active-class\">-epub-media-overlay-active</meta>\n"
        }
        let author = book.author.trimmingCharacters(in: .whitespaces)
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id" xml:lang="\(escape(book.language))">
        <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
        <dc:identifier id="book-id">\(escape(book.identifier))</dc:identifier>
        <dc:title>\(escape(book.title.isEmpty ? "Untitled" : book.title))</dc:title>
        \(author.isEmpty ? "" : "<dc:creator>\(escape(author))</dc:creator>\n")<dc:language>\(escape(book.language))</dc:language>
        <meta property="dcterms:modified">\(modified)</meta>
        \(meta)</metadata>
        <manifest>
        \(manifest)</manifest>
        <spine>
        \(spine)</spine>
        </package>

        """
    }

    static func nav(_ book: Book) -> String {
        let items = book.chapters.enumerated().map { i, c in
            "<li><a href=\"text/\(name(i)).xhtml\">\(escape(c.title))</a></li>"
        }.joined(separator: "\n")
        let lang = escape(book.language)
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="\(lang)" lang="\(lang)">
        <head><meta charset="UTF-8"/><title>Contents</title></head>
        <body>
        <nav epub:type="toc" id="toc">
        <h1>Contents</h1>
        <ol>
        \(items)
        </ol>
        </nav>
        <nav epub:type="landmarks" hidden="hidden">
        <ol><li><a epub:type="bodymatter" href="text/\(name(0)).xhtml">Start</a></li></ol>
        </nav>
        </body>
        </html>

        """
    }

    static func smil(_ chapter: ChapterFile, index: Int) -> String {
        let n = name(index)
        let pars = chapter.clips.enumerated().map { k, clip in
            "<par id=\"p\(k + 1)\"><text src=\"../text/\(n).xhtml#\(clip.fragment)\"/>"
                + "<audio src=\"../audio/\(n).m4a\" clipBegin=\"\(clock(clip.begin))\" clipEnd=\"\(clock(clip.end))\"/></par>"
        }.joined(separator: "\n")
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <smil xmlns="http://www.w3.org/ns/SMIL" xmlns:epub="http://www.idpf.org/2007/ops" version="3.0">
        <body>
        <seq id="seq-\(n)" epub:textref="../text/\(n).xhtml" epub:type="chapter">
        \(pars)
        </seq>
        </body>
        </smil>

        """
    }

    static let style = """
    body { font-family: serif; line-height: 1.5; margin: 0 5%; }
    h1 { text-align: center; font-weight: normal; margin: 2em 0 1.5em; }
    h2 { text-align: center; font-size: 1.1em; font-weight: normal; margin: 1.5em 0 1em; }
    p { margin: 0; text-indent: 1.5em; }
    h1 + p, h2 + p, p.break + p, blockquote + p { text-indent: 0; }
    p.break { text-align: center; text-indent: 0; margin: 1em 0; }
    blockquote { margin: 1em 2em; }
    blockquote p { text-indent: 0; }
    .-epub-media-overlay-active { background-color: rgba(255, 213, 79, 0.55); border-radius: 0.15em; }

    """

    /// SMIL full clock value, "0:01:02.345".
    public static func clock(_ seconds: Double) -> String { clock(ms: Int((max(0, seconds) * 1000).rounded())) }

    static func clock(ms: Int) -> String {
        String(format: "%d:%02d:%02d.%03d", ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000)
    }

    /// XML text, with the control characters XML forbids taken out.
    static func escape(_ s: String) -> String {
        var out = ""
        out.reserveCapacity(s.utf16.count)
        for c in s.unicodeScalars {
            switch c {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "\"": out += "&quot;"
            case "\t", "\n", "\r": out.unicodeScalars.append(c)
            default:
                if c.value < 0x20 || c.value == 0xFFFE || c.value == 0xFFFF { continue }
                out.unicodeScalars.append(c)
            }
        }
        return out
    }

    /// The book's language ("en", "fr"…) from its text; English when unsure.
    public static func language(of project: Project) -> String {
        let sample = project.chapters.lazy.flatMap(\.paragraphs).prefix(60).map(\.text).joined(separator: " ")
        return NLLanguageRecognizer.dominantLanguage(for: sample).map(\.rawValue).flatMap { $0 == "und" ? nil : $0 } ?? "en"
    }

    /// The same book (title and author) gets the same identifier every time, so a reading
    /// app replaces an earlier export instead of keeping both.
    public static func identifier(title: String, author: String) -> String {
        var bytes = Array(Insecure.MD5.hash(data: Data("innervoice-epub|\(title)|\(author)".utf8)))
        bytes[6] = (bytes[6] & 0x0F) | 0x30   // version 3 (name-based, MD5)
        bytes[8] = (bytes[8] & 0x3F) | 0x80
        let uuid = UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
                               bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]))
        return "urn:uuid:" + uuid.uuidString.lowercased()
    }
}

/// Writes an EPUB's zip: `mimetype` first and stored, text deflated, audio stored (it's
/// already compressed) and streamed from disk, so a long book never sits in memory.
enum EpubZip {
    enum ZipError: Error { case tooLarge }

    static func write(_ files: [(path: String, content: ReadAlongEpub.Content)], to url: URL) throws {
        try? FileManager.default.removeItem(at: url)
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path])
        }
        let out = try FileHandle(forWritingTo: url)
        defer { try? out.close() }
        var central = Data()
        var offset: UInt64 = 0
        let (time, date) = dosDateTime(Date())
        let chunk = 1 << 20
        for (path, content) in files {
            let name = Data(path.utf8)
            var method: UInt16 = 0
            var crc: UInt32 = 0
            var size: UInt64 = 0
            var payload: Data?
            switch content {
            case .text(let s):
                let data = Data(s.utf8)
                crc = CRC32.checksum(data)
                size = UInt64(data.count)
                payload = data
                if path != "mimetype", let packed = ZipFile.deflate(data), packed.count < data.count {
                    method = 8
                    payload = packed
                }
            case .file(let file):
                let h = try FileHandle(forReadingFrom: file)
                defer { try? h.close() }
                while let piece = try h.read(upToCount: chunk), !piece.isEmpty {
                    crc = CRC32.update(crc, with: piece)
                    size += UInt64(piece.count)
                }
            }
            let packedSize = payload.map { UInt64($0.count) } ?? size
            guard offset < UInt64(UInt32.max), packedSize < UInt64(UInt32.max) else { throw ZipError.tooLarge }
            var header = Data()
            header.appendLE32(0x0403_4B50)
            header.appendLE16(20)
            header.appendLE16(0x0800)
            header.appendLE16(method)
            header.appendLE16(time)
            header.appendLE16(date)
            header.appendLE32(crc)
            header.appendLE32(UInt32(packedSize))
            header.appendLE32(UInt32(size))
            header.appendLE16(UInt16(name.count))
            header.appendLE16(0)
            header.append(name)
            try out.write(contentsOf: header)
            if let payload {
                try out.write(contentsOf: payload)
            } else if case .file(let file) = content {
                let h = try FileHandle(forReadingFrom: file)
                defer { try? h.close() }
                while let piece = try h.read(upToCount: chunk), !piece.isEmpty { try out.write(contentsOf: piece) }
            }
            central.appendLE32(0x0201_4B50)
            central.appendLE16(20)
            central.appendLE16(20)
            central.appendLE16(0x0800)
            central.appendLE16(method)
            central.appendLE16(time)
            central.appendLE16(date)
            central.appendLE32(crc)
            central.appendLE32(UInt32(packedSize))
            central.appendLE32(UInt32(size))
            central.appendLE16(UInt16(name.count))
            central.appendLE16(0)
            central.appendLE16(0)
            central.appendLE16(0)
            central.appendLE16(0)
            central.appendLE32(0)
            central.appendLE32(UInt32(offset))
            central.append(name)
            offset += UInt64(header.count) + packedSize
        }
        guard offset < UInt64(UInt32.max) else { throw ZipError.tooLarge }
        var end = Data()
        end.appendLE32(0x0605_4B50)
        end.appendLE16(0)
        end.appendLE16(0)
        end.appendLE16(UInt16(files.count))
        end.appendLE16(UInt16(files.count))
        end.appendLE32(UInt32(central.count))
        end.appendLE32(UInt32(offset))
        end.appendLE16(0)
        try out.write(contentsOf: central)
        try out.write(contentsOf: end)
    }

    private static func dosDateTime(_ date: Date) -> (UInt16, UInt16) {
        let c = Calendar(identifier: .gregorian).dateComponents([.year, .month, .day, .hour, .minute, .second], from: date)
        let time = UInt16((c.hour ?? 0) << 11 | (c.minute ?? 0) << 5 | (c.second ?? 0) / 2)
        let day = UInt16(max(0, (c.year ?? 1980) - 1980) << 9 | (c.month ?? 1) << 5 | (c.day ?? 1))
        return (time, day)
    }
}

private extension Data {
    mutating func appendLE16(_ v: UInt16) { append(contentsOf: [UInt8(v & 0xFF), UInt8(v >> 8)]) }
    mutating func appendLE32(_ v: UInt32) { append(contentsOf: [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF), UInt8((v >> 16) & 0xFF), UInt8(v >> 24)]) }
}
