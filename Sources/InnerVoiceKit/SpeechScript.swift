import Foundation

/// One piece of the book to say aloud: a line in its character's voice, or narration in
/// the narrator's.
public struct SpeechSegment: Hashable, Identifiable, Sendable {
    public var id: String
    public var paragraphID: UUID
    public var lineID: UUID?
    /// nil is the narrator.
    public var speakerID: UUID?
    /// What the voice says, with pronunciations already applied.
    public var text: String
    public var tone: Tone
    public var kind: LineKind?
    /// Silence after it, in seconds.
    public var pauseAfter: Double

    public init(id: String, paragraphID: UUID, lineID: UUID? = nil, speakerID: UUID? = nil, text: String,
                tone: Tone = .neutral, kind: LineKind? = nil, pauseAfter: Double = 0.3) {
        self.id = id
        self.paragraphID = paragraphID
        self.lineID = lineID
        self.speakerID = speakerID
        self.text = text
        self.tone = tone
        self.kind = kind
        self.pauseAfter = pauseAfter
    }
}

/// Turns paragraphs into what is said aloud: each line in its speaker's voice and tone,
/// and (when asked) the narration around them in the narrator's voice. Pronunciations
/// from the guide replace the words they cover.
public enum SpeechScript {
    public static let paragraphPause = 0.6
    public static let chapterPause = 2.0

    public static func segments(for paragraph: Paragraph, project: Project, index: BookIndex,
                                includeNarration: Bool, only speaker: UUID? = nil) -> [SpeechSegment] {
        let say = sayings(project)
        switch paragraph.style {
        case .sceneBreak:
            return includeNarration ? [SpeechSegment(id: "\(paragraph.id)-0", paragraphID: paragraph.id, text: "", pauseAfter: 1.2)] : []
        case .heading:
            guard includeNarration, speaker == nil else { return [] }
            return [SpeechSegment(id: "\(paragraph.id)-0", paragraphID: paragraph.id, text: apply(say, to: paragraph.text), pauseAfter: 1.0)]
        case .body, .blockQuote:
            break
        }
        let ns = paragraph.text as NSString
        let lines = (index.linesByParagraph[paragraph.id] ?? []).map { project.lines[$0] }.sorted { $0.range.location < $1.range.location }
        var out: [SpeechSegment] = []
        var cursor = 0
        var n = 0
        func narration(upTo end: Int) {
            guard end > cursor else { return }
            let piece = clean(ns.substring(with: NSRange(location: cursor, length: end - cursor)))
            if includeNarration, speaker == nil, !piece.isEmpty {
                out.append(SpeechSegment(id: "\(paragraph.id)-\(n)", paragraphID: paragraph.id, text: apply(say, to: piece), pauseAfter: 0.25))
                n += 1
            }
        }
        for line in lines {
            let r = NSIntersectionRange(line.range.ns, NSRange(location: 0, length: ns.length))
            guard r.length > 0, r.location >= cursor else { continue }
            narration(upTo: r.location)
            let text = clean(ns.substring(with: r))
            if !text.isEmpty, speaker == nil || line.speakerID == speaker {
                out.append(SpeechSegment(id: "\(paragraph.id)-\(n)", paragraphID: paragraph.id, lineID: line.id, speakerID: line.speakerID,
                                         text: apply(say, to: text), tone: line.tone ?? .neutral, kind: line.kind, pauseAfter: 0.25))
                n += 1
            }
            cursor = NSMaxRange(r)
        }
        narration(upTo: ns.length)
        if !out.isEmpty { out[out.count - 1].pauseAfter = paragraphPause }
        return out
    }

    /// A run of paragraphs, from `start` (a paragraph id) to the end of its chapter, or the
    /// whole chapter. `chapters` > 1 carries on into the following chapters.
    public static func segments(chapter: Int, from start: UUID? = nil, chapters: Int = 1, project: Project, index: BookIndex,
                                includeNarration: Bool, only speaker: UUID? = nil) -> [SpeechSegment] {
        var out: [SpeechSegment] = []
        for c in chapter..<min(project.chapters.count, chapter + chapters) {
            var started = start == nil || c != chapter
            for p in project.chapters[c].paragraphs {
                if !started, p.id == start { started = true }
                guard started else { continue }
                out += segments(for: p, project: project, index: index, includeNarration: includeNarration, only: speaker)
            }
            if !out.isEmpty { out[out.count - 1].pauseAfter = chapterPause }
        }
        return out
    }

    /// Narration between quotes keeps stray quote marks and dashes; take them off.
    static func clean(_ s: String) -> String {
        s.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: "“”\"‘’'«»—–-,")))
    }

    static func sayings(_ project: Project) -> [(String, String)] {
        project.pronunciations
            .filter { !$0.dismissed && !$0.sayItAs.trimmingCharacters(in: .whitespaces).isEmpty }
            .map { ($0.term, $0.sayItAs) }
            .sorted { $0.0.count > $1.0.count }
    }

    /// "Kerensa" → "keh-REN-sa" from the pronunciation guide, whole words only.
    public static func apply(_ sayings: [(String, String)], to text: String) -> String {
        var out = text
        for (term, say) in sayings {
            let pattern = "\\b" + NSRegularExpression.escapedPattern(for: term) + "\\b"
            out = out.replacingOccurrences(of: pattern, with: NSRegularExpression.escapedTemplate(for: say), options: .regularExpression)
        }
        return out
    }
}
