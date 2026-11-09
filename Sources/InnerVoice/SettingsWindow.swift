import AppKit
import SwiftUI

/// Settings in an AppKit window with the standard toolbar tabs, so every pane opens at its
/// own height. SwiftUI's Settings scene gave each pane a fixed ideal height, so a pane that
/// would have fit the screen still opened short and scrolled.
@MainActor
final class SettingsWindow: NSObject, NSWindowDelegate {
    static let shared = SettingsWindow()

    private(set) var window: NSWindow?

    func show(tab: SettingsTab? = nil) {
        if window == nil {
            let tabs = SettingsTabs()
            let window = NSWindow(contentViewController: tabs)
            // Resizable as well as scrolling: drag it bigger to see more of a pane at once.
            window.styleMask = [.titled, .closable, .resizable]
            window.contentMinSize = SettingsTabs.minimumSize
            window.toolbarStyle = .preference
            window.isReleasedWhenClosed = false
            window.delegate = self
            window.center()
            self.window = window
        }
        if let tab, let tabs = window?.contentViewController as? SettingsTabs { tabs.select(tab) }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    /// A size the user dragged to is kept for every pane, and next time.
    func windowDidEndLiveResize(_ notification: Notification) {
        guard let window else { return }
        SettingsTabs.chosenSize = window.contentRect(forFrameRect: window.frame).size
    }

    /// Moved to another screen: the pane fits that one.
    func windowDidChangeScreen(_ notification: Notification) {
        (window?.contentViewController as? SettingsTabs)?.refit()
    }
}

/// One toolbar item per pane; the window fits each pane and takes its name as the title,
/// the way macOS's own Settings windows behave.
@MainActor
final class SettingsTabs: NSTabViewController {
    /// The width panes are laid out for, and the narrowest the window can be dragged to.
    static let width: CGFloat = 620
    static let minimumSize = NSSize(width: width, height: 280)

    /// The content size the user last dragged the window to (per Mac, not synced), or nil
    /// while they haven't: then each pane opens at its own height.
    static var chosenSize: NSSize? {
        get { UserDefaults.standard.string(forKey: "settings.contentSize").map(NSSizeFromString) }
        set { UserDefaults.standard.set(newValue.map(NSStringFromSize), forKey: "settings.contentSize") }
    }

    /// Measures a pane on a throwaway hosting view, so the window never follows SwiftUI.
    private var naturalHeight: [NSTabViewItem: () -> CGFloat] = [:]
    private var tabsByItem: [NSTabViewItem: SettingsTab] = [:]

    init() {
        super.init(nibName: nil, bundle: nil)
        // A display added, removed or resized.
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil,
                                               queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.refit() } }
        tabStyle = .toolbar
        transitionOptions = [.crossfade, .allowUserInteraction]
        for tab in SettingsTab.allCases { add(tab) }
        if let saved = SettingsTab(rawValue: UserDefaults.standard.string(forKey: "settings.tab") ?? "") { select(saved) }
    }

    required init?(coder: NSCoder) { fatalError() }

    private func add(_ tab: SettingsTab) {
        // What the window shows (scrolls when the window is smaller) and what is measured
        // (the same content at full height).
        let shown = AnyView(SettingsPane(tab: tab, scrolls: true))
        let measured = AnyView(SettingsPane(tab: tab, scrolls: false).frame(width: Self.width))
        // Letting SwiftUI size the window to the content crashed other apps once a pane grew
        // taller than the screen (AppKit clamped, SwiftUI asked again, AppKit aborted the
        // update-constraints loop). The pane fills whatever size the window is.
        let hosting = NSHostingController(rootView: shown.frame(minWidth: Self.minimumSize.width, maxWidth: .infinity, maxHeight: .infinity))
        hosting.sizingOptions = []
        hosting.title = tab.title
        let item = NSTabViewItem(viewController: hosting)
        item.label = tab.title
        item.image = NSImage(systemSymbolName: tab.symbol, accessibilityDescription: tab.title)
        addTabViewItem(item)
        tabsByItem[item] = tab
        naturalHeight[item] = { NSHostingView(rootView: measured).fittingSize.height }
        fit(item)
    }

    /// Sizes a pane before it's shown (measured each time, as panes change): its own height,
    /// no taller than the screen allows. Once the user has dragged the window to a size,
    /// every pane opens at that size instead, and scrolls when it holds more.
    @discardableResult
    private func fit(_ item: NSTabViewItem) -> NSSize? {
        guard let height = naturalHeight[item]?() else { return nil }
        let screen = (isViewLoaded ? view.window?.screen : nil) ?? NSScreen.main
        let available = (screen?.visibleFrame.height ?? 800) - 120
        var size = NSSize(width: Self.width, height: max(Self.minimumSize.height, min(height, available)))
        if let chosen = Self.chosenSize {
            size = NSSize(width: max(chosen.width, Self.minimumSize.width),
                          height: min(max(chosen.height, Self.minimumSize.height), available))
        }
        item.viewController?.preferredContentSize = size
        return size
    }

    /// The shown pane fitted again, keeping the window's top where it is and all of it on screen.
    func refit() {
        guard isViewLoaded, let window = view.window, selectedTabViewItemIndex >= 0,
              let size = fit(tabViewItems[selectedTabViewItemIndex]) else { return }
        var frame = window.frameRect(forContentRect: NSRect(origin: .zero, size: size))
        frame.origin = NSPoint(x: window.frame.minX, y: window.frame.maxY - frame.height)
        if let visible = window.screen?.visibleFrame {
            frame.origin.y = max(min(frame.origin.y, visible.maxY - frame.height), visible.minY)
            frame.origin.x = max(min(frame.origin.x, visible.maxX - frame.width), visible.minX)
        }
        window.setFrame(frame, display: true, animate: false)
    }

    override func tabView(_ tabView: NSTabView, willSelect tabViewItem: NSTabViewItem?) {
        if let tabViewItem { fit(tabViewItem) }
        super.tabView(tabView, willSelect: tabViewItem)
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        selectionChanged()
    }

    override func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) {
        super.tabView(tabView, didSelect: tabViewItem)
        selectionChanged()
    }

    func select(_ tab: SettingsTab) {
        if let index = tabViewItems.firstIndex(where: { tabsByItem[$0] == tab }) { selectedTabViewItemIndex = index }
    }

    var selectedTab: SettingsTab? {
        selectedTabViewItemIndex >= 0 ? tabsByItem[tabViewItems[selectedTabViewItemIndex]] : nil
    }

    private func selectionChanged() {
        guard selectedTabViewItemIndex >= 0 else { return }
        let item = tabViewItems[selectedTabViewItemIndex]
        view.window?.title = item.label
        if let tab = tabsByItem[item] { UserDefaults.standard.set(tab.rawValue, forKey: "settings.tab") }
    }
}

/// One Settings pane: a grouped Form (or the model list), with the Help button in the corner.
struct SettingsPane: View {
    let tab: SettingsTab
    /// False when measuring: the pane at its full height, with nothing scrolling.
    let scrolls: Bool

    var body: some View {
        VStack(spacing: 0) {
            content
            HStack {
                Spacer()
                HelpButton(topic: .settings).fixedSize()
            }
            .padding(.horizontal, Space.xl)
            .padding(.bottom, Space.md)
        }
        .themed()
        .noInitialFocus()
    }

    @ViewBuilder
    private var content: some View {
        switch tab {
        case .ai:
            if scrolls {
                ScrollView { ModelPicker().padding(Space.xl) }
            } else {
                ModelPicker().padding(Space.xl)
            }
        default:
            Form {
                switch tab {
                case .general: GeneralSettings()
                case .manuscript: ManuscriptSettings()
                case .voices: VoicesSettings()
                case .recording: RecordingSettings()
                case .appearance: AppearanceSettings()
                case .activity: ActivitySettings()
                case .ai: EmptyView()
                }
            }
            .formStyle(.grouped)
            .scrollDisabled(!scrolls)
        }
    }
}
