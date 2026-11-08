import AppKit
import InnerVoiceKit
import SwiftUI

struct CastPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var sort: Sort = .lines
    @State private var mergeSource: UUID?

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
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 260, maximum: 360), spacing: Space.lg)], spacing: Space.lg) {
                    ForEach(sorted) { c in
                        NavigationLink(value: c.id) {
                            CastCard(document: document, character: c)
                        }
                        .buttonStyle(.plain)
                        .contextMenu { menu(c) }
                    }
                }
                .padding(Space.margin)
            }
            .toolbar {
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

    @ViewBuilder
    private func menu(_ c: BookCharacter) -> some View {
        Button("Open") { workspace.castPath = [c.id] }
        Button("Rehearse Lines…") { workspace.rehearse = c.id }
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

    var body: some View {
        let stats = document.index.stats[character.id]
        let color = Color(hexString: character.colorHex)
        VStack(alignment: .leading, spacing: Space.md) {
            HStack(spacing: Space.md) {
                Avatar(character: character, size: 44)
                VStack(alignment: .leading, spacing: 2) {
                    Text(character.name).font(.headline).lineLimit(1)
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
