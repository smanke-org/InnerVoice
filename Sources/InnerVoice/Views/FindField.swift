import AppKit
import SwiftUI

/// The search field at the top of the Cast and Voices pages. Esc clears it; ⌘F (or
/// ⇧⌘F, Edit › Find) puts the cursor in it while its page shows.
struct FindField: View {
    let prompt: String
    @Binding var text: String
    /// Changes when Find is asked for on this page.
    var focusRequest: UUID?
    @FocusState private var focused: Bool
    @State private var monitor: Any?

    var body: some View {
        HStack(spacing: Space.xs) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField(prompt, text: $text)
                .textFieldStyle(.plain)
                .focused($focused)
                .onKeyPress(.escape) {
                    guard !text.isEmpty else { return .ignored }
                    text = ""
                    return .handled
                }
            if !text.isEmpty {
                Button("Clear", systemImage: "xmark.circle.fill") { text = "" }
                    .labelStyle(.iconOnly)
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .help("Clear the search (Esc)")
            }
        }
        .padding(.horizontal, Space.sm)
        .padding(.vertical, 5)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 7))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(prompt)
        .onChange(of: focusRequest) { focused = true }
        .onAppear {
            monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
                let mods = e.modifierFlags.intersection([.command, .shift, .option, .control])
                if mods == .command, e.charactersIgnoringModifiers == "f" {
                    focused = true
                    return nil
                }
                return e
            }
        }
        .onDisappear {
            if let monitor { NSEvent.removeMonitor(monitor) }
            monitor = nil
        }
    }
}
