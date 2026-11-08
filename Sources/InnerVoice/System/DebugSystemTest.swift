#if DEBUG
import AppKit

/// Debug-only check of the system features, with no clicks and no screen capture:
///
///     APP_STORE=/tmp/iv-sys/test.store INNERVOICE_DEBUG_SYSTEM_TEST=/tmp/iv-sys InnerVoice
///
/// Checks every Help page for unresolved tokens, opens Help, About, What's New and Settings
/// (through the same path the menus use), draws each window to a PNG in the folder with
/// cacheDisplay, writes the menu bar and ToolHUD menus and the ShortcutsLens report to
/// report.txt, then quits with SYSTEMTEST PASS or FAIL on stdout. Starts no updater, sync,
/// menu bar icon or hub. Refuses to run without APP_STORE.
enum DebugSystemTest {
    static func runIfAsked() -> Bool {
        let env = ProcessInfo.processInfo.environment
        guard let dir = env["INNERVOICE_DEBUG_SYSTEM_TEST"], !dir.isEmpty else { return false }
        guard !(env["APP_STORE"] ?? "").isEmpty else {
            print("SYSTEMTEST refused: set APP_STORE to a file in /tmp")
            QuitConfirmation.quitNow()
            return true
        }
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        Task { await run(in: URL(fileURLWithPath: dir)) }
        return true
    }

    private static func run(in dir: URL) async {
        var report: [String] = []
        var failures: [String] = []
        func check(_ ok: Bool, _ what: String) {
            report.append("\(ok ? "ok  " : "FAIL") \(what)")
            if !ok { failures.append(what) }
        }

        // Help text: no token left unresolved, every page has words.
        for topic in HelpTopic.allCases {
            let text = topic.blocks.map(\.searchText).joined(separator: " ")
            check(!text.contains("{") && !text.contains("}"), "help \(topic.rawValue): tokens resolved")
            check(text.count > 200, "help \(topic.rawValue): \(text.count) characters")
        }

        try? await Task.sleep(for: .seconds(1))
        Help.show(.gettingStarted)
        try? await Task.sleep(for: .seconds(1))
        check(HelpWindow.shared.window?.isVisible == true, "Help window opens")
        draw(HelpWindow.shared.window, to: dir.appending(path: "help-getting-started.png"))
        Help.show(.keyboardShortcuts)
        try? await Task.sleep(for: .seconds(0.6))
        check(HelpWindow.shared.state.selection == .keyboardShortcuts, "Help.show selects the topic")
        draw(HelpWindow.shared.window, to: dir.appending(path: "help-shortcuts.png"))

        AppInfo.showAbout()
        try? await Task.sleep(for: .seconds(2))
        check(AboutWindow.shared.debugWindow?.isVisible == true, "About window opens")
        draw(AboutWindow.shared.debugWindow, to: dir.appending(path: "about.png"))

        WhatsNew.shared.show(asked: true)
        try? await Task.sleep(for: .seconds(1))
        check(WhatsNew.shared.debugWindow?.isVisible == true, "What's New window opens")
        draw(WhatsNew.shared.debugWindow, to: dir.appending(path: "whats-new.png"))

        let before = Set(NSApp.windows.filter(\.isVisible).map(ObjectIdentifier.init))
        SystemHooks.openSettings()
        try? await Task.sleep(for: .seconds(1.5))
        let settings = NSApp.windows.first { $0.isVisible && !before.contains(ObjectIdentifier($0)) }
        check(settings != nil, "openSettings opens a window (\(settings?.title ?? "none"))")
        draw(settings, to: dir.appending(path: "settings.png"))

        let status = SystemMenu.make(forHub: false)
        let hub = SystemMenu.make(forHub: true)
        check(status.items.contains { $0.plainTitle == "New from Manuscript…" }, "menu bar menu has New from Manuscript…")
        check(status.items.contains { $0.plainTitle == "Open Recent" && $0.submenu != nil }, "menu bar menu has Open Recent")
        check(!status.items.contains { $0.plainTitle == "Tag Speakers" }, "menu bar menu leaves Tag Speakers to ToolHUD")
        check(hub.items.contains { $0.plainTitle == "Tag Speakers" } && hub.items.contains { $0.plainTitle == "Record" },
              "ToolHUD menu has Tag Speakers and Record")
        let hubItems = HubItem.items(of: hub)
        check(!hubItems.isEmpty && hubItems.contains { $0.imageInTitle == true }, "ToolHUD snapshot encodes icons in titles")
        report.append("--- menu bar menu\n" + dump(status))
        report.append("--- ToolHUD menu\n" + dump(hub))

        let shortcuts = ShortcutCatalog.reported
        check(shortcuts.count == 5 + ShortcutCatalog.pages.count, "ShortcutsLens report has \(shortcuts.count) shortcuts")
        report.append("--- ShortcutsLens\n" + shortcuts.map { "\($0.title) key=\($0.keyCode ?? -1) mods=\($0.carbonModifiers)" }.joined(separator: "\n"))

        let docMenu = SystemHooks.dockMenu()
        check(docMenu?.items.first?.title == "Settings…", "Dock menu has Settings…")
        check(AppInfo.name == "Inner Voice" && UpdateController.repository == "smanke-org/InnerVoice", "names and repository")

        report.append("--- windows\n" + NSApp.windows.filter(\.isVisible).map { "\($0.title) \(Int($0.frame.width))×\(Int($0.frame.height))" }.joined(separator: "\n"))
        try? report.joined(separator: "\n").write(to: dir.appending(path: "report.txt"), atomically: true, encoding: .utf8)
        print(report.joined(separator: "\n"))
        print(failures.isEmpty ? "SYSTEMTEST PASS" : "SYSTEMTEST FAIL: \(failures.joined(separator: "; "))")
        QuitConfirmation.quitNow()
    }

    private static func dump(_ menu: NSMenu, indent: String = "") -> String {
        menu.items.map { item in
            if item.isSeparatorItem { return indent + "---" }
            var line = indent + item.plainTitle
            if item.titleIcon != nil { line += "  [icon]" }
            if !item.isEnabled { line += "  (disabled)" }
            if let sub = item.submenu { line += "\n" + dump(sub, indent: indent + "    ") }
            return line
        }.joined(separator: "\n")
    }

    /// Draws a window's content (this app's own window only) to a PNG.
    private static func draw(_ window: NSWindow?, to url: URL) {
        guard let view = window?.contentView?.superview ?? window?.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }
}
#endif
