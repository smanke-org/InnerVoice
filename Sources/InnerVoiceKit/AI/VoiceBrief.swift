import Foundation

/// A casting director's note for one character: how to pitch, pace and colour the voice,
/// and how it should differ from the characters they share scenes with. Written by the AI
/// after Build Summaries, into Voice Notes under "Voice brief:"; casting and the AI voices
/// read it, and so does the narrator.
public struct VoiceBrief: Codable, Hashable, Sendable {
    public var pitch: String
    public var pace: String
    public var texture: String
    public var attitude: String
    public var contrast: String

    public init(pitch: String, pace: String, texture: String, attitude: String, contrast: String) {
        self.pitch = pitch
        self.pace = pace
        self.texture = texture
        self.attitude = attitude
        self.contrast = contrast
    }

    public static let heading = "Voice brief:"

    /// The block in Voice Notes.
    public var block: String {
        var lines = [Self.heading]
        for (title, value) in [("Pitch", pitch), ("Pace", pace), ("Texture", texture), ("Attitude", attitude), ("Contrast", contrast)] {
            let v = value.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "."))
            if !v.isEmpty { lines.append("\(title): " + VoiceNotesFromBook.capitalizedFirst(v)) }
        }
        return lines.joined(separator: "\n")
    }

    /// Reads a brief back out of Voice Notes (the narrator may have edited it), for casting.
    public static func read(from notes: String) -> (pitch: String?, pace: String?) {
        guard let r = notes.range(of: heading) else { return (nil, nil) }
        var pitch: String?, pace: String?
        for line in notes[r.upperBound...].split(separator: "\n") {
            let l = line.trimmingCharacters(in: .whitespaces)
            if l.lowercased().hasPrefix("pitch:") { pitch = String(l.dropFirst(6)).trimmingCharacters(in: .whitespaces) }
            if l.lowercased().hasPrefix("pace:") { pace = String(l.dropFirst(5)).trimmingCharacters(in: .whitespaces) }
            if l.hasSuffix(":") && !l.lowercased().hasPrefix("pitch") && !l.lowercased().hasPrefix("pace") && l != heading { break }
        }
        return (pitch, pace)
    }

    /// Puts the brief into the character's Voice Notes. Returns whether they changed.
    @discardableResult
    public func merge(into character: inout BookCharacter) -> Bool {
        GeneratedNotes.put(block, heading: Self.heading, previous: &character.voiceBriefGenerated, in: &character.voiceNotes)
    }
}

public enum VoiceBriefPrompt {
    public static let system = """
    You are the casting director for an audiobook. Write a short voice brief for one character, \
    so a narrator or an AI voice can perform them and so they sound clearly different from the \
    characters they share scenes with. Base it on what the book says, their lines, and the \
    narrator's own notes, which come first when they disagree. Keep each field under 12 words, \
    in plain words a performer can act on. Answer with JSON only.
    """

    /// The characters who share the most chapters with this one, heard most first.
    public static func partners(of character: BookCharacter, project: Project, index: BookIndex, limit: Int = 4) -> [BookCharacter] {
        let mine = index.stats[character.id]?.chapters ?? []
        guard !mine.isEmpty else { return [] }
        return project.characters
            .filter { $0.id != character.id && (index.stats[$0.id]?.lines ?? 0) > 0 }
            .map { ($0, (index.stats[$0.id]?.chapters ?? []).intersection(mine).count) }
            .filter { $0.1 > 0 }
            .sorted { ($0.1, index.stats[$0.0.id]?.lines ?? 0) > ($1.1, index.stats[$1.0.id]?.lines ?? 0) }
            .prefix(limit).map(\.0)
    }

    static func facts(_ c: BookCharacter) -> String {
        let kinds: [FactKind] = [.age, .gender, .accent, .voice, .personality]
        let parts = kinds.flatMap { k in c.facts.filter { $0.kind == k }.prefix(2).map { "\(k.title): \($0.text)" } }
        return parts.isEmpty ? "(the book says little)" : parts.joined(separator: "; ")
    }

    public static func messages(for character: BookCharacter, project: Project, index: BookIndex) -> [ChatMessage] {
        let lines = project.lines.filter { $0.speakerID == character.id && $0.kind == .spoken }
            .map { project.text(of: $0, index: index) }
            .filter { let n = Words.count($0); return n >= 4 && n <= 30 }
            .prefix(5)
        let own = GeneratedNotes.narratorsOwn(character)
        let partners = partners(of: character, project: project, index: index)
        let partnerText = partners.map { p -> String in
            var s = "- \(p.name): \(facts(p))"
            if let brief = p.voiceBriefGenerated ?? (p.voiceNotes.contains(VoiceBrief.heading) ? p.voiceNotes : nil) {
                let b = VoiceBrief.read(from: brief)
                if let pitch = b.pitch { s += "; their pitch: \(pitch)" }
                if let pace = b.pace { s += "; their pace: \(pace)" }
            }
            return s
        }.joined(separator: "\n")
        let user = """
        Character: \(character.name)
        What the book says: \(facts(character))
        \(own.isEmpty ? "" : "The narrator's notes: \(own)\n")Some of their lines:
        \(lines.map { "- “\($0)”" }.joined(separator: "\n"))

        Characters they share scenes with:
        \(partners.isEmpty ? "(none)" : partnerText)

        pitch: how high or low, e.g. "Low, chest voice". pace: e.g. "Slow and deliberate". texture: e.g. "Gravelly, breathy at the ends". \
        attitude: how they come across, e.g. "Gruff but warm". contrast: how this voice differs from each character above by name, \
        e.g. "Lower and slower than Mara; rougher than Edda", or "" if there is no one above.
        Reply as {"pitch":"…","pace":"…","texture":"…","attitude":"…","contrast":"…"}
        """
        return [ChatMessage(.system, system), ChatMessage(.user, user)]
    }

    public static let grammar = #"""
    root ::= "{" ws "\"pitch\"" ws ":" ws string ws "," ws "\"pace\"" ws ":" ws string ws "," ws "\"texture\"" ws ":" ws string ws "," ws "\"attitude\"" ws ":" ws string ws "," ws "\"contrast\"" ws ":" ws string ws "}"
    string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] ){0,140} "\""
    ws ::= [ ]?
    """#

    public static func parse(_ text: String) -> VoiceBrief? {
        guard let json = JSONText.extract(text), let b = try? JSONDecoder().decode(VoiceBrief.self, from: Data(json.utf8)) else { return nil }
        let all = [b.pitch, b.pace, b.texture, b.attitude].map { $0.trimmingCharacters(in: .whitespaces) }
        return all.contains(where: { !$0.isEmpty }) ? b : nil
    }
}
