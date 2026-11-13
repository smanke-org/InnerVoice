import Foundation

public extension Chapter {
    /// The recap to show and say, if there is one.
    var recapText: String? {
        let t = (recap ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return t.isEmpty ? nil : t
    }
}

/// Chapter Recaps: a short "Previously…" for each chapter after the first, written by Claude
/// from the chapter before it (and that chapter's own recap, so the thread carries on).
public enum RecapPrompt {
    public static let system = """
    You write the short "Previously…" recap a listener hears at the start of an audiobook \
    chapter, so someone coming back to the book remembers where the story is. From the \
    chapter given, write two to four plain sentences, under 70 words, in the past tense: who \
    did what, and what is at stake now. Use the characters' names. Don't quote the book, \
    don't judge it, and don't reveal anything that happens later. Answer with JSON only.
    """

    /// Words a chapter needs to be worth recapping (title pages and part openers aren't).
    public static let minimumWords = 150
    public static let maximumWords = 90

    public struct Job: Sendable {
        /// The chapter whose recap this is.
        public var chapterID: UUID
        public var messages: [ChatMessage]
    }

    /// One job per chapter that has a story chapter before it and no recap of the
    /// narrator's own. `chapters` (indexes) limits it; `replace` also rewrites Claude's
    /// earlier recaps.
    public static func jobs(project: Project, chapters: [Int]? = nil, replace: Bool = true) -> [Job] {
        let wanted = chapters.map(Set.init)
        var out: [Job] = []
        var source: Int? = nil   // the last story chapter before this one
        for c in project.chapters.indices {
            defer { if project.chapters[c].wordCount >= minimumWords { source = c } }
            guard let s = source, wanted?.contains(c) ?? true else { continue }
            let chapter = project.chapters[c]
            guard chapter.recapEdited != true, replace || chapter.recapText == nil else { continue }
            let before = project.chapters[s]
            let text = before.paragraphs.filter { $0.style != .sceneBreak }.map(\.text).joined(separator: "\n\n")
            let earlier = before.recapText.map { "\nWhat happened before that chapter: \($0)\n" } ?? ""
            let user = """
            Book: \(project.title)
            \(earlier)
            The chapter before "\(chapter.title)" (\(before.title)):

            \(text)

            Reply as {"recap":"…"}
            """
            out.append(Job(chapterID: chapter.id, messages: [ChatMessage(.system, system), ChatMessage(.user, user)]))
        }
        return out
    }

    /// The recap from a reply, tidied: no "Previously" lead-in, at most `maximumWords`.
    public static func parse(_ text: String) -> String? {
        struct Wrapper: Codable { var recap: String }
        var recap: String
        if let json = JSONText.extract(text), let w = try? JSONDecoder().decode(Wrapper.self, from: Data(json.utf8)) {
            recap = w.recap
        } else {
            return nil
        }
        recap = recap.trimmingCharacters(in: .whitespacesAndNewlines)
        for lead in ["Previously…", "Previously...", "Previously,", "Previously:", "Previously"] where recap.hasPrefix(lead) {
            recap = String(recap.dropFirst(lead.count)).trimmingCharacters(in: .whitespacesAndNewlines)
            break
        }
        guard Words.count(recap) >= 3 else { return nil }
        if Words.count(recap) > maximumWords {
            // Keep whole sentences up to the limit.
            var kept = ""
            for s in SpeechScript.sentences(recap) {
                if Words.count(kept + s) > maximumWords { break }
                kept += (kept.isEmpty ? "" : " ") + s
            }
            recap = kept.isEmpty ? recap : kept
        }
        return recap.prefix(1).uppercased() + recap.dropFirst()
    }

    /// Stores a recap unless the narrator wrote or edited this chapter's own. True if stored.
    @discardableResult
    public static func put(_ recap: String, chapterID: UUID, in p: inout Project) -> Bool {
        guard let c = p.chapters.firstIndex(where: { $0.id == chapterID }), p.chapters[c].recapEdited != true else { return false }
        p.chapters[c].recap = recap
        return true
    }

    /// The narrator's edit: kept from then on. Clearing it lets Claude write one again.
    public static func edit(_ text: String, chapterID: UUID, in p: inout Project) {
        guard let c = p.chapters.firstIndex(where: { $0.id == chapterID }) else { return }
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        p.chapters[c].recap = t.isEmpty ? nil : text
        p.chapters[c].recapEdited = t.isEmpty ? nil : true
    }
}
