import Foundation

/// What Build Summary learns about how a character sounds, written into their Voice Notes
/// as a "From the book:" block for the narrator, and read back by voice casting (and the
/// Qwen3-TTS voice description). The narrator's own notes are never touched; a block the
/// narrator edited is left as it is.
public enum VoiceNotesFromBook {
    public static let heading = "From the book:"

    /// The kinds of fact that shape a voice, and what each line is called.
    static let kinds: [(FactKind, String)] = [(.age, "Age"), (.gender, "Gender"), (.accent, "Accent"), (.voice, "Voice"), (.personality, "Manner")]

    /// The block for a character's current facts, or nil when the summary says nothing
    /// about their voice.
    public static func block(for character: BookCharacter) -> String? {
        var lines: [String] = []
        for (kind, title) in kinds {
            let texts = character.facts.filter { $0.kind == kind }
                .map { $0.text.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: ".;/ ")) }
                // Only facts with words in them (a model sometimes leaves "//").
                .filter { $0.contains(where: \.isLetter) }
            // Manner: the first two personality facts are enough to colour a read.
            let picked = kind == .personality ? Array(texts.prefix(2)) : texts
            guard !picked.isEmpty else { continue }
            var unique: [String] = []
            for t in picked where !unique.contains(where: { $0.caseInsensitiveCompare(t) == .orderedSame }) { unique.append(t) }
            lines.append("\(title): " + capitalizedFirst(unique.joined(separator: "; ")))
        }
        // Age or gender alone isn't much to voice from: the block needs accent, voice or manner.
        guard lines.contains(where: { $0.hasPrefix("Accent") || $0.hasPrefix("Voice") || $0.hasPrefix("Manner") }) else { return nil }
        return ([heading] + lines).joined(separator: "\n")
    }

    /// Puts the block into the character's Voice Notes. Returns whether they changed.
    @discardableResult
    public static func merge(into character: inout BookCharacter) -> Bool {
        guard let new = block(for: character) else { return false }
        return GeneratedNotes.put(new, heading: heading, previous: &character.voiceNotesFromBook, in: &character.voiceNotes)
    }

    static func capitalizedFirst(_ s: String) -> String {
        guard let f = s.first else { return s }
        return f.uppercased() + s.dropFirst()
    }
}

/// Blocks InnerVoice writes into Voice Notes ("From the book:", "Voice brief:"): each
/// replaced only while it's exactly as last written; once the narrator edits one, it's theirs.
public enum GeneratedNotes {
    /// Returns whether `notes` changed.
    @discardableResult
    public static func put(_ new: String, heading: String, previous: inout String?, in notes: inout String) -> Bool {
        if let old = previous, !old.isEmpty, let r = notes.range(of: old) {
            guard old != new else { return false }
            notes.replaceSubrange(r, with: new)
        } else if notes.contains(heading) {
            return false
        } else {
            let trimmed = notes.trimmingCharacters(in: .whitespacesAndNewlines)
            notes = trimmed.isEmpty ? new : trimmed + "\n\n" + new
        }
        previous = new
        return true
    }

    /// The narrator's own part of the notes: without the blocks InnerVoice wrote.
    public static func narratorsOwn(_ c: BookCharacter) -> String {
        var s = c.voiceNotes
        for block in [c.voiceNotesFromBook, c.voiceBriefGenerated].compactMap({ $0 }) where !block.isEmpty {
            s = s.replacingOccurrences(of: block, with: "")
        }
        return s.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// A character's Voice Notes turned into words a voice model can design a voice from
/// (Qwen3-TTS's VoiceDesign, ElevenLabs' Voice Design). The narrator's own notes come
/// first, then the voice brief, then what the book says; headings go, and the result is
/// cut at a whole line to fit the model.
public enum VoiceDescription {
    /// The notes as one paragraph, or nil when they say nothing.
    public static func notes(_ c: BookCharacter, limit: Int = 600) -> String? {
        let own = GeneratedNotes.narratorsOwn(c)
        let brief = c.voiceBriefGenerated.flatMap { c.voiceNotes.contains($0) ? $0 : nil }
        let book = c.voiceNotesFromBook.flatMap { c.voiceNotes.contains($0) ? $0 : nil }
        // Generated blocks the narrator edited are part of `own` already.
        var pieces: [String] = []
        for text in [own, brief ?? "", book ?? ""] {
            for raw in text.split(whereSeparator: \.isNewline) {
                var line = raw.trimmingCharacters(in: .whitespaces)
                    .trimmingCharacters(in: CharacterSet(charactersIn: "-•* "))
                guard line.contains(where: \.isLetter) else { continue }
                if line == VoiceNotesFromBook.heading || line == VoiceBrief.heading { continue }
                // Who they contrast with means nothing to a voice model; how they differ does.
                if line.lowercased().hasPrefix("contrast:") { continue }
                if !".!?".contains(line.last!) { line += "." }
                if pieces.contains(where: { $0.caseInsensitiveCompare(line) == .orderedSame }) { continue }
                pieces.append(line)
            }
        }
        var out = ""
        for p in pieces {
            let next = out.isEmpty ? p : out + " " + p
            if next.count > limit { break }
            out = next
        }
        return out.isEmpty ? nil : out
    }

    /// A voice description with the character's notes added, within `limit` characters.
    public static func design(base: String, character c: BookCharacter, limit: Int = 700) -> String {
        let b = base.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let n = notes(c, limit: max(0, limit - b.count - 12)) else { return b }
        return b.isEmpty ? n : b + " Voice notes: " + n
    }
}
