import AppKit
import ObjectiveC

/// When a book's file changes on disk and its window has no unsaved changes, AppKit reloads
/// it ("reverts"). OneDrive changes a book shortly after every save, and a reload that lands
/// while it is still writing reads half a file: "could not be reverted. The file isn't in
/// the correct format." (2026-11-21). A failed reload now tries again a few times, and if the
/// file still can't be read, the open book stays as it is (its next save writes it back)
/// instead of an alert.
@MainActor
enum RevertRetry {
    static let attempts = 5
    static let pause: TimeInterval = 0.4
    /// Reloads that never read the file and kept the open book (for checks).
    static var kept = 0

    private static var installed = false

    static func install() {
        guard !installed else { return }
        installed = true
        let original = #selector(NSDocument.revert(toContentsOf:ofType:))
        let replacement = #selector(NSDocument.innerVoiceRevert(toContentsOf:ofType:))
        guard let a = class_getInstanceMethod(NSDocument.self, original),
              let b = class_getInstanceMethod(NSDocument.self, replacement) else { return }
        method_exchangeImplementations(a, b)
    }

    /// Errors that mean the file was mid-write or briefly missing, not a reload to give up on.
    nonisolated static func isTransient(_ error: Error) -> Bool {
        var e: NSError? = error as NSError
        while let current = e {
            if current.domain == NSCocoaErrorDomain,
               [NSFileReadUnknownError, NSFileReadCorruptFileError, NSFileReadNoSuchFileError].contains(current.code) {
                return true
            }
            e = current.userInfo[NSUnderlyingErrorKey] as? NSError
        }
        return false
    }
}

extension NSDocument {
    /// Swapped with `revert(toContentsOf:ofType:)`: calling it calls AppKit's own.
    @objc func innerVoiceRevert(toContentsOf url: URL, ofType typeName: String) throws {
        for attempt in 1...RevertRetry.attempts {
            do {
                try innerVoiceRevert(toContentsOf: url, ofType: typeName)
                return
            } catch where RevertRetry.isTransient(error) {
                NSLog("InnerVoice: reload of %@ failed (try %d): %@", url.lastPathComponent, attempt, error.localizedDescription)
                if attempt < RevertRetry.attempts { Thread.sleep(forTimeInterval: RevertRetry.pause) }
            }
        }
        // Still unreadable: keep the book that's open. Record the file's date so the next
        // save doesn't take it for another app's change.
        if let date = try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate {
            fileModificationDate = date
        }
        MainActor.assumeIsolated { RevertRetry.kept += 1 }
        NSLog("InnerVoice: kept the open copy of %@; its file could not be read", url.lastPathComponent)
    }
}
