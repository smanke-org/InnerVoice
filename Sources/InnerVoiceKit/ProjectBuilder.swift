import Foundation

/// Turns an imported book into a project: finds the lines and runs the plain rules.
public enum ProjectBuilder {
    public static func make(from book: ImportedBook, fileName: String) -> Project {
        var project = Project(title: book.title, author: book.author, sourceFileName: fileName, chapters: book.chapters)
        project.lines = DialogueFinder.findAll(in: book.chapters)
        RuleAttribution.apply(to: &project)
        ToneRules.apply(to: &project)
        return project
    }
}

/// Finds names, places and made-up words a narrator may need to look up.
public enum PronunciationFinder {
    public struct Candidate: Hashable, Sendable {
        public var term: String
        public var occurrences: Int
        public var firstParagraphID: UUID
    }

    /// `isKnownWord` says whether a lowercase word is in the dictionary (the app passes the
    /// system spell checker). Capitalized words that appear mid-sentence and aren't
    /// dictionary words, and repeated non-dictionary lowercase words, are candidates.
    public static func candidates(in project: Project, isKnownWord: (String) -> Bool) -> [Candidate] {
        var counts: [String: Int] = [:]
        var first: [String: UUID] = [:]
        var midSentenceCapital: Set<String> = []
        let tokenizer = try! NSRegularExpression(pattern: "[\\p{L}][\\p{L}’'\\-]*[\\p{L}]|[\\p{L}]")
        for chapter in project.chapters {
            for p in chapter.paragraphs where p.style != .heading {
                let ns = p.text as NSString
                var previousEndsSentence = true
                var cursor = 0
                for m in tokenizer.matches(in: p.text, range: NSRange(location: 0, length: ns.length)) {
                    let gap = ns.substring(with: NSRange(location: cursor, length: m.range.location - cursor))
                    if gap.contains(where: { ".!?“\"‘:—".contains($0) }) { previousEndsSentence = true }
                    cursor = m.range.location + m.range.length
                    var word = ns.substring(with: m.range)
                    if word.hasSuffix("’s") || word.hasSuffix("'s") { word = String(word.dropLast(2)) }
                    guard word.count >= 3 else { previousEndsSentence = false; continue }
                    counts[word, default: 0] += 1
                    if first[word] == nil { first[word] = p.id }
                    if let c = word.first, c.isUppercase, !previousEndsSentence { midSentenceCapital.insert(word) }
                    previousEndsSentence = false
                }
            }
        }
        var out: [Candidate] = []
        var seenLower: Set<String> = []
        for (word, count) in counts {
            let lower = word.lowercased()
            guard !Names.notNames.contains(word), !Names.titles.contains(word) else { continue }
            let capitalized = word.first?.isUppercase == true
            if capitalized {
                guard midSentenceCapital.contains(word) else { continue }
                // A name that is also an everyday word ("Will", "Rose") is usually said as written.
                if isKnownWord(lower) || isKnownWord(word) { continue }
            } else {
                guard count >= 2, !isKnownWord(lower), counts[word.prefix(1).uppercased() + word.dropFirst()] == nil else { continue }
            }
            guard !seenLower.contains(lower) else { continue }
            seenLower.insert(lower)
            out.append(Candidate(term: word, occurrences: count + (capitalized ? 0 : counts[word.capitalized] ?? 0), firstParagraphID: first[word]!))
        }
        return out.sorted { $0.occurrences == $1.occurrences ? $0.term < $1.term : $0.occurrences > $1.occurrences }
    }

    /// Adds new candidates to the project's list, keeping the narrator's entries and dismissals.
    public static func refresh(_ project: inout Project, isKnownWord: (String) -> Bool) {
        let found = candidates(in: project, isKnownWord: isKnownWord)
        var byTerm = Dictionary(project.pronunciations.enumerated().map { ($1.term.lowercased(), $0) }, uniquingKeysWith: { a, _ in a })
        for c in found {
            if let i = byTerm[c.term.lowercased()] {
                project.pronunciations[i].occurrences = c.occurrences
                project.pronunciations[i].firstParagraphID = c.firstParagraphID
            } else {
                project.pronunciations.append(Pronunciation(term: c.term, occurrences: c.occurrences, firstParagraphID: c.firstParagraphID))
                byTerm[c.term.lowercased()] = project.pronunciations.count - 1
            }
        }
    }
}
