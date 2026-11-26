import SwiftUI
import InnerVoiceKit

/// A character's Physical Description on their page: a summary, one field per feature
/// (each says when the AI inferred it), changes during the story, and continuity to check.
/// Collapsed by default; narration doesn't use it.
struct PhysicalProfileSection: View {
    @ObservedObject var document: InnerVoiceDocument
    let character: BookCharacter
    @Environment(\.undoManager) private var undoManager
    @AppStorage("character.physical.expanded") private var expanded = false
    @State private var newChapter: Int = -1
    @State private var newField: PhysicalProfile.Field = .hair
    @State private var newText = ""

    private var profile: PhysicalProfile { character.physical ?? PhysicalProfile() }

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: Space.md) {
                if profile.isEmpty {
                    Text("Build Summary writes this from what the book says about how \(character.firstName) looks. You can also fill it in yourself.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                labeled("Description", inferred: profile.summaryInferred, toggle: { $0.summaryInferred.toggle() }) {
                    DraftTextField("How \(character.firstName) looks, in a few sentences", text: summaryBinding, axis: .vertical)
                        .lineLimit(2...8)
                        .textFieldStyle(.roundedBorder)
                }
                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: Space.md, verticalSpacing: Space.sm) {
                    ForEach(PhysicalProfile.Field.allCases, id: \.self) { f in
                        GridRow {
                            Text(f.title).foregroundStyle(.secondary).gridColumnAlignment(.trailing)
                            DraftTextField("", text: fieldBinding(f))
                                .textFieldStyle(.roundedBorder)
                            inferredBadge(profile.value(f).inferred && !profile.value(f).text.isEmpty) { p in
                                var v = p.value(f); v.inferred.toggle(); p.set(f, v)
                            }
                        }
                    }
                }
                changes
                flags
                Text("“Inferred” marks the AI’s interpretation, not something the book states; click it once you’ve checked. Once you edit this description, Build Summary leaves it alone.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.top, Space.sm)
            .id(character.id)
        } label: {
            HStack {
                Text("Physical Description").font(.title3.weight(.semibold))
                if !profile.flags.isEmpty {
                    Label("\(profile.flags.count)", systemImage: "exclamationmark.triangle.fill")
                        .font(.caption).foregroundStyle(.orange)
                        .help("Continuity to check")
                }
            }
            .contentShape(Rectangle())
            .onTapGesture { withAnimation { expanded.toggle() } }
        }
    }

    // MARK: Changes and flags

    private var changes: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Text("Changes During the Story").font(.headline)
            ForEach(profile.orderedChanges) { c in
                HStack(alignment: .firstTextBaseline) {
                    Text(c.chapter.map(chapterTitle) ?? "Chapter unknown").foregroundStyle(.secondary)
                    Text("\(c.field.title): \(c.text)")
                    if c.inferred { Text("Inferred").font(.caption2).foregroundStyle(.secondary) }
                    Spacer()
                    Button { edit("Delete Change") { $0.changes.removeAll { $0.id == c.id } } } label: { Image(systemName: "minus.circle") }
                        .buttonStyle(.borderless).help("Delete this change")
                }
            }
            HStack {
                Picker("Chapter", selection: $newChapter) {
                    Text("Chapter…").tag(-1)
                    ForEach(document.project.chapters.indices, id: \.self) { Text(chapterTitle($0)).tag($0) }
                }
                .labelsHidden().frame(maxWidth: 160)
                Picker("Feature", selection: $newField) {
                    ForEach(PhysicalProfile.Field.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                .labelsHidden().frame(maxWidth: 160)
                TextField("What changes, e.g. Cuts her hair short", text: $newText)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit(addChange)
                Button("Add", action: addChange).disabled(newText.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
    }

    @ViewBuilder private var flags: some View {
        if !profile.flags.isEmpty {
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("Continuity to Check").font(.headline)
                ForEach(profile.flags) { f in
                    HStack(alignment: .firstTextBaseline) {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                        Text("\(f.field.title): \(f.text)").fixedSize(horizontal: false, vertical: true)
                        Spacer()
                        Button("Dismiss") { edit("Dismiss Continuity Note") { $0.flags.removeAll { $0.id == f.id } } }
                            .buttonStyle(.borderless)
                    }
                }
            }
        }
    }

    private func addChange() {
        let t = newText.trimmingCharacters(in: .whitespaces)
        guard !t.isEmpty else { return }
        let change = PhysicalProfile.Change(chapter: newChapter >= 0 ? newChapter : nil, field: newField, text: t)
        edit("Add Change") { $0.changes.append(change) }
        newText = ""
    }

    // MARK: Helpers

    private func chapterTitle(_ i: Int) -> String {
        let t = document.project.chapters.indices.contains(i) ? document.project.chapters[i].title : ""
        return t.isEmpty ? "Chapter \(i + 1)" : t
    }

    private func edit(_ name: String, _ body: @escaping (inout PhysicalProfile) -> Void) {
        document.updateCharacter(character.id, name, undoManager: undoManager) { c in
            var p = c.physical ?? PhysicalProfile()
            body(&p)
            c.physical = p.isEmpty ? nil : p
        }
    }

    private var summaryBinding: Binding<String> {
        Binding(get: { profile.summary }, set: { v in
            edit("Edit Physical Description") { $0.summary = v; $0.summaryInferred = false }
        })
    }

    /// Typing in a field makes it the narrator's own: no longer inferred.
    private func fieldBinding(_ f: PhysicalProfile.Field) -> Binding<String> {
        Binding(get: { profile.value(f).text }, set: { v in
            edit("Edit \(f.title)") { $0.set(f, .init(text: v, inferred: false)) }
        })
    }

    private func labeled<Content: View>(_ title: String, inferred: Bool, toggle: @escaping (inout PhysicalProfile) -> Void,
                                         @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack { Text(title).foregroundStyle(.secondary); inferredBadge(inferred && !profile.summary.isEmpty, toggle) }
            content()
        }
    }

    @ViewBuilder
    private func inferredBadge(_ shown: Bool, _ toggle: @escaping (inout PhysicalProfile) -> Void) -> some View {
        if shown {
            Button { edit("Mark as Checked", toggle) } label: {
                Text("Inferred").font(.caption2.weight(.medium))
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(.yellow.opacity(0.25), in: Capsule())
            }
            .buttonStyle(.plain)
            .help("The AI interpreted or guessed this; the book doesn’t say it outright. Click once you’ve checked it.")
        } else {
            Color.clear.frame(width: 1, height: 1)
        }
    }
}
