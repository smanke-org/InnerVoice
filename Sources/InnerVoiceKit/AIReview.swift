import Foundation

/// What a cloud AI run (Claude) changed, for the narrator to keep or undo line by line.
public struct AIReviewItem: Identifiable, Equatable, Sendable {
    public var id: UUID { lineID }
    public var lineID: UUID
    public var speakerBefore: UUID?
    public var speakerAfter: UUID?
    public var toneBefore: Tone?
    public var toneAfter: Tone?
    public var speakerChanged: Bool { speakerBefore != speakerAfter }
    public var toneChanged: Bool { (toneBefore ?? .neutral) != (toneAfter ?? .neutral) }
    public init(lineID: UUID, speakerBefore: UUID?, speakerAfter: UUID?, toneBefore: Tone?, toneAfter: Tone?) {
        self.lineID = lineID
        self.speakerBefore = speakerBefore
        self.speakerAfter = speakerAfter
        self.toneBefore = toneBefore
        self.toneAfter = toneAfter
    }
}

public enum AIReview {
    /// The lines the AI changed in a run, in book order. `before` holds each touched line
    /// as it was. Only changes the AI still owns are listed: rules' and the narrator's aren't.
    public static func items(before: [UUID: Line], project: Project) -> [AIReviewItem] {
        project.lines.compactMap { line in
            guard let old = before[line.id] else { return nil }
            let item = AIReviewItem(lineID: line.id, speakerBefore: old.speakerID, speakerAfter: line.speakerID,
                                    toneBefore: old.tone, toneAfter: line.tone)
            let speaker = item.speakerChanged && line.source == .ai
            let tone = item.toneChanged && line.toneSource == .ai
            return speaker || tone ? item : nil
        }
    }

    /// Puts the listed lines back as they were before the run, where the AI still owns them,
    /// and removes characters the run added that no line uses any more.
    public static func undo(_ ids: Set<UUID>, before: [UUID: Line], addedCharacters: Set<UUID>, in p: inout Project) {
        for i in p.lines.indices {
            guard ids.contains(p.lines[i].id), let old = before[p.lines[i].id] else { continue }
            if p.lines[i].source == .ai {
                p.lines[i].speakerID = old.speakerID
                p.lines[i].confidence = old.confidence
                p.lines[i].source = old.source
                p.lines[i].kind = old.kind
            }
            if p.lines[i].toneSource == .ai {
                p.lines[i].tone = old.tone
                p.lines[i].toneConfidence = old.toneConfidence
                p.lines[i].toneSource = old.toneSource
            }
        }
        let used = Set(p.lines.compactMap(\.speakerID))
        p.characters.removeAll { addedCharacters.contains($0.id) && !used.contains($0.id) }
    }
}

public extension ProjectSettings {
    /// Claude's changes are listed for review unless the narrator turned it off.
    var reviewsCloudAI: Bool { reviewCloudAI ?? true }
}
