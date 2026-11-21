import AppKit
import InnerVoiceKit

/// Book › Remove Tones…: asks which tones to clear (Tag Speakers' or the narrator's, or
/// both, and which tones to keep), warns that tagging them again takes a while, then clears
/// them in one Undo step.
@MainActor
enum RemoveTones {
    struct Question {
        let alert: NSAlert
        let auto: NSButton
        let mine: NSButton
        let keepBoxes: [NSButton]

        var choice: ToneRemoval {
            let keep = Set(keepBoxes.filter { $0.state == .on }.compactMap { $0.identifier.flatMap { Tone(rawValue: $0.rawValue) } })
            return ToneRemoval(automatic: auto.state == .on, narrator: mine.state == .on, keep: keep)
        }
    }

    static func run(document: InnerVoiceDocument, undoManager: UndoManager?) {
        guard let q = question(for: document.project) else {
            let alert = NSAlert()
            alert.messageText = "No Tones to Remove"
            alert.informativeText = "No line in this book has a tone yet."
            alert.addButton(withTitle: "OK")
            alert.runModal()
            return
        }
        guard q.alert.runModal() == .alertSecondButtonReturn else { return }
        let choice = q.choice
        guard !choice.isEmpty else { return }
        perform(choice, document: document, undoManager: undoManager)
    }

    /// The question, or nil when no line has a tone (also used by snapshots).
    static func question(for project: Project) -> Question? {
        let counts = ToneRemoval.counts(in: project)
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Remove Tones?"
        guard counts.automatic + counts.narrator > 0 else { return nil }
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

        // The tones in this book, each with a Keep box: kept tones stay on their lines.
        let toneCounts = ToneRemoval.toneCounts(in: project)
        let tones = Tone.allCases.filter { $0 != .neutral && (toneCounts[$0] ?? 0) > 0 }
        let keepBoxes = tones.map { tone in
            let box = NSButton(checkboxWithTitle: "\(tone.title) (\((toneCounts[tone] ?? 0).formatted()))", target: nil, action: nil)
            box.state = .off
            box.identifier = NSUserInterfaceItemIdentifier(tone.rawValue)
            return box
        }
        var views: [NSView] = [auto, mine]
        if !keepBoxes.isEmpty {
            let label = NSTextField(wrappingLabelWithString: "Keep these tones (checked tones stay on their lines):")
            label.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
            label.textColor = .secondaryLabelColor
            label.preferredMaxLayoutWidth = 320
            let rows = stride(from: 0, to: keepBoxes.count, by: 2).map { i in
                [keepBoxes[i], i + 1 < keepBoxes.count ? keepBoxes[i + 1] : NSGridCell.emptyContentView]
            }
            let grid = NSGridView(views: rows)
            grid.rowSpacing = 4
            grid.columnSpacing = 18
            views += [label, grid]
        }
        let stack = NSStackView(views: views)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        stack.setCustomSpacing(14, after: mine)
        stack.frame = NSRect(origin: .zero, size: NSSize(width: 340, height: stack.fittingSize.height))
        alert.accessoryView = stack
        // Cancel is the default button: Return never removes anything.
        alert.addButton(withTitle: "Cancel")
        let remove = alert.addButton(withTitle: "Remove Tones")
        remove.hasDestructiveAction = true
        remove.keyEquivalent = ""
        alert.buttons[0].keyEquivalent = "\r"
        return Question(alert: alert, auto: auto, mine: mine, keepBoxes: keepBoxes)
    }

    static func perform(_ choice: ToneRemoval, document: InnerVoiceDocument, undoManager: UndoManager?) {
        document.change("Remove Tones", undoManager: undoManager) { choice.apply(to: &$0) }
    }
}
