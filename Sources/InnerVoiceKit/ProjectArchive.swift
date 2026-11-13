import Compression
import Foundation

/// A project on disk as one ordinary file: a zip archive holding project.json, the original
/// manuscript and the pronunciation samples.
///
///   Book.innervoice  (zip)
///     project.json
///     Original/<manuscript file>
///     Audio/<sample>.m4a
///
/// Projects used to be packages (a folder Finder shows as one file). Cloud folders handle
/// those badly: OneDrive renamed one under an open window, which then could not save. A
/// single file syncs like any document. Packages still open (`InnerVoiceDocument` reads both)
/// and become a single file the next time they are saved.
///
/// Any zip tool can open the file, so a project can always be taken apart by hand.
public enum ProjectArchive {
    public struct Contents: Equatable, Sendable {
        public var projectJSON: Data
        public var original: Original?
        public var audio: [String: Data]

        public struct Original: Equatable, Sendable {
            public var name: String
            public var data: Data
            public init(name: String, data: Data) { self.name = name; self.data = data }
        }

        public init(projectJSON: Data, original: Original? = nil, audio: [String: Data] = [:]) {
            self.projectJSON = projectJSON
            self.original = original
            self.audio = audio
        }
    }

    public enum ArchiveError: Error, Equatable {
        /// Not a zip, or a damaged one.
        case unreadable
        /// A zip without project.json.
        case missingProject
    }

    public static func write(_ contents: Contents) throws -> Data {
        var entries: [(String, Data)] = [("project.json", contents.projectJSON)]
        if let o = contents.original { entries.append(("Original/" + safeName(o.name), o.data)) }
        for name in contents.audio.keys.sorted() { entries.append(("Audio/" + safeName(name), contents.audio[name]!)) }
        return try ZipFile.make(entries)
    }

    public static func read(_ data: Data) throws -> Contents {
        let files = try ZipFile.entries(in: data)
        guard let json = files["project.json"] else { throw ArchiveError.missingProject }
        var contents = Contents(projectJSON: json)
        for (path, d) in files {
            if path.hasPrefix("Original/"), contents.original == nil {
                contents.original = .init(name: String(path.dropFirst("Original/".count)), data: d)
            } else if path.hasPrefix("Audio/") {
                contents.audio[String(path.dropFirst("Audio/".count))] = d
            }
        }
        return contents
    }

    /// True for data that starts like a zip archive.
    public static func looksLikeArchive(_ data: Data) -> Bool {
        data.count >= 4 && data.prefix(4) == Data([0x50, 0x4B, 0x03, 0x04])
    }

    /// File names inside the archive never climb out of their folder.
    static func safeName(_ name: String) -> String {
        let last = (name as NSString).lastPathComponent
        return last.isEmpty || last == "." || last == ".." ? "file" : last
    }
}

/// A small zip reader and writer: stored or deflated entries, UTF-8 names, no zip64
/// (a project is far below 4 GB). Deflate comes from Apple's Compression framework,
/// whose ZLIB format is the raw DEFLATE stream zip uses.
enum ZipFile {
    static func make(_ entries: [(String, Data)]) throws -> Data {
        var out = Data()
        var central = Data()
        let (time, date) = dosDateTime(Date())
        for (name, data) in entries {
            let nameBytes = Data(name.utf8)
            let crc = CRC32.checksum(data)
            var method: UInt16 = 0
            var payload = data
            if let packed = deflate(data), packed.count < data.count {
                method = 8
                payload = packed
            }
            guard out.count < Int(UInt32.max), payload.count < Int(UInt32.max) else { throw ProjectArchive.ArchiveError.unreadable }
            let offset = UInt32(out.count)
            // Local file header.
            out.append(le32: 0x0403_4B50)
            out.append(le16: 20)
            out.append(le16: 0x0800)            // names are UTF-8
            out.append(le16: method)
            out.append(le16: time)
            out.append(le16: date)
            out.append(le32: crc)
            out.append(le32: UInt32(payload.count))
            out.append(le32: UInt32(data.count))
            out.append(le16: UInt16(nameBytes.count))
            out.append(le16: 0)
            out.append(nameBytes)
            out.append(payload)
            // Central directory entry.
            central.append(le32: 0x0201_4B50)
            central.append(le16: 20)
            central.append(le16: 20)
            central.append(le16: 0x0800)
            central.append(le16: method)
            central.append(le16: time)
            central.append(le16: date)
            central.append(le32: crc)
            central.append(le32: UInt32(payload.count))
            central.append(le32: UInt32(data.count))
            central.append(le16: UInt16(nameBytes.count))
            central.append(le16: 0)             // extra
            central.append(le16: 0)             // comment
            central.append(le16: 0)             // disk
            central.append(le16: 0)             // internal attributes
            central.append(le32: 0)             // external attributes
            central.append(le32: offset)
            central.append(nameBytes)
        }
        let centralOffset = UInt32(out.count)
        out.append(central)
        out.append(le32: 0x0605_4B50)
        out.append(le16: 0)
        out.append(le16: 0)
        out.append(le16: UInt16(entries.count))
        out.append(le16: UInt16(entries.count))
        out.append(le32: UInt32(central.count))
        out.append(le32: centralOffset)
        out.append(le16: 0)
        return out
    }

    static func entries(in data: Data) throws -> [String: Data] {
        let bytes = [UInt8](data)
        let bad = ProjectArchive.ArchiveError.unreadable
        guard bytes.count >= 22 else { throw bad }
        // The end-of-central-directory record sits in the last 22 bytes plus any comment.
        var eocd = -1
        var i = bytes.count - 22
        let floor = max(0, bytes.count - 22 - 0xFFFF)
        while i >= floor {
            if bytes.le32(at: i) == 0x0605_4B50 { eocd = i; break }
            i -= 1
        }
        guard eocd >= 0 else { throw bad }
        let count = Int(bytes.le16(at: eocd + 10))
        var p = Int(bytes.le32(at: eocd + 16))
        var files: [String: Data] = [:]
        for _ in 0..<count {
            guard p + 46 <= bytes.count, bytes.le32(at: p) == 0x0201_4B50 else { throw bad }
            let method = bytes.le16(at: p + 10)
            let crc = bytes.le32(at: p + 16)
            let packedSize = Int(bytes.le32(at: p + 20))
            let size = Int(bytes.le32(at: p + 24))
            let nameLength = Int(bytes.le16(at: p + 28))
            let extraLength = Int(bytes.le16(at: p + 30))
            let commentLength = Int(bytes.le16(at: p + 32))
            let local = Int(bytes.le32(at: p + 42))
            guard p + 46 + nameLength <= bytes.count else { throw bad }
            let name = String(decoding: bytes[(p + 46)..<(p + 46 + nameLength)], as: UTF8.self)
            p += 46 + nameLength + extraLength + commentLength

            if name.hasSuffix("/") || name.hasPrefix("__MACOSX/") { continue }
            guard local + 30 <= bytes.count, bytes.le32(at: local) == 0x0403_4B50 else { throw bad }
            let start = local + 30 + Int(bytes.le16(at: local + 26)) + Int(bytes.le16(at: local + 28))
            guard start + packedSize <= bytes.count else { throw bad }
            let packed = Data(bytes[start..<(start + packedSize)])
            let content: Data
            switch method {
            case 0: content = packed
            case 8: guard let d = inflate(packed, size: size) else { throw bad }; content = d
            default: throw bad
            }
            guard content.count == size, CRC32.checksum(content) == crc else { throw bad }
            files[name] = content
        }
        return files
    }

    static func deflate(_ data: Data) -> Data? {
        guard !data.isEmpty else { return nil }
        let capacity = data.count + 1024
        var out = Data(count: capacity)
        let n = out.withUnsafeMutableBytes { dst in
            data.withUnsafeBytes { src in
                compression_encode_buffer(dst.bindMemory(to: UInt8.self).baseAddress!, capacity,
                                          src.bindMemory(to: UInt8.self).baseAddress!, data.count,
                                          nil, COMPRESSION_ZLIB)
            }
        }
        guard n > 0 else { return nil }
        out.count = n
        return out
    }

    private static func inflate(_ data: Data, size: Int) -> Data? {
        if size == 0 { return Data() }
        guard !data.isEmpty else { return nil }
        var out = Data(count: size)
        let n = out.withUnsafeMutableBytes { dst in
            data.withUnsafeBytes { src in
                compression_decode_buffer(dst.bindMemory(to: UInt8.self).baseAddress!, size,
                                          src.bindMemory(to: UInt8.self).baseAddress!, data.count,
                                          nil, COMPRESSION_ZLIB)
            }
        }
        return n == size ? out : nil
    }

    private static func dosDateTime(_ date: Date) -> (UInt16, UInt16) {
        let c = Calendar(identifier: .gregorian).dateComponents([.year, .month, .day, .hour, .minute, .second], from: date)
        let time = UInt16((c.hour ?? 0) << 11 | (c.minute ?? 0) << 5 | (c.second ?? 0) / 2)
        let date = UInt16(max(0, (c.year ?? 1980) - 1980) << 9 | (c.month ?? 1) << 5 | (c.day ?? 1))
        return (time, date)
    }
}

enum CRC32 {
    private static let table: [UInt32] = (0..<256).map { i -> UInt32 in
        var c = UInt32(i)
        for _ in 0..<8 { c = (c & 1) != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1 }
        return c
    }

    static func checksum(_ data: Data) -> UInt32 {
        update(0, with: data)
    }

    /// Carries a checksum on over more data, for files read in pieces (start from 0).
    static func update(_ checksum: UInt32, with data: Data) -> UInt32 {
        var crc = checksum ^ 0xFFFF_FFFF
        data.withUnsafeBytes { raw in
            for b in raw.bindMemory(to: UInt8.self) {
                crc = table[Int((crc ^ UInt32(b)) & 0xFF)] ^ (crc >> 8)
            }
        }
        return crc ^ 0xFFFF_FFFF
    }
}

private extension Data {
    mutating func append(le16 v: UInt16) { append(contentsOf: [UInt8(v & 0xFF), UInt8(v >> 8)]) }
    mutating func append(le32 v: UInt32) { append(contentsOf: [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF), UInt8((v >> 16) & 0xFF), UInt8(v >> 24)]) }
}

private extension Array where Element == UInt8 {
    func le16(at i: Int) -> UInt16 { UInt16(self[i]) | UInt16(self[i + 1]) << 8 }
    func le32(at i: Int) -> UInt32 { UInt32(self[i]) | UInt32(self[i + 1]) << 8 | UInt32(self[i + 2]) << 16 | UInt32(self[i + 3]) << 24 }
}
