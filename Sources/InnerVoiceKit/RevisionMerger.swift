import Foundation

/// Brings a revised manuscript into an existing project. Unchanged paragraphs keep their
/// identity, so every tag, fact source, recorded mark and retake stays put; lightly edited
/// paragraphs keep the speakers of lines whose words still match; new text is found and
/// tagged with the rules, ready for the AI.
public enum RevisionMerger {
    public struct Result: Sendable {
        public var project: Project
        public var record: RevisionRecord
    }

    public static func merge(_ old: Project, with book: ImportedBook, fileName: String) -> Result {
        let oldIndex = BookIndex(old)
        let oldParas = old.chapters.flatMap { c in c.paragraphs.map { (c.title, $0) } }
        var newChapters = book.chapters
        let newParas = newChapters.flatMap(\.paragraphs)
        let oldKeys = oldParas.map { TextMatch.normalize($0.1.text) }
        let newKeys = newParas.map { TextMatch.normalize($0.text) }

        // 1. Exact matches in order (a diff), then pair what is left by similarity.
        var newToOld: [Int: Int] = [:]
        var changed: Set<Int> = []
        let diff = newKeys.difference(from: oldKeys)
        var removedOld = Set<Int>(), insertedNew = Set<Int>()
        for change in diff {
            switch change {
            case .remove(let offset, _, _): removedOld.insert(offset)
            case .insert(let offset, _, _): insertedNew.insert(offset)
            }
        }
        var o = 0, n = 0
        while o < oldKeys.count, n < newKeys.count {
            if removedOld.contains(o) { o += 1; continue }
            if insertedNew.contains(n) { n += 1; continue }
            newToOld[n] = o
            o += 1
            n += 1
        }
        // Within each gap between exact matches, pair edited paragraphs.
        let anchors = newToOld.sorted { $0.key < $1.key }
        var gaps: [(Range<Int>, Range<Int>)] = []
        var prevNew = -1, prevOld = -1
        for (nn, oo) in anchors + [(newKeys.count, oldKeys.count)] {
            if nn - prevNew > 1, oo - prevOld > 1 { gaps.append(((prevOld + 1)..<oo, (prevNew + 1)..<nn)) }
            prevNew = nn
            prevOld = oo
        }
        var pairedOld = Set(newToOld.values)
        for (oldRange, newRange) in gaps {
            var cursor = oldRange.lowerBound
            for nn in newRange {
                var best: (Int, Double)? = nil
                for oo in cursor..<oldRange.upperBound where !pairedOld.contains(oo) {
                    let s = TextMatch.similarity(oldParas[oo].1.text, newParas[nn].text)
                    if s >= 0.5, s > (best?.1 ?? 0) { best = (oo, s) }
                }
                if let (oo, _) = best {
                    newToOld[nn] = oo
                    pairedOld.insert(oo)
                    changed.insert(nn)
                    cursor = oo + 1
                }
            }
        }

        // 2. Give matched paragraphs their old identity.
        var flat = 0
        for c in newChapters.indices {
            for p in newChapters[c].paragraphs.indices {
                if let oo = newToOld[flat] { newChapters[c].paragraphs[p].id = oldParas[oo].1.id }
                flat += 1
            }
        }
        // Chapters keep their id when their title matches an old one, so outlines stay stable.
        for c in newChapters.indices {
            if let match = old.chapters.first(where: { TextMatch.normalize($0.title) == TextMatch.normalize(newChapters[c].title) }) {
                newChapters[c].id = match.id
            }
        }

        // 3. Lines: unchanged paragraphs keep theirs; edited ones match by words.
        let found = DialogueFinder.findAll(in: newChapters)
        let oldLinesByParagraph = Dictionary(grouping: old.lines, by: \.paragraphID)
        var changedParagraphIDs = Set<UUID>()
        flat = 0
        for c in newChapters {
            for p in c.paragraphs {
                if changed.contains(flat) { changedParagraphIDs.insert(p.id) }
                flat += 1
            }
        }
        let newByID = Dictionary(newChapters.flatMap(\.paragraphs).map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        var lines: [Line] = []
        for line in found {
            guard let olds = oldLinesByParagraph[line.paragraphID] else { lines.append(line); continue }
            if !changedParagraphIDs.contains(line.paragraphID),
               let same = olds.first(where: { $0.range == line.range }) {
                lines.append(same)
                continue
            }
            // Edited paragraph: keep the speaker of the old line with the same words.
            let newText = newByID[line.paragraphID]?.substring(line.range) ?? ""
            let oldPara = old.paragraph(line.paragraphID, index: oldIndex)
            var best: (Line, Double)? = nil
            for ol in olds {
                let s = TextMatch.similarity(oldPara?.substring(ol.range) ?? "", newText)
                if s >= 0.6, s > (best?.1 ?? 0) { best = (ol, s) }
            }
            if let (ol, _) = best {
                var kept = line
                kept.id = ol.id
                kept.kind = ol.kind
                kept.speakerID = ol.speakerID
                kept.source = ol.source
                kept.confidence = ol.confidence
                lines.append(kept)
            } else {
                lines.append(line)
            }
        }

        var project = old
        project.chapters = newChapters
        project.lines = lines
        let keptIDs = Set(newChapters.flatMap(\.paragraphs).map(\.id))
        // Recorded audio of an edited paragraph no longer matches the text.
        project.recorded = old.recorded.intersection(keptIDs).subtracting(changedParagraphIDs)
        project.retakes = old.retakes.filter { keptIDs.contains($0.paragraphID) }
        for c in project.characters.indices {
            for f in project.characters[c].facts.indices {
                if let s = project.characters[c].facts[f].sourceParagraphID, !keptIDs.contains(s) {
                    project.characters[c].facts[f].sourceParagraphID = nil
                }
            }
        }
        for i in project.pronunciations.indices {
            if let s = project.pronunciations[i].firstParagraphID, !keptIDs.contains(s) { project.pronunciations[i].firstParagraphID = nil }
        }
        RuleAttribution.apply(to: &project)
        ToneRules.apply(to: &project)

        let matchedOld = Set(newToOld.values)
        let removed = oldParas.enumerated().filter { !matchedOld.contains($0.offset) && Words.count($0.element.1.text) > 0 }
            .map { RevisionRecord.Removed(text: $0.element.1.text, chapterTitle: $0.element.0) }
        let addedIDs = newChapters.flatMap(\.paragraphs).enumerated().filter { newToOld[$0.offset] == nil }.map(\.element.id)
        let record = RevisionRecord(fileName: fileName, added: addedIDs,
                                    changed: newChapters.flatMap(\.paragraphs).filter { changedParagraphIDs.contains($0.id) }.map(\.id),
                                    removed: removed, unchanged: newToOld.count - changed.count)
        project.revisions.append(record)
        project.sourceFileName = fileName
        return Result(project: project, record: record)
    }
}
