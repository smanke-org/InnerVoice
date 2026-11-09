import AppKit
import InnerVoiceKit
import SwiftUI

/// Everything about one character: names, color, narrator, summary facts with sources,
/// notes, and their lines.
struct CharacterDetail: View {
    @ObservedObject var document: InnerVoiceDocument
    let characterID: UUID
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var showColors = false
    @State private var confirmDelete = false

    private var character: BookCharacter? { document.project.character(characterID) }

    var body: some View {
        if let c = character {
            ScrollView {
                VStack(alignment: .leading, spacing: Space.xxl) {
                    header(c)
                    if document.project.showsVoices { voice(c) }
                    summary(c)
                    notes(c)
                    sampleLines(c)
                }
                .padding(Space.margin)
                .frame(maxWidth: 820, alignment: .leading)
                .frame(maxWidth: .infinity)
            }
            .navigationTitle(c.name)
            .toolbar {
                ToolbarItemGroup {
                    if document.project.showsNarrationTools {
                        Button("Rehearse", systemImage: "play.rectangle") { workspace.rehearse = c.id }
                            .help("Step through \(c.name)'s lines with cue lines")
                    }
                    Menu("Build Summary", systemImage: "text.badge.star") {
                        Button("Build \(c.firstName)’s Summary") {
                            TagCommand.summaries(document: document, workspace: workspace, undoManager: undoManager, only: [c.id])
                        }
                        Button("Build Everyone’s Summaries") {
                            TagCommand.summaries(document: document, workspace: workspace, undoManager: undoManager)
                        }
                    } primaryAction: {
                        TagCommand.summaries(document: document, workspace: workspace, undoManager: undoManager, only: [c.id])
                    }
                    .help("Have the AI read the book for \(c.name)’s age, accent, voice and manner, and add it to their voice notes. Click the arrow to do every character.")
                    .disabled(document.tagging.isRunning)
                    Menu("More", systemImage: "ellipsis") {
                        Menu("Merge Into") {
                            ForEach(document.project.characters.filter { $0.id != c.id }) { other in
                                Button(other.name) {
                                    document.merge(c.id, into: other.id, undoManager: undoManager)
                                    workspace.castPath = [other.id]
                                }
                            }
                        }
                        Button(c.isPinned ? "Unpin from Speaker Lists" : "Pin to Top of Speaker Lists") {
                            document.setPinned(c.id, !c.isPinned, undoManager: undoManager)
                        }
                        Button(document.project.settings.povCharacterID == c.id ? "Not the Narrator" : "Tells the Story (First Person)") {
                            let on = document.project.settings.povCharacterID != c.id
                            document.updateSettings("Set Point of View", undoManager: undoManager) { $0.povCharacterID = on ? c.id : nil }
                        }
                        Divider()
                        Button("Delete Character…", role: .destructive) { confirmDelete = true }
                    }
                    .help("More actions for \(c.name)")
                }
            }
            .confirmationDialog("Delete \(c.name)?", isPresented: $confirmDelete) {
                Button("Delete", role: .destructive) {
                    workspace.castPath = []
                    document.deleteCharacter(c.id, undoManager: undoManager)
                }
            } message: {
                Text("Their lines become Who's Speaking? You can undo this.")
            }
        } else {
            ContentUnavailableView("No Character", systemImage: "person.crop.circle.badge.questionmark")
        }
    }

    // MARK: Voice

    private func voice(_ c: BookCharacter) -> some View {
        VStack(alignment: .leading, spacing: Space.md) {
            VoiceChooser(document: document, characterID: c.id, compact: true)
            Button("All Voices…") { workspace.showVoice(c.id) }
                .controlSize(.small)
                .help("Hear and choose from every voice on the Voices page (⌘8)")
        }
        .padding(Space.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))
        .onAppear { VoiceCastingRun.castIfNeeded(document: document) }
    }

    // MARK: Header

    private func header(_ c: BookCharacter) -> some View {
        let stats = document.index.stats[c.id]
        return HStack(alignment: .top, spacing: Space.xl) {
            Button { showColors = true } label: {
                Avatar(character: c, size: 72)
                    .overlay(alignment: .bottomTrailing) {
                        Image(systemName: "paintpalette.fill")
                            .font(.caption)
                            .padding(5)
                            .background(.regularMaterial, in: Circle())
                    }
            }
            .buttonStyle(.plain)
            .help("Change \(c.name)'s color")
            .popover(isPresented: $showColors) { ColorPickerGrid(selected: c.colorHex) { hex in
                document.updateCharacter(c.id, "Change Color", undoManager: undoManager) { $0.colorHex = hex }
            } }
            VStack(alignment: .leading, spacing: Space.sm) {
                DraftTextField("Name", text: binding(c, \.name, "Rename Character"))
                    .id(c.id)
                    .font(.largeTitle.weight(.semibold))
                    .textFieldStyle(.plain)
                DraftTextField("Also called (comma separated)", text: Binding(
                    get: { c.aliases.joined(separator: ", ") },
                    set: { v in document.updateCharacter(c.id, "Edit Aliases", undoManager: undoManager) {
                        $0.aliases = v.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
                    } }))
                    .id(c.id)
                    .textFieldStyle(.plain)
                    .foregroundStyle(.secondary)
                HStack(spacing: Space.xl) {
                    metric("\(stats?.lines ?? 0)", "lines")
                    metric((stats?.words ?? 0).formatted(), "words")
                    metric("\(stats?.chapters.count ?? 0)", "chapters")
                    if let first = stats?.firstLine {
                        Button {
                            if let p = document.project.chapters[first.chapter].paragraphs[safe: first.paragraph] {
                                workspace.show(paragraph: p.id)
                            }
                        } label: { metric("Ch \(first.chapter + 1)", "first line") }
                            .buttonStyle(.plain)
                            .help("Show \(c.name)'s first line")
                    }
                }
                .padding(.top, Space.xs)
                HStack(spacing: Space.lg) {
                    if document.project.showsNarrationTools {
                    Picker("Narrator", selection: Binding(get: { c.narratorID }, set: { v in
                        document.updateCharacter(c.id, "Cast Narrator", undoManager: undoManager) { $0.narratorID = v }
                    })) {
                        Text("None").tag(UUID?.none)
                        ForEach(document.project.narrators) { Text($0.name).tag(UUID?.some($0.id)) }
                    }
                    .fixedSize()
                    }
                    if document.project.showsNarrationTools {
                    Toggle("Show in Index", isOn: Binding(get: { document.index.showsInIndex(c) }, set: { v in
                        document.updateCharacter(c.id, "Show in Index", undoManager: undoManager) { $0.showInIndex = v }
                    }))
                    .toggleStyle(.checkbox)
                    }
                }
                .padding(.top, Space.xs)
            }
        }
    }

    private func metric(_ value: String, _ label: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(value).font(.title3.weight(.semibold).monospacedDigit())
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
    }

    // MARK: Summary

    private func summary(_ c: BookCharacter) -> some View {
        let first = document.index.stats[c.id]?.firstLine
        return VStack(alignment: .leading, spacing: Space.md) {
            HStack {
                Label("Summary", systemImage: "sparkles").font(.title3.weight(.semibold))
                Spacer()
                Menu("Add Fact", systemImage: "plus") {
                    ForEach(FactKind.narratorOrder, id: \.self) { kind in
                        Button(kind.title) {
                            document.updateCharacter(c.id, "Add Fact", undoManager: undoManager) {
                                $0.facts.append(Fact(kind: kind, text: "", edited: true))
                            }
                        }
                    }
                }
                .fixedSize()
                .help("Add a fact you know about \(c.name)")
            }
            if c.facts.isEmpty {
                Text("Build Summary reads the book for \(c.name)'s age, gender, accent, voice, personality and history. Facts the AI finds link to the line they came from.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            VStack(spacing: 0) {
                ForEach(FactKind.narratorOrder, id: \.self) { kind in
                    let facts = c.facts.filter { $0.kind == kind }
                    if !facts.isEmpty {
                        ForEach(facts) { f in
                            FactRow(document: document, characterID: c.id, fact: f, firstLine: first, workspace: workspace,
                                    showKind: f.id == facts.first?.id)
                            Divider().padding(.leading, 120)
                        }
                    }
                }
            }
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.separator.opacity(0.6)))
            if c.facts.contains(where: \.fromAI) {
                Text("Facts marked with sparkles come from the AI. Check each source before you rely on it.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    // MARK: Notes

    private func notes(_ c: BookCharacter) -> some View {
        VStack(alignment: .leading, spacing: Space.md) {
            Text("Voice Notes").font(.title3.weight(.semibold))
            DraftTextField("How you'll voice \(c.name): pitch, pace, placement, a reference voice…",
                           text: binding(c, \.voiceNotes, "Edit Voice Notes"), axis: .vertical)
                .id(c.id)
                .lineLimit(2...12)
                .textFieldStyle(.roundedBorder)
            Text("Build Summary adds what the book says about \(c.firstName)’s voice under “From the book:”. Casting and the AI voices read these notes, so change them to fine-tune how \(c.firstName) sounds. Once you edit that part, Build Summary leaves it alone.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text("Notes").font(.title3.weight(.semibold)).padding(.top, Space.sm)
            DraftTextField("Anything else to remember", text: binding(c, \.notes, "Edit Notes"), axis: .vertical)
                .id(c.id)
                .lineLimit(2...8)
                .textFieldStyle(.roundedBorder)
        }
    }

    private func sampleLines(_ c: BookCharacter) -> some View {
        let lines = document.project.lines.filter { $0.speakerID == c.id }
        return VStack(alignment: .leading, spacing: Space.sm) {
            HStack {
                Text("Lines").font(.title3.weight(.semibold))
                Spacer()
                if lines.count > 8 {
                    Button("Show All in Index") {
                        document.updateCharacter(c.id, "Show in Index", undoManager: undoManager) { $0.showInIndex = true }
                        workspace.page = .index
                    }
                    .controlSize(.small)
                }
            }
            if lines.isEmpty {
                Text("No lines yet.").foregroundStyle(.secondary)
            }
            ForEach(lines.prefix(8)) { line in
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
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("Show this line in the manuscript")
            }
        }
    }

    private func binding(_ c: BookCharacter, _ key: WritableKeyPath<BookCharacter, String>, _ name: String) -> Binding<String> {
        Binding(get: { c[keyPath: key] }, set: { v in
            document.updateCharacter(c.id, name, undoManager: undoManager) { $0[keyPath: key] = v }
        })
    }
}

/// One fact: kind, editable text, source link, Revealed Late badge.
struct FactRow: View {
    @ObservedObject var document: InnerVoiceDocument
    let characterID: UUID
    let fact: Fact
    let firstLine: BookIndex.Position?
    @Bindable var workspace: Workspace
    var showKind: Bool
    @Environment(\.undoManager) private var undoManager
    @State private var hovering = false

    var body: some View {
        let source = fact.sourceParagraphID.flatMap { document.index.positions[$0] }
        let late = source.map { s in firstLine.map { s.chapter > $0.chapter } ?? false } ?? false
        HStack(alignment: .firstTextBaseline, spacing: Space.md) {
            Text(showKind ? fact.kind.title : "")
                .font(.callout.weight(.medium))
                .foregroundStyle(.secondary)
                .frame(width: 104, alignment: .leading)
            VStack(alignment: .leading, spacing: Space.xs) {
                HStack(alignment: .firstTextBaseline, spacing: Space.xs) {
                    if fact.fromAI && !fact.edited {
                        Image(systemName: "sparkles").font(.caption).foregroundStyle(.secondary)
                            .help("Found by the AI")
                    }
                    DraftTextField(fact.kind.title, text: Binding(get: { fact.text }, set: { v in
                        document.updateCharacter(characterID, "Edit Fact", undoManager: undoManager) { c in
                            if let i = c.facts.firstIndex(where: { $0.id == fact.id }) {
                                c.facts[i].text = v
                                c.facts[i].edited = true
                            }
                        }
                    }), axis: .vertical)
                    .textFieldStyle(.plain)
                }
                HStack(spacing: Space.sm) {
                    if let id = fact.sourceParagraphID, source != nil {
                        Button {
                            workspace.show(paragraph: id)
                        } label: {
                            Label(document.index.reference(id), systemImage: "text.quote")
                                .font(.caption)
                        }
                        .buttonStyle(.link)
                        .help(fact.quote.isEmpty ? "Show the source in the manuscript" : "“\(fact.quote)”")
                    }
                    if late, let s = source, let f = firstLine {
                        Label("Revealed Late", systemImage: "exclamationmark.circle.fill")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.orange)
                            .help("\(document.project.character(characterID)?.name ?? "They") first speak in Chapter \(f.chapter + 1), but this is only revealed in Chapter \(s.chapter + 1). Voice it from the start.")
                    }
                }
            }
            Spacer(minLength: 0)
            Button {
                document.updateCharacter(characterID, "Delete Fact", undoManager: undoManager) { $0.facts.removeAll { $0.id == fact.id } }
            } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                .buttonStyle(.plain)
                .opacity(hovering ? 1 : 0)
                .help("Delete this fact")
                .accessibilityLabel("Delete fact")
        }
        .padding(.horizontal, Space.md)
        .padding(.vertical, Space.sm)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .contextMenu {
            Picker("Kind", selection: Binding(get: { fact.kind }, set: { k in
                document.updateCharacter(characterID, "Change Fact Kind", undoManager: undoManager) { c in
                    if let i = c.facts.firstIndex(where: { $0.id == fact.id }) { c.facts[i].kind = k }
                }
            })) {
                ForEach(FactKind.narratorOrder, id: \.self) { Text($0.title).tag($0) }
            }
            Button("Delete Fact", role: .destructive) {
                document.updateCharacter(characterID, "Delete Fact", undoManager: undoManager) { $0.facts.removeAll { $0.id == fact.id } }
            }
        }
    }
}

/// The highlight palette, with a custom color well for anything else (never red).
struct ColorPickerGrid: View {
    let selected: String
    var pick: (String) -> Void
    @State private var custom = Color.gray
    @State private var warning = false

    var body: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            Text("Color").font(.headline)
            LazyVGrid(columns: Array(repeating: GridItem(.fixed(28), spacing: Space.sm), count: 6), spacing: Space.sm) {
                ForEach(CharacterPalette.swatches) { s in
                    Button { pick(s.hex) } label: {
                        Circle()
                            .fill(Color(hexString: s.hex))
                            .frame(width: 24, height: 24)
                            .overlay(Circle().strokeBorder(.primary.opacity(selected.uppercased() == s.hex.uppercased() ? 0.9 : 0), lineWidth: 2).padding(-3))
                    }
                    .buttonStyle(.plain)
                    .help(s.name)
                    .accessibilityLabel(s.name)
                }
            }
            HStack {
                ColorPicker("Other…", selection: $custom, supportsOpacity: false)
                    .onChange(of: custom) {
                        let hex = NSColor(custom).hexString
                        warning = CharacterPalette.isTooRed(hex)
                        if !warning { pick(hex) }
                    }
            }
            if warning {
                Text("Red is kept for Who's Speaking? Pick another color.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(Space.lg)
        .frame(width: 232)
    }
}

extension Array {
    subscript(safe i: Int) -> Element? { indices.contains(i) ? self[i] : nil }
}
