import Foundation

/// The steps from a manuscript to a finished book, in order. Each step shows how far along
/// it is; the first one not done is the current step. Nothing is locked: the narrator can
/// skip a step or go back to any of them, and a step finished by hand stays finished.
public enum GuidedStep: String, CaseIterable, Sendable, Identifiable {
    case manuscript, speakers, review, characters, voices, listen, pronunciations, record

    public var id: String { rawValue }

    /// The steps for a project: narrating it yourself, or producing it with AI voices.
    public static func steps(narration: Bool) -> [GuidedStep] {
        narration ? [.manuscript, .speakers, .review, .characters, .pronunciations, .record]
                  : [.manuscript, .speakers, .review, .characters, .voices, .listen]
    }

    public var title: String {
        switch self {
        case .manuscript: "Manuscript"
        case .speakers: "Speakers"
        case .review: "Review"
        case .characters: "Characters"
        case .voices: "Voices"
        case .listen: "Listen"
        case .pronunciations: "Pronunciations"
        case .record: "Record"
        }
    }

    /// The last step has no end of its own: it's done when the narrator says so.
    public var isLast: Bool { self == .listen || self == .record }
}

/// Where one step stands.
public struct GuidedStepStatus: Equatable, Sendable, Identifiable {
    public var step: GuidedStep
    public var number: Int
    public var isDone: Bool
    /// Marked done or skipped by the narrator.
    public var isMarked: Bool
    /// Marked by the narrator before it was done on its own.
    public var isSkipped: Bool
    /// "38 to review", "12 need a voice".
    public var detail: String
    public var id: GuidedStep { step }
}

public struct GuidedSteps: Equatable, Sendable {
    public var all: [GuidedStepStatus]

    /// The first step not done, or nil when every step is.
    public var current: GuidedStepStatus? { all.first { !$0.isDone } }
    public var allDone: Bool { current == nil }

    /// - Parameters:
    ///   - voicesNeeded: speaking characters whose voice needs the narrator, on the engine
    ///     in use (the app knows which engine that is).
    ///   - speaking: characters with at least one line.
    public init(project p: Project, narration: Bool, speaking: Int, voicesNeeded: Int) {
        let marked = Set(p.settings.stepsDone ?? [])
        let tagged = p.lastTagged != nil
        let needing = p.linesNeedingYou
        let paragraphs = p.chapters.reduce(0) { $0 + $1.paragraphs.count }
        let terms = p.pronunciations.filter { !$0.dismissed }
        let unsaid = terms.filter { $0.sayItAs.trimmingCharacters(in: .whitespaces).isEmpty }.count
        func plural(_ n: Int, _ one: String, _ many: String) -> String { "\(n.formatted()) \(n == 1 ? one : many)" }

        all = Self.steps(narration).enumerated().map { i, step in
            let isMarked = marked.contains(step.rawValue)
            var done: Bool
            let detail: String
            switch step {
            case .manuscript:
                done = tagged
                detail = plural(p.chapters.count, "chapter", "chapters")
            case .speakers:
                done = tagged
                detail = tagged ? plural(p.lines.count, "line", "lines") : plural(p.lines.count, "line found", "lines found")
            case .review:
                done = tagged && needing == 0
                detail = !tagged ? "After tagging" : (needing == 0 ? "All confirmed" : "\(needing.formatted()) to review")
            case .characters:
                done = p.summariesBuilt != nil
                detail = done ? plural(speaking, "character", "characters") : "Not read yet"
            case .voices:
                done = tagged && p.voicesCast != nil && voicesNeeded == 0
                detail = p.voicesCast == nil ? "Not cast yet" : (voicesNeeded == 0 ? "All cast" : "\(voicesNeeded.formatted()) need a voice")
            case .listen:
                done = false
                detail = "Export audio"
            case .pronunciations:
                done = !terms.isEmpty && unsaid == 0
                detail = terms.isEmpty ? "Names and places" : (unsaid == 0 ? "All added" : "\(unsaid.formatted()) to add")
            case .record:
                done = paragraphs > 0 && p.recorded.count >= paragraphs
                detail = "\(min(p.recorded.count, paragraphs).formatted()) of \(paragraphs.formatted()) recorded"
            }
            return GuidedStepStatus(step: step, number: i + 1, isDone: done || isMarked, isMarked: isMarked,
                                    isSkipped: isMarked && !done && !step.isLast, detail: detail)
        }
    }

    static func steps(_ narration: Bool) -> [GuidedStep] { GuidedStep.steps(narration: narration) }
}

extension ProjectSettings {
    /// Marks a step done (or skipped), or takes the mark off.
    public mutating func markStep(_ step: GuidedStep, done: Bool) {
        var marked = stepsDone ?? []
        marked.removeAll { $0 == step.rawValue }
        if done { marked.append(step.rawValue) }
        stepsDone = marked.isEmpty ? nil : marked
    }
}
