import AppKit

/// Every 30 seconds, saves each open project that has a name (the narrator saved it once,
/// so it has a file) and has changes. Untitled projects are left alone: their first save
/// is the narrator's, with a name and a place. Test runs never save.
@MainActor
enum AutoSave {
    /// `APP_AUTOSAVE_SECONDS` (checks only) shortens it and lets a test run autosave.
    static let override = ProcessInfo.processInfo.environment["APP_AUTOSAVE_SECONDS"].flatMap(TimeInterval.init)
    static var interval: TimeInterval { override ?? 30 }
    private static var timer: Timer?

    /// Call after launch finishes: touching NSDocumentController earlier crashes SwiftUI.
    static func start() {
        guard timer == nil, !SystemHooks.isTestRun || override != nil else { return }
        let t = Timer(timeInterval: interval, repeats: true) { _ in
            MainActor.assumeIsolated { saveNamedProjects() }
        }
        t.tolerance = 2
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    static func saveNamedProjects() {
        guard AppState.launched else { return }
        for document in NSDocumentController.shared.documents
        where document.fileURL != nil && document.isDocumentEdited && !document.isInViewingMode {
            document.save(nil)
        }
    }
}
