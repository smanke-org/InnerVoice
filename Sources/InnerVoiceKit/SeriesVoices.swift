import Foundation

/// Voices carried over from an earlier book in a series: each voiced character in that book
/// is matched to a character in this one (the narrator confirms or changes every match), and
/// the matched characters get the same voices, so the series sounds the same book to book.
public enum SeriesVoices {
    /// How a match was found, so the sheet can say how sure it is.
    public enum How: String, Sendable {
        /// The same name, or a name one of them also goes by.
        case name
        /// Only the first name is the same (and only one character here has it).
        case firstName
        /// Chosen by the narrator.
        case chosen
    }

    public struct Match: Hashable, Sendable, Identifiable {
        /// The character in the earlier book.
        public var source: UUID
        /// Who they are in this book; nil leaves them out.
        public var target: UUID?
        public var how: How?
        public var id: UUID { source }

        public init(source: UUID, target: UUID?, how: How?) {
            self.source = source
            self.target = target
            self.how = how
        }
    }

    public struct Options: Sendable {
        /// Bring each character's Voice Notes too: casting and ElevenLabs' Voice Design read
        /// them, so the characters come out the same as in the earlier book.
        public var voiceNotes = true
        /// Use the earlier book's narrator voice.
        public var narrator = true
        /// Use the earlier book's voice engine.
        public var engine = true
        public init(voiceNotes: Bool = true, narrator: Bool = true, engine: Bool = true) {
            self.voiceNotes = voiceNotes
            self.narrator = narrator
            self.engine = engine
        }
    }

    /// Characters in the earlier book that have a voice on any engine, most lines first.
    public static func voiced(_ old: Project, index: BookIndex) -> [BookCharacter] {
        old.characters.filter { !($0.voices ?? [:]).isEmpty }
            .sorted { (index.stats[$0.id]?.lines ?? 0, $1.name) > (index.stats[$1.id]?.lines ?? 0, $0.name) }
    }

    /// The best guess for each voiced character: who they are in this book. Main characters
    /// choose first, and nobody here is matched twice.
    public static func suggest(from old: Project, oldIndex: BookIndex, to new: Project) -> [Match] {
        var taken = Set<UUID>()
        return voiced(old, index: oldIndex).map { o in
            let free = new.characters.filter { !taken.contains($0.id) }
            if let n = free.first(where: { sameName(o, $0) }) {
                taken.insert(n.id)
                return Match(source: o.id, target: n.id, how: .name)
            }
            let first = firstName(o)
            let sameFirst = first.map { f in new.characters.filter { firstName($0) == f } } ?? []
            if sameFirst.count == 1, let n = sameFirst.first, !taken.contains(n.id) {
                taken.insert(n.id)
                return Match(source: o.id, target: n.id, how: .firstName)
            }
            return Match(source: o.id, target: nil, how: nil)
        }
    }

    static func key(_ s: String) -> String {
        s.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The same name, or a name one of them also goes by ("Mara Quinn" and "Mara", when the
    /// later book lists "Mara" as an alias).
    static func sameName(_ a: BookCharacter, _ b: BookCharacter) -> Bool {
        let an = Set(([a.name] + a.aliases).map(key).filter { !$0.isEmpty })
        let bn = Set(([b.name] + b.aliases).map(key).filter { !$0.isEmpty })
        return !an.isDisjoint(with: bn)
    }

    static func firstName(_ c: BookCharacter) -> String? {
        c.name.split(separator: " ").map(String.init).first { $0.count > 1 && !Names.titles.contains($0) }.map(key)
    }

    /// Gives the matched characters their voices from the earlier book, as the narrator's own
    /// choices (casting never replaces them). Returns how many characters got voices.
    @discardableResult
    public static func apply(_ matches: [Match], from old: Project, to new: inout Project, options: Options) -> Int {
        var count = 0
        for m in matches {
            guard let target = m.target, let o = old.character(m.source), let i = new.characterIndex(target),
                  let voices = o.voices, !voices.isEmpty else { continue }
            for (engine, v) in voices {
                guard let e = VoiceEngine(rawValue: engine) else { continue }
                new.characters[i].setVoice(CastVoice(choice: v.choice, confidence: 1, source: .user), for: e)
            }
            if options.voiceNotes, !o.voiceNotes.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                new.characters[i].voiceNotes = o.voiceNotes
                // Theirs now: Build Summaries won't rewrite these blocks for the new book.
                new.characters[i].voiceNotesFromBook = nil
                new.characters[i].voiceBriefGenerated = nil
            }
            count += 1
        }
        if options.narrator {
            for (engine, v) in old.settings.narratorVoices ?? [:] {
                guard let e = VoiceEngine(rawValue: engine) else { continue }
                new.settings.setNarratorVoice(CastVoice(choice: v.choice, confidence: 1, source: .user), for: e)
            }
        }
        if options.engine, let e = old.settings.voiceEngine { new.settings.voiceEngine = e }
        return count
    }
}
