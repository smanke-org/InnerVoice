import AppKit

/// Where the app shows itself: a Dock icon, a menu bar icon, both, or neither.
///
/// Inner Voice is a document app, so unlike the menu bar apps it starts with both on: the Dock
/// icon carries the project windows, and the menu bar icon is a quick way to start or open a
/// project. With the Dock icon on, right-clicking it offers Settings (macOS only shows an app's
/// own Dock-menu items while it is running with a Dock icon). With both off the app runs with
/// no icon at all; opening it again from Applications or Spotlight brings up Settings.
enum AppPresence {
    /// Posted when `showInMenuBar` changes, for the status item to follow.
    static let menuBarDidChange = Notification.Name("AppPresence.menuBarDidChange")

    enum Key {
        static let dock = "showInDock"
        static let menuBar = "showInMenuBar"
    }

    /// The choices on a new install.
    static let dockDefault = true
    static let menuBarDefault = true

    static var showInDock: Bool {
        get { UserDefaults.standard.object(forKey: Key.dock) as? Bool ?? dockDefault }
        set { UserDefaults.standard.set(newValue, forKey: Key.dock) }
    }

    static var showInMenuBar: Bool {
        get { UserDefaults.standard.object(forKey: Key.menuBar) as? Bool ?? menuBarDefault }
        set {
            UserDefaults.standard.set(newValue, forKey: Key.menuBar)
            NotificationCenter.default.post(name: menuBarDidChange, object: nil)
        }
    }

    /// Shown under the two checkboxes while both are off.
    static func hiddenEverywhereNote(appName: String, settingsName: String) -> String {
        "\(appName) will keep running with no icon. To get back here, open it again from Applications or Spotlight; that opens \(settingsName)."
    }

    /// Sets the activation policy to match `showInDock`. `keepInFront` is a
    /// window to keep visible when the Dock icon goes away (the settings window
    /// the change was made from), since leaving the Dock deactivates the app.
    static func applyDock(keepInFront window: NSWindow? = nil) {
        if showInDock {
            if NSApp.activationPolicy() != .regular { NSApp.setActivationPolicy(.regular) }
        } else {
            // Going back to .accessory while the app is active doesn't take if
            // done synchronously; a runloop turn later it does.
            DispatchQueue.main.async {
                NSApp.setActivationPolicy(.accessory)
                guard let window, window.isVisible else { return }
                // macOS refuses a plain activate once the app has left the Dock.
                window.orderFrontRegardless()
                NSApp.activate(ignoringOtherApps: true)
                window.makeKey()
            }
        }
    }

    /// The Dock icon's right-click menu: one item that opens Settings.
    static func dockMenu(title: String, target: AnyObject, action: Selector) -> NSMenu {
        let menu = NSMenu()
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = target
        menu.addItem(item)
        return menu
    }
}
