import Foundation

/// Book › Remove Tones…: clears tones by who set them, so a book the AI tagged with more
/// feeling than wanted can go back to even reading (or be tagged again).
public struct ToneRemoval: Sendable, Equatable {
    /// Tones Tag Speakers set: the AI's and the punctuation rules'.
    public var automatic: Bool
    /// Tones the narrator chose.
    public var narrator: Bool

    public init(automatic: Bool, narrator: Bool) {
        self.automatic = automatic
        self.narrator = narrator
    }

    public var isEmpty: Bool { !automatic && !narrator }

    public func matches(_ line: Line) -> Bool {
        guard line.tone != nil || line.toneSource != nil else { return false }
        return line.toneSource == .user ? narrator : automatic
    }

    /// How many lines each choice would clear: (automatic, narrator).
    public static func counts(in project: Project) -> (automatic: Int, narrator: Int) {
        var a = 0, n = 0
        for l in project.lines where l.tone != nil || l.toneSource != nil {
            if l.toneSource == .user { n += 1 } else { a += 1 }
        }
        return (a, n)
    }

    /// Clears the chosen tones and returns how many lines changed. Clearing the automatic
    /// tones also forgets when they were tagged, so Steps offers Tag Speakers again.
    @discardableResult
    public func apply(to project: inout Project) -> Int {
        var changed = 0
        for i in project.lines.indices where matches(project.lines[i]) {
            project.lines[i].tone = nil
            project.lines[i].toneConfidence = nil
            project.lines[i].toneSource = nil
            changed += 1
        }
        if automatic { project.tonesTagged = nil }
        return changed
    }
}
