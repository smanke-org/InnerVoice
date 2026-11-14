import AppKit
import InnerVoiceKit
import SwiftUI

struct CastPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var sort: Sort = .lines
    @State private var showNarrators = false
    /// Show only characters whose gender the book doesn't settle (Casting Style).
    @State private var genderFilter = false
    @State private var query = ""
    /// Where a ⇧-click run starts.
    @State private var pickAnchor: UUID?

    /// The picked characters still shown, in order.
    private var picked: [UUID] { shown.map(\.id).filter { workspace.pickedCast.contains($0) } }

    /// Narration Tools projects choose their Casting Style here (AI voices: the Voices page).
    private var narration: Bool { document.project.settings.purpose == .narration }

    enum Sort: String, CaseIterable { case lines = "Most Lines", name = "Name", appearance = "First Appearance" }

    var body: some View {
        // The background AI bar goes inside the stack, on the cast and on each character's
        // page: a NavigationStack doesn't show an inset placed around it.
        NavigationStack(path: $workspace.castPath) {
            grid
                .searchable(text: $query, placement: .toolbar, prompt: "Find a character")
                .safeAreaInset(edge: .top, spacing: 0) { BackgroundAIBar(document: document, page: .cast) }
                .safeAreaInset(edge: .bottom, spacing: 0) { pickedBar }
                .navigationDestination(for: UUID.self) { id in
                    CharacterDetail(document: document, characterID: id, workspace: workspace)
                        .safeAreaInset(edge: .top, spacing: 0) { BackgroundAIBar(document: document, page: .cast) }
                }
        }
        .navigationTitle("Cast")
        .pageSubtitle("\(document.project.characters.count) characters")
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
                if narration {
                    CastingStyleHeader(document: document, genderFilter: $genderFilter, narration: true)
                        .frame(maxWidth: 560, alignment: .leading)
                        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding([.horizontal, .top], Space.margin)
                }
                duplicates
                if narration, document.project.settings.castingStyle == .shared {
                    parts
                } else {
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
                    ForEach(shown) { c in card(c) }
                }
                .padding(Space.margin)
                }
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
                        .help("Have the AI read the book for every character’s age, accent, voice and manner, and add it to their voice notes (⇧⌘B)")
                        .disabled(document.tagging.isRunning)
                    }
                }
            }

        }
    }

    /// The characters shown: all, or with Gender to Review on, only those it lists.
    private var shown: [BookCharacter] {
        let found = query.isEmpty ? sorted : sorted.filter { $0.searchMatches(query) }
        guard genderFilter else { return found }
        let review = Set(CastingStyleRules.genderToReview(document.project, index: document.index))
        return found.filter { review.contains($0.id) }
    }

    private func card(_ c: BookCharacter) -> some View {
        // A button, not a NavigationLink: in this grid the link didn't open on a click.
        let isPicked = workspace.pickedCast.contains(c.id)
        return Button { click(c.id, modifiers: NSEvent.modifierFlags) } label: {
            CastCard(document: document, character: c)
                .overlay {
                    if isPicked { RoundedRectangle(cornerRadius: 12).strokeBorder(Color.accentColor, lineWidth: 2.5) }
                }
        }
        .buttonStyle(.plain)
        .help("Open \(c.name)’s page. ⌘-click picks several characters to change at once.")
        .accessibilityAddTraits(isPicked ? .isSelected : [])
        .contextMenu {
            if isPicked, picked.count > 1 {
                ManyCharactersMenu(document: document, ids: picked, narration: narration)
            } else {
                menu(c)
            }
        }
    }

    /// A click opens the character; ⌘-click adds or removes them from the picked
    /// characters, ⇧-click picks the run from the last one.
    func click(_ id: UUID, modifiers: NSEvent.ModifierFlags) {
        if modifiers.contains(.command) {
            if workspace.pickedCast.contains(id) { workspace.pickedCast.remove(id) } else { workspace.pickedCast.insert(id) }
            pickAnchor = id
        } else if modifiers.contains(.shift) {
            let order = shown.map(\.id)
            if let a = pickAnchor.flatMap({ order.firstIndex(of: $0) }), let b = order.firstIndex(of: id) {
                workspace.pickedCast.formUnion(order[min(a, b)...max(a, b)])
            } else {
                workspace.pickedCast.insert(id)
                pickAnchor = id
            }
        } else {
            workspace.pickedCast = []
            workspace.castPath = [id]
        }
    }

    /// While characters are picked: change their gender (and who reads them) at once.
    @ViewBuilder
    private var pickedBar: some View {
        let ids = picked
        if !ids.isEmpty {
            HStack(alignment: .top, spacing: Space.xl) {
                VStack(alignment: .leading, spacing: Space.xs) {
                    Text(ids.count == 1 ? "1 Character Selected" : "\(ids.count) Characters Selected").font(.headline)
                    Text(ids.compactMap { document.project.character($0)?.firstName }.joined(separator: ", "))
                        .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    Button("Clear Selection") { workspace.pickedCast = [] }
                        .keyboardShortcut(.cancelAction)
                        .controlSize(.small)
                        .padding(.top, Space.xs)
                }
                .frame(width: 220, alignment: .leading)
                ManyCharactersControls(document: document, ids: ids, narration: narration) {
                    if genderFilter { workspace.pickedCast = [] }
                }
                Spacer(minLength: 0)
            }
            .padding(Space.lg)
            .background(.bar)
            .overlay(alignment: .top) { Divider() }
        }
    }

    /// Three Narrators: the cast in parts (Narration, Men, Women, Other, their own), each
    /// headed by the narrator who reads it.
    private var parts: some View {
        let readers = CastingStyleRules.readers(document.project, index: document.index)
        let narrationReader = CastingStyleRules.reader(of: .narrator, settings: document.project.settings)
        return VStack(alignment: .leading, spacing: Space.xl) {
            ForEach([VoiceRole.narrator, .men, .women, .other, .own]) { r in
                let members = shown.filter { (readers[$0.id] ?? .own) == r }
                if !members.isEmpty || (r != .own && r != .other && !genderFilter) {
                    VStack(alignment: .leading, spacing: Space.md) {
                        HStack(alignment: .firstTextBaseline, spacing: Space.sm) {
                            Text(r == .narrator ? "Read by the Narrator" : r == .own ? "A Narrator of Their Own" : r.title).font(.title3.weight(.semibold))
                            if r != .own {
                                let who = document.project.narrators.first { $0.role == r }
                                    ?? (r == .other ? CastingStyleRules.narrationNarrator(document.project) : nil)
                                Text(who.map { "· \($0.name)" + (r == narrationReader && r != .narrator ? ", who also reads the narration" : "") }
                                     ?? "· no narrator yet")
                                    .foregroundStyle(who == nil ? Color(nsColor: .attention) : .secondary)
                            }
                            Spacer()
                            Text("\(members.reduce(0) { $0 + (document.index.stats[$1.id]?.lines ?? 0) }) lines").foregroundStyle(.secondary)
                        }
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 260, maximum: 360), spacing: Space.lg)], spacing: Space.lg) {
                            ForEach(members) { c in card(c) }
                        }
                    }
                }
            }
        }
        .padding(Space.margin)
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
        Button(c.isPinned ? "Unpin from Speaker Lists" : "Pin to Top of Speaker Lists") {
            document.setPinned(c.id, !c.isPinned, undoManager: undoManager)
        }
        Menu("Merge Into") {
            ForEach(document.project.characters.filter { $0.id != c.id }) { other in
                Button(other.name) { document.merge(c.id, into: other.id, undoManager: undoManager) }
            }
        }
        if narration {
            Divider()
            CastingStyleMenu(document: document, character: c, narration: true)
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
                    } else if document.project.settings.purpose == .narration, document.project.settings.castingStyle == .oneVoice
                                || CastingStyleRules.readers(document.project, index: document.index)[character.id] == .narrator,
                              let shade = document.narrationShades()[character.id], let cue = shade.cue {
                        // Just Me: how to shade them.
                        Label(cue, systemImage: shade.pitch < 0 ? "arrow.down" : shade.pitch > 0 ? "arrow.up" : "waveform")
                            .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    } else if document.project.showsNarrationTools, let narrator = document.effectiveNarrator(for: character) {
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
                    Text(document.project.characters.filter { document.effectiveNarrator(for: $0)?.id == n.id }.map(\.name).joined(separator: ", "))
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    if document.project.settings.castingStyle != nil {
                        Picker("Part", selection: Binding(get: { n.role }, set: { document.setNarratorRole($0, for: n.id, undoManager: undoManager) })) {
                            Text("No Part").tag(VoiceRole?.none)
                            ForEach([VoiceRole.narrator, .men, .women, .other]) { r in Text(r == .narrator ? "Narration" : r.title).tag(VoiceRole?.some(r)) }
                        }
                        .labelsHidden()
                        .fixedSize()
                        .help("The part \(n.name) reads: the narration, every man, every woman, or everyone else")
                    }
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
