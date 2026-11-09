import AppKit
import InnerVoiceKit
import Observation

/// Runs the AI over one document: Tag Speakers and Build Summaries. Results show up
/// chapter by chapter while the narrator keeps reading; Stop keeps what is done.
@Observable
final class TaggingController {
    enum Job: Equatable { case tagging, summaries }

    struct Status: Equatable {
        var job: Job
        var progress: Double
        var message: String
        /// Build Summaries: the character being read now, how far through them, and where
        /// they fall in the run ("2 of 12").
        var character: String? = nil
        var characterProgress: Double = 0
        var characterNumber = 0
        var characterCount = 0
    }

    /// This project's most recent job, so a page shows only the result of its own kind.
    private(set) var lastRun: Job?

    private(set) var status: Status? {
        didSet {
            if oldValue == nil, status != nil { Self.running += 1 }
            if oldValue != nil, status == nil { Self.running -= 1 }
            if let s = status { Self.lastJob = s.job; lastRun = s.job }
        }
    }
    private static var running = 0
    private static var lastJob: Job = .tagging
    /// For the quit question and the updater: what any project is busy with.
    static var busyReason: String? {
        running > 0 ? (lastJob == .tagging ? "tagging speakers" : "building summaries") : nil
    }
    /// The last run's result, shown in a banner until dismissed.
    var finished: String?
    var failure: String?

    @ObservationIgnored private weak var document: InnerVoiceDocument?
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var model: TextModel?

    init(document: InnerVoiceDocument) { self.document = document }

    var isRunning: Bool { status != nil }

    func stop() {
        model?.cancel()
        task?.cancel()
    }

    // MARK: Tag Speakers

    /// Tags every line the narrator and the rules haven't settled. With `chapters`, only those.
    func tagSpeakers(undoManager: UndoManager?, chapters: [Int]? = nil) {
        guard let document, !isRunning else { return }
        failure = nil
        finished = nil
        status = Status(job: .tagging, progress: 0, message: "Loading the model…")
        task = Task { [weak self] in
            guard let self else { return }
            var changed: [UUID: Line] = [:] // line id → before
            var newCharacters: [UUID] = []
            let charactersBefore = Set(document.project.characters.map(\.id))
            defer {
                self.status = nil
                self.model = nil
                self.registerUndo(changed: changed, document: document, undoManager: undoManager, charactersBefore: charactersBefore)
            }
            do {
                let model = try await ModelManager.shared.model()
                self.model = model
                let targets = chapters ?? Array(document.project.chapters.indices)
                // Lines the AI gave up on ("?") get another try: the rules first (they now
                // guess from the sentence before "he said"), then the AI, which is asked
                // for its best guess. Part of this run's one undo step.
                document.applyQuietly { p in
                    let ids = Set(targets.flatMap { p.chapters[$0].paragraphs.map(\.id) })
                    let before = Dictionary(p.lines.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
                    for i in p.lines.indices where ids.contains(p.lines[i].paragraphID) && p.lines[i].source == .ai && p.lines[i].speakerID == nil {
                        p.lines[i].source = .none
                        p.lines[i].confidence = 0
                    }
                    RuleAttribution.apply(to: &p)
                    ToneRules.apply(to: &p)
                    for l in p.lines where before[l.id] != l && changed[l.id] == nil { changed[l.id] = before[l.id] }
                }
                let hints = self.hintsFromRules(document)
                let total = max(1, targets.count)
                for (n, c) in targets.enumerated() {
                    try Task.checkCancellation()
                    let title = document.project.chapters[c].title
                    self.status = Status(job: .tagging, progress: Double(n) / Double(total),
                                         message: "Tagging \(title) (\(n + 1) of \(total))…")
                    let chunks = AttributionPrompt.chunks(project: document.project, index: document.index, chapterIndex: c,
                                                          hints: hints, maxWords: model.passageWords)
                    for (k, chunk) in chunks.enumerated() {
                        try Task.checkCancellation()
                        let maxTokens = 40 + chunk.labels.count * 34
                        var replies: [AttributionReply]? = nil
                        for attempt in 0..<2 where replies == nil {
                            let text = try await model.complete(chunk.messages, grammar: AttributionPrompt.grammar, maxTokens: maxTokens)
                            try Task.checkCancellation()
                            replies = AttributionPrompt.parse(text)
                            if replies == nil, attempt == 0 {
                                Self.log("unparseable reply (\(text.count) characters, \(maxTokens) tokens allowed): \(text.replacingOccurrences(of: "\n", with: "⏎").prefix(400))")
                            }
                        }
                        if let replies {
                            let modelName = model.displayName
                            document.applyQuietly { p in
                                let before = AttributionPrompt.apply(replies, chunk: chunk, to: &p)
                                for l in before where changed[l.id] == nil { changed[l.id] = l }
                                // Which model tagged each line, for its accuracy in Activity.
                                let ids = Set(before.map(\.id))
                                for i in p.lines.indices where ids.contains(p.lines[i].id) && p.lines[i].source == .ai { p.lines[i].aiModel = modelName }
                            }
                        }
                        self.status?.progress = (Double(n) + Double(k + 1) / Double(max(1, chunks.count)) * 0.7) / Double(total)
                    }
                    // Then how each line is said, now that the speakers are known.
                    let toneChunks = TonePrompt.chunks(project: document.project, index: document.index, chapterIndex: c,
                                                       maxWords: model.passageWords)
                    for (k, chunk) in toneChunks.enumerated() {
                        try Task.checkCancellation()
                        let text = try await model.complete(chunk.messages, grammar: chunk.grammar,
                                                            maxTokens: 40 + chunk.labels.count * 22)
                        try Task.checkCancellation()
                        if let replies = TonePrompt.parse(text) {
                            document.applyQuietly { p in
                                let before = TonePrompt.apply(replies, chunk: chunk, to: &p)
                                for l in before where changed[l.id] == nil { changed[l.id] = l }
                                if before.isEmpty {
                                    Self.log("tone reply matched no line: \(text.replacingOccurrences(of: "\n", with: "⏎").prefix(300))")
                                }
                            }
                        } else {
                            Self.log("unparseable tone reply (\(text.count) characters): \(text.replacingOccurrences(of: "\n", with: "⏎").prefix(300))")
                        }
                        self.status?.progress = (Double(n) + 0.7 + 0.3 * Double(k + 1) / Double(max(1, toneChunks.count))) / Double(total)
                    }
                }
                document.applyQuietly { $0.tonesTagged = .now }
                // Activity: lines the run gave a speaker or a tone (numbers only).
                for line in document.project.lines {
                    guard let old = changed[line.id] else { continue }
                    if old.speakerID == nil, line.speakerID != nil {
                        if line.source == .ai { ActivityStore.shared.record(ActivityKey.linesAI) }
                        if line.source == .rule || line.source == .exchange { ActivityStore.shared.record(ActivityKey.linesRules) }
                    }
                    if line.toneSource == .ai, old.toneSource != .ai { ActivityStore.shared.record(ActivityKey.tonesAI) }
                }
                newCharacters = document.project.characters.map(\.id).filter { !charactersBefore.contains($0) }
                document.applyQuietly { $0.lastTagged = .now }
                let needs = document.project.linesNeedingYou
                VoiceCastingRun.castIfNeeded(document: document)
                self.finished = "Tagged \(changed.count.formatted()) lines" + (newCharacters.isEmpty ? "" : " and found \(newCharacters.count) new characters")
                    + (needs > 0 ? ". \(needs.formatted()) need you." : ".")
            } catch is CancellationError {
                self.finished = "Stopped. Tag Speakers continues from there."
            } catch {
                if Task.isCancelled {
                    self.finished = "Stopped. Tag Speakers continues from there."
                } else {
                    self.failure = error.localizedDescription
                }
            }
        }
    }

    /// Pronoun hints for the prompt ("she said"), from the rules.
    private func hintsFromRules(_ document: InnerVoiceDocument) -> [UUID: RuleAttribution.Hint] {
        var copy = document.project
        // Rules on a copy: only to read hints; assignments were made at import.
        return RuleAttribution.apply(to: &copy)
    }

    private func registerUndo(changed: [UUID: Line], document: InnerVoiceDocument, undoManager: UndoManager?, charactersBefore: Set<UUID>) {
        guard !changed.isEmpty else { return }
        let afterLines = Dictionary(document.project.lines.filter { changed[$0.id] != nil }.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let added = document.project.characters.filter { !charactersBefore.contains($0.id) }
        document.registerRunUndo("Tag Speakers", undoManager: undoManager, restore: { p in
            // Put back only what the AI still owns; the narrator's later fixes stay. Speaker
            // and tone are separate: a line can have the narrator's speaker and the AI's tone.
            for i in p.lines.indices {
                guard let old = changed[p.lines[i].id] else { continue }
                if p.lines[i].source == .ai || p.lines[i].source == .rule {
                    p.lines[i].speakerID = old.speakerID
                    p.lines[i].confidence = old.confidence
                    p.lines[i].source = old.source
                    p.lines[i].kind = old.kind
                }
                if p.lines[i].toneSource != .user {
                    p.lines[i].tone = old.tone
                    p.lines[i].toneConfidence = old.toneConfidence
                    p.lines[i].toneSource = old.toneSource
                }
            }
            let used = Set(p.lines.compactMap(\.speakerID))
            let addedIDs = Set(added.map(\.id))
            p.characters.removeAll { addedIDs.contains($0.id) && !used.contains($0.id) }
        }, redo: { p in
            let existing = Set(p.characters.map(\.id))
            p.characters += added.filter { !existing.contains($0.id) }
            for i in p.lines.indices {
                guard let new = afterLines[p.lines[i].id] else { continue }
                if p.lines[i].source != .user {
                    p.lines[i].speakerID = new.speakerID
                    p.lines[i].confidence = new.confidence
                    p.lines[i].source = new.source
                    p.lines[i].kind = new.kind
                }
                if p.lines[i].toneSource != .user {
                    p.lines[i].tone = new.tone
                    p.lines[i].toneConfidence = new.toneConfidence
                    p.lines[i].toneSource = new.toneSource
                }
            }
        })
    }

    /// Lets the AI decide the AI's own lines again in these chapters (after a revision or a bad run).
    func retag(chapters: [Int], undoManager: UndoManager?) {
        guard let document else { return }
        document.change("Reset AI Tags", undoManager: undoManager) { p in
            let ids = Set(chapters.flatMap { p.chapters[$0].paragraphs.map(\.id) })
            for i in p.lines.indices where ids.contains(p.lines[i].paragraphID) && p.lines[i].source == .ai {
                p.lines[i].source = .none
                p.lines[i].speakerID = nil
                p.lines[i].confidence = 0
            }
            RuleAttribution.apply(to: &p)
        }
        tagSpeakers(undoManager: undoManager, chapters: chapters)
    }

    // MARK: Build Summaries

    /// A character's facts and voice notes, for Build Summaries' one Undo step.
    struct SavedSummary: Sendable {
        var facts: [Fact]
        var notes: String
        var fromBook: String?
    }

    nonisolated static func snapshot(_ p: Project) -> [UUID: SavedSummary] {
        Dictionary(p.characters.map { ($0.id, SavedSummary(facts: $0.facts, notes: $0.voiceNotes, fromBook: $0.voiceNotesFromBook)) },
                   uniquingKeysWith: { a, _ in a })
    }

    nonisolated static func put(_ saved: [UUID: SavedSummary], _ p: inout Project) {
        for i in p.characters.indices {
            guard let s = saved[p.characters[i].id] else { continue }
            p.characters[i].facts = s.facts
            p.characters[i].voiceNotes = s.notes
            p.characters[i].voiceNotesFromBook = s.fromBook
        }
    }

    func buildSummaries(undoManager: UndoManager?, only: [UUID]? = nil) {
        guard let document, !isRunning else { return }
        failure = nil
        finished = nil
        status = Status(job: .summaries, progress: 0, message: "Loading the model…")
        // Facts and voice notes before the run: one Undo puts both back.
        let before = Self.snapshot(document.project)
        task = Task { [weak self] in
            guard let self else { return }
            var added = 0
            var notesWritten = 0
            defer {
                self.status = nil
                self.model = nil
                if added > 0 || notesWritten > 0 {
                    let after = Self.snapshot(document.project)
                    document.registerRunUndo("Build Summaries", undoManager: undoManager,
                                             restore: { Self.put(before, &$0) }, redo: { Self.put(after, &$0) })
                }
            }
            do {
                let model = try await ModelManager.shared.model()
                self.model = model
                // Most-heard characters first; tiny walk-ons are skipped unless asked for.
                let index = document.index
                var cast = document.project.characters.sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
                if let only { cast = cast.filter { only.contains($0.id) } }
                for (n, character) in cast.enumerated() {
                    try Task.checkCancellation()
                    self.status = Status(job: .summaries, progress: Double(n) / Double(max(1, cast.count)),
                                         message: "Reading about \(character.name) (\(n + 1) of \(cast.count))…",
                                         character: character.name, characterProgress: 0, characterNumber: n + 1, characterCount: cast.count)
                    let chunks = Array(SummaryPrompt.chunks(for: character, project: document.project, index: document.index,
                                                            maxWords: model.passageWords + 200).prefix(4))
                    for (k, chunk) in chunks.enumerated() {
                        try Task.checkCancellation()
                        // The character's bar moves while the model reads and writes, not only
                        // between passages (a minor character often has just one).
                        let count = Double(max(1, chunks.count)), total = Double(max(1, cast.count))
                        let text = try await model.complete(chunk.messages, grammar: SummaryPrompt.grammar, maxTokens: 700) { [weak self] f in
                            Task { @MainActor in
                                guard let self, self.status?.job == .summaries, self.status?.characterNumber == n + 1 else { return }
                                let part = (Double(k) + f) / count
                                self.status?.characterProgress = max(self.status?.characterProgress ?? 0, part)
                                self.status?.progress = max(self.status?.progress ?? 0, (Double(n) + part) / total)
                            }
                        }
                        let part = Double(k + 1) / Double(max(1, chunks.count))
                        self.status?.characterProgress = part
                        self.status?.progress = (Double(n) + part) / Double(max(1, cast.count))
                        guard let replies = SummaryPrompt.parse(text) else { continue }
                        document.applyQuietly { p in
                            added += SummaryPrompt.merge(replies, chunk: chunk, characterID: character.id, into: &p, index: document.index)
                        }
                    }
                    // What the book says about how they sound, into their Voice Notes.
                    document.applyQuietly { p in
                        if let i = p.characterIndex(character.id), VoiceNotesFromBook.merge(into: &p.characters[i]) { notesWritten += 1 }
                    }
                }
                // Only a run over the whole cast counts as "analyzed the cast".
                if only == nil { document.applyQuietly { $0.summariesBuilt = .now } }
                ActivityStore.shared.record(ActivityKey.facts, count: added)
                // Summaries say who sounds like what: cast the voices that were unsure.
                VoiceCastingRun.castIfNeeded(document: document)
                let notes = notesWritten == 0 ? "" : (notesWritten == 1 && cast.count == 1 ? " Voice notes updated." : " Voice notes updated for \(notesWritten) characters.")
                    + (notesWritten == 0 ? "" : " Voices casting picked may change; edit a character’s voice notes to fine-tune how they sound.")
                self.finished = (added == 0 ? "No new facts found." : (cast.count == 1 ? "Added \(added) facts to \(cast[0].firstName)’s summary." : "Added \(added) facts to the cast’s summaries.")) + notes
            } catch {
                if Task.isCancelled || error is CancellationError {
                    self.finished = "Stopped. Facts found so far are kept."
                } else {
                    self.failure = error.localizedDescription
                }
            }
        }
    }

    /// Shows a status without running anything (snapshot mode).
    func debugStatus(_ message: String?, _ progress: Double) {
        status = message.map { Status(job: .tagging, progress: progress, message: $0) }
    }

    /// Build Summaries' progress without running it (snapshot mode).
    func debugSummaries(_ name: String?, number: Int = 1, count: Int = 1, character: Double = 0, overall: Double = 0) {
        status = name.map { Status(job: .summaries, progress: overall, message: "", character: $0, characterProgress: character,
                                   characterNumber: number, characterCount: count) }
    }

    static func log(_ s: String) {
        NSLog("InnerVoice tagging: %@", s)
    }
}
