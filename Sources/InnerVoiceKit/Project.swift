import Foundation

/// A range of UTF-16 code units inside one paragraph's text, so it maps straight onto
/// `NSString` and `NSTextView` ranges.
public struct TextSpan: Codable, Hashable, Sendable {
    public var location: Int
    public var length: Int
    public init(location: Int, length: Int) {
        self.location = location
        self.length = length
    }
    public init(_ range: NSRange) { self.init(location: range.location, length: range.length) }
    public var ns: NSRange { NSRange(location: location, length: length) }
    public var end: Int { location + length }
}

/// One block of text in a chapter.
public struct Paragraph: Codable, Identifiable, Hashable, Sendable {
    public enum Style: String, Codable, Sendable { case body, heading, sceneBreak, blockQuote }
    public var id: UUID
    public var text: String
    public var style: Style
    /// Italic runs, used to find thoughts.
    public var italic: [TextSpan]

    public init(id: UUID = UUID(), text: String, style: Style = .body, italic: [TextSpan] = []) {
        self.id = id
        self.text = text
        self.style = style
        self.italic = italic
    }

    public func substring(_ range: TextSpan) -> String {
        let ns = text as NSString
        let safe = NSIntersectionRange(range.ns, NSRange(location: 0, length: ns.length))
        return ns.substring(with: safe)
    }

    public var wordCount: Int { Words.count(text) }
}

/// A section of the manuscript that starts at a heading.
public struct Chapter: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var title: String
    public var paragraphs: [Paragraph]

    public init(id: UUID = UUID(), title: String, paragraphs: [Paragraph]) {
        self.id = id
        self.title = title
        self.paragraphs = paragraphs
    }

    public var wordCount: Int { paragraphs.reduce(0) { $0 + $1.wordCount } }
}

public enum LineKind: String, Codable, CaseIterable, Sendable {
    case spoken, thought, written

    public var title: String {
        switch self {
        case .spoken: "Spoken"
        case .thought: "Thought"
        case .written: "Written"
        }
    }
}

/// Who decided a line's speaker. Lines the narrator assigned are never changed by the AI.
public enum AssignmentSource: String, Codable, Sendable {
    case none, rule, exchange, ai, user

    public var title: String {
        switch self {
        case .none: "Not assigned"
        case .rule: "Found from “said …”"
        case .exchange: "Back-and-forth"
        case .ai: "Assigned by the AI"
        case .user: "Assigned by you"
        }
    }
}

/// One piece of text spoken, thought or written by a character.
public struct Line: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var paragraphID: UUID
    public var range: TextSpan
    public var kind: LineKind
    public var speakerID: UUID?
    /// 0…1. Lines below the project's threshold show as Unsure.
    public var confidence: Double
    public var source: AssignmentSource
    /// A quote that carries on from the previous paragraph (no closing mark there).
    public var continuesPrevious: Bool
    /// How the line is said (for character voices). nil until the rules or the AI read it.
    /// Optional, like the two below, so projects saved before tones open unchanged.
    public var tone: Tone?
    /// 0…1, like `confidence`. Below the project's threshold the tone is Unsure.
    public var toneConfidence: Double?
    public var toneSource: ToneSource?
    /// The AI model that tagged the line (for its accuracy in Activity). Optional and set
    /// only on AI lines.
    public var aiModel: String?

    public init(id: UUID = UUID(), paragraphID: UUID, range: TextSpan, kind: LineKind = .spoken,
                speakerID: UUID? = nil, confidence: Double = 0, source: AssignmentSource = .none,
                continuesPrevious: Bool = false, tone: Tone? = nil, toneConfidence: Double? = nil,
                toneSource: ToneSource? = nil) {
        self.id = id
        self.paragraphID = paragraphID
        self.range = range
        self.kind = kind
        self.speakerID = speakerID
        self.confidence = confidence
        self.source = source
        self.continuesPrevious = continuesPrevious
        self.tone = tone
        self.toneConfidence = toneConfidence
        self.toneSource = toneSource
    }

    public var isConfirmed: Bool { source == .user }
}

public enum FactKind: String, Codable, CaseIterable, Sendable {
    case age, gender, accent, voice, appearance, personality, history, relationships, other

    public var title: String {
        switch self {
        case .age: "Age"
        case .gender: "Gender"
        case .accent: "Accent & Origin"
        case .voice: "Voice"
        case .appearance: "Appearance"
        case .personality: "Personality"
        case .history: "History"
        case .relationships: "Relationships"
        case .other: "Other"
        }
    }

    /// The facts a narrator needs before the first line come first.
    public static var narratorOrder: [FactKind] { [.age, .gender, .accent, .voice, .personality, .appearance, .history, .relationships, .other] }
}

/// One item in a character's summary, with the paragraph it came from.
public struct Fact: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var kind: FactKind
    public var text: String
    public var sourceParagraphID: UUID?
    /// The words in the manuscript that support the fact.
    public var quote: String
    public var fromAI: Bool
    /// The narrator changed or wrote it; the AI never replaces it.
    public var edited: Bool

    public init(id: UUID = UUID(), kind: FactKind, text: String, sourceParagraphID: UUID? = nil,
                quote: String = "", fromAI: Bool = false, edited: Bool = false) {
        self.id = id
        self.kind = kind
        self.text = text
        self.sourceParagraphID = sourceParagraphID
        self.quote = quote
        self.fromAI = fromAI
        self.edited = edited
    }
}

/// Someone in the book who has lines.
public struct BookCharacter: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var aliases: [String]
    /// "#RRGGBB" from `CharacterPalette`.
    public var colorHex: String
    public var facts: [Fact]
    public var notes: String
    /// How the narrator plans to voice the character.
    public var voiceNotes: String
    public var narratorID: UUID?
    /// nil follows the automatic rule (main characters start hidden in the index).
    public var showInIndex: Bool?
    /// Pinned to the top of every speaker list (Assign To, the right-click menu).
    /// Optional so projects saved before pins open unchanged.
    public var pinned: Bool?
    /// The voice for each speech engine, keyed by `VoiceEngine.rawValue`, so switching
    /// engines keeps the casting done for each.
    public var voices: [String: CastVoice]?
    /// The "From the book:" block Build Summary last wrote into `voiceNotes`, so a later run
    /// can replace it, but only while the narrator hasn't edited it.
    public var voiceNotesFromBook: String?

    public var isPinned: Bool { pinned ?? false }

    public func voice(for engine: VoiceEngine) -> CastVoice? { voices?[engine.rawValue] }

    public mutating func setVoice(_ voice: CastVoice?, for engine: VoiceEngine) {
        var all = voices ?? [:]
        all[engine.rawValue] = voice
        voices = all.isEmpty ? nil : all
    }

    public init(id: UUID = UUID(), name: String, aliases: [String] = [], colorHex: String,
                facts: [Fact] = [], notes: String = "", voiceNotes: String = "",
                narratorID: UUID? = nil, showInIndex: Bool? = nil, pinned: Bool? = nil) {
        self.id = id
        self.name = name
        self.aliases = aliases
        self.colorHex = colorHex
        self.facts = facts
        self.notes = notes
        self.voiceNotes = voiceNotes
        self.narratorID = narratorID
        self.showInIndex = showInIndex
        self.pinned = pinned
    }

    /// True when a search typed in a speaker list finds this character: any of its names
    /// contains the text (ignoring case and accents), or the text starts its initials.
    public func matches(_ query: String) -> Bool {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return true }
        let options: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive]
        if ([name] + aliases).contains(where: { $0.range(of: q, options: options) != nil }) { return true }
        return q.count <= 3 && initials.range(of: q, options: [.caseInsensitive, .anchored]) != nil
    }

    /// Every name the character goes by, longest first, for matching in text.
    public var allNames: [String] {
        var names = [name] + aliases
        let parts = name.split(separator: " ").map(String.init).filter { $0.count > 1 && !Names.titles.contains($0) }
        if parts.count > 1 { names += parts }
        return Array(Set(names.filter { !$0.isEmpty })).sorted { $0.count > $1.count }
    }

    public var initials: String {
        let parts = name.split(separator: " ").filter { !Names.titles.contains(String($0)) }
        let letters = parts.prefix(2).compactMap(\.first).map(String.init)
        return letters.isEmpty ? String(name.prefix(2)) : letters.joined().uppercased()
    }

    /// The first name, skipping titles: "Dr. Edda Voss" is Edda. A title on its own
    /// ("Captain") or with only a surname ("Mr. Hale") keeps the whole name.
    public var firstName: String {
        let parts = name.split(separator: " ").map(String.init)
        guard let first = parts.first else { return name }
        if Names.titles.contains(first) { return parts.count > 2 ? parts[1] : name }
        return first
    }
}

/// A person reading the audiobook.
public struct Narrator: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var notes: String
    public init(id: UUID = UUID(), name: String, notes: String = "") {
        self.id = id
        self.name = name
        self.notes = notes
    }
}

/// A term and how to say it.
public struct Pronunciation: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var term: String
    public var sayItAs: String
    public var notes: String
    /// Checked with the author or rights holder.
    public var confirmed: Bool
    /// File name inside the project's Audio folder.
    public var audioFile: String?
    public var occurrences: Int
    public var firstParagraphID: UUID?
    /// Found automatically; hidden once the narrator says it needs no entry.
    public var dismissed: Bool
    public var addedByUser: Bool

    public init(id: UUID = UUID(), term: String, sayItAs: String = "", notes: String = "", confirmed: Bool = false,
                audioFile: String? = nil, occurrences: Int = 0, firstParagraphID: UUID? = nil,
                dismissed: Bool = false, addedByUser: Bool = false) {
        self.id = id
        self.term = term
        self.sayItAs = sayItAs
        self.notes = notes
        self.confirmed = confirmed
        self.audioFile = audioFile
        self.occurrences = occurrences
        self.firstParagraphID = firstParagraphID
        self.dismissed = dismissed
        self.addedByUser = addedByUser
    }
}

/// A paragraph marked to read again.
public struct Retake: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var paragraphID: UUID
    public var note: String
    public var created: Date
    public var done: Bool
    public init(id: UUID = UUID(), paragraphID: UUID, note: String = "", created: Date = .now, done: Bool = false) {
        self.id = id
        self.paragraphID = paragraphID
        self.note = note
        self.created = created
        self.done = done
    }
}

public enum ColorStyle: String, Codable, CaseIterable, Sendable {
    case highlight, outline, underline
    public var title: String {
        switch self {
        case .highlight: "Highlight"
        case .outline: "Outline"
        case .underline: "Underline"
        }
    }
}

/// What a project is for. Each shows its own pages and commands; switching hides the other
/// type's features without deleting anything.
public enum ProjectPurpose: String, Codable, CaseIterable, Identifiable, Sendable {
    /// A narrator preparing to read the book: who's speaking, cues, a teleprompter, scripts.
    case narration
    /// A writer producing an audiobook with AI voices instead of a human narrator.
    case aiVoices

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .narration: "Narration Tools"
        case .aiVoices: "Produce with AI Voices"
        }
    }

    public var detail: String {
        switch self {
        case .narration: "You’ll narrate the book yourself. See who’s speaking on every line, with character summaries, an index of each part, rehearsal, a teleprompter for the booth, retakes and narrator scripts."
        case .aiVoices: "You’ll make the audiobook with AI voices. Give every character a voice and a tone, hear a table read, and export the audio, with no human narrator."
        }
    }

    public var symbol: String {
        switch self {
        case .narration: "mic"
        case .aiVoices: "person.wave.2"
        }
    }
}

public struct ProjectSettings: Codable, Hashable, Sendable {
    public var style: ColorStyle = .highlight
    /// Lines the AI is less sure of than this show as Unsure.
    public var unsureBelow: Double = 0.6
    public var showInitials = true
    /// The point-of-view character in a first-person book ("I said").
    public var povCharacterID: UUID?
    /// Color narration too (for a first-person narrator voice).
    public var colorNarration = false
    /// The speech engine the project's voices use. nil is Apple's voices.
    public var voiceEngine: VoiceEngine?
    /// The voice that reads narration, per engine (like `BookCharacter.voices`).
    public var narratorVoices: [String: CastVoice]?
    /// What the project is for: narration tools or producing with AI voices. nil until asked
    /// (projects made before the question existed are asked once when opened).
    public var purpose: ProjectPurpose?
    /// When the narrator allowed this book's text to be sent to a cloud voice engine
    /// (ElevenLabs). nil: never sent.
    public var cloudVoicesAllowed: Date?
    public init() {}

    public var engine: VoiceEngine { voiceEngine ?? .apple }

    public func narratorVoice(for engine: VoiceEngine) -> CastVoice? { narratorVoices?[engine.rawValue] }

    public mutating func setNarratorVoice(_ voice: CastVoice?, for engine: VoiceEngine) {
        var all = narratorVoices ?? [:]
        all[engine.rawValue] = voice
        narratorVoices = all.isEmpty ? nil : all
    }
}

/// What happened in the last revision import, for the review list.
public struct RevisionRecord: Codable, Hashable, Sendable, Identifiable {
    public struct Removed: Codable, Hashable, Sendable {
        public var text: String
        public var chapterTitle: String
        public init(text: String, chapterTitle: String) {
            self.text = text
            self.chapterTitle = chapterTitle
        }
    }
    public var id: UUID
    public var date: Date
    public var fileName: String
    public var added: [UUID]
    public var changed: [UUID]
    public var removed: [Removed]
    public var unchanged: Int
    /// Paragraphs the narrator has looked at in the review list.
    public var reviewed: Set<UUID>

    public init(id: UUID = UUID(), date: Date = .now, fileName: String, added: [UUID], changed: [UUID],
                removed: [Removed], unchanged: Int, reviewed: Set<UUID> = []) {
        self.id = id
        self.date = date
        self.fileName = fileName
        self.added = added
        self.changed = changed
        self.removed = removed
        self.unchanged = unchanged
        self.reviewed = reviewed
    }
}

/// One book: the manuscript, its lines and cast, and the narrator's notes.
public struct Project: Codable, Hashable, Sendable {
    public var format = 1
    public var title: String
    public var author: String
    public var sourceFileName: String
    public var importedAt: Date
    public var chapters: [Chapter]
    /// Every line, in book order.
    public var lines: [Line]
    public var characters: [BookCharacter]
    public var narrators: [Narrator]
    public var pronunciations: [Pronunciation]
    /// Paragraphs read in Record.
    public var recorded: Set<UUID>
    public var retakes: [Retake]
    public var settings: ProjectSettings
    public var summariesBuilt: Date?
    public var lastTagged: Date?
    public var revisions: [RevisionRecord]
    /// When Tag Speakers last read the lines' tones, and when voices were last cast.
    public var tonesTagged: Date?
    public var voicesCast: Date?

    public init(title: String, author: String = "", sourceFileName: String = "", importedAt: Date = .now,
                chapters: [Chapter] = [], lines: [Line] = [], characters: [BookCharacter] = [],
                narrators: [Narrator] = [], pronunciations: [Pronunciation] = [], recorded: Set<UUID> = [],
                retakes: [Retake] = [], settings: ProjectSettings = ProjectSettings(),
                summariesBuilt: Date? = nil, lastTagged: Date? = nil, revisions: [RevisionRecord] = []) {
        self.title = title
        self.author = author
        self.sourceFileName = sourceFileName
        self.importedAt = importedAt
        self.chapters = chapters
        self.lines = lines
        self.characters = characters
        self.narrators = narrators
        self.pronunciations = pronunciations
        self.recorded = recorded
        self.retakes = retakes
        self.settings = settings
        self.summariesBuilt = summariesBuilt
        self.lastTagged = lastTagged
        self.revisions = revisions
    }

    public static let empty = Project(title: "Untitled")

    public var isEmpty: Bool { chapters.isEmpty }

    public func character(_ id: UUID?) -> BookCharacter? {
        guard let id else { return nil }
        return characters.first { $0.id == id }
    }

    public func characterIndex(_ id: UUID) -> Int? { characters.firstIndex { $0.id == id } }

    public var wordCount: Int { chapters.reduce(0) { $0 + $1.wordCount } }

    /// A line needs the narrator when nobody is assigned or the AI was unsure.
    public func needsYou(_ line: Line) -> Bool {
        line.speakerID == nil || (line.source != .user && line.confidence < settings.unsureBelow)
    }

    public var linesNeedingYou: Int { lines.reduce(0) { $0 + (needsYou($1) ? 1 : 0) } }

    /// A line's tone needs the narrator when the rules or the AI guessed it unsure.
    public func toneNeedsYou(_ line: Line) -> Bool {
        guard line.tone != nil, line.toneSource != .user else { return false }
        return (line.toneConfidence ?? 0) < settings.unsureBelow
    }

    /// A character's voice needs the narrator when casting was unsure of it, or found none.
    public func voiceNeedsYou(_ character: BookCharacter, engine: VoiceEngine? = nil) -> Bool {
        guard voicesCast != nil else { return false }
        guard let v = character.voice(for: engine ?? settings.engine) else { return true }
        return v.source != .user && v.confidence < settings.unsureBelow
    }

    /// Lines without any assignment yet (what Tag Speakers still has to do).
    public var untaggedCount: Int { lines.reduce(0) { $0 + ($1.source == .none ? 1 : 0) } }
}

/// Counting words the same way everywhere.
public enum Words {
    public static func count(_ text: String) -> Int {
        var n = 0
        var inWord = false
        for scalar in text.unicodeScalars {
            let letter = CharacterSet.alphanumerics.contains(scalar) || scalar == "’" || scalar == "'"
            if letter, !inWord { n += 1 }
            inWord = letter
        }
        return n
    }
}

/// Name helpers shared by rules, prompts and initials.
public enum Names {
    public static let titles: Set<String> = [
        "Mr.", "Mrs.", "Ms.", "Miss", "Mister", "Dr.", "Mr", "Mrs", "Ms", "Dr", "Lady", "Lord", "Sir", "Dame", "Captain", "Capt.",
        "Aunt", "Uncle", "Professor", "Prof.", "Father", "Mother", "Sister", "Brother", "King", "Queen", "Prince",
        "Princess", "Detective", "Inspector", "Sergeant", "Officer", "Agent", "Madam", "Madame", "Señor", "Señora",
        "General", "Colonel", "Major", "Lieutenant", "Admiral", "Doctor", "Granny", "Grandpa", "Grandma", "Saint",
    ]

    /// Capitalized words that start sentences or name things but are never speakers.
    public static let notNames: Set<String> = [
        "I", "He", "She", "They", "We", "You", "It", "The", "A", "An", "Then", "But", "And", "Or", "So", "Yes", "No",
        "Oh", "Well", "This", "That", "There", "Here", "What", "Who", "Why", "When", "Where", "How", "His", "Her",
        "Their", "Our", "My", "Your", "Its", "Someone", "Somebody", "Everyone", "Nobody", "Everybody", "Anyone",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "God", "Chapter", "Part",
        "Now", "Still", "Just", "After", "Before", "As", "If", "Once", "Even", "Another", "One", "Each", "Both",
        "Something", "Nothing", "All", "Some", "Most", "Not", "Again", "Finally", "Instead", "Later", "Soon",
    ]
}
