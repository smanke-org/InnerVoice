import AppKit
import InnerVoiceKit
import SwiftUI

struct ProjectSidebar: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    private enum Row: Hashable {
        case page(Workspace.Page)
        case chapter(Int)
        case character(UUID)
    }

    var body: some View {
        let project = document.project
        let index = document.index
        List(selection: selection) {
            Section("Book") {
                ForEach(pages) { page in
                    Label(page.title, systemImage: page.symbol)
                        .badge(badge(page))
                        .tag(Row.page(page))
                }
            }
            Section("Chapters") {
                ForEach(Array(project.chapters.enumerated()), id: \.element.id) { i, chapter in
                    let needs = needsCount(chapterIndex: i)
                    HStack {
                        Text(chapter.title).lineLimit(1)
                        Spacer()
                        if needs > 0 {
                            Text("\(needs)")
                                .font(.caption.monospacedDigit().weight(.semibold))
                                .foregroundStyle(Color(nsColor: .attention))
                                .help("\(needs) lines in this chapter need you")
                        } else if chapterTagged(i) {
                            Image(systemName: "checkmark")
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(.tertiary)
                                .help("Every line in this chapter has a speaker")
                        }
                    }
                    .tag(Row.chapter(i))
                    .contextMenu {
                        Button("Tag This Chapter Again") {
                            document.tagging.retag(chapters: [i], undoManager: nil)
                        }
                        .disabled(document.tagging.isRunning || !ModelManager.shared.isReady)
                        Divider()
                        Button("Delete Chapter") {
                            document.deleteText(cuts: [:], chapters: [chapter.id], undoManager: undoManager)
                        }
                        .disabled(document.tagging.isRunning)
                    }
                }
            }
            let cast = project.characters.sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
            if !cast.isEmpty {
                Section("Cast") {
                    ForEach(cast.prefix(12)) { c in
                        HStack(spacing: Space.sm) {
                            Circle().fill(Color(hexString: c.colorHex)).frame(width: 10, height: 10)
                            Text(c.name).lineLimit(1)
                            Spacer()
                            Text("\(index.stats[c.id]?.lines ?? 0)")
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                        }
                        .tag(Row.character(c.id))
                    }
                }
            }
        }
        .listStyle(.sidebar)
    }

    private var pages: [Workspace.Page] {
        Workspace.Page.allCases.filter { $0 != .revisions || !document.project.revisions.isEmpty }
    }

    private var selection: Binding<Row?> {
        Binding(get: {
            if workspace.page == .cast, let id = workspace.castPath.last { return .character(id) }
            return .page(workspace.page)
        }, set: { row in
            switch row {
            case .page(let p)?:
                workspace.page = p
                if p == .cast { workspace.castPath = [] }
            case .chapter(let i)?:
                if let first = document.project.chapters[i].paragraphs.first {
                    workspace.show(paragraph: first.id)
                    workspace.visibleChapter = i
                }
            case .character(let id)?:
                workspace.showCharacter(id)
            case nil: break
            }
        })
    }

    private func badge(_ page: Workspace.Page) -> Int {
        switch page {
        case .review: document.project.linesNeedingYou
        case .retakes: document.project.retakes.filter { !$0.done }.count
        case .voices: document.project.characters.filter { (document.index.stats[$0.id]?.lines ?? 0) > 0 && document.project.voiceNeedsYou($0) }.count
        default: 0
        }
    }

    private func needsCount(chapterIndex: Int) -> Int {
        let ids = Set(document.project.chapters[chapterIndex].paragraphs.map(\.id))
        return document.project.lines.filter { ids.contains($0.paragraphID) && document.project.needsYou($0) }.count
    }

    private func chapterTagged(_ i: Int) -> Bool {
        guard document.project.lastTagged != nil else { return false }
        let ids = Set(document.project.chapters[i].paragraphs.map(\.id))
        return document.project.lines.contains { ids.contains($0.paragraphID) }
    }
}

/// The selected line and its speaker, or the chapter's cast when nothing is selected.
struct ProjectInspector: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Space.xl) {
                if let line = selectedLine {
                    lineSection(line)
                    if let c = document.project.character(line.speakerID) {
                        CharacterCard(document: document, character: c, workspace: workspace)
                    }
                } else {
                    chapterSection
                }
                legend
            }
            .padding(Space.lg)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var selectedLine: Line? {
        guard workspace.selectedLines.count == 1, let i = document.index.lineIndexByID[workspace.selectedLines[0]] else { return nil }
        return document.project.lines[i]
    }

    private func lineSection(_ line: Line) -> some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Text("Line").font(.headline)
            Text("“" + document.project.text(of: line, index: document.index) + "”")
                .font(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(6)
                .textSelection(.enabled)
            LabeledContent("Speaker") {
                Menu {
                    let order = document.project.speakerOrder
                    if !order.pinned.isEmpty {
                        Section("Pinned") {
                            ForEach(order.pinned) { c in
                                Button(c.name) { document.assign([line.id], to: c.id, undoManager: undoManager) }
                            }
                        }
                    }
                    Section(order.pinned.isEmpty ? "" : "Everyone Else") {
                        ForEach(order.others) { c in
                            Button(c.name) { document.assign([line.id], to: c.id, undoManager: undoManager) }
                        }
                    }
                    Divider()
                    Button("Who's Speaking?") { document.assign([line.id], to: nil, undoManager: undoManager) }
                    Button("Narration (Not Spoken)") {
                        document.removeLines([line.id], undoManager: undoManager)
                        workspace.selectedLines = []
                    }
                } label: {
                    if let c = document.project.character(line.speakerID) {
                        Label(c.name, systemImage: "circle.fill")
                    } else {
                        Text("Who's Speaking?")
                    }
                }
                .fixedSize()
            }
            if document.project.needsYou(line), let guess = document.project.character(line.speakerID) {
                LabeledContent("") {
                    Button("Confirm \(guess.firstName)") { document.assign([line.id], to: guess.id, undoManager: undoManager) }
                        .help("\(guess.name) is a guess (\(Int(line.confidence * 100))% sure). Confirm it, and the line is yours: Tag Speakers won't change it.")
                }
            }
            LabeledContent("Kind") {
                Picker("Kind", selection: Binding(get: { line.kind }, set: { document.setKind($0, for: [line.id], undoManager: undoManager) })) {
                    ForEach(LineKind.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                .labelsHidden()
                .fixedSize()
            }
            LabeledContent("Tone") {
                Menu {
                    ForEach(Tone.allCases) { t in
                        Button { document.setTone(t, for: [line.id], undoManager: undoManager) } label: {
                            Label(t.title, systemImage: t.symbol)
                        }
                    }
                } label: {
                    Text((line.tone ?? .neutral).title + (document.project.toneNeedsYou(line) ? "?" : ""))
                        .foregroundStyle(document.project.toneNeedsYou(line) ? Color(nsColor: .attention) : .primary)
                }
                .fixedSize()
                .help(toneHelp(line))
            }
            if document.project.toneNeedsYou(line), let t = line.tone {
                LabeledContent("") {
                    Button("Confirm \(t.title)") { document.setTone(t, for: [line.id], undoManager: undoManager) }
                        .help("Keep this tone. Tag Speakers won’t change it.")
                }
            }
            LabeledContent("Voice") {
                HStack(spacing: Space.sm) {
                    Button("Play", systemImage: "play.fill") { PlayCommand.playLine(document: document, workspace: workspace) }
                        .help("Hear the line in its character’s voice (⌥Space)")
                    Button("Voices") { workspace.showVoice(line.speakerID) }
                        .help("Choose the voice on the Voices page")
                }
                .controlSize(.small)
            }
            LabeledContent("Source") {
                HStack(spacing: Space.xs) {
                    if line.source == .ai { Image(systemName: "sparkles").foregroundStyle(.secondary) }
                    Text((line.source == .rule && line.confidence < 0.6 ? "Guessed from the paragraph" : line.source.title)
                         + ((line.source == .ai || line.source == .rule) && line.speakerID != nil && line.confidence < 0.95
                        ? ", \(Int(line.confidence * 100))% sure" : ""))
                }
                .font(.callout)
            }
            LabeledContent("Where") {
                Text(document.index.reference(line.paragraphID)).font(.callout.monospacedDigit())
            }
        }
    }

    private func toneHelp(_ line: Line) -> String {
        switch line.toneSource {
        case .user?: "You set this tone."
        case .ai?: "The AI read this tone, \(Int((line.toneConfidence ?? 0) * 100))% sure."
        case .rule?: (line.toneConfidence ?? 0) >= ToneRules.sureCue ? "Read from the speech tag." : "A guess from the words; Tag Speakers asks the AI."
        case nil: "Not read yet. Tag Speakers reads how each line is said."
        }
    }

    private var chapterSection: some View {
        let project = document.project
        let ci = min(workspace.visibleChapter, max(0, project.chapters.count - 1))
        let chapter = project.chapters.indices.contains(ci) ? project.chapters[ci] : nil
        let ids = Set(chapter?.paragraphs.map(\.id) ?? [])
        let lines = project.lines.filter { ids.contains($0.paragraphID) }
        var counts: [UUID: Int] = [:]
        for l in lines { if let s = l.speakerID { counts[s, default: 0] += 1 } }
        let needs = lines.filter { project.needsYou($0) }.count
        return VStack(alignment: .leading, spacing: Space.md) {
            Text(chapter?.title ?? "Chapter").font(.headline)
            HStack(spacing: Space.xl) {
                stat("\(lines.count)", "lines")
                stat("\(counts.count)", "speakers")
                stat("\(needs)", "need you", tint: needs > 0 ? Color(nsColor: .attention) : nil)
            }
            if !counts.isEmpty {
                VStack(alignment: .leading, spacing: Space.sm) {
                    ForEach(counts.sorted { $0.value > $1.value }, id: \.key) { id, n in
                        if let c = project.character(id) {
                            Button {
                                workspace.showCharacter(id)
                            } label: {
                                HStack(spacing: Space.sm) {
                                    Circle().fill(Color(hexString: c.colorHex)).frame(width: 10, height: 10)
                                    Text(c.name)
                                    Spacer()
                                    Text("\(n)").font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                                }
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
            Text("Click a line to choose who says it.")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }

    private func stat(_ value: String, _ label: String, tint: Color? = nil) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(value).font(.title2.weight(.semibold).monospacedDigit()).foregroundStyle(tint ?? .primary)
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
    }

    private var legend: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Divider()
            Text("Key").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            legendRow(Image(systemName: "questionmark.circle.fill"), "Red: Who's Speaking? or a guess (Name?)", Color(nsColor: .attention))
            legendRow(Image(systemName: "rectangle.dashed"), "Dashed: a thought", .secondary)
            legendRow(Image(systemName: "ellipsis.rectangle"), "Dotted: written (a letter, a sign)", .secondary)
        }
    }

    private func legendRow(_ image: Image, _ text: String, _ tint: Color) -> some View {
        HStack(spacing: Space.sm) {
            image.foregroundStyle(tint).frame(width: 16)
            Text(text).font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// A compact summary of a character: voice cue, key facts, stats.
struct CharacterCard: View {
    @ObservedObject var document: InnerVoiceDocument
    let character: BookCharacter
    @Bindable var workspace: Workspace

    var body: some View {
        let stats = document.index.stats[character.id]
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack(spacing: Space.sm) {
                Avatar(character: character, size: 32)
                VStack(alignment: .leading, spacing: 0) {
                    Text(character.name).font(.headline)
                    Text("\(stats?.lines ?? 0) lines · \(stats?.words ?? 0) words")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if !character.voiceNotes.isEmpty {
                Text(character.voiceNotes).font(.callout)
            }
            ForEach(FactKind.narratorOrder.prefix(4), id: \.self) { kind in
                if let f = character.facts.first(where: { $0.kind == kind }) {
                    LabeledContent(kind.title) { Text(f.text).multilineTextAlignment(.trailing) }
                        .font(.callout)
                }
            }
            if character.facts.isEmpty {
                Text("No summary yet. Build Summaries reads the book for details.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Button("Open \(character.name)") { workspace.showCharacter(character.id) }
                .controlSize(.small)
        }
        .padding(Space.md)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
    }
}

/// A character's initials on their color.
struct Avatar: View {
    let character: BookCharacter
    var size: CGFloat = 40

    var body: some View {
        let color = Color(hexString: character.colorHex)
        Text(character.initials)
            .font(.system(size: size * 0.38, weight: .semibold, design: .rounded))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(color.gradient, in: Circle())
            .accessibilityHidden(true)
    }
}
