import Foundation
import Testing
@testable import InnerVoiceKit

// Claude's features (phase 3): cost estimate, Clean Up Text, Chapter Recaps, Delivery Hints,
// Suggest Spellings. All pure logic; no network.

private func make(_ chapters: [[String]]) -> Project {
    let book = ImportedBook(title: "Test", chapters: chapters.enumerated().map { n, ps in
        Chapter(title: "Chapter \(n + 1)", paragraphs: ps.map { Paragraph(text: $0) })
    })
    return ProjectBuilder.make(from: book, fileName: "t")
}

private func texts(_ p: Project) -> [String] {
    let index = BookIndex(p)
    return p.lines.map { p.text(of: $0, index: index) }
}

// MARK: Cost estimate

@Test func costEstimateUsesListPricesAndShowsTokensForUnknownModels() {
    let e = CostEstimate(inputTokens: 1_000_000, outputTokens: 100_000)
    #expect(e.dollars(modelID: "claude-sonnet-5-5") == 3.0)
    #expect(e.dollars(modelID: "claude-haiku-5-5")! < 0.16)
    #expect(e.dollars(modelID: "claude-fable-5-2") == 12.0)
    #expect(e.dollars(modelID: "claude-opus-5-5") == 6.0)
    #expect(e.phrase(modelID: "claude-sonnet-5-5") == "about $3.00")
    #expect(e.dollars(modelID: "claude-future-9") == nil)
    #expect(e.phrase(modelID: "claude-future-9") == "about 1,100,000 tokens")
    #expect(e.sentence(modelID: "claude-sonnet-5-5").contains("estimate"))
    #expect(CostEstimate(inputTokens: 10, outputTokens: 10).phrase(modelID: "claude-haiku-5-5") == "less than $0.01")
}

@Test func costEstimateGrowsWithTheBook() {
    let small = make([["“Hello,” said Tom. The rain fell on the town all night long."]])
    let big = make([Array(repeating: "“Hello,” said Tom. The rain fell on the town all night long and nobody slept at all.", count: 400)])
    let a = CostEstimator.tagSpeakers(project: small)
    let b = CostEstimator.tagSpeakers(project: big)
    #expect(a.inputTokens > 0 && b.inputTokens > a.inputTokens * 5)
    #expect(b.outputTokens > a.outputTokens)
    // Only the picked chapters count.
    let two = make([Array(repeating: "Words and more words in a long chapter of the book.", count: 200), ["Short."]])
    #expect(CostEstimator.tagSpeakers(project: two, chapters: [1]).inputTokens < CostEstimator.tagSpeakers(project: two, chapters: [0]).inputTokens)
    #expect(CostEstimator.buildSummaries(project: big, characters: 4).inputTokens > CostEstimator.buildSummaries(project: big, characters: 1).inputTokens)
    let req = CostEstimator.requests([[ChatMessage(.user, String(repeating: "abcd", count: 1000))]], outputEach: 100)
    #expect(req.inputTokens >= 1000 && req.outputTokens == 200)
}

// MARK: Clean Up Text

@Test func cleanupChunksLabelBodyParagraphsAndParseReplies() {
    let p = make([["The ﬁrst tbe night.", "12", "“Go,” said Tom."]])
    let chunks = CleanupPrompt.chunks(project: p)
    #expect(chunks.count == 1)
    #expect(chunks[0].labels.count == 3)
    #expect(chunks[0].messages[1].content.contains("[P1] The ﬁrst tbe night."))
    let reply = #"{"fixes":[{"paragraph":"P1","find":"tbe night","replace":"the night","issue":"ocr","note":"tbe"},{"paragraph":"P2","find":"12","replace":"","issue":"page-number"},{"paragraph":"P3","find":"not there","replace":"x"},{"paragraph":"P1","find":"The ﬁrst tbe night.","replace":"It was a dark and stormy night, said the author.","issue":"other"}]}"#
    let parsed = CleanupPrompt.parse(reply)
    #expect(parsed?.count == 4)
    let fixes = CleanupPrompt.fixes(from: parsed ?? [], chunk: chunks[0], project: p, index: BookIndex(p))
    // The made-up text and the rewrite are refused.
    #expect(fixes.count == 2)
    #expect(fixes.first?.issue == .ocr)
    #expect(fixes.last?.issue == .pageNumber)
}

@Test func cleanupKeepsLinesAndTheNarratorsAssignments() throws {
    var p = make([["Intro text here.", "“Go now, tbe boat leaves,” said Tom. “Hurry.”", "34", "Then she left."]])
    let tom = BookCharacter(name: "Tom", colorHex: "#3366CC")
    p.characters = [tom]
    let paraID = p.chapters[0].paragraphs[1].id
    let pageID = p.chapters[0].paragraphs[2].id
    // The narrator assigned the first line; the second is the AI's, with a tone and a hint.
    let idx = p.lines.indices.filter { p.lines[$0].paragraphID == paraID }
    #expect(idx.count == 2)
    p.lines[idx[0]].speakerID = tom.id
    p.lines[idx[0]].source = .user
    p.lines[idx[1]].speakerID = tom.id
    p.lines[idx[1]].source = .ai
    p.lines[idx[1]].tone = .scared
    p.lines[idx[1]].toneSource = .ai
    p.lines[idx[1]].delivery = "out of breath"
    let ids = idx.map { p.lines[$0].id }
    let fixes = [
        CleanupFix(paragraphID: paraID, original: "tbe boat", replacement: "the boat", issue: .ocr),
        CleanupFix(paragraphID: pageID, original: "34", replacement: "", issue: .pageNumber),
    ]
    let applied = TextCleanup.apply(fixes, to: &p)
    #expect(Set(applied) == Set(fixes.map(\.id)))
    #expect(p.chapters[0].paragraphs.count == 3)
    #expect(p.chapters[0].paragraphs[1].text == "“Go now, the boat leaves,” said Tom. “Hurry.”")
    // Same lines, same ids, valid ranges, owners kept.
    let lines = p.lines.filter { $0.paragraphID == paraID }
    #expect(lines.map(\.id) == ids)
    #expect(texts(p).contains("Go now, the boat leaves,"))
    #expect(texts(p).contains("Hurry."))
    #expect(lines[0].source == .user && lines[0].speakerID == tom.id)
    #expect(lines[1].tone == .scared && lines[1].delivery == "out of breath")
    for l in p.lines {
        let para = try #require(p.chapters.flatMap(\.paragraphs).first { $0.id == l.paragraphID })
        #expect(l.range.end <= (para.text as NSString).length)
    }
}

@Test func cleanupNeverLosesALineTheNarratorAssigned() {
    var p = make([["“Wait for me,” said Ann."]])
    let pid = p.chapters[0].paragraphs[0].id
    p.lines[0].source = .user
    let before = p
    // Removing the quote marks would turn the line into narration: refused, text unchanged.
    let applied = TextCleanup.apply([CleanupFix(paragraphID: pid, original: "“Wait for me,”", replacement: "Wait for me,")], to: &p)
    #expect(applied.isEmpty)
    #expect(p == before)
}

@Test func cleanupShiftsItalicsAndKeepsTheOnlyParagraph() {
    #expect(TextCleanup.shift([TextSpan(location: 10, length: 5)], replacing: NSRange(location: 0, length: 4), with: 2) == [TextSpan(location: 8, length: 5)])
    #expect(TextCleanup.shift([TextSpan(location: 0, length: 3)], replacing: NSRange(location: 5, length: 4), with: 2) == [TextSpan(location: 0, length: 3)])
    var p = make([["99"]])
    let pid = p.chapters[0].paragraphs[0].id
    TextCleanup.apply([CleanupFix(paragraphID: pid, original: "99", replacement: "", issue: .pageNumber)], to: &p)
    #expect(p.chapters[0].paragraphs.count == 1)
    let fix = CleanupFix(paragraphID: pid, original: "tbe", replacement: "the")
    let preview = TextCleanup.preview(fix, in: "And tbe end.")
    #expect(preview.before == "And tbe end." && preview.after == "And the end.")
}

@Test func cleanupRefusesRewrites() {
    #expect(CleanupPrompt.isRepair("con- tinue", "continue", wholeParagraph: false))
    #expect(CleanupPrompt.isRepair("T H E  E N D", "THE END", wholeParagraph: false))
    #expect(CleanupPrompt.isRepair("night.12 She", "night. She", wholeParagraph: false))
    #expect(!CleanupPrompt.isRepair("He walked home.", "He strolled back to his house.", wholeParagraph: false))
    #expect(CleanupPrompt.isRepair("THE LONG ROAD 143", "", wholeParagraph: true))
    #expect(!CleanupPrompt.isRepair(String(repeating: "word ", count: 30), "", wholeParagraph: true))
}

// MARK: Chapter Recaps

@Test func recapJobsUseThePreviousStoryChapterAndKeepTheNarratorsOwn() {
    let story = Array(repeating: "Mara found the key under the stone and ran to the harbor before dawn.", count: 15)
    var p = make([story, ["Part Two"], story, story])
    let jobs = RecapPrompt.jobs(project: p)
    // Chapter 1 has nothing before it; "Part Two" is too short to recap from but gets a recap.
    #expect(jobs.map(\.chapterID) == [p.chapters[1].id, p.chapters[2].id, p.chapters[3].id])
    #expect(jobs[1].messages[1].content.contains("(Chapter 1)"))
    RecapPrompt.edit("Mara has the key.", chapterID: p.chapters[2].id, in: &p)
    #expect(p.chapters[2].recapEdited == true)
    #expect(!RecapPrompt.put("Something else.", chapterID: p.chapters[2].id, in: &p))
    #expect(p.chapters[2].recapText == "Mara has the key.")
    #expect(RecapPrompt.jobs(project: p).map(\.chapterID) == [p.chapters[1].id, p.chapters[3].id])
    #expect(RecapPrompt.jobs(project: p, chapters: [3]).count == 1)
    RecapPrompt.edit("  ", chapterID: p.chapters[2].id, in: &p)
    #expect(p.chapters[2].recap == nil && p.chapters[2].recapEdited == nil)
}

@Test func recapRepliesAreTidied() {
    #expect(RecapPrompt.parse(#"{"recap":"Previously… Mara found the key and fled."}"#) == "Mara found the key and fled.")
    #expect(RecapPrompt.parse("no json") == nil)
    let long = (0..<30).map { "Sentence \($0) has five words." }.joined(separator: " ")
    let parsed = RecapPrompt.parse(#"{"recap":"\#(long)"}"#) ?? ""
    #expect(Words.count(parsed) <= RecapPrompt.maximumWords && parsed.hasSuffix("."))
}

@Test func recapIsSpokenAfterTheChapterTitleOnlyWhenAsked() {
    let book = ImportedBook(title: "T", chapters: [
        Chapter(title: "One", paragraphs: [Paragraph(text: "One", style: .heading), Paragraph(text: "It began.")]),
        Chapter(title: "Two", paragraphs: [Paragraph(text: "Two", style: .heading), Paragraph(text: "It went on.")]),
    ])
    var p = ProjectBuilder.make(from: book, fileName: "t")
    p.chapters[1].recap = "It began. Then more."
    let index = BookIndex(p)
    let plain = SpeechScript.segments(chapter: 1, project: p, index: index, includeNarration: true)
    #expect(!plain.contains { $0.text.hasPrefix("Previously") })
    let with = SpeechScript.segments(chapter: 1, project: p, index: index, includeNarration: true, recaps: true)
    #expect(with.map(\.text).prefix(3) == ["Two", "Previously…", "It began."])
    #expect(with.last?.pause == .chapter)
    #expect(SpeechScript.segments(chapter: 0, project: p, index: index, includeNarration: true, recaps: true).count == 2)
}

// MARK: Delivery Hints

@Test func deliveryChunksMarkLinesAndProposalsSkipTheNarratorsHints() {
    var p = make([["“Not here,” Mara whispered. “Not now.”"]])
    let mara = BookCharacter(name: "Mara", colorHex: "#3366CC")
    p.characters = [mara]
    for i in p.lines.indices { p.lines[i].speakerID = mara.id; p.lines[i].tone = .whispered }
    p.lines[1].delivery = "pleading"
    p.lines[1].deliverySource = .user
    let index = BookIndex(p)
    let chunks = DeliveryPrompt.chunks(project: p, index: index)
    #expect(chunks.count == 1 && chunks[0].labels.count == 1)
    #expect(chunks[0].messages[1].content.contains("{L1 Mara, whispered}“Not here,”"))
    let replies = DeliveryPrompt.parse(#"{"lines":[{"line":"L1","hint":"[Barely Audible]"},{"line":"L2","hint":"shouts"}]}"#) ?? []
    let proposals = DeliveryPrompt.proposals(replies, chunk: chunks[0], project: p, index: index)
    #expect(proposals.map(\.hint) == ["barely audible"])
    DeliveryPrompt.apply(proposals + [DeliveryProposal(lineID: p.lines[1].id, hint: "shouts")], to: &p)
    #expect(p.lines[0].delivery == "barely audible" && p.lines[0].deliverySource == .ai)
    #expect(p.lines[1].delivery == "pleading")
}

@Test func deliveryHintsAreCleanedForElevenLabs() {
    #expect(DeliveryPrompt.clean("[whispers] {loud} (aside)") == "whispers loud aside")
    #expect(DeliveryPrompt.clean("  ") == nil)
    #expect(DeliveryPrompt.clean("one two three four five six seven") == "one two three four five six")
    #expect(DeliveryPrompt.audioTag(tone: .sad, delivery: "barely audible") == "[sad, barely audible]")
    #expect(DeliveryPrompt.audioTag(tone: .neutral, delivery: "laughing") == "[laughing]")
    #expect(DeliveryPrompt.audioTag(tone: nil, delivery: nil) == nil)
    var p = make([["“Run,” said Tom."]])
    DeliveryPrompt.set("through gritted teeth", for: [p.lines[0].id], in: &p)
    #expect(p.lines[0].deliverySource == .user)
    DeliveryPrompt.set("", for: [p.lines[0].id], in: &p)
    #expect(p.lines[0].delivery == nil && p.lines[0].deliverySource == nil)
}

@Test func deliveryRidesOnSpeechSegments() {
    var p = make([["“Run,” said Tom."]])
    p.lines[0].delivery = "out of breath"
    let segs = SpeechScript.segments(chapter: 0, project: p, index: BookIndex(p), includeNarration: true)
    #expect(segs.first { $0.lineID != nil }?.delivery == "out of breath")
    #expect(segs.first { $0.lineID == nil }?.delivery == nil)
}

// MARK: Suggest Spellings

@Test func spellingsSendOnlyTermsAndTheirSentenceAndNeverOverwrite() {
    var p = make([["The road was long. Siobhan waited by the gate of Dunmore."]])
    let pid = p.chapters[0].paragraphs[0].id
    p.pronunciations = [
        Pronunciation(term: "Siobhan", occurrences: 3, firstParagraphID: pid),
        Pronunciation(term: "Dunmore", sayItAs: "dun-MORE", occurrences: 1, firstParagraphID: pid),
        Pronunciation(term: "Gone", dismissed: true),
    ]
    let index = BookIndex(p)
    let chunks = SpellingPrompt.chunks(project: p, index: index)
    #expect(chunks.count == 1 && chunks[0].labels.count == 1)
    let user = chunks[0].messages[1].content
    #expect(user.contains("[T1] Siobhan — “Siobhan waited by the gate of Dunmore.”"))
    #expect(!user.contains("The road was long"))
    let proposals = SpellingPrompt.parse(#"{"terms":[{"id":"T1","sayItAs":"shiv-AWN","note":"Irish name","confidence":90},{"id":"T9","sayItAs":"x"}]}"#, chunk: chunks[0], project: p)
    #expect(proposals?.count == 1 && proposals?.first?.sayItAs == "shiv-AWN")
    #expect(SpellingPrompt.apply(proposals ?? [], to: &p) == 1)
    #expect(p.pronunciations[0].sayItAs == "shiv-AWN" && !p.pronunciations[0].confirmed)
    #expect(p.pronunciations[0].notes.contains("Claude"))
    // Filled in meanwhile: kept.
    #expect(SpellingPrompt.apply([SpellingProposal(pronunciationID: p.pronunciations[1].id, term: "Dunmore", sayItAs: "DUN-mor")], to: &p) == 0)
    #expect(p.pronunciations[1].sayItAs == "dun-MORE")
}

// MARK: Older files

@Test func projectsWithoutTheNewFieldsStillOpen() throws {
    let p = make([["“Hi,” said Tom."]])
    var json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(p)) as! [String: Any]
    var chapters = json["chapters"] as! [[String: Any]]
    chapters[0].removeValue(forKey: "recap")
    json["chapters"] = chapters
    let data = try JSONSerialization.data(withJSONObject: json)
    let back = try JSONDecoder().decode(Project.self, from: data)
    #expect(back.chapters[0].recap == nil && back.lines[0].delivery == nil)
}

// MARK: Remove Tones and Find

@Test func removeTonesClearsOnlyTheChosenKinds() {
    var p = make([["“Run!” she said.", "“Wait,” he said.", "“Fine.”"]])
    #expect(p.lines.count >= 3)
    p.lines[0].tone = .excited; p.lines[0].toneSource = .ai
    p.lines[1].tone = .sad; p.lines[1].toneSource = .user
    p.lines[2].tone = .angry; p.lines[2].toneSource = .rule
    p.tonesTagged = .now
    #expect(ToneRemoval.counts(in: p) == (automatic: 2, narrator: 1))
    var a = p
    #expect(ToneRemoval(automatic: true, narrator: false).apply(to: &a) == 2)
    #expect(a.lines[0].tone == nil && a.lines[2].tone == nil && a.lines[1].tone == .sad)
    #expect(a.tonesTagged == nil)
    var n = p
    #expect(ToneRemoval(automatic: false, narrator: true).apply(to: &n) == 1)
    #expect(n.lines[1].tone == nil && n.lines[0].tone == .excited && n.tonesTagged != nil)
    var both = p
    #expect(ToneRemoval(automatic: true, narrator: true).apply(to: &both) == 3)
    #expect(ToneRemoval.counts(in: both) == (automatic: 0, narrator: 0))
}

@Test func findMatchesEveryPlaceIgnoringCaseAndQuotes() {
    let p = make([["Ada’s cat sat.", "No cats here"], ["CAT!"]])
    let m = TextSearch.matches(of: "cat", in: p)
    #expect(m.count == 3)
    #expect(m.map(\.chapter) == [0, 0, 1])
    #expect(m[0].range == TextSpan(location: 6, length: 3))
    #expect(TextSearch.matches(of: "ada's", in: p).count == 1)
    #expect(TextSearch.matches(of: "  ", in: p).isEmpty)
}
