import AppKit
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

/// Use Voices from Another Project: choose an earlier book in the series, check who each of
/// its voiced characters is in this book, then give them the same voices. Nothing changes
/// until Use These Voices, and one Undo takes it all back.
struct SeriesVoicesSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    @Binding var isPresented: Bool
    /// An earlier book already read (snapshots).
    var preset: Project?
    @Environment(\.undoManager) private var undoManager

    @State private var old: Project?
    @State private var oldIndex = BookIndex(.empty)
    @State private var matches: [SeriesVoices.Match] = []
    @State private var options = SeriesVoices.Options()
    @State private var loading = false
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            if let old {
                mapping(old)
            } else {
                chooser
            }
        }
        .padding(Space.xl)
        .frame(width: 720)
        .onAppear { if let preset { use(preset) } }
    }

    // MARK: Choosing the earlier book

    private var others: [URL] {
        RecentProjects.shared.urls.filter { $0.pathExtension == "innervoice" && $0.deletingPathExtension().lastPathComponent != document.project.title }
    }

    private var chooser: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            Text("Use Voices from Another Project").font(.title2.weight(.semibold))
            Text("Choose an earlier book in the series. You’ll match its characters to the ones in “\(document.project.title)” before anything changes.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if !others.isEmpty {
                Text("Recent Projects").font(.headline).padding(.top, Space.xs)
                VStack(spacing: 0) {
                    ForEach(others, id: \.self) { url in
                        Button { load(url) } label: {
                            HStack(spacing: Space.sm) {
                                Image(nsImage: NSWorkspace.shared.icon(forFile: url.path)).resizable().frame(width: 24, height: 24)
                                Text(url.deletingPathExtension().lastPathComponent).lineLimit(1)
                                Spacer()
                                Image(systemName: "chevron.right").foregroundStyle(.tertiary)
                            }
                            .padding(.horizontal, Space.sm)
                            .padding(.vertical, Space.xs)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .disabled(loading)
                    }
                }
                .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
            }
            if loading { ProgressView("Reading the project…").controlSize(.small) }
            if let error {
                Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.secondary).font(.callout)
            }
            HStack {
                HelpButton(topic: .voices)
                Spacer()
                Button("Cancel") { isPresented = false }.keyboardShortcut(.cancelAction)
                Button("Choose Project…", action: choose).disabled(loading)
            }
        }
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.innerVoiceProject]
        panel.message = "Choose an earlier book in the series"
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url { load(url) }
    }

    private func load(_ url: URL) {
        loading = true
        error = nil
        Task {
            // A project holds the whole manuscript: read it off the main thread.
            let result = await Task.detached(priority: .userInitiated) { () -> Result<Project, Error> in
                Result { try InnerVoiceDocument.readProject(at: url) }
            }.value
            loading = false
            switch result {
            case .success(let p):
                if p.title == document.project.title, p.importedAt == document.project.importedAt {
                    error = "That’s this project. Choose an earlier book in the series."
                } else if !p.characters.contains(where: { !($0.voices ?? [:]).isEmpty }) {
                    error = "“\(p.title)” has no voices yet. Open it and cast its voices on the Voices page first."
                } else {
                    use(p)
                }
            case .failure:
                error = "Unable to read “\(url.deletingPathExtension().lastPathComponent)”. Choose an InnerVoice project."
            }
        }
    }

    private func use(_ p: Project) {
        let index = BookIndex(p)
        oldIndex = index
        matches = SeriesVoices.suggest(from: p, oldIndex: index, to: document.project)
        old = p
    }

    // MARK: Matching

    private var current: [BookCharacter] {
        document.project.characters.sorted { (document.index.stats[$0.id]?.lines ?? 0) > (document.index.stats[$1.id]?.lines ?? 0) }
    }

    private func mapping(_ old: Project) -> some View {
        let matched = matches.filter { $0.target != nil }.count
        return VStack(alignment: .leading, spacing: Space.md) {
            Text("Voices from “\(old.title)”").font(.title2.weight(.semibold))
            Text("Check who each character is in this book. Matched characters get the same voices as before, as your own choices, so casting won’t change them. Characters left out are cast as usual.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            ScrollView {
                VStack(spacing: 0) {
                    narratorRow(old)
                    Divider()
                    ForEach($matches) { $m in
                        row(old, match: $m)
                        Divider()
                    }
                }
            }
            .frame(minHeight: 160, maxHeight: 380)
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: Space.xs) {
                Toggle("Bring their Voice Notes too", isOn: $options.voiceNotes)
                Text("Qwen3-TTS designs voices from Voice Notes, so this keeps those voices the same. It replaces the Voice Notes of the matched characters here.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    .padding(.leading, 20)
                if let e = old.settings.voiceEngine, e != document.project.settings.engine {
                    Toggle("Use the same voice engine (\(e.title))", isOn: $options.engine)
                }
            }
            HStack {
                Text("\(matched) of \(matches.count) characters get their voices" + (options.narrator ? ", and the narrator." : "."))
                    .font(.callout).foregroundStyle(.secondary)
                Spacer()
                Button("Back") { self.old = nil; error = nil }
                    .disabled(preset != nil)
                Button("Cancel") { isPresented = false }.keyboardShortcut(.cancelAction)
                Button("Use These Voices") { apply(old) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(matched == 0 && !options.narrator)
            }
        }
    }

    private func narratorRow(_ old: Project) -> some View {
        let e = old.settings.engine
        let v = old.settings.narratorVoice(for: e)
        return HStack(spacing: Space.md) {
            Image(systemName: "book.closed").frame(width: 10)
            VStack(alignment: .leading, spacing: 1) {
                Text("Narrator").fontWeight(.medium)
                Text(v.map { voiceLabel($0.choice) } ?? "No narrator voice").font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(width: 250, alignment: .leading)
            Image(systemName: "arrow.right").foregroundStyle(.tertiary)
            Toggle("Narrator", isOn: $options.narrator)
                .disabled(v == nil)
                .frame(width: 210, alignment: .leading)
            Spacer()
        }
        .padding(.horizontal, Space.md)
        .padding(.vertical, Space.sm)
    }

    private func row(_ old: Project, match: Binding<SeriesVoices.Match>) -> some View {
        let m = match.wrappedValue
        let c = old.character(m.source)
        return HStack(spacing: Space.md) {
            Circle().fill(Color(hexString: c?.colorHex ?? "#888888")).frame(width: 10, height: 10)
            VStack(alignment: .leading, spacing: 1) {
                Text(c?.name ?? "").fontWeight(.medium).lineLimit(1)
                Text(c.map { voiceSummary($0, old: old) } ?? "").font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(width: 250, alignment: .leading)
            .help(c.map { "\($0.name): \(voiceSummary($0, old: old))" } ?? "")
            Image(systemName: "arrow.right").foregroundStyle(.tertiary)
            Picker(c?.firstName ?? "", selection: Binding(get: { m.target }, set: { pick($0, for: m.source) })) {
                Text("Not in this book").tag(UUID?.none)
                Divider()
                ForEach(current) { n in Text(n.name).tag(UUID?.some(n.id)) }
            }
            .labelsHidden()
            .frame(width: 210)
            badge(m)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, Space.md)
        .padding(.vertical, Space.sm)
    }

    @ViewBuilder
    private func badge(_ m: SeriesVoices.Match) -> some View {
        switch (m.target, m.how) {
        case (nil, _): Label("Not matched", systemImage: "minus.circle").foregroundStyle(.secondary)
        case (_, .name?): Label("Same name", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
        case (_, .firstName?): Label("First name only", systemImage: "questionmark.circle.fill").foregroundStyle(.orange)
                .help("Only the first name matches. Check this is the same character.")
        default: Label("Your choice", systemImage: "hand.point.up.left").foregroundStyle(.secondary)
        }
    }

    /// Each character here gets one earlier character's voice: picking someone already
    /// matched moves the match.
    private func pick(_ target: UUID?, for source: UUID) {
        for i in matches.indices {
            if matches[i].source == source {
                matches[i].target = target
                matches[i].how = target == nil ? nil : .chosen
            } else if target != nil, matches[i].target == target {
                matches[i].target = nil
                matches[i].how = nil
            }
        }
    }

    /// "214 lines · Warm Storyteller · Qwen3-TTS": the voice on the earlier book's engine.
    private func voiceSummary(_ c: BookCharacter, old: Project) -> String {
        let lines = oldIndex.stats[c.id]?.lines ?? 0
        let v = c.voice(for: old.settings.engine) ?? c.voices?.values.first
        return ([lines > 0 ? "\(lines) lines" : nil, v.map { voiceLabel($0.choice) }].compactMap { $0 }).joined(separator: " · ")
    }

    private func voiceLabel(_ choice: VoiceChoice) -> String {
        VoiceCatalog.name(of: choice) + " · " + choice.engine.title
    }

    private func apply(_ old: Project) {
        let chosen = matches
        let opts = options
        document.change("Use Voices from \(old.title)", undoManager: undoManager) { p in
            SeriesVoices.apply(chosen, from: old, to: &p, options: opts)
        }
        isPresented = false
    }
}
