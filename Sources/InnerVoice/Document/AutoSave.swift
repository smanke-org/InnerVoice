import AppKit

/// Every 30 seconds, saves each open project that has a name (the narrator saved it once,
/// so it has a file) and has changes. Untitled projects are left alone: their first save
/// is the narrator's, with a name and a place. Test runs never save.
///
/// The save never waits on the main thread. A synchronous save (`save(nil)`) waits for the
/// document's current file activity, and when that activity was AppKit handling a change
/// to the file on disk (OneDrive touches a book shortly after every save), it needs the main
/// thread to finish: the two waited on each other and the app hung (1.1.4, 2026-11-21).
@MainActor
enum AutoSave {
    /// `APP_AUTOSAVE_SECONDS` (checks only) shortens it and lets a test run autosave.
    static let override = ProcessInfo.processInfo.environment["APP_AUTOSAVE_SECONDS"].flatMap(TimeInterval.init)
    static var interval: TimeInterval { override ?? 30 }
    private static var timer: Timer?
    /// Documents with a save in flight, so a slow save isn't started again on top of itself.
    private(set) static var saving = Set<ObjectIdentifier>()
    /// The last autosave's error, if it failed (for checks).
    private(set) static var lastError: Error?

    /// Call after launch finishes: touching NSDocumentController earlier crashes SwiftUI.
    static func start() {
        guard timer == nil, !SystemHooks.isTestRun || override != nil else { return }
        let t = Timer(timeInterval: interval, repeats: true) { _ in
            MainActor.assumeIsolated { saveNamedProjects() }
        }
        t.tolerance = 2
        // Default mode only: never while an alert, a sheet's modal loop or a menu is up.
        RunLoop.main.add(t, forMode: .default)
        timer = t
    }

    static func saveNamedProjects() {
        guard AppState.launched, NSApp.modalWindow == nil else { return }
        for document in NSDocumentController.shared.documents
        where document.fileURL != nil && document.isDocumentEdited && !document.isInViewingMode {
            // A sheet on the window (an error, Save As, Export) is the person's to answer first.
            if document.windowForSheet?.attachedSheet != nil { continue }
            let id = ObjectIdentifier(document)
            guard !saving.contains(id) else { continue }
            saving.insert(id)
            let done: @Sendable (Error?) -> Void = { error in
                let box = ErrorBox(error)
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        lastError = box.error
                        saving.remove(id)
                        if let error = box.error { show(error, for: document) }
                    }
                }
            }
            if type(of: document).autosavesInPlace {
                // Writes the book's own file, queued behind any file activity, without waiting.
                document.autosave(withImplicitCancellability: false, completionHandler: done)
            } else if let url = document.fileURL, let type = document.fileType {
                document.save(to: url, ofType: type, for: .saveOperation, completionHandler: done)
            } else {
                saving.remove(id)
            }
        }
    }
}

extension AutoSave {
    /// A save that failed for a reason the person must decide (the file changed under the
    /// window, a full disk) shows AppKit's own sheet with its choices, once: the timer skips
    /// windows with a sheet. A cancelled save says nothing.
    static func show(_ error: Error, for document: NSDocument) {
        let e = error as NSError
        if e.domain == NSCocoaErrorDomain && e.code == NSUserCancelledError { return }
        guard let window = document.windowForSheet, window.attachedSheet == nil, NSApp.modalWindow == nil else { return }
        document.presentError(error, modalFor: window, delegate: nil, didPresent: nil, contextInfo: nil)
    }
}

private nonisolated final class ErrorBox: @unchecked Sendable {
    let error: Error?
    init(_ error: Error?) { self.error = error }
}
