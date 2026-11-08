import AppKit
import InnerVoiceKit

/// Every open project window, for code with no view at hand (the agent CLI). Each
/// ProjectWindow registers its document, its Workspace and its window's undo manager, so a
/// change made from here is one step in that window's Edit › Undo.
@MainActor
enum OpenProjects {
    final class Entry {
        /// Stable for the window's life: the agent CLI shows its first 8 characters as the id.
        let id = UUID()
        weak var document: InnerVoiceDocument?
        let workspace: Workspace
        weak var undoManager: UndoManager?
        var fileURL: URL?
        /// Last time it registered or was made the front project; newest wins as the default.
        var touched = Date()

        init(document: InnerVoiceDocument, workspace: Workspace) {
            self.document = document
            self.workspace = workspace
        }

        /// What people call it: the file's name, or the book's title before it's saved.
        var name: String {
            if let fileURL { return fileURL.deletingPathExtension().lastPathComponent }
            return document?.project.title ?? "Untitled"
        }

        /// The AppKit document behind the window, when it has been saved (edited state).
        var nsDocument: NSDocument? {
            guard let fileURL else { return nil }
            return NSDocumentController.shared.documents.first { $0.fileURL?.standardizedFileURL == fileURL.standardizedFileURL }
        }
    }

    private static var entries: [Entry] = []

    /// Called from ProjectWindow whenever its file URL or undo manager changes.
    static func register(_ document: InnerVoiceDocument, workspace: Workspace, undoManager: UndoManager?, fileURL: URL?) {
        let entry = entries.first { $0.workspace === workspace } ?? {
            let new = Entry(document: document, workspace: workspace)
            entries.append(new)
            return new
        }()
        entry.document = document
        if let undoManager { entry.undoManager = undoManager }
        entry.fileURL = fileURL
        entry.touched = .now
    }

    static func unregister(_ workspace: Workspace) {
        entries.removeAll { $0.workspace === workspace }
    }

    /// Open projects, oldest window first.
    static var all: [Entry] {
        entries.removeAll { $0.document == nil }
        return entries
    }

    /// The project in front: the current AppKit document's window, else the newest one.
    static var front: Entry? {
        let live = all
        if AppState.launched, let url = NSDocumentController.shared.currentDocument?.fileURL?.standardizedFileURL,
           let match = live.first(where: { $0.fileURL?.standardizedFileURL == url }) {
            return match
        }
        return live.max { $0.touched < $1.touched }
    }
}
