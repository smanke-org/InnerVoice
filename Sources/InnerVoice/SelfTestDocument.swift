import AppKit
import InnerVoiceKit

/// Checks for a book's file being changed under an open window (OneDrive does this after
/// every save): the 30-second autosave never blocks the main thread, and a reload that reads
/// a half-written file retries, then keeps the open book instead of an alert.
extension SelfTest {
    static func documentFileChecks() async {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory()).appending(path: "innervoice-selftest-files-\(getpid())")
        try? FileManager.default.removeItem(at: dir)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appending(path: "Reload Check.innervoice")
        func archive(_ title: String) -> Data {
            var p = SeedData.project()
            p.title = title
            return (try? ProjectArchive.write(.init(projectJSON: InnerVoiceDocument.encoder.encode(p)))) ?? Data()
        }
        let a = archive("Reload A"), b = archive("Reload B")
        try? a.write(to: url)
        guard let (doc, _) = try? await NSDocumentController.shared.openDocument(withContentsOf: url, display: true) else {
            check("a project file opens for the reload checks", false); return
        }
        check("a project file opens for the reload checks", true)
        func openTitle() async -> String? {
            try? await Task.sleep(for: .milliseconds(400))
            return OpenProjects.all.first { $0.fileURL?.standardizedFileURL == url.standardizedFileURL }?.document?.project.title
        }
        check("the open project reads its file", await openTitle() == "Reload A", await openTitle() ?? "none")
        let type = doc.fileType ?? "com.smanke.innervoice.project"

        // Half a file now, the whole one a moment later (OneDrive still writing).
        try? a.prefix(200).write(to: url)
        let full = b
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.7) { try? full.write(to: url) }
        let keptBefore = RevertRetry.kept
        var threw: Error?
        do { try doc.revert(toContentsOf: url, ofType: type) } catch { threw = error }
        check("a reload of a half-written file retries until it reads", threw == nil && RevertRetry.kept == keptBefore,
              threw.map { "\($0)" } ?? "kept the old copy")
        check("the retried reload has the new book", await openTitle() == "Reload B", await openTitle() ?? "none")

        // A file that never becomes readable: no error, and the open book stays.
        try? Data("not a project".utf8).write(to: url)
        threw = nil
        let started = Date()
        do { try doc.revert(toContentsOf: url, ofType: type) } catch { threw = error }
        check("a reload of an unreadable file keeps the open book without an alert", threw == nil && RevertRetry.kept == keptBefore + 1,
              threw.map { "\($0)" } ?? "")
        check("giving up on a reload takes about two seconds at most", Date().timeIntervalSince(started) < 3.5)
        check("the open book is still there after a failed reload", await openTitle() == "Reload B", await openTitle() ?? "none")
        // The book is whole again; the window takes this file as its own, as the reload would.
        try? b.write(to: url)
        if let date = try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate { doc.fileModificationDate = date }

        // The autosave while another file activity waits for the main thread: the old
        // synchronous save waited forever here (the 1.1.4 hang).
        let finished = Flag()
        DispatchQueue.global().asyncAfter(deadline: .now() + 20) {
            if !finished.value { print("FAIL autosave returns while another file activity needs the main thread: hung"); exit(1) }
        }
        doc.performActivity(withSynchronousWaiting: false) { activityDone in
            // Finishes on the main thread, like AppKit's own handling of a changed file.
            let box = Done(activityDone)
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.3) { DispatchQueue.main.async { box.call() } }
        }
        doc.updateChangeCount(.changeDone)
        let before = Date()
        AutoSave.saveNamedProjects()
        finished.value = true
        check("autosave returns while another file activity needs the main thread", Date().timeIntervalSince(before) < 0.5,
              String(format: "%.2fs", Date().timeIntervalSince(before)))
        let until = Date().addingTimeInterval(10)
        while doc.isDocumentEdited, Date() < until { try? await Task.sleep(for: .milliseconds(100)) }
        check("the autosave still saves once that activity is done", !doc.isDocumentEdited,
              "in place \(Swift.type(of: doc).autosavesInPlace), saving \(AutoSave.saving.count), error \(AutoSave.lastError.map { "\($0)" } ?? "none")")
        check("the autosaved file is a project", (try? ProjectArchive.read(Data(contentsOf: url))) != nil)

        doc.updateChangeCount(.changeCleared)
        doc.close()
        try? FileManager.default.removeItem(at: dir)
    }

    final class Flag: @unchecked Sendable { var value = false }
    final class Done: @unchecked Sendable {
        let call: () -> Void
        init(_ call: @escaping () -> Void) { self.call = call }
    }
}
