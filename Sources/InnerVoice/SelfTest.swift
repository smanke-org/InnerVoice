import AppKit
import InnerVoiceKit

/// Self-test for agents: runs the app's real functions on the sample book (never a real
/// project), prints PASS or FAIL per check, and quits with exit code 0 or 1.
///   "<app>/Contents/MacOS/InnerVoice" --selftest           (tools/selftest.sh)
///   APP_SELFTEST_MODEL=<model.gguf> … --selftest          also tags the sample with a real model and scores it
enum SelfTest {
    static var requested: Bool { CommandLine.arguments.contains("--selftest") }
    static var exitCode: Int32 = 0
    private static var failures = 0
    private static var count = 0

    static func runIfRequested() {
        guard requested else { return }
        setvbuf(stdout, nil, _IOLBF, 0)
        Task {
            try? await Task.sleep(for: .seconds(0.3))
            await run()
            print(failures == 0 ? "SELFTEST PASS \(count) checks" : "SELFTEST FAIL \(failures) of \(count) checks")
            exitCode = failures == 0 ? 0 : 1
            for doc in NSDocumentController.shared.documents { doc.updateChangeCount(.changeCleared); doc.close() }
            NSApp.terminate(nil)
        }
    }

    private static func check(_ name: String, _ ok: Bool, _ detail: String = "") {
        count += 1
        if !ok { failures += 1 }
        print(ok ? "PASS \(name)" : "FAIL \(name)\(detail.isEmpty ? "" : ": \(detail)")")
    }

    private static func run() async {
        let project = SeedData.project()
        let doc = InnerVoiceDocument(project: project)
        let undo = UndoManager()
        let index = doc.index
        func line(_ prefix: String) -> Line? { doc.project.lines.first { doc.project.text(of: $0, index: doc.index).hasPrefix(prefix) } }
        func name(_ l: Line?) -> String { doc.project.character(l?.speakerID)?.name ?? "nobody" }

        check("sample book has 3 chapters", project.chapters.count == 3, "\(project.chapters.map(\.title))")
        check("dialogue finder found the lines", project.lines.count >= 30, "\(project.lines.count)")
        check("characters were found", project.characters.count >= 4, project.characters.map(\.name).joined(separator: ", "))

        // Rules, before any AI.
        var fresh = ProjectBuilder.make(from: ImportedBook(title: "t", chapters: project.chapters.map {
            Chapter(title: $0.title, paragraphs: $0.paragraphs.map { Paragraph(text: $0.text, style: $0.style, italic: $0.italic) })
        }), fileName: "t.md")
        let freshIndex = BookIndex(fresh)
        func freshLine(_ p: String) -> Line? { fresh.lines.first { fresh.text(of: $0, index: freshIndex).hasPrefix(p) } }
        check("“said Tom Hale” assigns Tom by rule", fresh.character(freshLine("You're late")?.speakerID)?.name == "Tom Hale",
              fresh.character(freshLine("You're late")?.speakerID)?.name ?? "nobody")
        check("“Mara said” assigns Mara by rule", fresh.character(freshLine("The path was")?.speakerID)?.name.hasPrefix("Mara") == true)
        check("a quote across paragraphs continues", freshLine("She was his sister")?.continuesPrevious == true)
        check("italic text is a thought", line("He hasn't asked")?.kind == .thought)
        check("a block quote is written text", line("My dear girl")?.kind == .written)
        check("dash dialogue in a paragraph is still found", line("—Da says if")?.kind == .spoken)
        _ = RuleAttribution.apply(to: &fresh)

        // Assign, undo, redo.
        if let l = line("Slow down"), let pip = doc.project.characters.first(where: { $0.name.hasPrefix("Pip") }) {
            let before = l.speakerID
            doc.assign([l.id], to: pip.id, undoManager: undo)
            check("assigning a line changes its speaker", doc.project.lines.first { $0.id == l.id }?.speakerID == pip.id)
            check("an assigned line is confirmed", doc.project.lines.first { $0.id == l.id }?.source == .user)
            undo.undo()
            check("Undo puts the speaker back", doc.project.lines.first { $0.id == l.id }?.speakerID == before)
        }

        // Exchange.
        if let l = line("Then you'll be"), let mara = doc.project.characters.first(where: { $0.name.hasPrefix("Mara") }) {
            let prev = l.speakerID
            doc.assign([l.id], to: mara.id, undoManager: undo)
            let proposals = Exchange.proposals(in: doc.project, after: l.id, previousSpeaker: prev)
            check("fixing one line of a back-and-forth proposes the rest", !proposals.isEmpty, "\(proposals.count)")
            undo.undo()
        }

        // Merge.
        let extra = doc.addCharacter(named: "Thomas", undoManager: undo)
        if let tom = doc.project.characters.first(where: { $0.name == "Tom Hale" }), let l = line("Aye.") {
            doc.assign([l.id], to: extra, undoManager: undo)
            doc.merge(extra, into: tom.id, undoManager: undo)
            check("merge moves lines", doc.project.lines.first { $0.id == l.id }?.speakerID == tom.id)
            check("merge keeps the other name as an alias", doc.project.character(tom.id)?.aliases.contains("Thomas") == true)
            check("merge removes the merged character", doc.project.character(extra) == nil)
        }

        // Saving.
        if let data = try? InnerVoiceDocument.encoder.encode(doc.project),
           let back = try? InnerVoiceDocument.decoder.decode(Project.self, from: data) {
            check("a project saves and reads back the same", back == doc.project)
        } else {
            check("a project saves and reads back the same", false)
        }

        // Revised manuscript.
        var book = ImportedBook(title: "t", chapters: doc.project.chapters.map { c in
            Chapter(title: c.title, paragraphs: c.paragraphs.map { Paragraph(text: $0.text, style: $0.style, italic: $0.italic) })
        })
        book.chapters[1].paragraphs[1].text = book.chapters[1].paragraphs[1].text.replacingOccurrences(of: "avoiding me", with: "avoiding me for weeks")
        book.chapters[0].paragraphs.insert(Paragraph(text: "Rain hammered the glass."), at: 1)
        let removedText = book.chapters[2].paragraphs.remove(at: 3).text
        let result = RevisionMerger.merge(doc.project, with: book, fileName: "v2.docx")
        check("revision finds the changed paragraph", result.record.changed.count == 1, "\(result.record.changed.count)")
        check("revision finds the added paragraph", result.record.added.count == 1, "\(result.record.added.count)")
        check("revision finds the removed paragraph", result.record.removed.contains { $0.text == removedText })
        let keptIndex = BookIndex(result.project)
        let keptLate = result.project.lines.first { result.project.text(of: $0, index: keptIndex).hasPrefix("You're late") }
        check("revision keeps tags on unchanged paragraphs", keptLate?.speakerID == line("You're late")?.speakerID)
        let edited = result.project.lines.first { result.project.text(of: $0, index: keptIndex).hasPrefix("You have been avoiding") }
        check("revision keeps the speaker of an edited line", edited?.speakerID != nil && edited?.speakerID == line("You have been avoiding")?.speakerID)
        check("revision keeps recorded marks on unchanged paragraphs", !result.project.recorded.isEmpty)

        // Exports.
        let dir = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-selftest")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for (kind, format, ext) in [(Exporter.Kind.narratorScript, Exporter.Format.pdf, "pdf"), (.narratorScript, .word, "docx"),
                                    (.manuscript, .pdf, "pdf"), (.cast, .csv, "csv"), (.pronunciations, .pdf, "pdf")] {
            let url = dir.appending(path: "\(kind.rawValue).\(ext)")
            try? FileManager.default.removeItem(at: url)
            var o = Exporter.Options()
            o.kind = kind
            o.format = format
            o.narratorID = doc.project.narrators.first?.id
            do {
                try Exporter.write(o, document: doc, to: url)
                let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                check("export \(kind.title) as \(format.rawValue)", size > 500, "\(size) bytes at \(url.path)")
            } catch {
                check("export \(kind.title) as \(format.rawValue)", false, error.localizedDescription)
            }
        }

        // Model advice.
        check("8 GB Macs get a small model", ModelCatalog.recommendedID(memoryGB: 8) == "qwen3-4b")
        if let big = ModelCatalog.model(id: "gemma-4-31b") {
            check("a 31B model is too large for 16 GB", ModelCatalog.advice(for: big, memoryGB: 16).fit == .tooLarge)
            check("a 31B model is recommended for 64 GB", ModelCatalog.advice(for: big, memoryGB: 64).fit == .recommended)
        }
        check("revealed-late facts exist in the sample", {
            guard let tom = doc.project.characters.first(where: { $0.name == "Tom Hale" }),
                  let first = doc.index.stats[tom.id]?.firstLine else { return false }
            return tom.facts.contains { f in f.sourceParagraphID.flatMap { doc.index.positions[$0] }.map { $0.chapter > first.chapter } ?? false }
        }())
        _ = index

        if let path = ProcessInfo.processInfo.environment["APP_SELFTEST_MODEL"] {
            await liveModel(path: path)
        }
    }

    /// Tags the sample book with a real model and scores it against the right answers.
    private static func liveModel(path: String) async {
        var project = SeedData.project()
        for i in project.lines.indices where project.lines[i].source == .ai {
            project.lines[i].source = .none
            project.lines[i].speakerID = nil
            project.lines[i].confidence = 0
        }
        let doc = InnerVoiceDocument(project: project)
        let manager = ModelManager.shared
        manager.selection = path == "apple" ? "apple" : "file:" + path
        let start = Date()
        doc.tagging.tagSpeakers(undoManager: nil)
        while doc.tagging.isRunning { try? await Task.sleep(for: .milliseconds(200)) }
        let seconds = Date().timeIntervalSince(start)
        if let failure = doc.tagging.failure { check("live model tags the sample", false, failure); return }
        let p = doc.project
        let index = doc.index
        func id(_ n: String) -> UUID { p.characters.first { $0.name.hasPrefix(n) }?.id ?? UUID() }
        var right = 0, total = 0, wrong: [String] = []
        for l in p.lines where l.source == .ai {
            let text = p.text(of: l, index: index)
            guard let truth = SeedData.answer(for: text, tom: id("Tom"), mara: id("Mara"), edda: id("Edda"), pip: id("Pip")) else { continue }
            total += 1
            if l.speakerID == truth { right += 1 } else { wrong.append("“\(text.prefix(24))” → \(p.character(l.speakerID)?.name ?? "?")") }
        }
        let score = total == 0 ? 0 : Double(right) / Double(total)
        print("LIVE \(manager.selectionName): \(right)/\(total) lines right (\(Int(score * 100))%) in \(Int(seconds)) s")
        for w in wrong { print("  wrong: \(w)") }
        check("live model tags most sample lines right", score >= 0.6, "\(Int(score * 100))%")
        manager.unloadNow()
    }
}
