import Foundation

/// Book › Remove Tones…: clears tones by who set them, so a book the AI tagged with more
/// feeling than wanted can go back to even reading (or be tagged again). Tones in `keep`
/// stay on their lines (a narrator who likes Sarcastic but finds the rest too dramatic).
public struct ToneRemoval: Sendable, Equatable {
    /// Tones Tag Speakers set: the AI's and the punctuation rules'.
    public var automatic: Bool
    /// Tones the narrator chose.
    public var narrator: Bool
    /// Tones that stay, whoever set them. Neutral is never kept: clearing it changes nothing
    /// you hear.
    public var keep: Set<Tone>

    public init(automatic: Bool, narrator: Bool, keep: Set<Tone> = []) {
        self.automatic = automatic
        self.narrator = narrator
        self.keep = keep.subtracting([.neutral])
    }

    public var isEmpty: Bool { !automatic && !narrator }

    static func hasTone(_ line: Line) -> Bool { line.tone != nil || line.toneSource != nil }

    public func matches(_ line: Line) -> Bool {
        guard Self.hasTone(line) else { return false }
        if keep.contains(line.tone ?? .neutral) { return false }
        return line.toneSource == .user ? narrator : automatic
    }

    /// How many lines each choice would clear: (automatic, narrator).
    public static func counts(in project: Project) -> (automatic: Int, narrator: Int) {
        var a = 0, n = 0
        for l in project.lines where hasTone(l) {
            if l.toneSource == .user { n += 1 } else { a += 1 }
        }
        return (a, n)
    }

    /// How many lines have each tone (any source), for the list of tones to keep.
    public static func toneCounts(in project: Project) -> [Tone: Int] {
        var counts: [Tone: Int] = [:]
        for l in project.lines where hasTone(l) { counts[l.tone ?? .neutral, default: 0] += 1 }
        return counts
    }

    /// Clears the chosen tones and returns how many lines changed. Clearing all of Tag
    /// Speakers' tones also forgets when they were tagged, so Steps offers Tag Speakers again;
    /// keeping some doesn't (the book still has the tones you wanted).
    @discardableResult
    public func apply(to project: inout Project) -> Int {
        var changed = 0
        for i in project.lines.indices where matches(project.lines[i]) {
            project.lines[i].tone = nil
            project.lines[i].toneConfidence = nil
            project.lines[i].toneSource = nil
            changed += 1
        }
        if automatic && keep.isEmpty { project.tonesTagged = nil }
        return changed
    }
}
