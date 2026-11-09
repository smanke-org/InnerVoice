import InnerVoiceKit
import SwiftUI

/// The steps from manuscript to finished book, across the top of every page: where the
/// book stands, and the next step with one button to start it. Nothing runs until the
/// narrator clicks. Any step can be skipped, and the whole bar hidden (View › Hide Steps).
struct GuidedStepsBar: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    var undoManager: UndoManager?
    @AppStorage(GuidedStepsBar.hiddenKey) private var hidden = false

    static let hiddenKey = "steps.hidden"

    var body: some View {
        let steps = Self.steps(document)
        VStack(spacing: 0) {
            chips(steps)
                .padding(.horizontal, Space.md)
                .padding(.top, Space.sm)
                .padding(.bottom, Space.xs)
            next(steps)
                .padding(.horizontal, Space.md)
                .padding(.bottom, Space.sm)
            Divider()
        }
        .frame(maxWidth: .infinity)
        .background(.bar)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Steps")
    }

    static func steps(_ document: InnerVoiceDocument) -> GuidedSteps {
        let p = document.project
        let speaking = p.characters.filter { (document.index.stats[$0.id]?.lines ?? 0) > 0 }
        let engine = p.engineInUse
        return GuidedSteps(project: p, narration: !p.showsVoices, speaking: speaking.count,
                           voicesNeeded: speaking.filter { p.voiceNeedsYou($0, engine: engine) }.count)
    }

    // MARK: The steps

    private func chips(_ steps: GuidedSteps) -> some View {
        let current = steps.current?.step
        return HStack(spacing: Space.xs) {
            ForEach(Array(steps.all.enumerated()), id: \.element.id) { i, s in
                if i > 0 {
                    Image(systemName: "chevron.compact.right").foregroundStyle(.tertiary).accessibilityHidden(true)
                }
                chip(s, isCurrent: s.step == current)
            }
        }
    }

    private func chip(_ s: GuidedStepStatus, isCurrent: Bool) -> some View {
        Button { go(s.step) } label: {
            HStack(spacing: Space.sm) {
                ZStack {
                    if s.isDone {
                        Circle().fill(Color.green)
                        Image(systemName: "checkmark").font(.caption2.weight(.bold)).foregroundStyle(.white)
                    } else if isCurrent {
                        Circle().fill(Color.accentColor)
                        Text("\(s.number)").font(.caption.weight(.semibold)).foregroundStyle(.white)
                    } else {
                        Circle().strokeBorder(.secondary, lineWidth: 1)
                        Text("\(s.number)").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .frame(width: 18, height: 18)
                VStack(alignment: .leading, spacing: 0) {
                    Text(s.step.title)
                        .font(.callout.weight(isCurrent ? .semibold : .regular))
                        .foregroundStyle(s.isDone || isCurrent ? .primary : .secondary)
                    Text(s.isSkipped ? "Skipped" : s.detail)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .lineLimit(1)
            }
            .padding(.horizontal, Space.sm)
            .padding(.vertical, Space.xs)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(isCurrent ? Color.accentColor.opacity(0.12) : .clear, in: RoundedRectangle(cornerRadius: 8))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(Self.help(s.step, document: document))
        .accessibilityLabel("Step \(s.number), \(s.step.title), \(s.isDone ? "done" : (isCurrent ? "next" : "not done")), \(s.detail)")
        .contextMenu {
            if s.isMarked {
                Button("Mark as Not Done") { mark(s.step, false) }
            } else if !s.isDone {
                Button(s.step.isLast ? "Mark as Done" : "Skip This Step") { mark(s.step, true) }
            }
            Divider()
            Button("Hide Steps") { hidden = true }
        }
    }

    // MARK: The next step

    @ViewBuilder
    private func next(_ steps: GuidedSteps) -> some View {
        let card = Self.card(for: steps.current?.step, steps: steps, document: document, page: workspace.page)
        HStack(spacing: Space.md) {
            Image(systemName: card.symbol)
                .font(.title3)
                .foregroundStyle(.tint)
                .frame(width: 24)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text(card.title).font(.headline)
                Text(card.text)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .help(card.text)
            Spacer(minLength: Space.md)
            if let step = steps.current?.step, !card.running {
                Button(step.isLast ? "Mark as Done" : "Skip") { mark(step, true) }
                    .help(step.isLast ? "Mark this step done" : "Skip this step for now. Right-click it above to bring it back.")
            }
            if let action = card.action, !card.running {
                Button(action.title) { run(action.kind) }
                    .buttonStyle(.borderedProminent)
            }
            if steps.allDone {
                Button("Hide Steps") { hidden = true }
                    .buttonStyle(.borderedProminent)
            }
            Button {
                hidden = true
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .help("Hide the steps. Show them again with View › Show Steps.")
            .accessibilityLabel("Hide Steps")
        }
    }

    struct Card {
        enum Kind { case manuscriptReady, tag, review, summaries, castVoices, showVoices, exportAudio, pronunciations, record }
        struct Action { var title: String; var kind: Kind }
        var symbol: String
        var title: String
        var text: String
        var action: Action?
        var running = false
    }

    static func card(for step: GuidedStep?, steps: GuidedSteps, document: InnerVoiceDocument, page: Workspace.Page) -> Card {
        let p = document.project
        let job = document.tagging.status?.job
        switch step {
        case .manuscript?:
            return Card(symbol: "doc.text.magnifyingglass", title: "Check the manuscript",
                        text: "Select anything you won’t read aloud, like a copyright page, an ISBN or ads for other books, and press Delete. The file you imported doesn’t change.",
                        action: .init(title: "It’s Ready", kind: .manuscriptReady))
        case .speakers?:
            if job == .tagging {
                return Card(symbol: "sparkles", title: "Tagging speakers…",
                            text: "Chapters fill with color as the AI finishes them. Keep reading while it works.", running: true)
            }
            return Card(symbol: "sparkles", title: "Tag the speakers",
                        text: "The AI on this Mac finds who says each of the \(p.lines.count.formatted()) lines. Lines it isn’t sure of turn red for you to check. Your manuscript stays on this Mac.",
                        action: .init(title: "Tag Speakers", kind: .tag))
        case .review?:
            let n = p.linesNeedingYou
            return Card(symbol: "person.fill.questionmark", title: n == 1 ? "Confirm who says 1 line" : "Confirm who says \(n.formatted()) lines",
                        text: "The AI wasn’t sure about these. Return confirms its guess, or type a name to pick someone else. A name that isn’t in the cast adds a new character.",
                        action: page == .review ? nil : .init(title: "Start Review", kind: .review))
        case .characters?:
            if job == .summaries {
                return Card(symbol: "sparkles", title: "Reading the book for each character…",
                            text: "Age, accent, voice and manner go into each character’s summary and Voice Notes.", running: true)
            }
            let use = p.showsVoices
                ? "Casting and the AI voices use them, so this makes each voice fit the character."
                : "Use them to prepare each character’s voice before you record."
            return Card(symbol: "person.text.rectangle", title: "Learn how each character sounds",
                        text: "The AI reads the book again for each character’s age, accent, pace and manner, and writes a voice brief into their Voice Notes. " + use,
                        action: .init(title: "Build Summaries", kind: .summaries))
        case .voices?:
            if p.voicesCast == nil {
                return Card(symbol: "person.wave.2", title: "Cast the voices",
                            text: "InnerVoice picks a voice for each character from their summary and Voice Notes. Then play each one and change any you don’t like.",
                            action: .init(title: "Cast Voices", kind: .castVoices))
            }
            let n = steps.all.first { $0.step == .voices }?.detail ?? ""
            return Card(symbol: "person.wave.2", title: "Check the voices (\(n))",
                        text: "Play each character’s voice with their own words and change any that don’t fit. Characters you added in Review need one too.",
                        action: page == .voices ? nil : .init(title: "Show Voices", kind: .showVoices))
        case .listen?:
            return Card(symbol: "play.circle", title: "Listen to the book",
                        text: "Play a table read from anywhere with ⌥⇧Space, then export the book, a chapter or one character’s lines as audio.",
                        action: .init(title: "Export Audio…", kind: .exportAudio))
        case .pronunciations?:
            return Card(symbol: "character.bubble", title: "Add pronunciations",
                        text: "InnerVoice lists the names, places and invented words it found. Add how to say each one, or dismiss the ones you already know.",
                        action: page == .pronunciations ? nil : .init(title: "Show Pronunciations", kind: .pronunciations))
        case .record?:
            return Card(symbol: "mic", title: "Record the book",
                        text: "A large-type teleprompter for the booth. Space moves on and marks the paragraph recorded; R marks a retake.",
                        action: page == .record ? nil : .init(title: "Start Recording", kind: .record))
        case nil:
            return Card(symbol: "checkmark.circle", title: "Every step is done",
                        text: "Hide the steps if you don’t need them. Show them again with View › Show Steps.")
        }
    }

    static func help(_ step: GuidedStep, document: InnerVoiceDocument) -> String {
        switch step {
        case .manuscript: "Delete what you won’t read aloud, then mark the manuscript ready"
        case .speakers: "The on-device AI finds who says every line (⇧⌘T)"
        case .review: "Confirm the lines the AI wasn’t sure of, and name new speakers"
        case .characters: "The AI reads the book again for how each character sounds (⇧⌘B)"
        case .voices: "Cast and audition a voice for every character"
        case .listen: "Play a table read and export the audio"
        case .pronunciations: "Say how to pronounce names, places and invented words"
        case .record: "Record with the teleprompter"
        }
    }

    // MARK: Actions

    private func go(_ step: GuidedStep) {
        switch step {
        case .manuscript, .speakers, .listen: workspace.page = .manuscript
        case .review: workspace.page = .review
        case .characters: workspace.page = .cast; workspace.castPath = []
        case .voices: workspace.page = .voices
        case .pronunciations: workspace.page = .pronunciations
        case .record: workspace.page = .record
        }
    }

    private func mark(_ step: GuidedStep, _ done: Bool) {
        let name = done ? (step.isLast ? "Mark Step Done" : "Skip Step") : "Mark Step Not Done"
        document.updateSettings(name, undoManager: undoManager) { $0.markStep(step, done: done) }
    }

    private func run(_ kind: Card.Kind) {
        switch kind {
        case .manuscriptReady: mark(.manuscript, true)
        case .tag:
            workspace.page = .manuscript
            TagCommand.run(document: document, workspace: workspace, undoManager: undoManager)
        case .review: workspace.page = .review
        case .summaries:
            workspace.page = .cast
            workspace.castPath = []
            TagCommand.summaries(document: document, workspace: workspace, undoManager: undoManager)
        case .castVoices:
            workspace.page = .voices
            document.castVoicesAgain(undoManager: undoManager)
        case .showVoices: workspace.page = .voices
        case .exportAudio: workspace.showAudioExport = true
        case .pronunciations: workspace.page = .pronunciations
        case .record: workspace.page = .record
        }
    }
}
