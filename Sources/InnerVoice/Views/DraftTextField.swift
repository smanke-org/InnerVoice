import SwiftUI

/// A text field that types into a draft of its own and saves to the book when typing pauses,
/// on Return, or when it loses focus. Saving a change rebuilds the book's index and redraws
/// every view of the book, which on a full novel is slower than typing; with a draft, typing
/// never waits for it, and a burst of typing is one undo step.
struct DraftTextField: View {
    let title: String
    /// The saved value, in the book.
    @Binding var text: String
    var axis: Axis = .horizontal
    var delay: Duration = .milliseconds(700)

    /// What's typed and not saved yet; nil shows the saved value.
    @State private var draft: String?
    /// The saved value when typing began, to keep changes made meanwhile (Build Summary
    /// writing a block into Voice Notes).
    @State private var base = ""
    @State private var saveTask: Task<Void, Never>?
    @FocusState private var focused: Bool

    init(_ title: String, text: Binding<String>, axis: Axis = .horizontal) {
        self.title = title
        _text = text
        self.axis = axis
    }

    var body: some View {
        TextField(title, text: Binding(get: { draft ?? text }, set: { new in
            if draft == nil { base = text }
            draft = new
            scheduleSave()
        }), axis: axis)
        .focused($focused)
        .onSubmit(save)
        .onChange(of: focused) { _, isFocused in if !isFocused { save() } }
        .onDisappear(perform: save)
    }

    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { @MainActor in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            save()
        }
    }

    private func save() {
        saveTask?.cancel()
        saveTask = nil
        guard let typed = draft else { return }
        let merged = Self.merge(typed, base: base, current: text)
        if merged != text { text = merged }
        draft = nil
    }

    /// The typed text, keeping anything added to the saved value since typing began.
    static func merge(_ typed: String, base: String, current: String) -> String {
        guard current != base else { return typed }
        if base.isEmpty { return typed.isEmpty ? current : typed + "\n\n" + current }
        if current.hasPrefix(base) { return typed + current.dropFirst(base.count) }
        if current.hasSuffix(base) { return current.dropLast(base.count) + typed }
        return typed
    }
}
