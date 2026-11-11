import AppKit
import InnerVoiceKit
import SwiftUI

/// The narrator's edits. Each is one undo step with a name for the Edit menu.
@MainActor extension InnerVoiceDocument {
    func assign(_ lineIDs: [UUID], to speaker: UUID?, undoManager: UndoManager?) {
        let name = lineIDs.count == 1 ? "Assign Line" : "Assign \(lineIDs.count) Lines"
        let ids = Set(lineIDs)
        ActivityStore.shared.recordReview(before: project.lines.filter { ids.contains($0.id) }, newSpeaker: speaker)
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
        // A guess that turned out to be narration was a wrong guess.
        let ids = Set(lineIDs)
        ActivityStore.shared.recordReview(before: project.lines.filter { ids.contains($0.id) && $0.source != .none }, newSpeaker: nil)
        change(lineIDs.count == 1 ? "Mark as Narration" : "Mark \(lineIDs.count) Lines as Narration", undoManager: undoManager) { p in
            let set = Set(lineIDs)
            p.lines.removeAll { set.contains($0.id) }
        }
    }

    /// Deletes text the narrator doesn't read (an ISBN, an ad for another book) from the
    /// project's copy of the book. One undo step; the imported file is never changed.
    func deleteText(cuts: [UUID: [TextSpan]], chapters: Set<UUID> = [], headings: [UUID: TextSpan] = [:], undoManager: UndoManager?) {
        guard !cuts.isEmpty || !chapters.isEmpty || !headings.isEmpty else { return }
        let onlyChapters = cuts.isEmpty && headings.isEmpty
        let name = chapters.count == 1 && onlyChapters ? "Delete Chapter" : (chapters.count > 1 && onlyChapters ? "Delete Chapters" : "Delete Text")
        change(name, undoManager: undoManager, textChanged: true) { $0.deleteText(cuts: cuts, chapters: chapters, headings: headings) }
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
        let c = BookCharacter(name: trimmed, colorHex: CharacterPalette.forNew(named: trimmed, used: project.characters.map(\.colorHex)))
        change("New Character", undoManager: undoManager) { $0.characters.append(c) }
        return c.id
    }

    /// Sets a character's color, or puts it back on Automatic (`hex == nil`): a color from
    /// their gender's family, kept up to date as the book says more about them.
    func setColor(_ id: UUID, hex: String?, undoManager: UndoManager?) {
        let index = self.index
        change(hex == nil ? "Automatic Color" : "Change Color", undoManager: undoManager) { p in
            guard let i = p.characterIndex(id) else { return }
            if let hex {
                p.characters[i].colorHex = hex
                p.characters[i].colorChosen = true
            } else {
                p.characters[i].colorChosen = false
                // A color picked with Other… isn't in the palette: start from the family's next one.
                if CharacterPalette.family(of: p.characters[i].colorHex) == nil {
                    p.characters[i].colorHex = CharacterPalette.next(used: p.characters.map(\.colorHex))
                }
                CharacterPalette.recolorAutomatic(&p, index: index, only: id)
            }
        }
    }

    /// Matches every Automatic color to what the book now says about each character.
    /// Runs after Tag Speakers and Build Summaries, and when a project opens. No undo step:
    /// nothing the narrator chose changes.
    func recolorAutomatic() {
        let project = self.project
        let index = self.index
        Task {
            // Gender reads the whole book: off the main thread, then the colors on it.
            let families = await Task.detached(priority: .utility) { CharacterPalette.families(project, index: index) }.value
            let now = self.index
            applyQuietly { CharacterPalette.recolorAutomatic(&$0, index: now, families: families) }
        }
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

    /// Paragraphs that hold lines needing the narrator, in book order.
    func linesNeedingYou() -> [Line] {
        project.lines.filter { project.needsYou($0) }
    }

    /// The speaker and summary text used as a voice cue (Teleprompter, scripts).
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
        case manuscript, cast, voices, index, pronunciations, teleprompter, review, revisions
        var id: String { rawValue }
        var title: String {
            switch self {
            case .manuscript: "Manuscript"
            case .cast: "Cast"
            case .voices: "Voices"
            case .index: "Index"
            case .pronunciations: "Pronunciations"
            case .teleprompter: "Teleprompter"
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
            case .teleprompter: "text.alignleft"
            case .review: "checklist"
            case .revisions: "doc.badge.clock"
            }
        }
        var helpTopic: HelpTopic {
            switch self {
            case .manuscript, .review: .fixingLines
            case .cast: .castAndSummaries
            case .voices: .voices
            case .index: .index
            case .pronunciations: .pronunciations
            case .teleprompter: .teleprompter
            case .revisions: .revisedManuscripts
            }
        }
        var shortcut: KeyEquivalent? {
            switch self {
            case .manuscript: "1"
            case .cast: "2"
            case .index: "3"
            case .pronunciations: "4"
            case .teleprompter: "5"
            case .review: "6"
            case .voices: "7"
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
    var showBookStats = false
    /// Use Voices from Another Project (a book earlier in the series).
    var showSeriesVoices = false
    /// The earlier book, already read (snapshots pass a made-up one).
    var seriesSource: Project?
    /// "Analyze the Whole Cast Again?" before a second Build Summaries for everyone.
    var askAnalyzeCastAgain = false
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
    var showExport = false
    var showModelSheet = false
    /// The paragraph the teleprompter shows (it starts where this book was left off).
    var teleprompterParagraph: UUID?
    /// Opens Assign To on this line (menu commands, snapshots).
    var pendingAssign: UUID?
    var message: String?
    var error: String?
    /// The chapter showing in the manuscript.
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

    /// Clone a Voice (ElevenLabs), and who the new voice is first meant for.
    var showCloneVoice = false
    var cloneFor: VoiceTarget = .narrator

    /// Opens Clone a Voice for a character (nil: the narrator).
    func cloneVoice(for id: UUID?) {
        cloneFor = id.map { .character($0) } ?? .narrator
        showCloneVoice = true
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
