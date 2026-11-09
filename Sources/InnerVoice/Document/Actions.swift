import AppKit
import InnerVoiceKit
import SwiftUI

/// The narrator's edits. Each is one undo step with a name for the Edit menu.
@MainActor extension InnerVoiceDocument {
    func assign(_ lineIDs: [UUID], to speaker: UUID?, undoManager: UndoManager?) {
        let name = lineIDs.count == 1 ? "Assign Line" : "Assign \(lineIDs.count) Lines"
        change(name, undoManager: undoManager) { p in
            let set = Set(lineIDs)
            for i in p.lines.indices where set.contains(p.lines[i].id) {
                p.lines[i].speakerID = speaker
                p.lines[i].source = .user
                p.lines[i].confidence = 1
            }
        }
    }

    func applyExchange(_ proposals: [Exchange.Proposal], undoManager: UndoManager?) {
        change("Swap Exchange", undoManager: undoManager) { p in
            let map = Dictionary(proposals.map { ($0.lineID, $0.speakerID) }, uniquingKeysWith: { a, _ in a })
            for i in p.lines.indices {
                if let s = map[p.lines[i].id] {
                    p.lines[i].speakerID = s
                    p.lines[i].source = .user
                    p.lines[i].confidence = 1
                }
            }
        }
    }

    func setKind(_ kind: LineKind, for lineIDs: [UUID], undoManager: UndoManager?) {
        change("Change Line Kind", undoManager: undoManager) { p in
            let set = Set(lineIDs)
            for i in p.lines.indices where set.contains(p.lines[i].id) { p.lines[i].kind = kind }
        }
    }

    /// The text is narration after all.
    func removeLines(_ lineIDs: [UUID], undoManager: UndoManager?) {
        change(lineIDs.count == 1 ? "Mark as Narration" : "Mark \(lineIDs.count) Lines as Narration", undoManager: undoManager) { p in
            let set = Set(lineIDs)
            p.lines.removeAll { set.contains($0.id) }
        }
    }

    /// Deletes text the narrator doesn't read (an ISBN, an ad for another book) from the
    /// project's copy of the book. One undo step; the imported file is never changed.
    func deleteText(cuts: [UUID: [TextSpan]], chapters: Set<UUID> = [], undoManager: UndoManager?) {
        guard !cuts.isEmpty || !chapters.isEmpty else { return }
        let name = chapters.count == 1 && cuts.isEmpty ? "Delete Chapter" : (chapters.count > 1 && cuts.isEmpty ? "Delete Chapters" : "Delete Text")
        change(name, undoManager: undoManager, textChanged: true) { $0.deleteText(cuts: cuts, chapters: chapters) }
    }

    /// Pinned characters head every speaker list.
    func setPinned(_ id: UUID, _ on: Bool, undoManager: UndoManager?) {
        updateCharacter(id, on ? "Pin Speaker" : "Unpin Speaker", undoManager: undoManager) { $0.pinned = on ? true : nil }
    }

    /// Makes a line from text the finder missed.
    @discardableResult
    func makeLine(paragraphID: UUID, range: TextSpan, speaker: UUID?, undoManager: UndoManager?) -> UUID {
        let line = Line(paragraphID: paragraphID, range: range, kind: .spoken, speakerID: speaker,
                        confidence: speaker == nil ? 0 : 1, source: speaker == nil ? .none : .user)
        change("Make Line", undoManager: undoManager) { p in
            let index = BookIndex(p)
            guard let pos = index.positions[paragraphID] else { return }
            // Drop lines it overlaps, then keep book order: before the first line that
            // starts after it. (Finding the place first put it late when an overlapped
            // line came before it.)
            p.lines.removeAll { $0.paragraphID == paragraphID && NSIntersectionRange($0.range.ns, range.ns).length > 0 }
            let at = p.lines.firstIndex { l in
                guard let lp = index.positions[l.paragraphID] else { return false }
                return lp > pos || (lp == pos && l.range.location > range.location)
            } ?? p.lines.count
            p.lines.insert(line, at: at)
        }
        return line.id
    }

    @discardableResult
    func addCharacter(named name: String, undoManager: UndoManager?) -> UUID {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if let existing = project.characters.first(where: { $0.name.caseInsensitiveCompare(trimmed) == .orderedSame }) {
            return existing.id
        }
        let c = BookCharacter(name: trimmed, colorHex: CharacterPalette.next(used: project.characters.map(\.colorHex)))
        change("New Character", undoManager: undoManager) { $0.characters.append(c) }
        return c.id
    }

    func updateCharacter(_ id: UUID, _ name: String = "Edit Character", undoManager: UndoManager?, _ body: @escaping (inout BookCharacter) -> Void) {
        change(name, undoManager: undoManager) { p in
            if let i = p.characterIndex(id) { body(&p.characters[i]) }
        }
    }

    /// Moves every line and fact of `from` to `into`; `from`'s names become aliases.
    func merge(_ from: UUID, into: UUID, undoManager: UndoManager?) {
        guard from != into else { return }
        change("Merge Characters", undoManager: undoManager) { p in
            guard let fi = p.characterIndex(from), let ti = p.characterIndex(into) else { return }
            let source = p.characters[fi]
            var target = p.characters[ti]
            for alias in [source.name] + source.aliases where alias != target.name && !target.aliases.contains(alias) {
                target.aliases.append(alias)
            }
            for f in source.facts where !target.facts.contains(where: { $0.kind == f.kind && TextMatch.similarity($0.text, f.text) >= 0.6 }) {
                target.facts.append(f)
            }
            if target.notes.isEmpty { target.notes = source.notes }
            if target.voiceNotes.isEmpty { target.voiceNotes = source.voiceNotes }
            p.characters[ti] = target
            for i in p.lines.indices where p.lines[i].speakerID == from { p.lines[i].speakerID = into }
            if p.settings.povCharacterID == from { p.settings.povCharacterID = into }
            p.characters.remove(at: fi)
        }
    }

    /// Deletes a character; their lines become Who's Speaking?
    func deleteCharacter(_ id: UUID, undoManager: UndoManager?) {
        change("Delete Character", undoManager: undoManager) { p in
            for i in p.lines.indices where p.lines[i].speakerID == id {
                p.lines[i].speakerID = nil
                p.lines[i].source = .none
                p.lines[i].confidence = 0
            }
            if p.settings.povCharacterID == id { p.settings.povCharacterID = nil }
            p.characters.removeAll { $0.id == id }
        }
    }

    func updateSettings(_ name: String, undoManager: UndoManager?, _ body: @escaping (inout ProjectSettings) -> Void) {
        change(name, undoManager: undoManager) { body(&$0.settings) }
    }

    func toggleRecorded(_ paragraphID: UUID, undoManager: UndoManager?) {
        change(project.recorded.contains(paragraphID) ? "Mark Not Recorded" : "Mark Recorded", undoManager: undoManager) { p in
            if p.recorded.contains(paragraphID) { p.recorded.remove(paragraphID) } else { p.recorded.insert(paragraphID) }
        }
    }

    func setRecorded(_ paragraphID: UUID, _ on: Bool, undoManager: UndoManager?) {
        guard project.recorded.contains(paragraphID) != on else { return }
        toggleRecorded(paragraphID, undoManager: undoManager)
    }

    func addRetake(_ paragraphID: UUID, note: String, undoManager: UndoManager?) {
        change("Mark Retake", undoManager: undoManager) { p in
            if let i = p.retakes.firstIndex(where: { $0.paragraphID == paragraphID && !$0.done }) {
                p.retakes[i].note = note
            } else {
                p.retakes.append(Retake(paragraphID: paragraphID, note: note))
            }
        }
    }

    /// Paragraphs that hold lines needing the narrator, in book order.
    func linesNeedingYou() -> [Line] {
        project.lines.filter { project.needsYou($0) }
    }

    /// The speaker and summary text used as a voice cue (Record, scripts).
    func voiceCue(for character: BookCharacter) -> String {
        let order: [FactKind] = [.age, .gender, .accent, .voice]
        let parts = order.compactMap { kind in character.facts.first { $0.kind == kind }?.text }
        var cue = ([character.voiceNotes].filter { !$0.isEmpty } + parts)
        if cue.isEmpty, let any = FactKind.narratorOrder.lazy.compactMap({ k in character.facts.first { $0.kind == k }?.text }).first { cue = [any] }
        return cue.joined(separator: " · ")
    }
}

/// Per-window state that is not part of the book.
@Observable
final class Workspace {
    enum Page: String, CaseIterable, Identifiable {
        case manuscript, cast, voices, index, pronunciations, record, retakes, review, revisions
        var id: String { rawValue }
        var title: String {
            switch self {
            case .manuscript: "Manuscript"
            case .cast: "Cast"
            case .voices: "Voices"
            case .index: "Index"
            case .pronunciations: "Pronunciations"
            case .record: "Record"
            case .retakes: "Retakes"
            case .review: "Review"
            case .revisions: "Revisions"
            }
        }
        var symbol: String {
            switch self {
            case .manuscript: "book.pages"
            case .cast: "person.2"
            case .voices: "person.wave.2"
            case .index: "list.bullet.indent"
            case .pronunciations: "character.bubble"
            case .record: "mic"
            case .retakes: "arrow.counterclockwise"
            case .review: "checklist"
            case .revisions: "doc.badge.clock"
            }
        }
        var helpTopic: HelpTopic {
            switch self {
            case .manuscript, .review: .fixingLines
            case .cast: .castAndSummaries
            case .voices: .voices
            case .index: .indexAndRehearse
            case .pronunciations: .pronunciations
            case .record, .retakes: .record
            case .revisions: .revisedManuscripts
            }
        }
        var shortcut: KeyEquivalent? {
            switch self {
            case .manuscript: "1"
            case .cast: "2"
            case .index: "3"
            case .pronunciations: "4"
            case .record: "5"
            case .retakes: "6"
            case .review: "7"
            case .voices: "8"
            case .revisions: nil
            }
        }
    }

    /// A request to show a paragraph (and maybe a line) in the manuscript.
    struct Reveal: Equatable {
        var paragraphID: UUID
        var lineID: UUID?
        var token = UUID()
    }

    var page: Page = .manuscript
    var selectedLines: [UUID] = []
    var reveal: Reveal?
    /// Character opened in the Cast page.
    var castPath: [UUID] = []
    /// The engine to switch to once the narrator allows this book's text to go to
    /// ElevenLabs (the Voices page asks).
    var askCloudEngine: VoiceEngine?
    /// "What is this project for?" (asked once per project).
    var showProjectType = false
    var showProjectSettings = false
    /// Where to go when the running Tag Speakers finishes (the AI voices setup ends on Voices).
    var afterTagging: Page?
    /// The narrator or character chosen on the Voices page.
    var voiceTarget: VoiceTarget = .narrator
    var showAudioExport = false
    /// Switches Review to this filter (snapshots, links from other pages).
    var reviewFilter: String?
    /// Lines to swap after a fix in a back-and-forth.
    var exchange: [Exchange.Proposal] = []
    var showInspector = true
    var rehearse: UUID?
    var showExport = false
    var showModelSheet = false
    var recordParagraph: UUID?
    /// Opens Assign To on this line (menu commands, snapshots).
    var pendingAssign: UUID?
    var message: String?
    var error: String?
    /// The paragraph shown in Record and highlighted while reading.
    var visibleChapter: Int = 0

    func show(paragraph: UUID, line: UUID? = nil) {
        page = .manuscript
        if let line { selectedLines = [line] }
        reveal = Reveal(paragraphID: paragraph, lineID: line)
    }

    func showCharacter(_ id: UUID) {
        page = .cast
        castPath = [id]
    }

    func showVoice(_ id: UUID?) {
        page = .voices
        voiceTarget = id.map { .character($0) } ?? .narrator
    }
}

extension Color {
    init(hexString: String) {
        let v = UInt32(hexString.trimmingCharacters(in: CharacterSet(charactersIn: "#")), radix: 16) ?? 0x888888
        self.init(hex: v)
    }
}

extension NSColor {
    convenience init(hexString: String) {
        let v = UInt32(hexString.trimmingCharacters(in: CharacterSet(charactersIn: "#")), radix: 16) ?? 0x888888
        self.init(hex: v)
    }

    var hexString: String {
        guard let c = usingColorSpace(.sRGB) else { return "#888888" }
        return String(format: "#%02X%02X%02X", Int(c.redComponent * 255), Int(c.greenComponent * 255), Int(c.blueComponent * 255))
    }

    static var attention: NSColor { NSColor(hexString: CharacterPalette.attentionHex) }
}
