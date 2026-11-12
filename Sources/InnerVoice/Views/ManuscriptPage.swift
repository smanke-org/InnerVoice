import AppKit
import InnerVoiceKit
import SwiftUI

/// The colored manuscript with its status banners and the exchange prompt.
struct ManuscriptPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @AppStorage("reading.fontSize", store: ReadingStyle.store) private var fontSize = ReadingStyle.standardSize
    @AppStorage("reading.serif", store: ReadingStyle.store) private var serif = true
    @AppStorage("reading.lineSpacing", store: ReadingStyle.store) private var spacing = LineSpacing.single.rawValue
    @AppStorage("reading.marginNames", store: ReadingStyle.store) private var marginNames = MarginNames.initials.rawValue

    private var look: ReadingLook {
        ReadingLook(fontSize: fontSize, serif: serif, spacing: LineSpacing(rawValue: spacing) ?? .single,
                    margin: MarginNames(rawValue: marginNames) ?? .initials)
    }

    var body: some View {
        ZStack(alignment: .bottom) {
            VStack(spacing: 0) {
                StatusBanners(document: document, workspace: workspace)
                ManuscriptTextView(document: document, workspace: workspace, undoManager: undoManager,
                                   onAssignRequest: showAssign, onVisibleChapter: { workspace.visibleChapter = $0 }, look: look)
            }
            if workspace.exchange.isEmpty, pickedCount > 1 {
                PickedLinesBanner(document: document, workspace: workspace)
                    .padding(.bottom, Space.xl)
                    .transition(.opacity.combined(with: .move(edge: .bottom)))
            }
            if !workspace.exchange.isEmpty {
                ExchangeBanner(count: workspace.exchange.count) {
                    document.applyExchange(workspace.exchange, undoManager: undoManager)
                    withAnimation(.easeOut(duration: 0.2)) { workspace.exchange = [] }
                } keep: {
                    withAnimation(.easeOut(duration: 0.2)) { workspace.exchange = [] }
                }
                .padding(.bottom, Space.xl)
                .transition(.opacity.combined(with: .move(edge: .bottom)))
            }
        }
        .navigationTitle(document.project.title)
        .pageSubtitle(subtitle)
    }

    /// Picked lines still in the book (Narration removes lines).
    private var pickedCount: Int {
        let picked = Set(workspace.selectedLines)
        return picked.count > 1 ? picked.filter { document.index.lineIndexByID[$0] != nil }.count : picked.count
    }

    private var subtitle: String {
        let chapter = document.project.chapters.indices.contains(workspace.visibleChapter)
            ? document.project.chapters[workspace.visibleChapter].title : ""
        return chapter
    }

    private func showAssign(_ ids: [UUID], _ rect: NSRect, _ view: NSView) {
        guard let lineID = ids.last else { return }
        let popover = AssignPopover(document: document, lineID: ids[0], undoManager: undoManager, onDone: { assigned, previous in
            PopoverPresenter.shared.close()
            // Several lines given at once: done with them.
            if ids.count > 1 { workspace.selectedLines = [] }
            guard let assigned, assigned != previous else { workspace.exchange = []; return }
            let proposals = Exchange.proposals(in: document.project, after: lineID, previousSpeaker: previous)
            withAnimation(.easeOut(duration: 0.2)) { workspace.exchange = proposals }
        }, onClose: {
            PopoverPresenter.shared.close()
            workspace.selectedLines = workspace.selectedLines.filter { document.index.lineIndexByID[$0] != nil }
        }, group: ids)
        PopoverPresenter.shared.show(popover, at: rect, in: view)
    }
}

/// Several lines picked (⌘-click names in the margin, or lines): assign them all at once.
struct PickedLinesBanner: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace

    var body: some View {
        let lines = document.project.lines.filter { Set(workspace.selectedLines).contains($0.id) }
        let picked = Set(lines.map(\.id))
        let paragraphs = Set(lines.map(\.paragraphID)).count
        HStack(spacing: Space.md) {
            Image(systemName: "checklist").foregroundStyle(.secondary)
            Text("\(picked.count) lines" + (paragraphs > 1 ? " in \(paragraphs) paragraphs" : "") + " picked")
            Text("⌘-click names to add more").foregroundStyle(.secondary)
            Button("Clear") { workspace.selectedLines = [] }
                .keyboardShortcut(.cancelAction)
            Button("Assign…") {
                if let first = lines.first { workspace.pendingAssign = first.id }
            }
            .buttonStyle(.borderedProminent)
            .help("Give every picked line to one character (Return)")
        }
        .padding(.horizontal, Space.lg)
        .padding(.vertical, Space.sm)
        .glassEffect(.regular, in: Capsule())
        .accessibilityElement(children: .contain)
    }
}

/// "Swap the next 6 lines of this exchange too?"
struct ExchangeBanner: View {
    var count: Int
    var swap: () -> Void
    var keep: () -> Void

    var body: some View {
        HStack(spacing: Space.md) {
            Image(systemName: "arrow.left.arrow.right")
                .foregroundStyle(.secondary)
            Text(count == 1 ? "Swap the next line of this exchange too?" : "Swap the next \(count) lines of this exchange too?")
            Button("Keep", action: keep)
                .keyboardShortcut(.cancelAction)
            Button("Swap", action: swap)
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
        }
        .padding(.horizontal, Space.lg)
        .padding(.vertical, Space.sm)
        .glassEffect(.regular, in: Capsule())
        .accessibilityElement(children: .contain)
    }
}

/// The AI's progress, its result, and the first-run hint.
struct StatusBanners: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    /// With the steps showing, Tag Speakers is their next step: no second invitation here.
    @AppStorage(GuidedStepsBar.hiddenKey) private var stepsHidden = false

    var body: some View {
        let tagging = document.tagging
        VStack(spacing: 0) {
            if let status = tagging.status {
                banner(symbol: "sparkles", tint: .accentColor) {
                    VStack(alignment: .leading, spacing: Space.xs) {
                        Text(status.message).lineLimit(1)
                        ProgressView(value: status.progress)
                            .progressViewStyle(.linear)
                            .frame(maxWidth: 360)
                    }
                } actions: {
                    Button("Stop") { tagging.stop() }
                        .keyboardShortcut(".", modifiers: .command)
                }
            } else if let failure = tagging.failure {
                banner(symbol: "exclamationmark.triangle.fill", tint: .orange) {
                    Text(failure)
                } actions: {
                    Button("Choose Model…") { workspace.showModelSheet = true }
                    Button("Dismiss") { tagging.failure = nil }
                }
            } else if let finished = tagging.finished {
                banner(symbol: "checkmark.circle.fill", tint: .green) {
                    Text(finished)
                } actions: {
                    if document.project.linesNeedingYou > 0 {
                        Button("Show Lines That Need You") {
                            tagging.finished = nil
                            NextLine.go(document: document, workspace: workspace, forward: true)
                        }
                    }
                    Button("Dismiss") { tagging.finished = nil }
                }
            } else if stepsHidden, document.project.lastTagged == nil, !document.project.lines.isEmpty {
                banner(symbol: "sparkles", tint: .accentColor) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("InnerVoice found \(document.project.lines.count.formatted()) lines in \(document.project.chapters.count) chapters.")
                        Text("Tag Speakers uses the AI on this Mac to find who says each one. Your manuscript never leaves this Mac.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                } actions: {
                    Button("Tag Speakers") { TagCommand.run(document: document, workspace: workspace, undoManager: undoManager) }
                        .buttonStyle(.borderedProminent)
                }
            }
        }
        .animation(.easeOut(duration: 0.2), value: tagging.status?.message)
    }

    private func banner<Content: View, Actions: View>(symbol: String, tint: Color, @ViewBuilder content: () -> Content,
                                                      @ViewBuilder actions: () -> Actions) -> some View {
        VStack(spacing: 0) {
            HStack(alignment: .center, spacing: Space.md) {
                Image(systemName: symbol)
                    .font(.title3)
                    .foregroundStyle(tint)
                    .symbolEffect(.pulse, isActive: symbol == "sparkles" && document.tagging.isRunning)
                content()
                Spacer(minLength: Space.md)
                HStack(spacing: Space.sm) { actions() }
            }
            .padding(.horizontal, Space.xl)
            .padding(.vertical, Space.md)
            .background(.bar)
            Divider()
        }
    }
}

/// Starts tagging, or asks for a model first.
enum TagCommand {
    static func run(document: InnerVoiceDocument, workspace: Workspace, undoManager: UndoManager?) {
        guard ModelManager.shared.isReady else {
            workspace.showModelSheet = true
            return
        }
        document.tagging.tagSpeakers(undoManager: undoManager)
    }

    static func summaries(document: InnerVoiceDocument, workspace: Workspace, undoManager: UndoManager?, only: [UUID]? = nil,
                          confirmed: Bool = false) {
        guard ModelManager.shared.isReady else {
            workspace.showModelSheet = true
            return
        }
        // The whole cast was analyzed before: ask first (new notes can change the voices).
        if only == nil, !confirmed, document.project.summariesBuilt != nil {
            workspace.askAnalyzeCastAgain = true
            return
        }
        document.tagging.buildSummaries(undoManager: undoManager, only: only)
    }
}

/// ⌘↓ / ⌘↑: the next line that is red.
enum NextLine {
    static func go(document: InnerVoiceDocument, workspace: Workspace, forward: Bool) {
        let project = document.project
        let index = document.index
        let needing = project.lines.indices.filter { project.needsYou(project.lines[$0]) }
        guard !needing.isEmpty else {
            workspace.message = "No lines need you."
            return
        }
        let current = workspace.selectedLines.first.flatMap { index.lineIndexByID[$0] }
        let target: Int
        if let current {
            target = forward ? (needing.first { $0 > current } ?? needing[0]) : (needing.last { $0 < current } ?? needing[needing.count - 1])
        } else {
            target = forward ? needing[0] : needing[needing.count - 1]
        }
        let line = project.lines[target]
        workspace.page = .manuscript
        workspace.show(paragraph: line.paragraphID, line: line.id)
        // Assign To opens on it with the AI's guess picked: Return confirms, typing picks
        // someone else, Esc leaves it.
        workspace.selectedLines = [line.id]
        workspace.pendingAssign = line.id
    }
}
