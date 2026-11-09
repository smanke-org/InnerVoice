import AppKit
import InnerVoiceKit
import SwiftUI

struct CastPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var sort: Sort = .lines
    @State private var showNarrators = false

    enum Sort: String, CaseIterable { case lines = "Most Lines", name = "Name", appearance = "First Appearance" }

    var body: some View {
        NavigationStack(path: $workspace.castPath) {
            grid
                .navigationDestination(for: UUID.self) { id in
                    CharacterDetail(document: document, characterID: id, workspace: workspace)
                }
        }
        .navigationTitle("Cast")
        .navigationSubtitle("\(document.project.characters.count) characters")
    }

    private var sorted: [BookCharacter] {
        let index = document.index
        switch sort {
        case .lines: return document.project.characters.sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
        case .name: return document.project.characters.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        case .appearance:
            return document.project.characters.sorted {
                (index.stats[$0.id]?.firstLine?.global ?? .max) < (index.stats[$1.id]?.firstLine?.global ?? .max)
            }
        }
    }

    @ViewBuilder
    private var grid: some View {
        if document.project.characters.isEmpty {
            ContentUnavailableView {
                Label("No Characters Yet", systemImage: "person.2")
            } description: {
                Text("Tag Speakers finds the cast as it reads the book. You can also assign a line to a new character.")
            } actions: {
                Button("Tag Speakers") { TagCommand.run(document: document, workspace: workspace, undoManager: undoManager) }
                    .buttonStyle(.borderedProminent)
            }
        } else {
            ScrollView {
                duplicates
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 260, maximum: 360), spacing: Space.lg)], spacing: Space.lg) {
                    // The narrator first: the voice for everything no character says.
                    if document.project.showsVoices {
                    Button { workspace.showVoice(nil) } label: {
                        NarratorCard(document: document)
                    }
                    .buttonStyle(.plain)
                    .help("Choose the narrator’s voice on the Voices page")
                    .contextMenu {
                        Button("Change Narrator Voice…") { workspace.showVoice(nil) }
                        Button("Play the Narrator Voice") { PlayCommand.audition(VoiceChoice(engine: document.project.engineInUse, voiceID: VoiceChoice.narratorID), character: nil, document: document) }
                    }
                    }
                    ForEach(sorted) { c in
                        // A button, not a NavigationLink: in this grid the link didn't open on a click.
                        Button { workspace.castPath = [c.id] } label: {
                            CastCard(document: document, character: c)
                        }
                        .buttonStyle(.plain)
                        .help("Open \(c.name)’s page")
                        .contextMenu { menu(c) }
                    }
                }
                .padding(Space.margin)
            }
            .toolbar {
                if document.project.showsNarrationTools {
                    ToolbarItem {
                        Button("Narrators", systemImage: "person.wave.2") { showNarrators = true }
                            .help("Add narrators and cast characters to them")
                            .popover(isPresented: $showNarrators) { NarratorsEditor(document: document).padding(Space.lg).frame(width: 340) }
                    }
                }
                ToolbarItem {
                    Picker("Sort", selection: $sort) {
                        ForEach(Sort.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                    }
                    .help("Sort the cast")
                }
                ToolbarItem {
                    if document.tagging.status?.job == .summaries {
                        Button("Stop", systemImage: "stop.circle") { document.tagging.stop() }
                    } else {
                        Button("Build Summaries", systemImage: "text.badge.star") {
                            TagCommand.summaries(document: document, workspace: workspace, undoManager: undoManager)
                        }
                        .help("Read the book for each character's age, accent, voice and more (⇧⌘B)")
                        .disabled(document.tagging.isRunning)
                    }
                }
            }
            .safeAreaInset(edge: .top) {
                if let status = document.tagging.status, status.job == .summaries {
                    HStack(spacing: Space.md) {
                        Image(systemName: "sparkles").foregroundStyle(.tint)
                        Text(status.message)
                        ProgressView(value: status.progress).frame(maxWidth: 240)
                    }
                    .padding(Space.md)
                    .frame(maxWidth: .infinity)
                    .background(.bar)
                }
            }
        }
    }

    /// "Mara" and "Mara Quinn" are probably one person: offer to merge.
    @ViewBuilder
    private var duplicates: some View {
        let pairs = Duplicates.pairs(in: document.project)
        if !pairs.isEmpty {
            VStack(alignment: .leading, spacing: Space.sm) {
                Label("These may be the same character", systemImage: "person.2.badge.gearshape")
                    .font(.headline)
                ForEach(pairs, id: \.self) { pair in
                    if let m = document.project.character(pair.merge), let k = document.project.character(pair.keep) {
                        HStack(spacing: Space.sm) {
                            Avatar(character: m, size: 22)
                            Text(m.name)
                            Image(systemName: "arrow.right").foregroundStyle(.secondary)
                            Avatar(character: k, size: 22)
                            Text(k.name)
                            Spacer()
                            Button("Merge") { document.merge(m.id, into: k.id, undoManager: undoManager) }
                                .help("Give all of \(m.name)'s lines to \(k.name) and keep “\(m.name)” as another name")
                        }
                    }
                }
            }
            .padding(Space.lg)
            .background(.orange.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.orange.opacity(0.3)))
            .padding([.horizontal, .top], Space.margin)
        }
    }

    @ViewBuilder
    private func menu(_ c: BookCharacter) -> some View {
        Button("Open") { workspace.castPath = [c.id] }
        if document.project.showsNarrationTools { Button("Rehearse Lines…") { workspace.rehearse = c.id } }
        Button(c.isPinned ? "Unpin from Speaker Lists" : "Pin to Top of Speaker Lists") {
            document.setPinned(c.id, !c.isPinned, undoManager: undoManager)
        }
        Menu("Merge Into") {
            ForEach(document.project.characters.filter { $0.id != c.id }) { other in
                Button(other.name) { document.merge(c.id, into: other.id, undoManager: undoManager) }
            }
        }
        Divider()
        Button("Delete Character", role: .destructive) { document.deleteCharacter(c.id, undoManager: undoManager) }
    }
}

struct CastCard: View {
    @ObservedObject var document: InnerVoiceDocument
    let character: BookCharacter
    @State private var hovering = false

    /// Where each name sits in its window, for the snapshot click test.
    static var nameFrames: [UUID: CGRect] = [:]

    var body: some View {
        let stats = document.index.stats[character.id]
        let color = Color(hexString: character.colorHex)
        VStack(alignment: .leading, spacing: Space.md) {
            HStack(spacing: Space.md) {
                Avatar(character: character, size: 44)
                VStack(alignment: .leading, spacing: 2) {
                    // Underlined while the pointer is over the card: a click opens their page.
                    Text(character.name).font(.headline).lineLimit(1).underline(hovering)
                        .background {
                            if SystemHooks.isTestRun {
                                GeometryReader { g in
                                    Color.clear.onAppear { Self.nameFrames[character.id] = g.frame(in: .global) }
                                }
                            }
                        }
                    if !character.aliases.isEmpty {
                        Text(character.aliases.joined(separator: ", "))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    } else if let narrator = document.project.narrators.first(where: { $0.id == character.narratorID }) {
                        Text("Read by \(narrator.name)").font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
                if document.project.settings.povCharacterID == character.id {
                    Text("Narrator").font(.caption2.weight(.semibold)).padding(.horizontal, 6).padding(.vertical, 2)
                        .background(.quaternary, in: Capsule())
                }
            }
            let cue = document.voiceCue(for: character)
            Text(cue.isEmpty ? "No summary yet" : cue)
                .font(.callout)
                .foregroundStyle(cue.isEmpty ? .tertiary : .secondary)
                .lineLimit(2, reservesSpace: true)
            HStack(spacing: Space.lg) {
                metric("\(stats?.lines ?? 0)", "lines")
                metric("\((stats?.words ?? 0).formatted())", "words")
                metric(stats?.firstLine.map { "Ch \($0.chapter + 1)" } ?? "—", "first line")
            }
        }
        .padding(Space.lg)
        .background {
            RoundedRectangle(cornerRadius: 12)
                .fill(Color(nsColor: .controlBackgroundColor))
                .overlay(alignment: .leading) {
                    UnevenRoundedRectangle(topLeadingRadius: 12, bottomLeadingRadius: 12)
                        .fill(color)
                        .frame(width: 4)
                }
                .shadow(color: .black.opacity(hovering ? 0.12 : 0.05), radius: hovering ? 8 : 3, y: hovering ? 3 : 1)
        }
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.separator.opacity(0.6)))
        .contentShape(RoundedRectangle(cornerRadius: 12))
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.15), value: hovering)
        .accessibilityElement(children: .combine)
    }

    private func metric(_ value: String, _ label: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(value).font(.callout.weight(.semibold).monospacedDigit())
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// Narrators for a multi-narrator production, and who reads whom.
struct NarratorsEditor: View {
    @ObservedObject var document: InnerVoiceDocument
    @Environment(\.undoManager) private var undoManager
    @State private var name = ""

    var body: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            Text("Narrators").font(.headline)
            if document.project.narrators.isEmpty {
                Text("For a production with more than one narrator, add each one, then pick a narrator on each character's page.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            ForEach(document.project.narrators) { n in
                HStack {
                    Image(systemName: "person.wave.2").foregroundStyle(.secondary)
                    Text(n.name)
                    Spacer()
                    Text(document.project.characters.filter { $0.narratorID == n.id }.map(\.name).joined(separator: ", "))
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    Button {
                        document.change("Remove Narrator", undoManager: undoManager) { p in
                            p.narrators.removeAll { $0.id == n.id }
                            for i in p.characters.indices where p.characters[i].narratorID == n.id { p.characters[i].narratorID = nil }
                        }
                    } label: { Image(systemName: "minus.circle") }
                        .buttonStyle(.borderless)
                        .help("Remove \(n.name)")
                }
            }
            HStack {
                TextField("Narrator's name", text: $name).onSubmit(add)
                Button("Add", action: add).disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
    }

    private func add() {
        let n = name.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty else { return }
        document.change("Add Narrator", undoManager: undoManager) { $0.narrators.append(Narrator(name: n)) }
        name = ""
    }
}

/// The narrator, as the first card on the Cast page: the voice for narration, lines nobody
/// is cast for, and characters set to Same as Narrator.
struct NarratorCard: View {
    @ObservedObject var document: InnerVoiceDocument
    @State private var hovering = false

    var body: some View {
        let project = document.project
        let pov = VoiceResolver.povNarrator(project: project)
        let voice = VoiceResolver(project: project).narrator
        let sharing = project.characters.filter { $0.voice(for: project.engineInUse)?.choice.isNarrator == true }
        VStack(alignment: .leading, spacing: Space.md) {
            HStack(spacing: Space.md) {
                Image(systemName: "book.closed.fill")
                    .font(.title3)
                    .foregroundStyle(.secondary)
                    .frame(width: 44, height: 44)
                    .background(.quaternary, in: Circle())
                VStack(alignment: .leading, spacing: 2) {
                    Text("Narrator").font(.headline).lineLimit(1).underline(hovering)
                    Text(pov.map { "\($0.firstName)’s voice (first person)" } ?? voice.map { VoiceCatalog.name(of: $0) } ?? "No voice yet")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer()
            }
            Text("Reads the narration and every line nobody is cast for.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(2, reservesSpace: true)
            HStack(spacing: Space.lg) {
                metric(project.engineInUse.title, "engine")
                if !sharing.isEmpty {
                    metric("\(sharing.count)", sharing.count == 1 ? "character shares it" : "characters share it")
                }
            }
        }
        .padding(Space.lg)
        .background {
            RoundedRectangle(cornerRadius: 12)
                .fill(Color(nsColor: .controlBackgroundColor))
                .shadow(color: .black.opacity(hovering ? 0.12 : 0.05), radius: hovering ? 8 : 3, y: hovering ? 3 : 1)
        }
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.separator.opacity(0.6)))
        .contentShape(RoundedRectangle(cornerRadius: 12))
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.15), value: hovering)
        .accessibilityElement(children: .combine)
    }

    private func metric(_ value: String, _ label: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(value).font(.callout.weight(.semibold)).lineLimit(1)
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
    }
}
