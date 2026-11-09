import AppKit
import InnerVoiceKit

/// The list of speakers in a right-click menu: a search field on top that has focus as
/// soon as the menu opens (type to narrow the list, Return picks the first match), then
/// pinned speakers, then everyone else, each A to Z by first name. Holding Option turns
/// each name into Pin or Unpin.
///
/// Used as the Assign To submenu on a line and as the whole menu on a name in the margin.
@MainActor
final class SpeakerMenu: NSObject, NSMenuDelegate, NSSearchFieldDelegate {
    /// The menu to show. It keeps this object alive (a menu's delegate is weak).
    let menu = NSMenu(title: "Assign To")
    private let field = NSSearchField()
    private var rows: [(character: BookCharacter, item: NSMenuItem, pin: NSMenuItem)] = []
    private var pinnedHeader: NSMenuItem?
    private var othersHeader: NSMenuItem?
    private let noMatch = NSMenuItem(title: "No Speaker Matches", action: nil, keyEquivalent: "")
    private let assign: (UUID?) -> Void

    /// - Parameters:
    ///   - heading: a line of context above the search field ("Cyrus's 2 lines in this paragraph").
    ///   - current: the speaker the lines have now, checked in the list.
    init(project: Project, heading: String? = nil, current: UUID?,
         assign: @escaping (UUID?) -> Void, narration: @escaping () -> Void, pin: @escaping (UUID, Bool) -> Void) {
        self.assign = assign
        super.init()
        objc_setAssociatedObject(menu, &Self.ownerKey, self, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
        menu.delegate = self
        menu.autoenablesItems = false
        func addItem(_ item: NSMenuItem) { menu.addItem(item) }

        if let heading {
            addItem(NSMenuItem.sectionHeader(title: heading))
        }
        addItem(searchItem())

        let order = project.speakerOrder
        if !order.pinned.isEmpty {
            let header = NSMenuItem.sectionHeader(title: "Pinned")
            addItem(header)
            pinnedHeader = header
            for c in order.pinned { addRow(c, current: current, pin: pin) }
            let others = NSMenuItem.sectionHeader(title: "Everyone Else")
            addItem(others)
            othersHeader = others
        }
        for c in order.others { addRow(c, current: current, pin: pin) }
        noMatch.isEnabled = false
        noMatch.isHidden = true
        addItem(noMatch)

        addItem(.separator())
        addItem(ClosureMenuItem(title: "Who's Speaking? (Clear)") { assign(nil) })
        addItem(ClosureMenuItem(title: "Narration (Not Spoken)") { narration() })
    }

    nonisolated(unsafe) private static var ownerKey: UInt8 = 0

    private func searchItem() -> NSMenuItem {
        let item = NSMenuItem()
        let box = NSView(frame: NSRect(x: 0, y: 0, width: 240, height: 30))
        box.autoresizingMask = [.width]
        field.frame = NSRect(x: 14, y: 4, width: 212, height: 22)
        field.autoresizingMask = [.width]
        field.placeholderString = "Find a speaker"
        field.controlSize = .regular
        field.delegate = self
        field.sendsSearchStringImmediately = true
        field.focusRingType = .none
        field.setAccessibilityLabel("Find a speaker")
        box.addSubview(field)
        item.view = box
        return item
    }

    private func addRow(_ c: BookCharacter, current: UUID?, pin: @escaping (UUID, Bool) -> Void) {
        let assign = self.assign
        let item = ClosureMenuItem(title: c.name) { assign(c.id) }
        item.image = ManuscriptTextView.Coordinator.dot(NSColor(hexString: c.colorHex))
        item.state = c.id == current ? .on : .off
        item.keyEquivalentModifierMask = []
        let pinned = c.isPinned
        let alternate = ClosureMenuItem(title: pinned ? "Unpin \(c.name)" : "Pin \(c.name) to Top") { pin(c.id, !pinned) }
        alternate.image = NSImage(systemSymbolName: pinned ? "pin.slash" : "pin", accessibilityDescription: nil)
        alternate.keyEquivalentModifierMask = .option
        alternate.isAlternate = true
        menu.addItem(item)
        menu.addItem(alternate)
        rows.append((c, item, alternate))
    }

    // MARK: Focus

    func menuWillOpen(_ menu: NSMenu) {
        field.stringValue = ""
        filter("")
        // The menu's window exists once it is on screen; focus the field then, in the
        // run loop mode menus track in.
        RunLoop.current.perform(inModes: [.eventTracking, .default]) { [weak self] in
            MainActor.assumeIsolated {
                guard let self, let window = self.field.window else { return }
                window.makeFirstResponder(self.field)
            }
        }
    }

    // MARK: Filtering

    func controlTextDidChange(_ obj: Notification) { filter(field.stringValue) }

    func filter(_ query: String) {
        var anyPinned = false, anyOther = false
        for row in rows {
            let show = row.character.matches(query)
            row.item.isHidden = !show
            row.pin.isHidden = !show
            if show { if row.character.isPinned { anyPinned = true } else { anyOther = true } }
        }
        pinnedHeader?.isHidden = !anyPinned
        othersHeader?.isHidden = !anyOther || !anyPinned
        noMatch.isHidden = anyPinned || anyOther
    }

    /// Closes this menu and the menu it opened from.
    private func closeAll() {
        var top: NSMenu = menu
        while let up = top.supermenu { top = up }
        top.cancelTracking()
    }

    /// The first speaker the search still shows, in list order.
    var firstMatch: BookCharacter? { rows.first { !$0.item.isHidden }?.character }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        switch selector {
        case #selector(NSResponder.insertNewline(_:)):
            guard let c = firstMatch else { return true }
            // After the menu has closed (a run-loop turn later, which also works while a
            // main-queue job is waiting on the menu).
            nonisolated(unsafe) let assign = self.assign
            let id = c.id
            closeAll()
            RunLoop.main.perform { MainActor.assumeIsolated { assign(id) } }
            return true
        case #selector(NSResponder.cancelOperation(_:)):
            if field.stringValue.isEmpty { closeAll() } else { field.stringValue = ""; filter("") }
            return true
        default:
            return false
        }
    }
}
