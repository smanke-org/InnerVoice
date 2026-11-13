import AppKit
import InnerVoiceKit

/// Snapshots of Claude's own features (Cloud): the run bar, each review list, the recap and
/// the delivery hint in the inspector, and Export Audio's recap switch. Nothing is sent: the
/// lists are filled with made-up proposals, and the book is put back afterward.
extension Snapshot {
    @MainActor
    static func claudeShots(document: InnerVoiceDocument, workspace: Workspace,
                            shot: @MainActor (String, @MainActor () -> Void) async -> Void) async {
        let saved = document.project
        let claude = document.claude
        let inspectorWas = workspace.showInspector

        await shot("claude-run") {
            workspace.page = .cast
            claude.debugStatus(.cleanup, 0.4, "Clean Up Text with Claude Sonnet 5.5 (4 of 11)…")
        }
        claude.debugClearStatus()
        await shot("claude-done") {
            claude.finished = "Fixed 14 import problems. Edit › Undo puts the text back."
        }
        claude.finished = nil

        // Plant a few import problems to review.
        var fixes: [CleanupFix] = []
        document.change("", undoManager: nil, textChanged: true) { p in
            var planted = 0
            for c in p.chapters.indices {
                for i in p.chapters[c].paragraphs.indices where planted < 3 && p.chapters[c].paragraphs[i].style == .body {
                    let para = p.chapters[c].paragraphs[i]
                    let ns = para.text as NSString
                    switch planted {
                    case 0:
                        let r = ns.range(of: " the ")
                        guard r.location != NSNotFound else { continue }
                        p.chapters[c].paragraphs[i].text = ns.replacingCharacters(in: r, with: " tbe ")
                        fixes.append(CleanupFix(paragraphID: para.id, original: " tbe ", replacement: " the ", issue: .ocr, note: "“tbe” for “the”"))
                    case 1:
                        let r = ns.range(of: ". ")
                        guard r.location != NSNotFound else { continue }
                        p.chapters[c].paragraphs[i].text = ns.replacingCharacters(in: r, with: ".12 ")
                        fixes.append(CleanupFix(paragraphID: para.id, original: ".12 ", replacement: ". ", issue: .footnote, note: "footnote number"))
                    default:
                        guard let word = para.text.split(separator: " ").map(String.init).first(where: { $0.count >= 8 && $0.allSatisfy(\.isLetter) }) else { continue }
                        let split = String(word.prefix(4)) + "- " + String(word.dropFirst(4))
                        p.chapters[c].paragraphs[i].text = para.text.replacingOccurrences(of: word, with: split)
                        fixes.append(CleanupFix(paragraphID: para.id, original: split, replacement: word, issue: .hyphenation, note: "broken at a line end"))
                    }
                    planted += 1
                }
            }
            if p.chapters.count > 1 {
                let header = Paragraph(text: "THE QUIET HARBOR 37")
                p.chapters[1].paragraphs.insert(header, at: min(2, p.chapters[1].paragraphs.count))
                fixes.append(CleanupFix(paragraphID: header.id, original: header.text, replacement: "", issue: .header, note: "running header and page number"))
            }
        }
        await shot("claude-cleanup") {
            workspace.page = .manuscript
            claude.pending = .init(proposals: .cleanup(fixes), model: "Claude Sonnet 5.5")
        }
        claude.pending = nil
        try? await Task.sleep(for: .seconds(0.5))
        document.change("", undoManager: nil, textChanged: true) { $0 = saved }

        let chapters = document.project.chapters
        let recapTexts = [
            "Tom found the letter in the lighthouse and kept it from Mara. The storm cut the island off from the mainland.",
            "Mara learned Tom had hidden the letter. They argued on the pier, and she set out for the old keeper's cottage alone.",
        ]
        await shot("claude-recaps") {
            let recaps = chapters.indices.dropFirst().prefix(2).enumerated().map { n, c in
                ClaudeFeatures.RecapProposal(chapterID: chapters[c].id, text: recapTexts[n], before: n == 1 ? "Mara and Tom argued." : nil)
            }
            claude.pending = .init(proposals: .recaps(recaps), model: "Claude Sonnet 5.5")
        }
        claude.pending = nil
        try? await Task.sleep(for: .seconds(0.5))

        let spoken = document.project.lines.filter { $0.speakerID != nil && $0.kind != .written }
        let hintWords = ["barely audible", "through gritted teeth", "laughing", "out of breath", "slowly, hesitant", "pleading"]
        await shot("claude-delivery") {
            let hints = spoken.prefix(6).enumerated().map { n, l in
                DeliveryProposal(lineID: l.id, hint: hintWords[n], before: n == 3 ? "breathless" : nil)
            }
            claude.pending = .init(proposals: .delivery(hints), model: "Claude Sonnet 5.5")
        }
        claude.pending = nil
        try? await Task.sleep(for: .seconds(0.5))

        await shot("claude-spellings") {
            let terms: [(String, String, String, Int)] = [
                ("Siobhan", "shiv-AWN", "Irish name", 95), ("Caoimhe", "KEE-va", "Irish name", 90),
                ("Ardnamurchan", "ard-na-MUR-khan", "Scottish place name", 80), ("Velthari", "vel-THAR-ee", "invented; follows the book's spelling", 45),
            ]
            let s = terms.map { SpellingProposal(pronunciationID: UUID(), term: $0.0, sayItAs: $0.1, note: $0.2, confidence: $0.3) }
            claude.pending = .init(proposals: .spellings(s), model: "Claude Sonnet 5.5")
        }
        claude.pending = nil
        try? await Task.sleep(for: .seconds(0.5))

        // The recap in the inspector, next to its chapter.
        if chapters.count > 1, let para = chapters[1].paragraphs.filter({ $0.style == .body }).dropFirst(5).first ?? chapters[1].paragraphs.last {
            document.change("", undoManager: nil) { p in
                p.chapters[1].recap = recapTexts[0]
                if p.chapters.count > 2 { p.chapters[2].recap = recapTexts[1]; p.chapters[2].recapEdited = true }
            }
            await shot("chapter-recap") {
                workspace.showInspector = true
                workspace.selectedLines = []
                workspace.show(paragraph: para.id)
                workspace.visibleChapter = 1
            }
            await shot("audio-export-recaps") { workspace.showAudioExport = true }
            workspace.showAudioExport = false
            try? await Task.sleep(for: .seconds(0.5))
        }
        if let l = spoken.first(where: { (document.index.positions[$0.paragraphID]?.chapter ?? 0) >= 1 }) ?? spoken.first {
            document.change("", undoManager: nil) { DeliveryPrompt.set("through gritted teeth", for: [l.id], in: &$0) }
            await shot("line-delivery") { workspace.showInspector = true; workspace.show(paragraph: l.paragraphID, line: l.id) }
            workspace.selectedLines = []
        }
        workspace.showInspector = inspectorWas
        document.change("", undoManager: nil, textChanged: true) { $0 = saved }
    }
}
