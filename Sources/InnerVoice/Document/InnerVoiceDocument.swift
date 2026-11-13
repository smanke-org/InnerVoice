import AppKit
import Combine
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

extension UTType {
    nonisolated static let innerVoiceProject = UTType(exportedAs: "com.smanke.innervoice.project", conformingTo: .data)
}

/// One book on disk: a single file (a zip, see `ProjectArchive`) with project.json, the
/// original manuscript and pronunciation samples. Projects saved by 0.1.0 are packages
/// (folders with the same three parts); they still open, and are saved as one file.
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
        didSet { index = BookIndex(project); shadeCache = nil }
    }
    /// Narration Tools: each character's shade (Casting Style), worked out once per change
    /// because the margin asks on every scroll.
    private var shadeCache: [UUID: Shade]?

    @MainActor func narrationShades() -> [UUID: Shade] {
        if let shadeCache { return shadeCache }
        let s = CastingStyleRules.shades(project, index: index, narratorGender: project.settings.narratorGender)
        shadeCache = s
        return s
    }
    /// The project was read with Qwen3-TTS voices and they became Chatterbox voices.
    var migratedVoices = false

    /// Marks the window's document edited once after a voice migration, so the next save
    /// (or autosave) writes it. Its NSDocument is found by file URL, after launch.
    @MainActor func markMigrationEdited(fileURL: URL?) {
        guard migratedVoices, let fileURL, AppState.launched else { return }
        guard let ns = NSDocumentController.shared.documents.first(where: { $0.fileURL?.standardizedFileURL == fileURL.standardizedFileURL }) else { return }
        migratedVoices = false
        ns.updateChangeCount(.changeDone)
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
    /// Bumped when lines are added, removed or moved without the text changing, so the
    /// reading view remaps its text to lines.
    private(set) var lineRevision = 0 { willSet { objectWillChange.send() } }
    /// Per-document AI work (Tag Speakers, Build Summaries).
    @MainActor lazy var tagging = TaggingController(document: self)
    /// Claude's own features (Clean Up Text, Chapter Recaps, Delivery Hints, Suggest Spellings).
    @MainActor lazy var claude = ClaudeFeatures(document: self)

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
        let contents: ProjectArchive.Contents
        if wrapper.isDirectory {
            contents = try Self.readPackage(wrapper)
        } else if let data = wrapper.regularFileContents, ProjectArchive.looksLikeArchive(data) {
            do { contents = try ProjectArchive.read(data) } catch { throw CocoaError(.fileReadCorruptFile) }
        } else {
            throw CocoaError(.fileReadCorruptFile)
        }
        var p = try Self.decoder.decode(Project.self, from: contents.projectJSON)
        // A project from before Chatterbox: its Qwen3-TTS voices become Chatterbox voices as
        // it's read (no undo step: there's nothing to go back to). The window marks the
        // project edited so the change is saved (`markMigrationEdited`).
        migratedVoices = LegacyVoices.migrate(&p)
        project = p
        index = BookIndex(p)
        audio = contents.audio
        original = contents.original.map { ($0.name, $0.data) }
    }

    /// Reads another project's book model from disk without opening it (Use Voices from
    /// Another Project). Single files and 0.1.0 packages both work.
    nonisolated static func readProject(at url: URL) throws -> Project {
        let wrapper = try FileWrapper(url: url, options: .immediate)
        let contents: ProjectArchive.Contents
        if wrapper.isDirectory {
            contents = try readPackage(wrapper)
        } else if let data = wrapper.regularFileContents, ProjectArchive.looksLikeArchive(data) {
            contents = try ProjectArchive.read(data)
        } else {
            throw CocoaError(.fileReadCorruptFile)
        }
        var p = try decoder.decode(Project.self, from: contents.projectJSON)
        LegacyVoices.migrate(&p)
        return p
    }

    /// A project saved as a package (Inner Voice 0.1.0).
    private static func readPackage(_ wrapper: FileWrapper) throws -> ProjectArchive.Contents {
        guard let data = wrapper.fileWrappers?["project.json"]?.regularFileContents else {
            throw CocoaError(.fileReadCorruptFile)
        }
        var contents = ProjectArchive.Contents(projectJSON: data)
        for (name, file) in wrapper.fileWrappers?["Audio"]?.fileWrappers ?? [:] {
            if let d = file.regularFileContents { contents.audio[name] = d }
        }
        if let (name, file) = wrapper.fileWrappers?["Original"]?.fileWrappers?.first, let d = file.regularFileContents {
            contents.original = .init(name: name, data: d)
        }
        return contents
    }

    func snapshot(contentType: UTType) throws -> Snapshot {
        Snapshot(project: project, audio: audio, original: original)
    }

    /// Always one ordinary file, even when the project was opened from a package: cloud
    /// folders (OneDrive) rename and replace packages under an open window.
    func fileWrapper(snapshot: Snapshot, configuration: WriteConfiguration) throws -> FileWrapper {
        try FileWrapper(regularFileWithContents: Self.archive(snapshot))
    }

    static func archive(_ snapshot: Snapshot) throws -> Data {
        try ProjectArchive.write(.init(
            projectJSON: try encoder.encode(snapshot.project),
            original: snapshot.original.map { .init(name: $0.name, data: $0.data) },
            audio: snapshot.audio))
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
        Self.closeUndoGroupSoon()
    }

    /// An undo step recorded outside a click or key press (the agent CLI, the end of an AI
    /// run) sits in an undo group that only closes after the next event, so the window
    /// didn't count as edited and autosave never ran while the person was away. An empty
    /// app event right behind it closes the group at once.
    @MainActor
    static func closeUndoGroupSoon() {
        guard let event = NSEvent.otherEvent(with: .applicationDefined, location: .zero, modifierFlags: [], timestamp: 0,
                                             windowNumber: 0, context: nil, subtype: 0x4956, data1: 0, data2: 0) else { return }
        NSApp.postEvent(event, atStart: false)
    }

    @MainActor
    private func replace(with p: Project, textChanged: Bool) {
        let linesMoved = !Self.sameLinePlaces(project.lines, p.lines)
        project = p
        if linesMoved { lineRevision += 1 }
        if textChanged { textRevision += 1 }
        tagRevision += 1
    }

    static func sameLinePlaces(_ a: [Line], _ b: [Line]) -> Bool {
        a.count == b.count && zip(a, b).allSatisfy { $0.id == $1.id && $0.range == $1.range && $0.paragraphID == $1.paragraphID }
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
        Self.closeUndoGroupSoon()
    }

    @MainActor
    func setAudio(_ data: Data?, name: String) {
        audio[name] = data
    }
}
