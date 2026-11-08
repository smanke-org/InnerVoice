import Foundation
import InnerVoiceKit

/// A short original story used by snapshots, the self-test and the demo. Every name and
/// place is made up. `answers` says who really speaks each line, to score the AI.
enum SeedData {
    static let markdown = """
    # Chapter 1: The Storm

    The wind came off the sea like a held breath finally let go. Mara Quinn pulled the door shut behind her and leaned on it until the latch caught.

    “You're late,” said Tom Hale, without looking up from the lamp.

    “The path was underwater,” Mara said. “I had to go round by the chapel.”

    “Then you'll be wanting tea.”

    “I'll be wanting a towel first.”

    “Towels are in the chest. Same as always.”

    “Same as always,” she repeated, and almost smiled.

    *He hasn't asked about the letter,* Mara thought. *Maybe he doesn't know.*

    Tom set down the wick trimmer. He was a broad man gone narrow with age, sixty if he was a day, and when he spoke again his voice had the gravel of a man who had shouted over gales for forty years. “Doctor Voss came by. Asked after you.”

    “What did you tell her?”

    “That you'd be back when the sea let you.”

    ⁂

    A knock shook the door. Pip Aelwyn stood on the step, soaked to the skin, a lantern swinging from one small fist.

    “Miss Quinn! Miss Quinn, the boat's come loose from the jetty and Da says—”

    “Slow down, Pip.”

    “—Da says if it goes on the rocks we'll none of us eat till spring!”

    # Chapter 2: The Letter

    Doctor Edda Voss arrived the next morning with her bag and her clipped, careful vowels, every word set down as if it might break. She had come from Hamburg twenty years ago and never quite let the north of Germany go.

    “You have been avoiding me, Mara.”

    “I've been busy.”

    “Busy.” The doctor folded her gloves. “Your father wrote to me before he died. I think you should read what he wrote.”

    She laid an envelope on the table. The handwriting on it was small and slanted.

    > My dear girl — if Edda has given you this, then I was too much of a coward to say it myself. The light was never mine to leave you. It belongs to Kerensa.

    “Who is Kerensa?” Mara whispered.

    “Read the rest.”

    “I can't.”

    “You can. You are your father's daughter.”

    “That's what frightens me.”

    Pip had crept in behind them, dripping on the flagstones. “Is it treasure? Da says old Quinn had treasure.”

    “Out,” said the doctor, not unkindly.

    # Chapter 3: The Keeper

    That night Tom told her the story. He told it the way he told everything, in the soft burr of the Highlands he had never lost, though he'd left Inverness as a boy of twelve.

    “Your father and I kept this light together for thirty years,” he said. “And in all that time he never once lied to me. Except about Kerensa.

    “She was his sister. Not dead, as he told the village. Gone to sea, and never written, and him too proud to look for her.”

    “Is she alive?”

    “Aye. In Penzance. She wrote to me last spring.”

    “And you didn't tell me.”

    “I'm telling you now, lass.”
    """

    static func project() -> Project {
        let blocks = PlainTextReaderBridge.blocks(markdown)
        let chapters = ChapterDetector.chapters(from: blocks, fallbackTitle: "The Keeper of Penhallow Light")
        var project = ProjectBuilder.make(from: ImportedBook(title: "The Keeper of Penhallow Light", author: "A. N. Example", chapters: chapters),
                                          fileName: "Penhallow.docx")
        // Make sure the four main characters exist with good colors.
        func id(_ name: String) -> UUID {
            if let c = project.characters.first(where: { $0.name == name || $0.allNames.contains(name) }) { return c.id }
            let c = BookCharacter(name: name, colorHex: CharacterPalette.next(used: project.characters.map(\.colorHex)))
            project.characters.append(c)
            return c.id
        }
        let tom = id("Tom Hale"), mara = id("Mara Quinn"), edda = id("Edda Voss"), pip = id("Pip Aelwyn")
        // Simulate a finished AI run: fill every line the rules left, with one unsure line left red.
        let index = BookIndex(project)
        for i in project.lines.indices where project.lines[i].source == .none || project.lines[i].source == .exchange {
            let text = project.text(of: project.lines[i], index: index)
            let truth = answer(for: text, tom: tom, mara: mara, edda: edda, pip: pip)
            project.lines[i].speakerID = truth
            project.lines[i].source = .ai
            project.lines[i].confidence = text.hasPrefix("Is it treasure") ? 0.4 : 0.86
            if text.hasPrefix("Who is Kerensa") { project.lines[i].speakerID = nil; project.lines[i].confidence = 0 }
        }
        project.lastTagged = .now
        project.summariesBuilt = .now
        project.settings.unsureBelow = 0.6

        func para(containing s: String) -> UUID? {
            project.chapters.flatMap(\.paragraphs).first { $0.text.contains(s) }?.id
        }
        func fact(_ who: UUID, _ kind: FactKind, _ text: String, _ source: String, ai: Bool = true) {
            guard let i = project.characterIndex(who) else { return }
            project.characters[i].facts.append(Fact(kind: kind, text: text, sourceParagraphID: para(containing: source), quote: source, fromAI: ai))
        }
        fact(tom, .age, "About sixty", "sixty if he was a day")
        fact(tom, .voice, "Gravelly from shouting over gales", "the gravel of a man")
        fact(tom, .accent, "Soft Highland Scottish burr (from Inverness)", "soft burr of the Highlands")
        fact(tom, .history, "Kept the light with Mara's father for thirty years", "kept this light together for thirty years")
        fact(tom, .appearance, "Broad man gone narrow with age", "broad man gone narrow")
        fact(edda, .accent, "German, from Hamburg; clipped, careful vowels", "clipped, careful vowels")
        fact(edda, .history, "Came from Hamburg twenty years ago", "come from Hamburg twenty years ago")
        fact(edda, .personality, "Firm but kind", "not unkindly")
        fact(mara, .relationships, "Daughter of the late keeper, old Quinn", "You are your father's daughter")
        fact(pip, .age, "A small boy", "one small fist")
        fact(pip, .voice, "Breathless, tumbling", "Miss Quinn! Miss Quinn")
        if let i = project.characterIndex(tom) {
            project.characters[i].voiceNotes = "Low and slow, chest voice, Highland lilt on the vowels"
            project.characters[i].aliases = ["Tom"]
        }
        if let i = project.characterIndex(mara) { project.characters[i].aliases = ["Mara", "Miss Quinn"] }
        if let i = project.characterIndex(edda) { project.characters[i].aliases = ["Doctor Voss", "the doctor"] }
        if let i = project.characterIndex(pip) { project.characters[i].aliases = ["Pip"] }
        project.settings.povCharacterID = nil

        let jane = Narrator(name: "Jane Doe"), sam = Narrator(name: "Sam Roe")
        project.narrators = [jane, sam]
        for i in project.characters.indices {
            project.characters[i].narratorID = [mara, edda].contains(project.characters[i].id) ? jane.id : sam.id
        }
        PronunciationFinder.refresh(&project, isKnownWord: { ["the", "and"].contains($0) || $0.count < 3 })
        for i in project.pronunciations.indices {
            switch project.pronunciations[i].term {
            case "Penhallow": project.pronunciations[i].sayItAs = "pen-HAL-oh"; project.pronunciations[i].confirmed = true
            case "Aelwyn": project.pronunciations[i].sayItAs = "ALE-win"; project.pronunciations[i].notes = "Welsh"
            case "Kerensa": project.pronunciations[i].sayItAs = "keh-REN-sa"; project.pronunciations[i].notes = "Cornish name"
            default: break
            }
        }
        let paragraphs = project.chapters[0].paragraphs
        for p in paragraphs.prefix(6) { project.recorded.insert(p.id) }
        if paragraphs.count > 3 { project.retakes = [Retake(paragraphID: paragraphs[3].id, note: "Tom too young here; drop the pitch")] }
        if let changed = para(containing: "Doctor Edda Voss arrived"), let added = para(containing: "Out,") {
            project.revisions = [RevisionRecord(fileName: "Penhallow v2.docx", added: [added], changed: [changed],
                                                removed: [.init(text: "The gulls had gone quiet.", chapterTitle: "Chapter 2: The Letter")],
                                                unchanged: 41)]
        }
        return project
    }

    /// Who really says each line (the self-test scores the AI against this).
    static func answer(for text: String, tom: UUID, mara: UUID, edda: UUID, pip: UUID) -> UUID? {
        let table: [(String, String)] = [
            ("You're late", "tom"), ("The path was", "mara"), ("Then you'll be", "tom"), ("I'll be wanting", "mara"),
            ("Towels are", "tom"), ("Same as always", "mara"), ("He hasn't asked", "mara"), ("Maybe he doesn't", "mara"),
            ("Doctor Voss came", "tom"), ("What did you", "mara"), ("That you'd be", "tom"), ("Miss Quinn!", "pip"),
            ("Slow down", "mara"), ("Da says if", "pip"), ("You have been avoiding", "edda"), ("I've been busy", "mara"),
            ("Busy.", "edda"), ("Your father wrote", "edda"), ("Who is Kerensa", "mara"),
            ("Read the rest", "edda"), ("I can't", "mara"), ("You can.", "edda"), ("That's what frightens", "mara"),
            ("Is it treasure", "pip"), ("Out", "edda"), ("Your father and I", "tom"), ("She was his sister", "tom"),
            ("Is she alive", "mara"), ("Aye.", "tom"), ("And you didn't", "mara"), ("I'm telling you", "tom"),
        ]
        let ids = ["tom": tom, "mara": mara, "edda": edda, "pip": pip]
        let text = text.trimmingCharacters(in: CharacterSet(charactersIn: "—– "))
        for (prefix, who) in table where text.hasPrefix(prefix) { return ids[who] }
        return nil
    }
}

/// Seed text goes through the same Markdown reader as a real file.
enum PlainTextReaderBridge {
    static func blocks(_ markdown: String) -> [RawBlock] {
        let url = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-seed-\(UUID().uuidString).md")
        try? markdown.write(to: url, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: url) }
        let book = try? ManuscriptImporter.read(url)
        // Re-flatten to blocks: chapters were already found; keep headings as level 1.
        return book?.chapters.flatMap { c in
            [RawBlock(text: c.title, headingLevel: 1)] + c.paragraphs.map { RawBlock(text: $0.text, italic: $0.italic, isBlockQuote: $0.style == .blockQuote) }
        } ?? []
    }
}
