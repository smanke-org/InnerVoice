import Foundation
import Synchronization

/// Two pronunciation lists, and only two:
/// - **This Book** (`Project.pronunciations`): the book's own entries, saved in the project.
/// - **This Mac** / **This PC** (`PronunciationList`): one list for the whole computer (on the
///   Mac it follows the narrator's other Macs with their settings), used by every book that
///   has "Use This Mac's Pronunciations" on (`ProjectSettings.usesComputerPronunciations`,
///   on unless turned off).
///
/// A book's own entry always wins over the computer's for the same word. Each book keeps a
/// copy of the computer entries it used (`Project.computerPronunciations`, refreshed every
/// time it's saved), so it sounds the same on another computer: there, the copy fills in the
/// words that computer's list doesn't have. On the computer the copy came from, the live list
/// is the truth: a word deleted there is gone, never brought back from a book's copy (each
/// entry remembers the list it came from, `ListedPronunciation.list`).
///
/// The Mac and Windows apps share this format and the import/export files
/// (`PronunciationFile`).
public struct ListedPronunciation: Codable, Hashable, Identifiable, Sendable {
    public var id: UUID
    public var term: String
    public var sayItAs: String
    public var notes: String
    public var matchCase: Bool
    /// In a book's copy: the `PronunciationList.listID` the entry came from.
    public var list: String?

    public init(id: UUID = UUID(), term: String, sayItAs: String, notes: String = "", matchCase: Bool = false, list: String? = nil) {
        self.id = id
        self.term = term
        self.sayItAs = sayItAs
        self.notes = notes
        self.matchCase = matchCase
        self.list = list
    }

    enum CodingKeys: String, CodingKey { case id, term, sayItAs, notes, matchCase, list }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        term = try c.decode(String.self, forKey: .term)
        sayItAs = try c.decodeIfPresent(String.self, forKey: .sayItAs) ?? ""
        notes = try c.decodeIfPresent(String.self, forKey: .notes) ?? ""
        matchCase = try c.decodeIfPresent(Bool.self, forKey: .matchCase) ?? false
        list = try c.decodeIfPresent(String.self, forKey: .list)
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(term, forKey: .term)
        try c.encode(sayItAs, forKey: .sayItAs)
        if !notes.isEmpty { try c.encode(notes, forKey: .notes) }
        if matchCase { try c.encode(true, forKey: .matchCase) }
        try c.encodeIfPresent(list, forKey: .list)
    }

    /// The word as a lookup key: trimmed, any case.
    public var key: String { Self.key(term) }
    public static func key(_ term: String) -> String { term.trimmingCharacters(in: .whitespaces).lowercased() }

    /// Something to say: a word and how to say it.
    public var isUsable: Bool { !key.isEmpty && !sayItAs.trimmingCharacters(in: .whitespaces).isEmpty }

    public var saying: SpeechScript.Saying {
        SpeechScript.Saying(term.trimmingCharacters(in: .whitespaces), sayItAs.trimmingCharacters(in: .whitespaces), matchCase: matchCase)
    }
}

/// The computer's list. `listID` names it (made once; on the Mac it travels with the list
/// through settings sync, so Macs that share settings share one list).
public struct PronunciationList: Codable, Hashable, Sendable {
    public var listID: String
    public var entries: [ListedPronunciation]

    public init(listID: String = UUID().uuidString, entries: [ListedPronunciation] = []) {
        self.listID = listID
        self.entries = entries
    }

    public func entry(_ term: String) -> ListedPronunciation? {
        let k = ListedPronunciation.key(term)
        return entries.first { $0.key == k }
    }

    /// Sorted for showing: A to Z, any case.
    public var sorted: [ListedPronunciation] {
        entries.sorted { $0.term.localizedCaseInsensitiveCompare($1.term) == .orderedAscending }
    }
}

/// Where a word's pronunciation comes from, for the Pronunciations page.
public enum PronunciationSource: String, Sendable {
    /// The book's own entry.
    case book
    /// The computer's list.
    case computer
    /// The book's copy of another computer's list (this computer's list doesn't have it).
    case bookCopy
}

/// What to do with a word both lists (or an import and the list) have.
public enum PronunciationConflict: String, Sendable, CaseIterable {
    /// Keep what's there.
    case keepExisting
    /// Use the one coming in.
    case replace
}

public enum PronunciationLists {
    /// The computer's list as the app has it now; nil before the app set it (Kit tests) or
    /// when a test wants none. The app sets it at launch and whenever the list changes, so
    /// `SpeechScript.sayings` and saving read it without reaching into the app.
    private static let current = Mutex<PronunciationList?>(nil)

    public static var computer: PronunciationList? {
        get { current.withLock { $0 } }
        set { current.withLock { $0 = newValue } }
    }

    /// The computer entries a book uses, in order of what wins: this computer's list, then
    /// the book's copy of another computer's words that this one doesn't have. Empty when the
    /// book has the computer's list turned off.
    public static func computerEntries(_ project: Project, computer: PronunciationList?) -> [(ListedPronunciation, PronunciationSource)] {
        guard project.settings.usesComputerPronunciations else { return [] }
        var out: [(ListedPronunciation, PronunciationSource)] = []
        var seen = Set<String>()
        for e in computer?.entries ?? [] where e.isUsable && !seen.contains(e.key) {
            seen.insert(e.key)
            out.append((e, .computer))
        }
        for e in project.computerPronunciations ?? [] where e.isUsable && !seen.contains(e.key) {
            // A word from this computer's own list that isn't in it anymore was deleted here.
            if let computer, e.list == computer.listID { continue }
            seen.insert(e.key)
            out.append((e, .bookCopy))
        }
        return out
    }

    /// The words the book's own list covers (entries with something to say).
    public static func bookKeys(_ project: Project) -> Set<String> {
        Set(project.pronunciations.filter { !$0.dismissed && !$0.sayItAs.trimmingCharacters(in: .whitespaces).isEmpty }
            .map { ListedPronunciation.key($0.term) }.filter { !$0.isEmpty })
    }

    /// Every saying the book speaks with: its own entries, then the computer's for words it
    /// doesn't have. Longest first, so "New York" is swapped before "York".
    public static func sayings(_ project: Project, computer: PronunciationList?) -> [SpeechScript.Saying] {
        let own = project.pronunciations
            .filter { !$0.dismissed && !$0.sayItAs.trimmingCharacters(in: .whitespaces).isEmpty
                && !$0.term.trimmingCharacters(in: .whitespaces).isEmpty }
            .map { SpeechScript.Saying($0.term.trimmingCharacters(in: .whitespaces), $0.sayItAs.trimmingCharacters(in: .whitespaces),
                                       matchCase: $0.matchCase) }
        let ownKeys = Set(own.map { $0.term.lowercased() })
        let shared = computerEntries(project, computer: computer).map(\.0).filter { !ownKeys.contains($0.key) }.map(\.saying)
        return (own + shared).sorted { $0.term.count > $1.term.count }
    }

    /// The copy a book saves of the computer entries it uses (nil when it uses none and never
    /// had a copy). Each entry keeps the list it came from.
    public static func copyForSaving(_ project: Project, computer: PronunciationList?) -> [ListedPronunciation]? {
        guard project.settings.usesComputerPronunciations else { return project.computerPronunciations }
        let entries = computerEntries(project, computer: computer).map { e, source -> ListedPronunciation in
            var out = e
            if source == .computer { out.list = computer?.listID }
            return out
        }
        if entries.isEmpty, project.computerPronunciations == nil { return nil }
        return entries
    }

    /// The book's entries that could go to the computer's list: with something to say.
    public static func bookEntries(_ project: Project) -> [ListedPronunciation] {
        project.pronunciations.filter { !$0.dismissed && !$0.sayItAs.trimmingCharacters(in: .whitespaces).isEmpty
            && !$0.term.trimmingCharacters(in: .whitespaces).isEmpty }
            .map { ListedPronunciation(term: $0.term.trimmingCharacters(in: .whitespaces), sayItAs: $0.sayItAs.trimmingCharacters(in: .whitespaces),
                                       notes: $0.notes, matchCase: $0.matchCase) }
    }

    /// Incoming entries that are already in the list with a different pronunciation (the
    /// same word saying the same thing isn't a conflict).
    public static func conflicts(_ incoming: [ListedPronunciation], in list: PronunciationList) -> [ListedPronunciation] {
        incoming.filter { e in
            guard let have = list.entry(e.term) else { return false }
            return have.sayItAs.trimmingCharacters(in: .whitespaces) != e.sayItAs.trimmingCharacters(in: .whitespaces)
                || have.matchCase != e.matchCase
        }
    }

    public struct MergeResult: Equatable, Sendable {
        public var added = 0
        public var replaced = 0
        public var kept = 0
        public var unchanged = 0
        public init(added: Int = 0, replaced: Int = 0, kept: Int = 0, unchanged: Int = 0) {
            self.added = added; self.replaced = replaced; self.kept = kept; self.unchanged = unchanged
        }
    }

    /// Adds entries to the list. Words it already has are kept or replaced as `conflict` says;
    /// the same entry again changes nothing. Entries without a word or a pronunciation are
    /// skipped.
    @discardableResult
    public static func merge(_ incoming: [ListedPronunciation], into list: inout PronunciationList, conflict: PronunciationConflict) -> MergeResult {
        var r = MergeResult()
        var seen = Set<String>()
        for var e in incoming where e.isUsable && !seen.contains(e.key) {
            seen.insert(e.key)
            e.list = nil
            e.term = e.term.trimmingCharacters(in: .whitespaces)
            e.sayItAs = e.sayItAs.trimmingCharacters(in: .whitespaces)
            if let i = list.entries.firstIndex(where: { $0.key == e.key }) {
                let have = list.entries[i]
                if have.sayItAs == e.sayItAs, have.matchCase == e.matchCase {
                    if have.notes.isEmpty, !e.notes.isEmpty { list.entries[i].notes = e.notes }
                    r.unchanged += 1
                } else if conflict == .replace {
                    e.id = have.id
                    list.entries[i] = e
                    r.replaced += 1
                } else {
                    r.kept += 1
                }
            } else {
                e.id = UUID()
                list.entries.append(e)
                r.added += 1
            }
        }
        return r
    }
}

/// Importing and exporting the computer's list.
/// - CSV (export and import): `Word,Say It As,Match Case,Notes`, a header row first, quoted
///   as CSV does; opens in Numbers and Excel.
/// - Text (import): one `word = how to say it` a line (also `→`, `->`, a tab); `#` starts a
///   comment.
/// - JSON (export and import): `{"format":"InnerVoice Pronunciations","version":1,"entries":[…]}`,
///   or a bare array of entries.
public enum PronunciationFile {
    public enum Format: String, CaseIterable, Sendable {
        case csv, json
        public var fileExtension: String { rawValue }
    }

    public enum ReadError: Error, LocalizedError, Equatable {
        case empty
        case unreadable(String)
        public var errorDescription: String? {
            switch self {
            case .empty: "The file has no pronunciations in it."
            case .unreadable(let why): "InnerVoice can’t read this file: \(why)"
            }
        }
    }

    static let formatName = "InnerVoice Pronunciations"

    struct JSONFile: Codable {
        var format: String
        var version: Int
        var entries: [ListedPronunciation]
    }

    public static func export(_ entries: [ListedPronunciation], as format: Format) -> Data {
        let sorted = entries.sorted { $0.term.localizedCaseInsensitiveCompare($1.term) == .orderedAscending }
            .map { e -> ListedPronunciation in var out = e; out.list = nil; return out }
        switch format {
        case .json:
            let enc = JSONEncoder()
            enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
            return (try? enc.encode(JSONFile(format: formatName, version: 1, entries: sorted))) ?? Data()
        case .csv:
            var lines = ["Word,Say It As,Match Case,Notes"]
            for e in sorted {
                lines.append([e.term, e.sayItAs, e.matchCase ? "Yes" : "", e.notes].map(csvField).joined(separator: ","))
            }
            return Data((lines.joined(separator: "\r\n") + "\r\n").utf8)
        }
    }

    /// Reads a file by its contents (and its name's extension as a hint).
    public static func read(_ data: Data, fileName: String = "") throws -> [ListedPronunciation] {
        var text = String(decoding: data, as: UTF8.self)
        if text.hasPrefix("\u{FEFF}") { text.removeFirst() }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw ReadError.empty }
        let ext = (fileName as NSString).pathExtension.lowercased()
        let entries: [ListedPronunciation]
        if ext == "json" || trimmed.hasPrefix("{") || trimmed.hasPrefix("[") {
            let dec = JSONDecoder()
            if let file = try? dec.decode(JSONFile.self, from: Data(trimmed.utf8)) {
                entries = file.entries
            } else if let list = try? dec.decode([ListedPronunciation].self, from: Data(trimmed.utf8)) {
                entries = list
            } else if let list = try? dec.decode(PronunciationList.self, from: Data(trimmed.utf8)) {
                entries = list.entries
            } else {
                throw ReadError.unreadable("it isn’t a pronunciations file.")
            }
        } else if ext == "csv" || ext == "tsv" || looksLikeCSV(trimmed) {
            entries = readCSV(trimmed, separator: ext == "tsv" ? "\t" : ",")
        } else {
            entries = readText(trimmed)
        }
        let usable = entries.filter(\.isUsable).map { e -> ListedPronunciation in var out = e; out.list = nil; return out }
        guard !usable.isEmpty else { throw ReadError.empty }
        return usable
    }

    static func looksLikeCSV(_ text: String) -> Bool {
        let first = text.split(whereSeparator: \.isNewline).first.map(String.init)?.lowercased() ?? ""
        return first.hasPrefix("word,") || first.hasPrefix("\"word\",") || first.hasPrefix("term,")
    }

    static func csvField(_ s: String) -> String {
        guard s.contains(where: { ",\"\n\r".contains($0) }) || s.hasPrefix(" ") || s.hasSuffix(" ") else { return s }
        return "\"" + s.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    /// CSV rows, quotes and line breaks inside quotes included.
    static func csvRows(_ text: String, separator: Character) -> [[String]] {
        var rows: [[String]] = []
        var row: [String] = []
        var field = ""
        var quoted = false
        var it = Array(text).makeIterator()
        var pending: Character?
        func next() -> Character? { if let p = pending { pending = nil; return p }; return it.next() }
        while let c = next() {
            if quoted {
                if c == "\"" {
                    if let n = next() {
                        if n == "\"" { field.append("\"") } else { quoted = false; pending = n }
                    } else { quoted = false }
                } else { field.append(c) }
            } else if c == "\"" && field.isEmpty {
                quoted = true
            } else if c == separator {
                row.append(field); field = ""
            } else if c == "\n" || c == "\r" || c == "\r\n" {
                row.append(field); field = ""
                if row.contains(where: { !$0.isEmpty }) { rows.append(row) }
                row = []
            } else {
                field.append(c)
            }
        }
        row.append(field)
        if row.contains(where: { !$0.isEmpty }) { rows.append(row) }
        return rows
    }

    static func readCSV(_ text: String, separator: Character) -> [ListedPronunciation] {
        var rows = csvRows(text, separator: separator)
        guard !rows.isEmpty else { return [] }
        // Columns by the header when there is one, else Word, Say It As, Match Case, Notes.
        let header = rows[0].map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
        var col = (term: 0, say: 1, match: 2, notes: 3)
        if header.contains(where: { ["word", "term"].contains($0) }) {
            col.term = header.firstIndex { ["word", "term"].contains($0) } ?? 0
            col.say = header.firstIndex { ["say it as", "sayitas", "pronunciation", "say as", "spoken as"].contains($0) } ?? 1
            col.match = header.firstIndex { ["match case", "matchcase", "case"].contains($0) } ?? -1
            col.notes = header.firstIndex { $0 == "notes" || $0 == "note" } ?? -1
            rows.removeFirst()
        }
        func at(_ r: [String], _ i: Int) -> String { i >= 0 && i < r.count ? r[i].trimmingCharacters(in: .whitespaces) : "" }
        return rows.map { r in
            let m = at(r, col.match).lowercased()
            return ListedPronunciation(term: at(r, col.term), sayItAs: at(r, col.say), notes: at(r, col.notes),
                                       matchCase: ["yes", "true", "1", "y", "x"].contains(m))
        }
    }

    static func readText(_ text: String) -> [ListedPronunciation] {
        text.split(whereSeparator: \.isNewline).compactMap { raw -> ListedPronunciation? in
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty, !line.hasPrefix("#") else { return nil }
            for sep in [" = ", "=", " → ", "→", " -> ", "->", "\t", ": "] {
                if let r = line.range(of: sep) {
                    return ListedPronunciation(term: String(line[..<r.lowerBound]).trimmingCharacters(in: .whitespaces),
                                               sayItAs: String(line[r.upperBound...]).trimmingCharacters(in: .whitespaces))
                }
            }
            return nil
        }
    }
}
