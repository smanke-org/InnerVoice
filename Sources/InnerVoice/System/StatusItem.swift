import AppKit

/// The menu bar icon (Settings › General › Show in menu bar) and the menu ToolHUD shows.
/// Both menus are built fresh each time they open, so Open Recent and the update item are
/// current. The menus hold actions; preferences live in Settings.
final class StatusItemController: NSObject, NSMenuDelegate {
    static let shared = StatusItemController()

    private var statusItem: NSStatusItem?

    func start() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = Self.icon
        let menu = NSMenu()
        menu.delegate = self
        // Enabled state is set by hand below; auto-enabling would override it.
        menu.autoenablesItems = false
        item.menu = menu
        statusItem = item

        // Settings can hide the menu bar icon, and ToolHUD can take the menu into its own
        // while it runs.
        item.isVisible = MenuHub.shared.showsOwnIcon
        NotificationCenter.default.addObserver(forName: AppPresence.menuBarDidChange, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.statusItem?.isVisible = MenuHub.shared.showsOwnIcon }
        }
        MenuHub.shared.start(menu: { SystemMenu.make(forHub: true) }, icon: { Self.icon })
    }

    private static var icon: NSImage? {
        let image = NSImage(systemSymbolName: "quote.bubble", accessibilityDescription: AppInfo.name)
        image?.isTemplate = true
        return image
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        SystemMenu.fill(menu, forHub: false)
    }
}

/// What the menu bar menu and ToolHUD's submenu hold.
enum SystemMenu {
    /// A new menu, as it would show when opened.
    static func make(forHub: Bool) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        fill(menu, forHub: forHub)
        return menu
    }

    /// The menu bar menu: New from Manuscript…, Open…, Open Recent, Settings…, Help, the update
    /// item, About and Quit. ToolHUD's copy adds Tag Speakers and Record for the project in front.
    static func fill(_ menu: NSMenu, forHub: Bool) {
        menu.removeAllItems()
        let target = SystemMenuTarget.shared

        menu.addItem(withTitle: "New from Manuscript…", action: #selector(SystemMenuTarget.newFromManuscript), target: target)
            .showIcon("doc.badge.plus")
        menu.addItem(withTitle: "Open…", action: #selector(SystemMenuTarget.open), target: target)
            .showIcon("folder")
        let recent = menu.addItem(withTitle: "Open Recent", action: nil, target: nil)
        let recentMenu = NSMenu(title: "Open Recent")
        recentMenu.autoenablesItems = false
        let urls = NSDocumentController.shared.recentDocumentURLs
        for url in urls {
            let item = recentMenu.addItem(withTitle: FileManager.default.displayName(atPath: url.path),
                                          action: #selector(SystemMenuTarget.openRecent(_:)), target: target)
            item.representedObject = url
            item.toolTip = url.path
        }
        if !urls.isEmpty { recentMenu.addItem(.separator()) }
        recentMenu.addItem(withTitle: "Clear Menu", action: #selector(SystemMenuTarget.clearRecent), target: target)
            .isEnabled = !urls.isEmpty
        recent.submenu = recentMenu

        if forHub {
            let hasProject = !NSDocumentController.shared.documents.isEmpty
            menu.addItem(.separator())
            let tag = menu.addItem(withTitle: "Tag Speakers", action: #selector(SystemMenuTarget.tagSpeakers), target: target)
            tag.showIcon("sparkles")
            tag.isEnabled = hasProject
            tag.toolTip = "Use the on-device AI to find who speaks each line in the project in front."
            let record = menu.addItem(withTitle: "Record", action: #selector(SystemMenuTarget.record), target: target)
            record.showIcon("record.circle")
            record.isEnabled = hasProject
        }

        menu.addItem(.separator())
        menu.addItem(withTitle: "Settings…", action: #selector(SystemMenuTarget.settings), keyEquivalent: ",", target: target)
            .showIcon("gearshape")
        menu.addItem(withTitle: "\(AppInfo.name) Help", action: #selector(SystemMenuTarget.help), target: target)
            .showIcon("questionmark.circle")
        if let staged = AutoUpdater.shared.staged {
            menu.addItem(withTitle: "Update to \(staged.version) Now", action: #selector(SystemMenuTarget.installStaged), target: target)
                .showIcon("arrow.down.circle")
        } else if let pending = UpdateAvailability.shared.pending {
            menu.addItem(withTitle: "Update to \(pending)…", action: #selector(SystemMenuTarget.checkForUpdates), target: target)
                .showIcon("arrow.down.circle").toolTip = "A newer release is available. Installing it needs your confirmation."
        }
        menu.addItem(withTitle: "About \(AppInfo.name)", action: #selector(SystemMenuTarget.about), target: target)
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit \(AppInfo.name)", action: #selector(SystemMenuTarget.quit), keyEquivalent: "q", target: target)
        menu.addItem(.separator())
        menu.addItem(withTitle: "\(AppInfo.name) \(AppInfo.displayVersion)", action: nil, target: nil).isEnabled = false
    }
}

/// The menus' actions. Items run from the menu bar or from ToolHUD, so each one that opens a
/// window brings Inner Voice forward first.
final class SystemMenuTarget: NSObject {
    static let shared = SystemMenuTarget()

    private func bringForward() {
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc func newFromManuscript() {
        bringForward()
        NotificationCenter.default.post(name: SystemHooks.newFromManuscript, object: nil)
    }

    @objc func open() {
        bringForward()
        NSDocumentController.shared.openDocument(nil)
    }

    @objc func openRecent(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? URL else { return }
        bringForward()
        NSDocumentController.shared.openDocument(withContentsOf: url, display: true) { _, _, error in
            if let error { Diagnostics.note("open recent failed: \(error.localizedDescription)") }
        }
    }

    @objc func clearRecent() { NSDocumentController.shared.clearRecentDocuments(nil) }

    @objc func tagSpeakers() {
        bringForward()
        NotificationCenter.default.post(name: SystemHooks.tagSpeakers, object: nil)
    }

    @objc func record() {
        bringForward()
        NotificationCenter.default.post(name: SystemHooks.record, object: nil)
    }

    @objc func settings() { SystemHooks.openSettings() }
    @objc func help() { Help.show() }
    @objc func checkForUpdates() { UpdateController.checkForUpdates() }
    @objc func installStaged() { AutoUpdater.shared.installNow() }
    @objc func about() { AppInfo.showAbout() }
    @objc func quit() { NSApp.terminate(nil) }
}

extension NSMenu {
    /// An item aimed at `target`.
    @discardableResult
    func addItem(withTitle title: String, action: Selector?, keyEquivalent: String = "", target: AnyObject?) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: keyEquivalent)
        item.target = target
        addItem(item)
        return item
    }
}
