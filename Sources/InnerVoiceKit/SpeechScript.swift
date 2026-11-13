import Foundation
import NaturalLanguage

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
    /// A sigh, laugh or gasp the speech tag asks for.
    public var sound: VocalSound?
    /// What kind of gap follows, so the player can use the narrator's current pause
    /// settings (Settings › Voices) without remaking the script.
    public var pause: PauseKind = .line
    /// The line's delivery hint ("barely audible"). Only ElevenLabs is sent it; every other
    /// engine ignores it.
    public var delivery: String? = nil

    public init(id: String, paragraphID: UUID, lineID: UUID? = nil, speakerID: UUID? = nil, text: String,
                tone: Tone = .neutral, kind: LineKind? = nil, pauseAfter: Double = 0.3, sound: VocalSound? = nil) {
        self.id = id
        self.paragraphID = paragraphID
        self.lineID = lineID
        self.speakerID = speakerID
        self.text = text
        self.tone = tone
        self.kind = kind
        self.pauseAfter = pauseAfter
        self.sound = sound
    }
}

/// Turns paragraphs into what is said aloud: each line in its speaker's voice and tone,
/// and (when asked) the narration around them in the narrator's voice. Pronunciations
/// from the guide replace the words they cover.
/// The gap after a piece of speech.
public enum PauseKind: String, Codable, CaseIterable, Sendable {
    /// Between sentences in the same line or piece of narration.
    case sentence
    /// Between a line and the narration around it ("…,” she said.).
    case line
    case paragraph, sceneBreak, heading, chapter
    /// No gap (a single sample line).
    case none
}

/// How long each pause is, in seconds at normal speed. Settings › Voices › Pauses.
public struct PauseSettings: Codable, Hashable, Sendable {
    public var sentence: Double
    public var line: Double
    public var paragraph: Double
    public var sceneBreak: Double
    public var heading: Double
    public var chapter: Double
    /// Speak one sentence at a time, so the gap between sentences is `sentence` exactly
    /// rather than whatever the voice does on its own.
    public var splitSentences: Bool

    public init(sentence: Double = 0.35, line: Double = 0.25, paragraph: Double = 0.7, sceneBreak: Double = 1.5,
                heading: Double = 1.0, chapter: Double = 2.5, splitSentences: Bool = true) {
        self.sentence = sentence
        self.line = line
        self.paragraph = paragraph
        self.sceneBreak = sceneBreak
        self.heading = heading
        self.chapter = chapter
        self.splitSentences = splitSentences
    }

    public static let standard = PauseSettings()
    /// The narrator's settings (the app sets this from Settings › Voices › Pauses); scripts
    /// use it unless they're given others.
    nonisolated(unsafe) public static var current = PauseSettings()
    public static let tight = PauseSettings(sentence: 0.2, line: 0.12, paragraph: 0.45, sceneBreak: 1.0, heading: 0.7, chapter: 1.5)
    public static let relaxed = PauseSettings(sentence: 0.55, line: 0.4, paragraph: 1.1, sceneBreak: 2.2, heading: 1.4, chapter: 3.5)

    public func seconds(_ kind: PauseKind) -> Double {
        switch kind {
        case .sentence: sentence
        case .line: line
        case .paragraph: paragraph
        case .sceneBreak: sceneBreak
        case .heading: heading
        case .chapter: chapter
        case .none: 0
        }
    }
}

/// Turns paragraphs into what is said aloud: each line in its speaker's voice and tone,
/// and (when asked) the narration around them in the narrator's voice. Pronunciations
/// from the guide replace the words they cover. With `splitSentences`, every sentence is
/// its own segment, so the pause between sentences is exactly as set.
public enum SpeechScript {
    public static var paragraphPause: Double { PauseSettings.standard.paragraph }
    public static var chapterPause: Double { PauseSettings.standard.chapter }

    /// Sentences of a piece of text (one, when it has no sentence break).
    static func sentences(_ text: String) -> [String] {
        let tokenizer = NLTokenizer(unit: .sentence)
        tokenizer.string = text
        var out: [String] = []
        tokenizer.enumerateTokens(in: text.startIndex..<text.endIndex) { r, _ in
            let s = text[r].trimmingCharacters(in: .whitespacesAndNewlines)
            if !s.isEmpty { out.append(s) }
            return true
        }
        return out.isEmpty ? [text] : out
    }

    public static func segments(for paragraph: Paragraph, project: Project, index: BookIndex,
                                includeNarration: Bool, only speaker: UUID? = nil, pauses: PauseSettings = .current) -> [SpeechSegment] {
        let say = sayings(project)
        switch paragraph.style {
        case .sceneBreak:
            guard includeNarration else { return [] }
            var seg = SpeechSegment(id: "\(paragraph.id)-0", paragraphID: paragraph.id, text: "", pauseAfter: pauses.sceneBreak)
            seg.pause = .sceneBreak
            return [seg]
        case .heading:
            guard includeNarration, speaker == nil else { return [] }
            var seg = SpeechSegment(id: "\(paragraph.id)-0", paragraphID: paragraph.id, text: apply(say, to: paragraph.text), pauseAfter: pauses.heading)
            seg.pause = .heading
            return [seg]
        case .body, .blockQuote:
            break
        }
        let ns = paragraph.text as NSString
        let lines = (index.linesByParagraph[paragraph.id] ?? []).map { project.lines[$0] }.sorted { $0.range.location < $1.range.location }
        var out: [SpeechSegment] = []
        var cursor = 0
        var n = 0
        /// One piece (narration or a line), split into sentences when asked.
        func add(_ text: String, make: (String, Bool) -> SpeechSegment) {
            let parts = pauses.splitSentences ? sentences(text) : [text]
            for (k, part) in parts.enumerated() {
                var seg = make(part, k == 0)
                let last = k == parts.count - 1
                seg.pause = last ? .line : .sentence
                seg.pauseAfter = pauses.seconds(seg.pause)
                out.append(seg)
                n += 1
            }
        }
        func narration(upTo end: Int) {
            guard end > cursor else { return }
            let piece = clean(ns.substring(with: NSRange(location: cursor, length: end - cursor)))
            if includeNarration, speaker == nil, !piece.isEmpty {
                add(apply(say, to: piece)) { text, _ in
                    SpeechSegment(id: "\(paragraph.id)-\(n)", paragraphID: paragraph.id, text: text)
                }
            }
        }
        for line in lines {
            let r = NSIntersectionRange(line.range.ns, NSRange(location: 0, length: ns.length))
            guard r.length > 0, r.location >= cursor else { continue }
            narration(upTo: r.location)
            let text = clean(ns.substring(with: r))
            if !text.isEmpty, speaker == nil || line.speakerID == speaker {
                let sound = ToneRules.sound(for: line, in: paragraph, linesInParagraph: lines)
                add(apply(say, to: text)) { part, first in
                    var seg = SpeechSegment(id: "\(paragraph.id)-\(n)", paragraphID: paragraph.id, lineID: line.id, speakerID: line.speakerID,
                                            text: part, tone: line.tone ?? .neutral, kind: line.kind, sound: first ? sound : nil)
                    seg.delivery = DeliveryPrompt.clean(line.delivery ?? "")
                    return seg
                }
            }
            cursor = NSMaxRange(r)
        }
        narration(upTo: ns.length)
        if !out.isEmpty {
            out[out.count - 1].pause = .paragraph
            out[out.count - 1].pauseAfter = pauses.paragraph
        }
        return out
    }

    /// The picked chapters (indexes, any order, gaps allowed), read in book order; each ends
    /// with the chapter pause, as a run of chapters does.
    public static func segments(chapters picked: [Int], project: Project, index: BookIndex,
                                includeNarration: Bool, only speaker: UUID? = nil, pauses: PauseSettings = .current,
                                recaps: Bool = false) -> [SpeechSegment] {
        Set(picked).sorted().filter { project.chapters.indices.contains($0) }.flatMap {
            segments(chapter: $0, project: project, index: index, includeNarration: includeNarration, only: speaker, pauses: pauses, recaps: recaps)
        }
    }

    /// The chapter's recap, read by the narrator: "Previously…", then the recap.
    public static func recapSegments(chapter c: Int, project: Project, pauses: PauseSettings = .current) -> [SpeechSegment] {
        guard project.chapters.indices.contains(c), let recap = project.chapters[c].recapText else { return [] }
        let chapter = project.chapters[c]
        let anchor = chapter.paragraphs.first(where: { $0.style == .heading })?.id ?? chapter.paragraphs.first?.id ?? chapter.id
        let parts = ["Previously…"] + (pauses.splitSentences ? sentences(recap) : [recap])
        let say = sayings(project)
        return parts.enumerated().map { k, part in
            var seg = SpeechSegment(id: "\(chapter.id)-recap-\(k)", paragraphID: anchor, text: k == 0 ? part : apply(say, to: part))
            seg.pause = k == parts.count - 1 ? .paragraph : .sentence
            seg.pauseAfter = pauses.seconds(seg.pause)
            return seg
        }
    }

    /// "Chapter 3", "Chapters 1–3, 5": picked chapter indexes as the narrator counts them (from 1).
    public static func chapterLabel(_ picked: [Int]) -> String {
        let n = Set(picked).sorted().map { $0 + 1 }
        var runs: [String] = []
        var i = 0
        while i < n.count {
            var j = i
            while j + 1 < n.count, n[j + 1] == n[j] + 1 { j += 1 }
            runs.append(j == i ? "\(n[i])" : j == i + 1 ? "\(n[i]), \(n[j])" : "\(n[i])–\(n[j])")
            i = j + 1
        }
        return (n.count == 1 ? "Chapter " : "Chapters ") + runs.joined(separator: ", ")
    }

    /// A run of paragraphs, from `start` (a paragraph id) to the end of its chapter, or the
    /// whole chapter. `chapters` > 1 carries on into the following chapters.
    public static func segments(chapter: Int, from start: UUID? = nil, chapters: Int = 1, project: Project, index: BookIndex,
                                includeNarration: Bool, only speaker: UUID? = nil, pauses: PauseSettings = .current,
                                recaps: Bool = false) -> [SpeechSegment] {
        var out: [SpeechSegment] = []
        for c in chapter..<min(project.chapters.count, chapter + chapters) {
            var started = start == nil || c != chapter
            // "Previously…" after the chapter's title, when the whole chapter is read.
            var recap = recaps && started && includeNarration && speaker == nil ? recapSegments(chapter: c, project: project, pauses: pauses) : []
            for p in project.chapters[c].paragraphs {
                if !started, p.id == start { started = true }
                guard started else { continue }
                if !recap.isEmpty, p.style != .heading {
                    out += recap
                    recap = []
                }
                out += segments(for: p, project: project, index: index, includeNarration: includeNarration, only: speaker, pauses: pauses)
            }
            out += recap
            if !out.isEmpty {
                out[out.count - 1].pause = .chapter
                out[out.count - 1].pauseAfter = pauses.chapter
            }
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
