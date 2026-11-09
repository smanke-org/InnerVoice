import AppKit
import SwiftUI

/// The one place the app wires in the house system features: updater, settings sync, appearance,
/// Dock and menu bar icons, ToolHUD's menu hub, the ShortcutsLens report, asking before quitting,
/// About and Help.
///
/// From the app delegate:
///
///     func applicationDidFinishLaunching(_:)  { SystemHooks.didFinishLaunching() }
///     func applicationShouldTerminate(_ app:) -> NSApplication.TerminateReply { SystemHooks.shouldTerminate(app) }
///     func applicationWillTerminate(_:)       { SystemHooks.willTerminate() }
///     func applicationDockMenu(_:) -> NSMenu? { SystemHooks.dockMenu() }
///     func applicationShouldHandleReopen(_:hasVisibleWindows flag: Bool) -> Bool {
///         SystemHooks.shouldHandleReopen(hasVisibleWindows: flag)
///     }
///
/// In the scene: `.commands { SystemAppCommands(); SystemHelpCommands() }`, and
/// `SystemGeneralSettings()` inside Settings › General's Form.
///
/// The menus post these notifications for the app to act on (object nil, on the main thread):
/// `newFromManuscript`, `tagSpeakers`, `record`. Open… and Open Recent go straight to
/// NSDocumentController.
enum SystemHooks {
    // MARK: Notifications the app observes

    /// File › New from Manuscript… from the menu bar menu or ToolHUD: show the open panel.
    static let newFromManuscript = Notification.Name("InnerVoice.newFromManuscript")
    /// Tag Speakers on the project in front (ToolHUD's menu).
    static let tagSpeakers = Notification.Name("InnerVoice.tagSpeakers")
    /// Record on the project in front (ToolHUD's menu).
    static let record = Notification.Name("InnerVoice.record")

    // MARK: Set by the app

    /// What InnerVoice is busy with right now, in words that follow "InnerVoice is"
    /// ("tagging speakers", "building summaries"), or nil. While it says something, an
    /// automatic update waits, and the quit question mentions it.
    static var busyReason: (() -> String?)?

    /// Called after settings from another Mac were adopted, for the app to re-read its own
    /// synced preferences (register them with `SettingsSync.register`).
    static var onSettingsSynced: (() -> Void)?

    /// Opens Settings when the app knows how (a SwiftUI `openSettings` action it captured).
    /// Without it, `openSettings()` runs the app menu's Settings… item.
    static var settingsOpener: (() -> Void)?

    // MARK: Test runs

    /// A test or snapshot run (APP_STORE, APP_SNAPSHOT or --selftest): no updater, sync, menu
    /// bar icon, ToolHUD hub or ShortcutsLens report, and quitting never asks.
    static var isTestRun: Bool {
        let env = ProcessInfo.processInfo.environment
        return !(env["APP_STORE"] ?? "").isEmpty || env["APP_SNAPSHOT"] != nil
            || CommandLine.arguments.contains("--selftest")
    }

    // MARK: App delegate

    static func didFinishLaunching() {
        // Before anything saves a setting: an empty domain means a brand-new install.
        let newInstall = SettingsSync.domainIsEmpty()
        Diagnostics.note("launch \(AppInfo.displayVersion) from \(Bundle.main.bundlePath)")
        AppAppearance.start()
        #if DEBUG
        if DebugSystemTest.runIfAsked() { return }
        #endif
        Autosave.start()
        guard !isTestRun else { return }

        AppPresence.applyDock()
        StatusItemController.shared.start()
        ShortcutsLensReport.start { ShortcutCatalog.reported }

        AutoUpdater.shared.appBusyReason = { busyReason?() }
        SettingsSync.shared.onApply = { onSettingsSynced?() }
        if newInstall { SettingsSync.shared.adoptOnNewInstall() }
        SettingsSync.shared.start()

        // Debug builds leave updates to the real app unless an updater test asks: they share the
        // installed app's defaults, and a stray one stamping update.lastCheck every hour once
        // kept a real app from ever checking.
        #if DEBUG
        let updatesAllowed = ProcessInfo.processInfo.environment["INNERVOICE_UPDATE_CHECK_SECONDS"] != nil
        #else
        let updatesAllowed = true
        #endif
        if updatesAllowed { AutoUpdater.shared.start(newInstall: newInstall) }
    }

    /// Projects with changes save themselves 30 seconds after the change, in place, while
    /// the person keeps working: the document is copied on the main thread (a quick copy of
    /// the book's values) and written to disk in the background.
    enum Autosave {
        static let seconds: TimeInterval = 30

        static func start() {
            #if DEBUG
            // A test copy autosaves only when a test asks (its /tmp projects only).
            let test = ProcessInfo.processInfo.environment["INNERVOICE_AUTOSAVE_SECONDS"].flatMap(TimeInterval.init)
            guard !isTestRun || test != nil else { return }
            NSDocumentController.shared.autosavingDelay = test ?? seconds
            #else
            guard !isTestRun else { return }
            NSDocumentController.shared.autosavingDelay = seconds
            #endif
        }
    }

    static func shouldTerminate(_ app: NSApplication) -> NSApplication.TerminateReply {
        QuitConfirmation.shouldTerminate()
    }

    static func willTerminate() {
        Diagnostics.note("quit")
    }

    /// The Dock icon's right-click menu, while "Show in Dock" is on.
    static func dockMenu() -> NSMenu? {
        AppPresence.dockMenu(title: "Settings…", target: SystemMenuTarget.shared, action: #selector(SystemMenuTarget.settings))
    }

    /// Opening InnerVoice again (Dock icon, Applications, Spotlight). With both icons hidden
    /// it opens Settings, the way back, and returns false so no new project is started; otherwise
    /// true, for the usual document behavior.
    static func shouldHandleReopen(hasVisibleWindows: Bool) -> Bool {
        guard !AppPresence.showInDock, !AppPresence.showInMenuBar else { return true }
        openSettings()
        return false
    }

    // MARK: Actions

    /// Opens Settings from anywhere, including AppKit menus with no SwiftUI view at hand.
    static func openSettings() {
        NSApp.activate(ignoringOtherApps: true)
        if let settingsOpener {
            settingsOpener()
            return
        }
        // The app menu's own Settings… item (⌘,), which SwiftUI's Settings scene answers.
        guard let appMenu = NSApp.mainMenu?.items.first?.submenu,
              let index = appMenu.items.firstIndex(where: { $0.keyEquivalent == "," && $0.keyEquivalentModifierMask == .command })
        else {
            Diagnostics.note("openSettings: no Settings… item in the app menu")
            return
        }
        appMenu.performActionForItem(at: index)
    }
}

// MARK: - Menus

/// About InnerVoice (the window with the last 10 releases) in place of the standard panel, then
/// Check for Updates…, which reads "Update to x.y.z…" once a newer release is known.
struct SystemAppCommands: Commands {
    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button("About \(AppInfo.name)") { AppInfo.showAbout() }
            UpdateMenuButton()
        }
        // Settings is an AppKit window (see SettingsWindow), so the app menu item is ours.
        CommandGroup(replacing: .appSettings) {
            Button("Settings…") { SettingsWindow.shared.show() }
                .keyboardShortcut(",", modifiers: .command)
        }
    }
}

private struct UpdateMenuButton: View {
    private var availability = UpdateAvailability.shared

    var body: some View {
        if let pending = availability.pending {
            Button("Update to \(pending)…") { UpdateController.checkForUpdates() }
        } else {
            Button("Check for Updates…") { UpdateController.checkForUpdates() }
        }
    }
}

/// Help › InnerVoice Help (⌘?), in place of the standard item.
struct SystemHelpCommands: Commands {
    var body: some Commands {
        CommandGroup(replacing: .help) {
            Button("\(AppInfo.name) Help") { Help.show() }
                .keyboardShortcut("?", modifiers: .command)
        }
    }
}

// MARK: - Settings

/// The house sections of Settings › General, for the app's own grouped Form: Appearance, asking
/// before quitting, where InnerVoice appears, iCloud sync and updates.
struct SystemGeneralSettings: View {
    @AppStorage(QuitConfirmation.key) private var confirmQuit = true
    @AppStorage(AppPresence.Key.dock) private var showInDock = AppPresence.dockDefault
    @AppStorage(AppPresence.Key.menuBar) private var showInMenuBar = AppPresence.menuBarDefault
    @Bindable private var hub = MenuHub.shared

    var body: some View {
        Section {
            AppearancePicker()
            SystemCaption(AppAppearance.footer(appName: AppInfo.name))
        }
        // After an update the user didn't see the notes for, what changed.
        .onAppear { WhatsNew.shared.show(asked: false) }
        Section {
            Toggle("Ask before quitting", isOn: $confirmQuit)
                .help("Quitting InnerVoice asks first, so a stray ⌘Q doesn't stop Tag Speakers.")
        }
        Section {
            Toggle("Show in Dock", isOn: $showInDock)
                .help("The Dock icon's right-click menu opens Settings.")
            Toggle("Show in menu bar", isOn: $showInMenuBar)
                .help("A menu bar menu with New from Manuscript…, Open…, Open Recent and Settings….")
            Toggle("Put in ToolHUD’s menu while ToolHUD is running", isOn: $hub.foldIntoToolHUD)
                .help("ToolHUD shows this menu as a submenu of its own, and InnerVoice’s icon leaves the menu bar until ToolHUD quits.")
            if !showInDock && !showInMenuBar {
                SystemCaption(AppPresence.hiddenEverywhereNote(appName: AppInfo.name, settingsName: "Settings"))
            }
        }
        .onChange(of: showInDock) { AppPresence.applyDock(keepInFront: NSApp.keyWindow) }
        .onChange(of: showInMenuBar) { NotificationCenter.default.post(name: AppPresence.menuBarDidChange, object: nil) }
        SyncUpdateSections()
    }
}
