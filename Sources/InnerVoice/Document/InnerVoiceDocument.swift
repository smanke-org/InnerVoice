import AppKit
import Combine
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

extension UTType {
    nonisolated static let innerVoiceProject = UTType(exportedAs: "com.smanke.innervoice.project", conformingTo: .package)
}

/// One book on disk: a package with project.json, the original manuscript, and
/// pronunciation samples.
///   Book.innervoice/
///     project.json
///     Original/<manuscript file>
///     Audio/<sample>.m4a
nonisolated final class InnerVoiceDocument: ReferenceFileDocument {
    static var readableContentTypes: [UTType] { [.innerVoiceProject] }

    struct Snapshot {
        var project: Project
        var audio: [String: Data]
        var original: (name: String, data: Data)?
    }

    // Published by hand: @Published can't be used in a nonisolated class here.
    let objectWillChange = ObservableObjectPublisher()

    private(set) var project: Project {
        willSet { objectWillChange.send() }
        didSet { index = BookIndex(project) }
    }
    /// Lookups over the project, rebuilt with every change.
    private(set) var index: BookIndex
    /// Pronunciation samples by file name.
    var audio: [String: Data] = [:] { willSet { objectWillChange.send() } }
    var original: (name: String, data: Data)?
    /// Bumped when the manuscript's text changes, so the reading view rebuilds.
    private(set) var textRevision = 0 { willSet { objectWillChange.send() } }
    /// Bumped when only colors, speakers or kinds change.
    private(set) var tagRevision = 0 { willSet { objectWillChange.send() } }
    /// Per-document AI work (Tag Speakers, Build Summaries).
    @MainActor lazy var tagging = TaggingController(document: self)

    init() {
        project = .empty
        index = BookIndex(.empty)
    }

    init(project: Project, original: (name: String, data: Data)? = nil) {
        self.project = project
        index = BookIndex(project)
        self.original = original
    }

    required init(configuration: ReadConfiguration) throws {
        let wrapper = configuration.file
        guard let data = wrapper.fileWrappers?["project.json"]?.regularFileContents else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let p = try Self.decoder.decode(Project.self, from: data)
        project = p
        index = BookIndex(p)
        for (name, file) in wrapper.fileWrappers?["Audio"]?.fileWrappers ?? [:] {
            if let d = file.regularFileContents { audio[name] = d }
        }
        if let (name, file) = wrapper.fileWrappers?["Original"]?.fileWrappers?.first, let d = file.regularFileContents {
            original = (name, d)
        }
    }

    func snapshot(contentType: UTType) throws -> Snapshot {
        Snapshot(project: project, audio: audio, original: original)
    }

    func fileWrapper(snapshot: Snapshot, configuration: WriteConfiguration) throws -> FileWrapper {
        let root = FileWrapper(directoryWithFileWrappers: [:])
        let json = FileWrapper(regularFileWithContents: try Self.encoder.encode(snapshot.project))
        json.preferredFilename = "project.json"
        root.addFileWrapper(json)
        if !snapshot.audio.isEmpty {
            let folder = FileWrapper(directoryWithFileWrappers: [:])
            folder.preferredFilename = "Audio"
            for (name, data) in snapshot.audio {
                let f = FileWrapper(regularFileWithContents: data)
                f.preferredFilename = name
                folder.addFileWrapper(f)
            }
            root.addFileWrapper(folder)
        }
        if let original = snapshot.original {
            let folder = FileWrapper(directoryWithFileWrappers: [:])
            folder.preferredFilename = "Original"
            let f = FileWrapper(regularFileWithContents: original.data)
            f.preferredFilename = original.name
            folder.addFileWrapper(f)
            root.addFileWrapper(folder)
        }
        return root
    }

    static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.sortedKeys]
        return e
    }()

    static let decoder: JSONDecoder = {
        JSONDecoder()
    }()

    // MARK: Changes

    /// Every change goes through here, so it can be undone and marks the document edited.
    @MainActor
    func change(_ actionName: String, undoManager: UndoManager?, textChanged: Bool = false, _ body: (inout Project) -> Void) {
        let before = project
        var after = project
        body(&after)
        guard after != before else { return }
        replace(with: after, textChanged: textChanged)
        register(actionName, before: before, after: after, undoManager: undoManager, textChanged: textChanged)
    }

    @MainActor
    private func register(_ name: String, before: Project, after: Project, undoManager: UndoManager?, textChanged: Bool) {
        guard let undoManager else { return }
        undoManager.registerUndo(withTarget: self) { doc in
            doc.replace(with: before, textChanged: textChanged)
            doc.register(name, before: after, after: before, undoManager: undoManager, textChanged: textChanged)
        }
        undoManager.setActionName(name)
    }

    @MainActor
    private func replace(with p: Project, textChanged: Bool) {
        project = p
        if textChanged { textRevision += 1 }
        tagRevision += 1
    }

    /// Changes made by background AI work, applied without one undo step per passage.
    /// The tagging controller registers a single undo for the whole run.
    @MainActor
    func applyQuietly(_ body: (inout Project) -> Void) {
        var p = project
        body(&p)
        guard p != project else { return }
        replace(with: p, textChanged: false)
    }

    /// Registers one undo step for a finished AI run, restoring only what it changed.
    @MainActor
    func registerRunUndo(_ name: String, undoManager: UndoManager?, restore: @escaping @Sendable (inout Project) -> Void, redo: @escaping @Sendable (inout Project) -> Void) {
        guard let undoManager else { return }
        undoManager.registerUndo(withTarget: self) { doc in
            doc.applyQuietly(restore)
            doc.registerRunUndo(name, undoManager: undoManager, restore: redo, redo: restore)
        }
        undoManager.setActionName(name)
    }

    @MainActor
    func setAudio(_ data: Data?, name: String) {
        audio[name] = data
    }
}
