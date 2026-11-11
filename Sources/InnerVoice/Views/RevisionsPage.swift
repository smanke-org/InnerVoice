import InnerVoiceKit
import SwiftUI

/// What a revised manuscript changed, to look through one by one.
struct RevisionsPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        if let record = document.project.revisions.last {
            List {
                Section {
                    HStack(spacing: Space.xxl) {
                        stat(record.unchanged, "unchanged", .secondary)
                        stat(record.changed.count, "changed", .orange)
                        stat(record.added.count, "added", .green)
                        stat(record.removed.count, "removed", Color(nsColor: .attention))
                    }
                    .padding(.vertical, Space.sm)
                    Text("Tags and notes were kept on unchanged paragraphs. Changed paragraphs keep the speakers of lines whose words still match.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    if document.project.untaggedCount > 0 {
                        Button("Tag New Lines") { TagCommand.run(document: document, workspace: workspace, undoManager: undoManager) }
                            .buttonStyle(.borderedProminent)
                    }
                } header: {
                    Text("“\(record.fileName)”, \(record.date.formatted(date: .abbreviated, time: .shortened))")
                }
                paragraphSection("Changed", ids: record.changed, record: record)
                paragraphSection("Added", ids: record.added, record: record)
                if !record.removed.isEmpty {
                    Section("Removed") {
                        ForEach(Array(record.removed.enumerated()), id: \.offset) { _, r in
                            VStack(alignment: .leading, spacing: 2) {
                                Text(r.chapterTitle).font(.caption).foregroundStyle(.secondary)
                                Text(r.text).strikethrough().foregroundStyle(.secondary).lineLimit(3)
                            }
                        }
                    }
                }
            }
            .listStyle(.inset)
            .navigationTitle("Revisions")
            .pageSubtitle()
        } else {
            ContentUnavailableView("No Revisions", systemImage: "doc.badge.clock",
                                   description: Text("Choose File › Import Revised Manuscript… when the author sends a new draft."))
        }
    }

    private func stat(_ n: Int, _ label: String, _ tint: Color) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("\(n)").font(.title2.weight(.semibold).monospacedDigit()).foregroundStyle(tint)
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func paragraphSection(_ title: String, ids: [UUID], record: RevisionRecord) -> some View {
        if !ids.isEmpty {
            Section(title) {
                ForEach(ids.filter { document.index.positions[$0] != nil }, id: \.self) { id in
                    Button {
                        markReviewed(id)
                        workspace.show(paragraph: id)
                    } label: {
                        HStack(alignment: .firstTextBaseline, spacing: Space.md) {
                            Image(systemName: record.reviewed.contains(id) ? "checkmark.circle.fill" : "circle")
                                .foregroundStyle(record.reviewed.contains(id) ? Color.green : Color.secondary)
                            Text(document.index.reference(id)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                                .frame(width: 72, alignment: .leading)
                            Text(document.project.paragraph(id, index: document.index)?.text ?? "").lineLimit(2)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private func markReviewed(_ id: UUID) {
        document.change("Review Change", undoManager: undoManager) { p in
            if !p.revisions.isEmpty { p.revisions[p.revisions.count - 1].reviewed.insert(id) }
        }
    }
}
