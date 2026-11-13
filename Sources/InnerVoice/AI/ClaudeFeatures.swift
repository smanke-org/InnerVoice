import AppKit
import InnerVoiceKit
import Observation

/// The questions before a whole-book Claude run: the book's permission (asked once, with the
/// estimated cost), then, on later runs, the estimated cost again unless the narrator turned
/// that off. The estimate is worked out on the Mac; nothing is sent to count tokens.
@MainActor
enum ClaudeAsk {
    static let skipKey = "claude.skipCostQuestion"

    /// The Claude model the Claude-only features use: the chosen model when it's Claude,
    /// otherwise the default (Sonnet).
    static var featureModelID: String {
        let s = ModelManager.shared.selection
        return s.hasPrefix("claude:") ? String(s.dropFirst(7)) : ClaudeAccount.defaultModelID
    }

    /// True to go ahead. `what` says what is sent ("the book, a passage at a time").
    static func confirm(_ document: InnerVoiceDocument, undoManager: UndoManager?, action: String, modelID: String,
                        estimate: CostEstimate, what: String = "the book, a passage at a time") -> Bool {
        let name = ClaudeAccount.shared.name(of: modelID)
        if document.project.settings.cloudTextAllowed == nil {
            guard !SystemHooks.isTestRun else { return false }
            let alert = NSAlert()
            alert.messageText = "Send this book to Claude?"
            alert.informativeText = ClaudeAccount.warning
                + "\n\n\(action) sends \(what) to \(name). \(estimate.sentence(modelID: modelID))"
                + "\n\nInnerVoice sends text only when you run an AI feature. To stop, choose Book › Stop Sending This Book to Claude."
            alert.addButton(withTitle: "Don't Send")
            alert.addButton(withTitle: "Send to Claude")
            guard alert.runModal() == .alertSecondButtonReturn else { return false }
            document.setCloudTextAllowed(true, undoManager: undoManager)
            return true
        }
        guard !SystemHooks.isTestRun, !UserDefaults.standard.bool(forKey: skipKey) else { return true }
        let alert = NSAlert()
        alert.messageText = "\(action) with \(name)?"
        alert.informativeText = "This sends \(what) to Anthropic (Cloud). \(estimate.sentence(modelID: modelID))"
        alert.showsSuppressionButton = true
        alert.suppressionButton?.title = "Don’t show the estimate again"
        alert.addButton(withTitle: action)
        alert.addButton(withTitle: "Cancel")
        let ok = alert.runModal() == .alertFirstButtonReturn
        if ok, alert.suppressionButton?.state == .on { UserDefaults.standard.set(true, forKey: skipKey) }
        return ok
    }
}

/// The Claude-only features (all Cloud): Clean Up Text, Write Chapter Recaps, Suggest
/// Delivery Hints and Suggest Spellings. Each sends text only with a key, the narrator's yes
/// in Settings and this book's permission (`ClaudeTextModel` checks all three). Results wait
/// for the narrator in a review list when Project Settings › Review Claude's changes is on
/// (spellings always do: the narrator confirms each), and are applied in one Undo step.
@Observable
@MainActor
final class ClaudeFeatures {
    enum Job: String, CaseIterable, Identifiable {
        case cleanup = "Clean Up Text"
        case recaps = "Write Chapter Recaps"
        case delivery = "Suggest Delivery Hints"
        case spellings = "Suggest Spellings"
        var id: String { rawValue }
    }

    struct RecapProposal: Identifiable, Hashable {
        var id: UUID { chapterID }
        var chapterID: UUID
        var text: String
        var before: String?
    }

    /// Results waiting for the narrator to keep or leave out.
    enum Proposals {
        case cleanup([CleanupFix])
        case recaps([RecapProposal])
        case delivery([DeliveryProposal])
        case spellings([SpellingProposal])

        var count: Int {
            switch self {
            case .cleanup(let a): a.count
            case .recaps(let a): a.count
            case .delivery(let a): a.count
            case .spellings(let a): a.count
            }
        }
    }

    struct Pending {
        var proposals: Proposals
        var model: String
    }

    struct Status: Equatable {
        var job: Job
        var progress: Double
        var message: String
    }

    private(set) var status: Status?
    var finished: String?
    var failure: String?
    var pending: Pending?

    @ObservationIgnored private weak var document: InnerVoiceDocument?
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var model: ClaudeTextModel?

    init(document: InnerVoiceDocument) { self.document = document }

    var isRunning: Bool { status != nil }

    func stop() {
        model?.cancel()
        task?.cancel()
    }

    /// Whether this job applies to this book (project type).
    static func offered(_ job: Job, in project: Project) -> Bool {
        switch job {
        case .cleanup, .recaps: true
        case .delivery: project.showsVoices
        case .spellings: project.showsPronunciations
        }
    }

    // MARK: Requests

    private struct Request {
        var messages: [ChatMessage]
        var maxTokens: Int
        var labels: [String: UUID] = [:]
        var chapterID: UUID? = nil
    }

    private func requests(_ job: Job, _ p: Project, _ index: BookIndex, passageWords: Int) -> [Request] {
        switch job {
        case .cleanup:
            CleanupPrompt.chunks(project: p, maxWords: passageWords).map { Request(messages: $0.messages, maxTokens: 1500, labels: $0.labels) }
        case .recaps:
            RecapPrompt.jobs(project: p).map { Request(messages: $0.messages, maxTokens: 400, chapterID: $0.chapterID) }
        case .delivery:
            DeliveryPrompt.chunks(project: p, index: index, maxWords: passageWords).map { Request(messages: $0.messages, maxTokens: 1200, labels: $0.labels) }
        case .spellings:
            SpellingPrompt.chunks(project: p, index: index).map { Request(messages: $0.messages, maxTokens: 2000, labels: $0.labels) }
        }
    }

    private static func outputEach(_ job: Job) -> Int {
        switch job {
        case .cleanup: 250
        case .recaps: 150
        case .delivery: 200
        case .spellings: 900
        }
    }

    private static func what(_ job: Job) -> String {
        switch job {
        case .cleanup, .delivery: "the book, a passage at a time"
        case .recaps: "each chapter (to recap it for the next one)"
        case .spellings: "each unfinished term in the pronunciation guide, with the sentence it first appears in"
        }
    }

    /// What a run would cost, worked out on the Mac.
    func estimate(_ job: Job) -> CostEstimate {
        guard let document else { return CostEstimate() }
        let reqs = requests(job, document.project, document.index, passageWords: 2500)
        return CostEstimator.requests(reqs.map(\.messages), outputEach: Self.outputEach(job))
    }

    // MARK: Run

    func run(_ job: Job, undoManager: UndoManager?) {
        guard let document, !isRunning, !document.tagging.isRunning else { return }
        failure = nil
        finished = nil
        guard ClaudeAccount.shared.isSetUp else {
            failure = "Set up Claude first: Book › Choose Model…, then Set Up next to Claude."
            return
        }
        let modelID = ClaudeAsk.featureModelID
        let reqs = requests(job, document.project, document.index, passageWords: 2500)
        guard !reqs.isEmpty else {
            finished = switch job {
            case .cleanup: "There’s no text to clean up."
            case .recaps: "No chapters need a recap: the first has nothing before it, and recaps you wrote are kept."
            case .delivery: "No lines to direct yet. Tag Speakers first; lines with your own hints are kept."
            case .spellings: "Every term in the pronunciation guide has a Say It As already."
            }
            return
        }
        let estimate = CostEstimator.requests(reqs.map(\.messages), outputEach: Self.outputEach(job))
        guard ClaudeAsk.confirm(document, undoManager: undoManager, action: job.rawValue, modelID: modelID,
                                estimate: estimate, what: Self.what(job)) else { return }
        status = Status(job: job, progress: 0, message: "\(job.rawValue) with Claude…")
        task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.status = nil
                self.model = nil
            }
            do {
                let model = try ClaudeTextModel(modelID: modelID, bookAllowed: document.project.settings.cloudTextAllowed != nil, book: document.project.usageKey)
                self.model = model
                var cleanup: [CleanupFix] = []
                var recaps: [RecapProposal] = []
                var delivery: [DeliveryProposal] = []
                var spellings: [SpellingProposal] = []
                var refused = 0
                var unreadable = 0
                for (n, req) in reqs.enumerated() {
                    try Task.checkCancellation()
                    self.status?.progress = Double(n) / Double(reqs.count)
                    self.status?.message = "\(job.rawValue) with \(model.displayName) (\(n + 1) of \(reqs.count))…"
                    let text: String
                    do {
                        text = try await model.complete(req.messages, grammar: nil, maxTokens: req.maxTokens)
                    } catch ClaudeError.refused {
                        refused += 1
                        continue
                    }
                    try Task.checkCancellation()
                    let p = document.project
                    let index = document.index
                    switch job {
                    case .cleanup:
                        let chunk = CleanupPrompt.Chunk(labels: req.labels, messages: req.messages)
                        if let replies = CleanupPrompt.parse(text) {
                            cleanup += CleanupPrompt.fixes(from: replies, chunk: chunk, project: p, index: index)
                        } else { unreadable += 1 }
                    case .recaps:
                        if let id = req.chapterID, let recap = RecapPrompt.parse(text) {
                            recaps.append(RecapProposal(chapterID: id, text: recap, before: p.chapters.first { $0.id == id }?.recapText))
                        } else { unreadable += 1 }
                    case .delivery:
                        let chunk = DeliveryPrompt.Chunk(labels: req.labels, messages: req.messages)
                        if let replies = DeliveryPrompt.parse(text) {
                            delivery += DeliveryPrompt.proposals(replies, chunk: chunk, project: p, index: index)
                        } else { unreadable += 1 }
                    case .spellings:
                        let chunk = SpellingPrompt.Chunk(labels: req.labels, messages: req.messages)
                        if let found = SpellingPrompt.parse(text, chunk: chunk, project: p) {
                            spellings += found
                        } else { unreadable += 1 }
                    }
                }
                let proposals: Proposals = switch job {
                case .cleanup: .cleanup(cleanup)
                case .recaps: .recaps(recaps)
                case .delivery: .delivery(delivery)
                case .spellings: .spellings(spellings)
                }
                let skipped = refused + unreadable
                let note = skipped == 0 ? "" : " Claude didn’t answer for \(skipped) of \(reqs.count) " + (skipped == 1 ? "passage." : "passages.")
                if proposals.count == 0 {
                    self.finished = Self.nothing(job) + note
                } else if job == .spellings || document.project.settings.reviewsCloudAI {
                    self.pending = Pending(proposals: proposals, model: model.displayName)
                    self.finished = Self.found(job, proposals.count) + note
                } else {
                    self.apply(proposals, undoManager: undoManager)
                    self.finished = Self.applied(job, proposals.count) + note
                }
            } catch is CancellationError {
                self.finished = "Stopped. Nothing was changed."
            } catch {
                if Task.isCancelled { self.finished = "Stopped. Nothing was changed." } else { self.failure = error.localizedDescription }
            }
        }
    }

    private static func nothing(_ job: Job) -> String {
        switch job {
        case .cleanup: "Claude found no import problems to fix."
        case .recaps: "Claude wrote no recaps."
        case .delivery: "Claude found no lines that need more than their tone."
        case .spellings: "Claude had no spellings to suggest."
        }
    }

    private static func found(_ job: Job, _ n: Int) -> String {
        switch job {
        case .cleanup: "Claude proposed \(n.formatted()) " + (n == 1 ? "fix" : "fixes") + " to review."
        case .recaps: "Claude wrote \(n.formatted()) " + (n == 1 ? "recap" : "recaps") + " to review."
        case .delivery: "Claude suggested \(n.formatted()) delivery " + (n == 1 ? "hint" : "hints") + " to review."
        case .spellings: "Claude suggested \(n.formatted()) " + (n == 1 ? "spelling" : "spellings") + " to confirm."
        }
    }

    private static func applied(_ job: Job, _ n: Int) -> String {
        switch job {
        case .cleanup: "Fixed \(n.formatted()) import " + (n == 1 ? "problem." : "problems.") + " Edit › Undo puts the text back."
        case .recaps: "Wrote \(n.formatted()) chapter " + (n == 1 ? "recap." : "recaps.")
        case .delivery: "Added \(n.formatted()) delivery " + (n == 1 ? "hint." : "hints.")
        case .spellings: ""
        }
    }

    /// Applies the kept proposals in one Undo step.
    func apply(_ proposals: Proposals, undoManager: UndoManager?) {
        guard let document, proposals.count > 0 else { return }
        switch proposals {
        case .cleanup(let fixes):
            document.change("Clean Up Text", undoManager: undoManager, textChanged: true) { TextCleanup.apply(fixes, to: &$0) }
        case .recaps(let recaps):
            document.change("Write Chapter Recaps", undoManager: undoManager) { p in
                for r in recaps { RecapPrompt.put(r.text, chapterID: r.chapterID, in: &p) }
            }
        case .delivery(let hints):
            document.change("Delivery Hints", undoManager: undoManager) { DeliveryPrompt.apply(hints, to: &$0) }
        case .spellings(let kept):
            document.change("Use Suggested Spellings", undoManager: undoManager) { _ = SpellingPrompt.apply(kept, to: &$0) }
        }
    }

    /// Snapshots: a run in progress.
    func debugStatus(_ job: Job, _ progress: Double, _ message: String) { status = Status(job: job, progress: progress, message: message) }
    func debugClearStatus() { status = nil }
}
