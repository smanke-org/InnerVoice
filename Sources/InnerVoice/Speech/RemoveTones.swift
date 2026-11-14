import AppKit
import InnerVoiceKit

/// Book › Remove Tones…: asks which tones to clear (Tag Speakers' or the narrator's, or
/// both), warns that tagging them again takes a while, then clears them in one Undo step.
@MainActor
enum RemoveTones {
    static func run(document: InnerVoiceDocument, undoManager: UndoManager?) {
        let counts = ToneRemoval.counts(in: document.project)
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Remove Tones?"
        guard counts.automatic + counts.narrator > 0 else {
            alert.messageText = "No Tones to Remove"
            alert.informativeText = "No line in this book has a tone yet."
            alert.addButton(withTitle: "OK")
            alert.runModal()
            return
        }
        alert.informativeText = """
            Lines without a tone are read evenly. Getting tones back means running Tag Speakers again, \
            which can take a long time for a whole book. Tones you remove from your own choices can't \
            be tagged back; you'd set them again by hand.

            You can undo this with Edit › Undo while the book is open.
            """
        let auto = NSButton(checkboxWithTitle: "Tones from Tag Speakers (\(counts.automatic.formatted()) lines)", target: nil, action: nil)
        auto.state = counts.automatic > 0 ? .on : .off
        auto.isEnabled = counts.automatic > 0
        let mine = NSButton(checkboxWithTitle: "Tones you set yourself (\(counts.narrator.formatted()) lines)", target: nil, action: nil)
        mine.state = .off
        mine.isEnabled = counts.narrator > 0
        let stack = NSStackView(views: [auto, mine])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        stack.frame = NSRect(x: 0, y: 0, width: 320, height: 46)
        alert.accessoryView = stack
        // Cancel is the default button: Return never removes anything.
        alert.addButton(withTitle: "Cancel")
        let remove = alert.addButton(withTitle: "Remove Tones")
        remove.hasDestructiveAction = true
        remove.keyEquivalent = ""
        alert.buttons[0].keyEquivalent = "\r"
        guard alert.runModal() == .alertSecondButtonReturn else { return }
        let choice = ToneRemoval(automatic: auto.state == .on, narrator: mine.state == .on)
        guard !choice.isEmpty else { return }
        perform(choice, document: document, undoManager: undoManager)
    }

    static func perform(_ choice: ToneRemoval, document: InnerVoiceDocument, undoManager: UndoManager?) {
        document.change("Remove Tones", undoManager: undoManager) { choice.apply(to: &$0) }
    }
}
