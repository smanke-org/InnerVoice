import AppKit
import InnerVoiceKit
import Observation
import UniformTypeIdentifiers

/// This Mac's pronunciations: one list every book uses unless it turned it off
/// (`PronunciationLists` in the Kit has the rules). Kept in the app's preferences as JSON, so
/// it follows the narrator's other Macs with their settings (settings sync). Test runs keep
/// it in memory and never save it.
@Observable
@MainActor
final class ComputerPronunciations {
    static let shared = ComputerPronunciations()
    static let key = "pronunciations.computer"

    private(set) var list: PronunciationList

    private init() {
        list = SystemHooks.isTestRun ? PronunciationList() : Self.load()
        if !SystemHooks.isTestRun, UserDefaults.standard.string(forKey: Self.key) == nil { Self.write(list) }
        PronunciationLists.computer = list
    }

    private static func load() -> PronunciationList {
        guard let s = UserDefaults.standard.string(forKey: key),
              let l = try? JSONDecoder().decode(PronunciationList.self, from: Data(s.utf8)) else { return PronunciationList() }
        return l
    }

    private static func write(_ list: PronunciationList) {
        guard !SystemHooks.isTestRun, let d = try? JSONEncoder().encode(list) else { return }
        UserDefaults.standard.set(String(decoding: d, as: UTF8.self), forKey: key)
    }

    /// The list follows the narrator's other Macs (an empty one is the default when there's
    /// nothing to adopt).
    static func registerSync() {
        SettingsSync.register([key: ""])
        SystemHooks.onSettingsSynced = { Task { @MainActor in ComputerPronunciations.shared.reload() } }
    }

    /// Another Mac's list arrived through settings sync.
    func reload() {
        guard !SystemHooks.isTestRun else { return }
        let l = Self.load()
        guard l != list else { return }
        list = l
        PronunciationLists.computer = l
    }

    /// Every change goes through here: saved, and speech uses it from the next line on.
    func change(_ body: (inout PronunciationList) -> Void) {
        var l = list
        body(&l)
        guard l != list else { return }
        list = l
        PronunciationLists.computer = l
        Self.write(l)
    }

    func update(_ id: UUID, _ body: (inout ListedPronunciation) -> Void) {
        change { l in if let i = l.entries.firstIndex(where: { $0.id == id }) { body(&l.entries[i]) } }
    }

    func remove(_ ids: Set<UUID>) { change { $0.entries.removeAll { ids.contains($0.id) } } }

    func remove(term: String) {
        let k = ListedPronunciation.key(term)
        change { $0.entries.removeAll { $0.key == k } }
    }

    @discardableResult
    func add(term: String = "New Word", sayItAs: String = "") -> UUID {
        let e = ListedPronunciation(term: term, sayItAs: sayItAs)
        change { $0.entries.append(e) }
        return e.id
    }

    /// Adds entries, asking first when some words are already here with a different
    /// pronunciation. `incoming` names where they come from ("This Book’s", "the Imported
    /// Ones"). Returns what happened, or nil when the narrator canceled.
    @discardableResult
    func addAsking(_ entries: [ListedPronunciation], incoming: String, window: NSWindow? = nil) -> PronunciationLists.MergeResult? {
        let conflicts = PronunciationLists.conflicts(entries, in: list)
        var policy = PronunciationConflict.keepExisting
        if !conflicts.isEmpty {
            let alert = NSAlert()
            alert.messageText = conflicts.count == 1
                ? "“\(conflicts[0].term)” is already in This Mac’s list with a different pronunciation."
                : "\(conflicts.count) words are already in This Mac’s list with a different pronunciation."
            alert.informativeText = conflicts.prefix(6).map { c in
                "\(c.term): \(list.entry(c.term)?.sayItAs ?? "") → \(c.sayItAs)"
            }.joined(separator: "\n") + (conflicts.count > 6 ? "\n…" : "")
            alert.addButton(withTitle: "Keep This Mac’s")
            alert.addButton(withTitle: "Use \(incoming)")
            alert.addButton(withTitle: "Cancel")
            switch alert.runModal() {
            case .alertFirstButtonReturn: policy = .keepExisting
            case .alertSecondButtonReturn: policy = .replace
            default: return nil
            }
        }
        var result = PronunciationLists.MergeResult()
        change { result = PronunciationLists.merge(entries, into: &$0, conflict: policy) }
        return result
    }

    static func describe(_ r: PronunciationLists.MergeResult) -> String {
        var parts: [String] = []
        if r.added > 0 { parts.append("added \(r.added)") }
        if r.replaced > 0 { parts.append("replaced \(r.replaced)") }
        if r.kept > 0 { parts.append("kept \(r.kept) of This Mac’s") }
        if r.unchanged > 0 { parts.append("\(r.unchanged) already there") }
        guard !parts.isEmpty else { return "Nothing to add." }
        let s = parts.joined(separator: ", ")
        return s.prefix(1).uppercased() + s.dropFirst() + "."
    }

    /// Adds every entry the book has to This Mac's list (Book › Add This Book’s Pronunciations
    /// to This Mac…).
    static func addBook(_ document: InnerVoiceDocument) {
        let entries = PronunciationLists.bookEntries(document.project)
        let alert = NSAlert()
        guard !entries.isEmpty else {
            alert.messageText = "This book has no pronunciations to add."
            alert.informativeText = "Only entries with a Say It As are added."
            alert.runModal()
            return
        }
        guard let r = shared.addAsking(entries, incoming: "This Book’s") else { return }
        alert.messageText = "This book’s pronunciations are in This Mac’s list."
        alert.informativeText = describe(r) + " Every book on this Mac now uses them, unless it has its own entry for the word."
        alert.runModal()
    }

    // MARK: Import and export

    static let importTypes: [UTType] = [.commaSeparatedText, .tabSeparatedText, .plainText, .json, .utf8PlainText]

    func importFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = Self.importTypes
        panel.message = "Choose a pronunciations file: CSV, text (word = how to say it) or InnerVoice’s JSON."
        panel.prompt = "Import"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        importFile(url)
    }

    func importFile(_ url: URL) {
        let alert = NSAlert()
        do {
            let entries = try PronunciationFile.read(try Data(contentsOf: url), fileName: url.lastPathComponent)
            guard let r = addAsking(entries, incoming: "the Imported Ones") else { return }
            alert.messageText = "Imported \(url.lastPathComponent)"
            alert.informativeText = Self.describe(r)
        } catch {
            alert.alertStyle = .warning
            alert.messageText = "Couldn’t import \(url.lastPathComponent)"
            alert.informativeText = error.localizedDescription
        }
        alert.runModal()
    }

    func exportFile() {
        let panel = NSSavePanel()
        let format = NSPopUpButton(frame: .zero, pullsDown: false)
        format.addItems(withTitles: ["CSV (Numbers, Excel)", "JSON (InnerVoice)"])
        let label = NSTextField(labelWithString: "Format:")
        let box = NSStackView(views: [label, format])
        box.edgeInsets = NSEdgeInsets(top: 8, left: 8, bottom: 8, right: 8)
        panel.accessoryView = box
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.nameFieldStringValue = "Pronunciations.csv"
        let watcher = FormatWatcher(panel: panel)
        format.target = watcher
        format.action = #selector(FormatWatcher.changed(_:))
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let kind: PronunciationFile.Format = format.indexOfSelectedItem == 1 ? .json : .csv
        do {
            try PronunciationFile.export(list.entries, as: kind).write(to: url, options: .atomic)
        } catch {
            let alert = NSAlert(error: error)
            alert.runModal()
        }
        _ = watcher
    }

    /// Keeps the save panel's file type in step with the Format menu.
    private final class FormatWatcher: NSObject {
        weak var panel: NSSavePanel?
        init(panel: NSSavePanel) { self.panel = panel }
        @MainActor @objc func changed(_ sender: NSPopUpButton) {
            guard let panel else { return }
            let json = sender.indexOfSelectedItem == 1
            panel.allowedContentTypes = [json ? .json : .commaSeparatedText]
            let base = (panel.nameFieldStringValue as NSString).deletingPathExtension
            panel.nameFieldStringValue = base + (json ? ".json" : ".csv")
        }
    }
}
