import AppKit
import InnerVoiceKit
import SwiftUI

/// Every shown character's lines, by chapter, each a link into the manuscript.
struct IndexPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var narrator: UUID?
    @State private var filter = ""
    /// Chapters folded by the narrator ("character|chapter"); everything starts open.
    @State private var collapsed: Set<String> = []

    var body: some View {
        HSplitView {
            characterList
                .frame(minWidth: 220, idealWidth: 250, maxWidth: 320)
            lineList
                .frame(minWidth: 380, maxWidth: .infinity)
        }
        .navigationTitle("Index")
        .pageSubtitle()
        .toolbar {
            if !document.project.narrators.isEmpty {
                ToolbarItem {
                    Picker("Narrator", selection: $narrator) {
                        Text("All Narrators").tag(UUID?.none)
                        ForEach(document.project.narrators) { Text($0.name).tag(UUID?.some($0.id)) }
                    }
                    .help("Show only the characters one narrator reads")
                }
            }
        }
    }

    private var cast: [BookCharacter] {
        let index = document.index
        return document.project.characters
            .filter { narrator == nil || $0.narratorID == narrator }
            .filter { filter.isEmpty || $0.name.localizedCaseInsensitiveContains(filter) }
            .sorted { (index.stats[$0.id]?.lines ?? 0) < (index.stats[$1.id]?.lines ?? 0) }
    }

    private var characterList: some View {
        VStack(spacing: 0) {
            TextField("Filter characters", text: $filter)
                .textFieldStyle(.roundedBorder)
                .padding(Space.md)
            List {
                Section {
                    ForEach(cast) { c in
                        Toggle(isOn: Binding(get: { document.index.showsInIndex(c) }, set: { v in
                            document.updateCharacter(c.id, "Show in Index", undoManager: undoManager) { $0.showInIndex = v }
                        })) {
                            HStack(spacing: Space.sm) {
                                Circle().fill(Color(hexString: c.colorHex)).frame(width: 10, height: 10)
                                Text(c.name).lineLimit(1)
                                Spacer()
                                Text("\(document.index.stats[c.id]?.lines ?? 0)")
                                    .font(.caption.monospacedDigit())
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .toggleStyle(.checkbox)
                    }
                } header: {
                    Text("Show in Index")
                } footer: {
                    Text("Main characters (over 15% of the lines) start hidden, so the index stays short.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .listStyle(.inset)
            HStack {
                Button("Show All") { setAll(true) }
                Button("Hide All") { setAll(false) }
            }
            .controlSize(.small)
            .padding(Space.sm)
        }
    }

    private func setAll(_ on: Bool) {
        let ids = Set(cast.map(\.id))
        document.change(on ? "Show All in Index" : "Hide All in Index", undoManager: undoManager) { p in
            for i in p.characters.indices where ids.contains(p.characters[i].id) { p.characters[i].showInIndex = on }
        }
    }

    private var lineList: some View {
        let project = document.project
        let index = document.index
        let shown = cast.filter { index.showsInIndex($0) }.reversed()
        return Group {
            if shown.isEmpty {
                ContentUnavailableView("No Characters Shown", systemImage: "list.bullet.indent",
                                       description: Text("Turn on characters at the left to list their lines."))
            } else {
                List {
                    ForEach(Array(shown)) { c in
                        Section {
                            let lines = project.lines.filter { $0.speakerID == c.id }
                            let byChapter = Dictionary(grouping: lines) { index.positions[$0.paragraphID]?.chapter ?? 0 }
                            ForEach(byChapter.keys.sorted(), id: \.self) { ch in
                                let key = "\(c.id)|\(ch)"
                                DisclosureGroup(isExpanded: Binding(get: { !collapsed.contains(key) }, set: { open in
                                    if open { collapsed.remove(key) } else { collapsed.insert(key) }
                                })) {
                                    ForEach(byChapter[ch] ?? []) { line in
                                        IndexLineRow(document: document, line: line, workspace: workspace)
                                    }
                                } label: {
                                    HStack {
                                        Text(project.chapters[ch].title).lineLimit(1)
                                        Spacer()
                                        Text("\(byChapter[ch]?.count ?? 0)")
                                            .font(.caption.monospacedDigit())
                                            .foregroundStyle(.secondary)
                                    }
                                }
                            }
                        } header: {
                            HStack(spacing: Space.sm) {
                                Avatar(character: c, size: 22)
                                Text(c.name).font(.headline)
                                Text("\(index.stats[c.id]?.lines ?? 0) lines").foregroundStyle(.secondary)
                                Spacer()
                                Button("Rehearse") { workspace.rehearse = c.id }
                                    .controlSize(.small)
                            }
                            .padding(.vertical, Space.xs)
                        }
                    }
                }
                .listStyle(.inset)
            }
        }
    }
}

struct IndexLineRow: View {
    @ObservedObject var document: InnerVoiceDocument
    let line: Line
    @Bindable var workspace: Workspace

    var body: some View {
        Button {
            workspace.show(paragraph: line.paragraphID, line: line.id)
        } label: {
            HStack(alignment: .firstTextBaseline, spacing: Space.md) {
                Text(document.index.reference(line.paragraphID))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .frame(width: 72, alignment: .leading)
                Text("“" + document.project.text(of: line, index: document.index) + "”")
                    .lineLimit(2)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if line.kind != .spoken {
                    Text(line.kind.title).font(.caption).foregroundStyle(.secondary)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Show this line in the manuscript")
    }
}

/// One character's lines, one at a time, each with the line before it as a cue.
struct RehearseView: View {
    @ObservedObject var document: InnerVoiceDocument
    let characterID: UUID
    @Bindable var workspace: Workspace
    @Environment(\.dismiss) private var dismiss
    @State private var position = 0
    @AppStorage("rehearse.speakCues") private var speakCues = true

    private var lines: [Line] { document.project.lines.filter { $0.speakerID == characterID } }

    /// Says a line in its character's voice and tone.
    private func play(_ line: Line) {
        VoiceCastingRun.castIfNeeded(document: document)
        let project = document.project
        let index = document.index
        guard let p = project.paragraph(line.paragraphID, index: index) else { return }
        let segs = SpeechScript.segments(for: p, project: project, index: index, includeNarration: false).filter { $0.lineID == line.id }
        SpeechPlayer.shared.play(segs, resolver: VoiceResolver(project: project))
    }

    private func speakCue() {
        guard speakCues, lines.indices.contains(position), let cue = cue(for: lines[position]) else { return }
        play(cue)
    }

    /// The line spoken just before (any speaker), as the cue.
    private func cue(for line: Line) -> Line? {
        guard let i = document.index.lineIndexByID[line.id], i > 0 else { return nil }
        return document.project.lines[i - 1]
    }

    var body: some View {
        let character = document.project.character(characterID)
        VStack(spacing: 0) {
            HStack {
                if let character { Avatar(character: character, size: 28) }
                Text("Rehearse \(character?.name ?? "")").font(.headline)
                Spacer()
                Text(lines.isEmpty ? "" : "\(position + 1) of \(lines.count)")
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            .padding(Space.lg)
            Divider()
            if lines.indices.contains(position) {
                let line = lines[position]
                VStack(alignment: .leading, spacing: Space.xl) {
                    if let cue = cue(for: line) {
                        VStack(alignment: .leading, spacing: Space.xs) {
                            HStack(spacing: Space.sm) {
                                Text("Cue · " + (document.project.character(cue.speakerID)?.name ?? "Who's Speaking?"))
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(.secondary)
                                Button { play(cue) } label: { Image(systemName: "play.circle") }
                                    .buttonStyle(.plain)
                                    .help("Hear the cue in \(document.project.character(cue.speakerID)?.firstName ?? "its speaker")’s voice")
                            }
                            Text("“" + document.project.text(of: cue, index: document.index) + "”")
                                .font(.system(size: 15, design: .serif))
                                .foregroundStyle(.secondary)
                                .lineLimit(4)
                        }
                    }
                    VStack(alignment: .leading, spacing: Space.xs) {
                        Text(document.index.reference(line.paragraphID) + (line.kind == .spoken ? "" : " · " + line.kind.title))
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(Color(hexString: character?.colorHex ?? "#888888"))
                        Text("“" + document.project.text(of: line, index: document.index) + "”")
                            .font(.system(size: 24, design: .serif))
                            .textSelection(.enabled)
                    }
                    if let character, !document.voiceCue(for: character).isEmpty {
                        Label(document.voiceCue(for: character), systemImage: "waveform")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(Space.xxl)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                ContentUnavailableView("No Lines", systemImage: "text.quote")
            }
            Divider()
            HStack {
                Toggle("Speak Cues", isOn: $speakCues)
                    .toggleStyle(.checkbox)
                    .help("Read each cue line aloud in its character’s voice when you move to a line")
                Button("Show in Manuscript") {
                    if lines.indices.contains(position) {
                        let l = lines[position]
                        dismiss()
                        workspace.show(paragraph: l.paragraphID, line: l.id)
                    }
                }
                Spacer()
                Button("Previous", systemImage: "chevron.left") { position = max(0, position - 1) }
                    .keyboardShortcut(.leftArrow, modifiers: [])
                    .disabled(position == 0)
                Button("Next", systemImage: "chevron.right") { position = min(lines.count - 1, position + 1) }
                    .keyboardShortcut(.rightArrow, modifiers: [])
                    .disabled(position >= lines.count - 1)
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(Space.lg)
        }
        .frame(width: 680, height: 460)
        .noInitialFocus()
        .onChange(of: position) { speakCue() }
        .onAppear { speakCue() }
        .onDisappear { SpeechPlayer.shared.stop() }
    }
}
