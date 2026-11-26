import Foundation

/// What a character looks like: free text in a fixed set of fields, so every profile reads
/// the same way. Not used for narration; it's for the author's character profiles and for
/// tools that draw or film the book later. Written by Build Summaries from the Appearance
/// and Age facts; each value says whether the book states it or the AI inferred it.
public struct PhysicalProfile: Codable, Hashable, Sendable {
    public enum Field: String, Codable, CodingKeyRepresentable, CaseIterable, Sendable {
        case age, height, build, hair, eyes, skin, face, marks, clothing, other

        public var title: String {
            switch self {
            case .age: "Apparent Age"
            case .height: "Height"
            case .build: "Build"
            case .hair: "Hair"
            case .eyes: "Eyes"
            case .skin: "Skin"
            case .face: "Face"
            case .marks: "Distinguishing Marks"
            case .clothing: "Typical Clothing"
            case .other: "Other"
            }
        }
    }

    /// One field's text, and whether the AI guessed it rather than read it in the book.
    public struct Value: Codable, Hashable, Sendable {
        public var text: String
        public var inferred: Bool
        public init(text: String, inferred: Bool = false) { self.text = text; self.inferred = inferred }
    }

    /// How their looks change during the story, from a chapter on.
    public struct Change: Codable, Hashable, Identifiable, Sendable {
        public var id: UUID
        /// The chapter it happens in (index into `Project.chapters`); nil: not known.
        public var chapter: Int?
        public var field: Field
        public var text: String
        public var inferred: Bool
        public init(id: UUID = UUID(), chapter: Int?, field: Field, text: String, inferred: Bool = false) {
            self.id = id; self.chapter = chapter; self.field = field; self.text = text; self.inferred = inferred
        }
    }

    /// A place where the book contradicts itself about their looks, with no change in the
    /// story to explain it.
    public struct ContinuityFlag: Codable, Hashable, Identifiable, Sendable {
        public var id: UUID
        public var field: Field
        public var text: String
        public init(id: UUID = UUID(), field: Field, text: String) { self.id = id; self.field = field; self.text = text }
    }

    /// A short prose description built from the fields.
    public var summary: String
    public var summaryInferred: Bool
    public var values: [Field: Value]
    public var changes: [Change]
    public var flags: [ContinuityFlag]

    public init(summary: String = "", summaryInferred: Bool = false, values: [Field: Value] = [:],
                changes: [Change] = [], flags: [ContinuityFlag] = []) {
        self.summary = summary
        self.summaryInferred = summaryInferred
        self.values = values
        self.changes = changes
        self.flags = flags
    }

    public var isEmpty: Bool {
        summary.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && values.values.allSatisfy { $0.text.trimmingCharacters(in: .whitespaces).isEmpty }
            && changes.isEmpty && flags.isEmpty
    }

    public func value(_ f: Field) -> Value { values[f] ?? Value(text: "") }

    public mutating func set(_ f: Field, _ v: Value) {
        if v.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { values[f] = nil } else { values[f] = v }
    }

    /// Changes in story order (unknown chapters last).
    public var orderedChanges: [Change] {
        changes.sorted { ($0.chapter ?? .max, $0.field.rawValue) < ($1.chapter ?? .max, $1.field.rawValue) }
    }

    /// Puts a freshly written profile on the character, but only while the narrator hasn't
    /// edited the last one the AI wrote (same rule as Voice Notes' generated blocks).
    /// Returns whether it changed.
    @discardableResult
    public static func put(_ fresh: PhysicalProfile, into c: inout BookCharacter) -> Bool {
        let current = c.physical ?? PhysicalProfile()
        let unedited = current.isEmpty || current == c.physicalGenerated
        guard unedited, current != fresh else { return false }
        c.physical = fresh.isEmpty ? nil : fresh
        c.physicalGenerated = c.physical
        return true
    }

    /// The narrator changed the profile since the AI last wrote it.
    public static func isEdited(_ c: BookCharacter) -> Bool {
        guard let p = c.physical, !p.isEmpty else { return false }
        return p != c.physicalGenerated
    }

    /// Markdown for the Character Profiles export.
    public func markdown(name: String, chapterTitle: (Int) -> String) -> String {
        var out: [String] = []
        let tag = " *(inferred)*"
        if !summary.trimmingCharacters(in: .whitespaces).isEmpty { out.append(summary + (summaryInferred ? tag : "")) }
        let rows = Field.allCases.compactMap { f -> String? in
            guard let v = values[f], !v.text.isEmpty else { return nil }
            return "- **\(f.title):** \(v.text)" + (v.inferred ? tag : "")
        }
        if !rows.isEmpty { out.append(rows.joined(separator: "\n")) }
        if !changes.isEmpty {
            out.append("**Changes during the story**\n" + orderedChanges.map {
                "- " + ($0.chapter.map { chapterTitle($0) + ": " } ?? "") + "\($0.field.title): \($0.text)" + ($0.inferred ? tag : "")
            }.joined(separator: "\n"))
        }
        if !flags.isEmpty {
            out.append("**Continuity to check**\n" + flags.map { "- \($0.field.title): \($0.text)" }.joined(separator: "\n"))
        }
        return out.joined(separator: "\n\n")
    }
}

/// The Character Profiles export: every character with a physical description or summary.
public enum CharacterProfiles {
    public static func markdown(_ project: Project, index: BookIndex) -> String {
        let title = { (i: Int) -> String in
            let t = project.chapters.indices.contains(i) ? project.chapters[i].title : ""
            return t.isEmpty ? "Chapter \(i + 1)" : t
        }
        var out = ["# Character Profiles"]
        let cast = project.characters.sorted {
            (($0.isMain ?? false) ? 0 : 1, -(index.stats[$0.id]?.lines ?? 0), $0.name) <
                (($1.isMain ?? false) ? 0 : 1, -(index.stats[$1.id]?.lines ?? 0), $1.name)
        }
        for c in cast {
            let other = FactKind.narratorOrder.filter { $0 != .appearance && $0 != .voice && $0 != .accent }
                .flatMap { k in c.facts.filter { $0.kind == k }.map { "- **\(k.title):** \($0.text)" } }
            guard !(c.physical?.isEmpty ?? true) || !other.isEmpty else { continue }
            var s = "## \(c.name)"
            if !c.aliases.isEmpty { s += "\n\n*Also called:* " + c.aliases.joined(separator: ", ") }
            if let p = c.physical, !p.isEmpty { s += "\n\n### Physical Description\n\n" + p.markdown(name: c.name, chapterTitle: title) }
            if !other.isEmpty { s += "\n\n### From the Summary\n\n" + other.joined(separator: "\n") }
            out.append(s)
        }
        if out.count == 1 { out.append("No character profiles yet. Run Build Summaries first.") }
        out.append("*Items marked (inferred) are the AI’s interpretation, not stated in the book.*")
        return out.joined(separator: "\n\n") + "\n"
    }
}

public enum PhysicalProfilePrompt {
    public static let system = """
    You build character profiles for an author. From what the book says about one character, \
    write how they look. Use only the facts given. Mark a value inferred when the book doesn't \
    state it outright and you are interpreting or guessing; leave a field empty rather than \
    invent something with no basis. Note real changes in the story (a haircut, a scar, aging) \
    with the chapter number, and note contradictions the story doesn't explain. Answer with JSON only.
    """

    /// The facts used: Appearance and Age, with chapter numbers, in story order.
    public static func facts(_ c: BookCharacter, project: Project, index: BookIndex) -> [(chapter: Int?, kind: FactKind, text: String, quote: String)] {
        c.facts.filter { $0.kind == .appearance || $0.kind == .age }
            .map { f in (f.sourceParagraphID.flatMap { index.positions[$0]?.chapter }, f.kind, f.text, f.quote) }
            .sorted { ($0.chapter ?? .max) < ($1.chapter ?? .max) }
    }

    /// Characters worth a profile: the book describes their looks.
    public static func wanted(_ c: BookCharacter) -> Bool { c.facts.contains { $0.kind == .appearance } }

    public static func messages(for c: BookCharacter, project: Project, index: BookIndex) -> [ChatMessage] {
        let list = facts(c, project: project, index: index).prefix(24).map { f in
            "- " + (f.chapter.map { "[chapter \($0 + 1)] " } ?? "") + "\(f.kind.title): \(f.text)"
                + (f.quote.isEmpty ? "" : " (“\(f.quote.prefix(160))”)")
        }.joined(separator: "\n")
        let user = """
        Character: \(c.name)
        What the book says:
        \(list.isEmpty ? "(nothing)" : list)

        Fields: age, height, build, hair, eyes, skin, face, marks (scars, tattoos, birthmarks), clothing (what they usually wear), other. \
        Each field is {"text":"…","inferred":true|false}; "" text if unknown. summary: two or three plain sentences describing them, \
        summaryInferred true if any of it is guessed. changes: looks that change during the story, each {"chapter":N,"field":"hair","text":"Cuts her hair short","inferred":false}. \
        flags: contradictions with no story reason, each {"field":"eyes","text":"Blue in chapter 3, green in chapter 20"}.
        Reply as {"summary":"…","summaryInferred":false,"age":{…},"height":{…},"build":{…},"hair":{…},"eyes":{…},"skin":{…},"face":{…},"marks":{…},"clothing":{…},"other":{…},"changes":[…],"flags":[…]}
        """
        return [ChatMessage(.system, system), ChatMessage(.user, user)]
    }

    public static let grammar = #"""
    root ::= "{" ws "\"summary\"" ws ":" ws long ws "," ws "\"summaryInferred\"" ws ":" ws bool ws fields ws "," ws "\"changes\"" ws ":" ws changes ws "," ws "\"flags\"" ws ":" ws flags ws "}"
    fields ::= "," ws "\"age\"" ws ":" ws value ws "," ws "\"height\"" ws ":" ws value ws "," ws "\"build\"" ws ":" ws value ws "," ws "\"hair\"" ws ":" ws value ws "," ws "\"eyes\"" ws ":" ws value ws "," ws "\"skin\"" ws ":" ws value ws "," ws "\"face\"" ws ":" ws value ws "," ws "\"marks\"" ws ":" ws value ws "," ws "\"clothing\"" ws ":" ws value ws "," ws "\"other\"" ws ":" ws value
    value ::= "{" ws "\"text\"" ws ":" ws string ws "," ws "\"inferred\"" ws ":" ws bool ws "}"
    changes ::= "[" ws ( change ( ws "," ws change ){0,5} )? ws "]"
    change ::= "{" ws "\"chapter\"" ws ":" ws [0-9]{1,3} ws "," ws "\"field\"" ws ":" ws field ws "," ws "\"text\"" ws ":" ws string ws "," ws "\"inferred\"" ws ":" ws bool ws "}"
    flags ::= "[" ws ( flag ( ws "," ws flag ){0,3} )? ws "]"
    flag ::= "{" ws "\"field\"" ws ":" ws field ws "," ws "\"text\"" ws ":" ws string ws "}"
    field ::= "\"" ( "age" | "height" | "build" | "hair" | "eyes" | "skin" | "face" | "marks" | "clothing" | "other" ) "\""
    bool ::= "true" | "false"
    string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] ){0,120} "\""
    long ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] ){0,400} "\""
    ws ::= [ ]?
    """#

    private struct Reply: Decodable {
        struct V: Decodable { var text: String; var inferred: Bool? }
        struct C: Decodable { var chapter: Int?; var field: String; var text: String; var inferred: Bool? }
        struct F: Decodable { var field: String; var text: String }
        var summary: String?
        var summaryInferred: Bool?
        var age, height, build, hair, eyes, skin, face, marks, clothing, other: V?
        var changes: [C]?
        var flags: [F]?
    }

    /// `chapters`: how many chapters the book has (a chapter number past it is dropped).
    public static func parse(_ text: String, chapters: Int) -> PhysicalProfile? {
        guard let json = JSONText.extract(text), let r = try? JSONDecoder().decode(Reply.self, from: Data(json.utf8)) else { return nil }
        func clean(_ s: String) -> String { s.trimmingCharacters(in: .whitespacesAndNewlines) }
        var p = PhysicalProfile(summary: clean(r.summary ?? ""), summaryInferred: r.summaryInferred ?? false)
        let pairs: [(PhysicalProfile.Field, Reply.V?)] = [(.age, r.age), (.height, r.height), (.build, r.build), (.hair, r.hair), (.eyes, r.eyes),
                                                          (.skin, r.skin), (.face, r.face), (.marks, r.marks), (.clothing, r.clothing), (.other, r.other)]
        for (f, v) in pairs {
            guard let v else { continue }
            let t = clean(v.text)
            if t.isEmpty || ["unknown", "n/a", "none", "not stated"].contains(t.lowercased()) { continue }
            p.set(f, .init(text: VoiceNotesFromBook.capitalizedFirst(t), inferred: v.inferred ?? false))
        }
        p.changes = (r.changes ?? []).compactMap { c in
            guard let f = PhysicalProfile.Field(rawValue: c.field), !clean(c.text).isEmpty else { return nil }
            let ch = c.chapter.flatMap { $0 >= 1 && $0 <= chapters ? $0 - 1 : nil }
            return .init(chapter: ch, field: f, text: VoiceNotesFromBook.capitalizedFirst(clean(c.text)), inferred: c.inferred ?? false)
        }
        p.flags = (r.flags ?? []).compactMap { c in
            guard let f = PhysicalProfile.Field(rawValue: c.field), !clean(c.text).isEmpty else { return nil }
            return .init(field: f, text: VoiceNotesFromBook.capitalizedFirst(clean(c.text)))
        }
        return p.isEmpty ? nil : p
    }
}
