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
                .map { $0.text.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: ".;")) }
                .filter { !$0.isEmpty }
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
        var notes = character.voiceNotes
        if let old = character.voiceNotesFromBook, !old.isEmpty, let r = notes.range(of: old) {
            // The block is as InnerVoice last wrote it: bring it up to date.
            guard old != new else { return false }
            notes.replaceSubrange(r, with: new)
        } else if notes.contains(heading) {
            // The narrator edited the block: theirs now.
            return false
        } else {
            let trimmed = notes.trimmingCharacters(in: .whitespacesAndNewlines)
            notes = trimmed.isEmpty ? new : trimmed + "\n\n" + new
        }
        character.voiceNotes = notes
        character.voiceNotesFromBook = new
        return true
    }

    static func capitalizedFirst(_ s: String) -> String {
        guard let f = s.first else { return s }
        return f.uppercased() + s.dropFirst()
    }
}
