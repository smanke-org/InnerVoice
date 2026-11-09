import AppKit

/// "Ask before quitting" (Settings › General, on by default, synced).
///
/// The question is skipped when macOS is logging out, restarting or shutting down, when Inner
/// Voice quits itself (installing an update, the end of a test run), and in test runs, which
/// quit on their own and would otherwise wait on the alert forever.
enum QuitConfirmation {
    static let key = "confirmQuit"

    private(set) static var confirmed = false

    static var isEnabled: Bool {
        get { UserDefaults.standard.object(forKey: key) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: key) }
    }

    /// Quits without asking: for quitting itself.
    static func quitNow() {
        confirmed = true
        NSApp.terminate(nil)
    }

    /// For `applicationShouldTerminate`.
    static func shouldTerminate() -> NSApplication.TerminateReply {
        let systemQuit = NSAppleEventManager.shared().currentAppleEvent?
            .attributeDescriptor(forKeyword: AEKeyword(kAEQuitReason)) != nil
        guard isEnabled, !confirmed, !systemQuit, !SystemHooks.isTestRun else { return .terminateNow }

        // Without a Dock icon the app needs to be a regular one for the alert to come to the
        // front; the old policy goes back a runloop turn later, once the alert is gone.
        let previousPolicy = NSApp.activationPolicy()
        if previousPolicy != .regular { NSApp.setActivationPolicy(.regular) }
        defer { if previousPolicy != .regular { DispatchQueue.main.async { NSApp.setActivationPolicy(previousPolicy) } } }

        let alert = NSAlert()
        alert.messageText = "Quit \(AppInfo.name)?"
        if let reason = SystemHooks.busyReason?() {
            alert.informativeText = "InnerVoice is \(reason). Quitting stops it; what's done so far is kept in the project."
        } else {
            alert.informativeText = "Your projects are saved. Open InnerVoice again to pick up where you left off."
        }
        // Quit has no key, so Return does nothing: quitting takes a click, never a stray keystroke.
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Quit").keyEquivalent = ""
        alert.showsSuppressionButton = true
        alert.suppressionButton?.title = "Don't ask again"
        alert.layout()
        NSApp.activate(ignoringOtherApps: true)
        alert.window.makeKeyAndOrderFront(nil)

        let response = alert.runModal()
        Diagnostics.note("quit question: \(response == .alertSecondButtonReturn ? "quit" : "cancel")")
        guard response == .alertSecondButtonReturn else { return .terminateCancel }
        if alert.suppressionButton?.state == .on { isEnabled = false }
        return .terminateNow
    }
}
