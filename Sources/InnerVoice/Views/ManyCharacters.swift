import AppKit
import InnerVoiceKit
import SwiftUI

/// Several characters picked at once (⌘-click or ⇧-click on the Voices list or the Cast):
/// set their gender, who reads them, or main character in one go, one undo step each.
struct ManyCharactersControls: View {
    @ObservedObject var document: InnerVoiceDocument
    let ids: [UUID]
    /// Narration Tools: the parts are narrators, not voices.
    var narration = false
    /// After a change (the list may drop them, with Genders to Review on).
    var changed: () -> Void = {}
    @Environment(\.undoManager) private var undoManager

    private var characters: [BookCharacter] { ids.compactMap { document.project.character($0) } }

    /// "Male 3 · From the book 2": what they're set to now.
    private var genders: String {
        var order: [CharacterGender?] = []
        var counts: [CharacterGender?: Int] = [:]
        for c in characters {
            if counts[c.gender] == nil { order.append(c.gender) }
            counts[c.gender, default: 0] += 1
        }
        return order.map { "\($0?.title ?? "From the book") \(counts[$0] ?? 0)" }.joined(separator: " · ")
    }

    var body: some View {
        let style = document.project.settings.castingStyle
        VStack(alignment: .leading, spacing: Space.md) {
            VStack(alignment: .leading, spacing: Space.xs) {
                Text("Gender").font(.headline)
                Text("Now: " + genders).font(.caption).foregroundStyle(.secondary)
                HStack(spacing: Space.sm) {
                    ForEach(CharacterGender.allCases) { g in
                        Button(g.title) { apply { document.setGender(g, for: ids, undoManager: undoManager) } }
                            .help("Set all \(ids.count) to \(g == .other ? "Other (neither male nor female)" : g.title)")
                    }
                    Button("From the Book") { apply { document.setGender(nil, for: ids, undoManager: undoManager) } }
                        .help("Go back to what the book says about each of them")
                }
                .controlSize(.large)
            }
            if let style {
                Picker("Read By", selection: Binding(get: { Set(characters.map(\.role)).count == 1 ? characters.first?.role : nil },
                                                     set: { r in apply { document.setRole(r, for: ids, undoManager: undoManager) } })) {
                    Text("Automatic").tag(VoiceRole?.none)
                    ForEach(style == .oneVoice ? [VoiceRole.narrator, .own] : VoiceRole.allCases) { r in
                        Text(narration ? r.narratorTitle : r == .own ? r.title : r.voiceTitle).tag(VoiceRole?.some(r))
                    }
                }
                .fixedSize()
                if style == .fullCast {
                    HStack(spacing: Space.sm) {
                        Button("Mark as Main") { apply { document.setMain(true, for: ids, undoManager: undoManager) } }
                        Button("Not Main") { apply { document.setMain(false, for: ids, undoManager: undoManager) } }
                    }
                }
            }
        }
    }

    private func apply(_ edit: () -> Void) {
        edit()
        changed()
    }
}

/// The right side of the Voices page while several characters are picked.
struct ManyCharactersPanel: View {
    @ObservedObject var document: InnerVoiceDocument
    let ids: [UUID]
    var changed: () -> Void
    var clear: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Space.lg) {
                Text("\(ids.count) Characters Selected").font(.title2.weight(.semibold))
                Text(ids.compactMap { document.project.character($0)?.name }.joined(separator: ", "))
                    .foregroundStyle(.secondary)
                    .lineLimit(4)
                    .fixedSize(horizontal: false, vertical: true)
                ManyCharactersControls(document: document, ids: ids, changed: changed)
                Button("Clear Selection", action: clear)
                    .keyboardShortcut(.cancelAction)
                    .help("Go back to one character (Esc)")
                Text("⌘-click characters in the list to add or remove one; ⇧-click picks a run of them.")
                    .font(.caption).foregroundStyle(.tertiary)
            }
            .padding(Space.xl)
            .frame(maxWidth: 640, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

/// Right-click on several picked characters: the same choices as a menu.
struct ManyCharactersMenu: View {
    @ObservedObject var document: InnerVoiceDocument
    let ids: [UUID]
    var narration = false
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        let style = document.project.settings.castingStyle
        Menu("Gender of \(ids.count) Characters") {
            ForEach(CharacterGender.allCases) { g in Button(g.title) { document.setGender(g, for: ids, undoManager: undoManager) } }
            Divider()
            Button("From the Book") { document.setGender(nil, for: ids, undoManager: undoManager) }
        }
        if let style {
            Menu("Read By") {
                Button("Automatic") { document.setRole(nil, for: ids, undoManager: undoManager) }
                ForEach(style == .oneVoice ? [VoiceRole.narrator, .own] : VoiceRole.allCases) { r in
                    Button(narration ? r.narratorTitle : r == .own ? r.title : r.voiceTitle) { document.setRole(r, for: ids, undoManager: undoManager) }
                }
            }
            if style == .fullCast {
                Button("Mark \(ids.count) as Main Characters") { document.setMain(true, for: ids, undoManager: undoManager) }
                Button("Unmark \(ids.count) as Main Characters") { document.setMain(false, for: ids, undoManager: undoManager) }
            }
        }
    }
}
