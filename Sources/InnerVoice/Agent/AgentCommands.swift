import AppKit
import InnerVoiceKit

/// Everything an agent can do in InnerVoice. Add a command here and the CLI, `help`, and the
/// MCP tools all get it. Each `run` is on the main actor and calls the same code as the window's
/// buttons and menus (InnerVoiceDocument's edits, TaggingController, Exporter, ModelManager), with
/// the window's own undo manager, so the window updates at once and Edit › Undo works.
enum AgentCommands {
    static var all: [AgentCommand] {
        var list = app
        list += lines
        list += characters
        list += ai
        list += pronunciations
        list += output
        list += models
        return list
    }

    // MARK: Status and projects

    private static var app: [AgentCommand] { [
        AgentCommand(name: "status", summary: "Show the version, the open projects (the frontmost first), the model and whether it is ready, and what the AI is doing in each project.",
                     example: "innervoice status --json", readOnly: true) { _ in
            let front = OpenProjects.front
            let open = OpenProjects.all.sorted { a, _ in a === front }
            var result: JSONObject = ["app": AppInfo.name, "version": AppInfo.version, "stage": AppInfo.stage ?? "release", "build": AppInfo.build,
                                      "projects": open.map { projectJSON($0, front: front) }, "model": modelSummary,
                                      "busy": TaggingController.busyReason ?? NSNull()]
            if AgentSocket.isTestRun { result["testRun"] = true }
            return result
        },
        AgentCommand(name: "open", summary: "Show a page in a project's window: manuscript, cast, voices, index, pronunciations, record, retakes, review or revisions. With --character, opens that character on the Cast page.",
                     params: [.init(name: "page", positional: true, help: "manuscript, cast, voices, index, pronunciations, record, retakes, review or revisions (default manuscript)"),
                              .init(name: "character", help: "open this character's page in Cast (a name, alias or id)"),
                              projectParam],
                     example: "innervoice open cast --character Mara --json") { args in
            let (entry, document) = try project(args)
            if let name = args.string("character") {
                let c = try character(name, in: document)
                entry.workspace.showCharacter(c.id)
                return ["project": entry.name, "page": "cast", "character": c.name]
            }
            let raw = (args.string("page") ?? "manuscript").lowercased()
            guard let page = Workspace.Page(rawValue: raw) else {
                throw AgentError.usage("no page \"\(raw)\". Pages: \(Workspace.Page.allCases.map(\.rawValue).joined(separator: ", "))")
            }
            entry.workspace.page = page
            return ["project": entry.name, "page": page.rawValue]
        },
        AgentCommand(name: "projects list", summary: "List the open projects, the frontmost first. Returns `projects` (id, name, title, author, path, front, edited, chapters, lines, linesNeedingYou, characters, tagging).",
                     example: "innervoice projects list --json", readOnly: true) { _ in
            let front = OpenProjects.front
            let open = OpenProjects.all.sorted { a, _ in a === front }
            return ["projects": open.map { projectJSON($0, front: front) }, "total": open.count]
        },
        AgentCommand(name: "projects open", summary: "Open a saved project (.innervoice) in its own window, as File › Open… does. If it is open already, returns it.",
                     params: [.init(name: "path", kind: .path, required: true, positional: true, help: "the .innervoice project")],
                     example: "innervoice projects open ~/Documents/Harbor.innervoice --json") { args in
            let url = URL(fileURLWithPath: try args.required("path"))
            guard url.pathExtension.lowercased() == "innervoice" else {
                throw AgentError.usage("\(url.lastPathComponent) isn't an InnerVoice project (.innervoice). To start one from a manuscript: innervoice projects new --manuscript \"\(url.path)\"")
            }
            guard FileManager.default.fileExists(atPath: url.path) else { throw AgentError.notFound("no project at \(url.path)") }
            try refuseRealFilesInTestRun(url)
            if let open = OpenProjects.all.first(where: { $0.fileURL?.standardizedFileURL == url.standardizedFileURL }) {
                return projectJSON(open, front: OpenProjects.front).merging(["opened": false]) { a, _ in a }
            }
            let before = Set(OpenProjects.all.map(\.id))
            do {
                let controller = NSDocumentController.shared
                if AgentSocket.isTestRun {
                    // Same as opening, but a test copy's /tmp project stays out of Open Recent,
                    // which the real app shares.
                    let document = try controller.makeDocument(withContentsOf: url, ofType: try controller.typeForContents(of: url))
                    controller.addDocument(document)
                    document.makeWindowControllers()
                    document.showWindows()
                } else {
                    _ = try await controller.openDocument(withContentsOf: url, display: true)
                }
            } catch {
                throw AgentError.invalid("couldn't open \(url.lastPathComponent): \(error.localizedDescription)")
            }
            let entry = try await AgentWait.newProject(after: before)
            return projectJSON(entry, front: OpenProjects.front).merging(["opened": true]) { a, _ in a }
        },
        AgentCommand(name: "projects new", summary: "Start a project from a manuscript (Word, PDF, RTF, text, Markdown, OpenDocument, Pages, EPUB or HTML), as File › New from Manuscript… does: finds every line and runs the plain rules. The window opens untitled; the person saves it with File › Save.",
                     params: [.init(name: "manuscript", kind: .path, required: true, help: "the manuscript file")],
                     example: "innervoice projects new --manuscript ~/Documents/Harbor.docx --json") { args in
            let url = URL(fileURLWithPath: try args.required("manuscript"))
            guard FileManager.default.fileExists(atPath: url.path) else { throw AgentError.notFound("no file at \(url.path)") }
            guard ManuscriptImporter.fileExtensions.contains(url.pathExtension.lowercased()) else {
                throw AgentError.usage("InnerVoice can't read .\(url.pathExtension) files. It reads \(ManuscriptImporter.fileExtensions.sorted().joined(separator: ", "))")
            }
            guard let newDocument = AppState.newDocument else {
                throw AgentError.unavailable("InnerVoice has no window open yet to start a project from. Try again in a second")
            }
            let (book, original): (Project, (name: String, data: Data)?)
            do { (book, original) = try await Importer.project(from: url) } catch {
                throw AgentError.invalid("couldn't read \(url.lastPathComponent): \(error.localizedDescription)")
            }
            let before = Set(OpenProjects.all.map(\.id))
            newDocument { InnerVoiceDocument(project: book, original: original) }
            let entry = try await AgentWait.newProject(after: before)
            return projectJSON(entry, front: OpenProjects.front)
        },
        AgentCommand(name: "projects save", summary: "Save a project, as File › Save does. With --as, save it to a new .innervoice file, as File › Save As… does (needed the first time an untitled project is saved).",
                     params: [.init(name: "as", kind: .path, help: "save to this .innervoice file instead"), projectParam],
                     example: "innervoice projects save --as ~/Documents/Harbor.innervoice --json") { args in
            let (entry, _) = try project(args)
            let controller = NSDocumentController.shared
            let untitled = controller.documents.filter { $0.fileURL == nil }
            guard let document = entry.nsDocument ?? (entry.fileURL == nil && untitled.count == 1 ? untitled[0] : nil) else {
                throw AgentError.invalid("can't tell which window holds \(entry.name). Save it once with File › Save")
            }
            let type = document.fileType ?? "com.smanke.innervoice.project"
            if let raw = args.string("as") {
                var url = URL(fileURLWithPath: (raw as NSString).expandingTildeInPath)
                if url.pathExtension.lowercased() != "innervoice" { url.appendPathExtension("innervoice") }
                try refuseRealFilesInTestRun(url.deletingLastPathComponent().appending(path: url.lastPathComponent))
                guard !FileManager.default.fileExists(atPath: url.path) || url.standardizedFileURL == document.fileURL?.standardizedFileURL else {
                    throw AgentError.invalid("\(url.path) already exists. Choose another name, or move that file first")
                }
                try await document.save(to: url, ofType: type, for: .saveAsOperation)
            } else {
                guard let url = document.fileURL else {
                    throw AgentError.usage("\(entry.name) has never been saved. Give it a file: innervoice projects save --as ~/Documents/\(entry.name).innervoice")
                }
                try refuseRealFilesInTestRun(url)
                try await document.save(to: url, ofType: type, for: .saveOperation)
            }
            return projectJSON(entry, front: OpenProjects.front).merging(["path": document.fileURL?.path ?? NSNull(), "saved": true]) { _, b in b }
        },
    ] + demo }

    /// Debug test copies only: the sample book, so there is a project to drive.
    private static var demo: [AgentCommand] {
        guard AgentSocket.isTestRun else { return [] }
        return [AgentCommand(name: "projects demo", summary: "Open the sample book in a new window (Debug test copies only).",
                             example: "APP_STORE=/tmp/iv-cli innervoice projects demo --json") { _ in
            projectJSON(try await AgentDemo.openSample(), front: OpenProjects.front)
        }]
    }

    // MARK: Chapters and lines

    private static var lines: [AgentCommand] { [
        AgentCommand(name: "chapters list", summary: "List the chapters in book order. Returns `chapters` (number, title, paragraphs, words, lines, linesNeedingYou).",
                     params: [projectParam],
                     example: "innervoice chapters list --json", readOnly: true) { args in
            let (entry, document) = try project(args)
            let p = document.project
            return ["project": entry.name, "total": p.chapters.count,
                    "chapters": p.chapters.enumerated().map { n, chapter -> JSONObject in
                        let ids = Set(chapter.paragraphs.map(\.id))
                        let inChapter = p.lines.filter { ids.contains($0.paragraphID) }
                        return ["number": n + 1, "title": chapter.title, "paragraphs": chapter.paragraphs.count,
                                "words": chapter.wordCount, "lines": inChapter.count,
                                "linesNeedingYou": inChapter.filter(p.needsYou).count]
                    }]
        },
        AgentCommand(name: "lines list", summary: "List lines in book order. Returns `lines` (id, ref like \"Ch 3 ¶12\", speaker, kind, source, confidence, needsYou, text, tone, toneSource, toneConfidence, toneNeedsYou), `total` and `nextOffset` when there are more.",
                     params: [.init(name: "chapter", kind: .integer, help: "only this chapter (from 1, see `innervoice chapters list`)"),
                              .init(name: "speaker", help: "only this character's lines (a name, alias or id), or \"none\" for Who's Speaking?"),
                              .init(name: "needs-you", kind: .boolean, help: "only red lines: Who's Speaking? and Unsure"),
                              .init(name: "search", help: "only lines whose text contains this"),
                              .init(name: "limit", kind: .integer, help: "at most this many (default 50)"),
                              .init(name: "offset", kind: .integer, help: "skip this many, for the next page"),
                              projectParam],
                     example: "innervoice lines list --chapter 2 --needs-you --json", readOnly: true) { args in
            let (entry, document) = try project(args)
            let p = document.project, index = document.index
            var picked = p.lines
            if args.values["chapter"] != nil {
                let n = try args.int("chapter", default: 1)
                guard p.chapters.indices.contains(n - 1) else { throw AgentError.notFound("no chapter \(n): this book has \(p.chapters.count). Run `innervoice chapters list`") }
                picked = picked.filter { index.positions[$0.paragraphID]?.chapter == n - 1 }
            }
            if let speaker = args.string("speaker") {
                if ["none", "nobody", "who's speaking?", "whos speaking"].contains(speaker.lowercased()) {
                    picked = picked.filter { $0.speakerID == nil }
                } else {
                    let c = try character(speaker, in: document)
                    picked = picked.filter { $0.speakerID == c.id }
                }
            }
            if try args.bool("needs-you") == true { picked = picked.filter(p.needsYou) }
            if let search = args.string("search"), !search.isEmpty {
                picked = picked.filter { p.text(of: $0, index: index).localizedCaseInsensitiveContains(search) }
            }
            return try page(picked, args, key: "lines", extra: ["project": entry.name]) { lineJSON($0, document) }
        },
        AgentCommand(name: "lines assign", summary: "Give lines to a character, as Assign To does in the manuscript (one Edit › Undo step). Or --clear them back to Who's Speaking?; --clear only reports what it would change until --yes.",
                     params: [idsParam,
                              .init(name: "to", help: "the character (a name, alias or id)"),
                              .init(name: "new", kind: .boolean, help: "make the character named by --to if there is none yet"),
                              .init(name: "clear", kind: .boolean, help: "make the lines Who's Speaking? again"),
                              projectParam],
                     example: "innervoice lines assign 3f2a9c1e 7b0d44e2 --to Mara --json", destructive: true,
                     yesHelp: "with --clear: do it. Assigning to a character needs no --yes") { args in
            let (entry, document) = try project(args)
            let picked = try findLines(args.list("ids"), in: document)
            let clear = try args.bool("clear") == true
            guard clear != (args.string("to") != nil) else {
                throw AgentError.usage("give --to <character> or --clear. Example: innervoice lines assign 3f2a9c1e --to Mara")
            }
            if clear {
                guard args.confirmed else {
                    return ["project": entry.name, "changed": false, "wouldClear": picked.count,
                            "lines": picked.map { lineJSON($0, document) },
                            "hint": "Nothing changed. Add --yes to make these lines Who's Speaking? again."]
                }
                document.assign(picked.map(\.id), to: nil, undoManager: entry.undoManager)
                return ["project": entry.name, "changed": true, "cleared": picked.count,
                        "lines": picked.compactMap { line($0.id, document) }.map { lineJSON($0, document) }]
            }
            let name = try args.required("to")
            var created = false
            let target: BookCharacter
            if try args.bool("new") == true, (try? character(name, in: document)) == nil {
                let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !trimmed.isEmpty else { throw AgentError.usage("--to can't be empty") }
                let id = document.addCharacter(named: trimmed, undoManager: entry.undoManager)
                target = document.project.character(id)!
                created = true
            } else {
                target = try character(name, in: document, hint: "or add --new to make \"\(name)\"")
            }
            document.assign(picked.map(\.id), to: target.id, undoManager: entry.undoManager)
            return ["project": entry.name, "changed": true, "character": target.name, "createdCharacter": created,
                    "lines": picked.compactMap { line($0.id, document) }.map { lineJSON($0, document) }]
        },
        AgentCommand(name: "lines kind", summary: "Change what kind of line these are: spoken, thought or written (one Edit › Undo step).",
                     params: [idsParam,
                              .init(name: "kind", help: "spoken, thought or written. May also come last: `lines kind 3f2a9c1e thought`"),
                              projectParam],
                     example: "innervoice lines kind 3f2a9c1e thought --json") { args in
            let (entry, document) = try project(args)
            var ids = args.list("ids")
            var raw = args.string("kind")
            if raw == nil, let last = ids.last, LineKind(rawValue: last.lowercased()) != nil { raw = ids.removeLast() }
            guard let raw, let kind = LineKind(rawValue: raw.lowercased()) else {
                let shown = raw ?? ids.last.flatMap { $0.allSatisfy(\.isHexDigit) ? nil : $0 }
                throw AgentError.usage((shown.map { "\"\($0)\" isn't a kind. " } ?? "") + "Give spoken, thought or written. Example: innervoice lines kind 3f2a9c1e thought")
            }
            let picked = try findLines(ids, in: document)
            let changing = picked.filter { $0.kind != kind }
            if !changing.isEmpty { document.setKind(kind, for: changing.map(\.id), undoManager: entry.undoManager) }
            return ["project": entry.name, "changed": !changing.isEmpty, "kind": kind.rawValue,
                    "lines": picked.compactMap { line($0.id, document) }.map { lineJSON($0, document) }]
        },
        AgentCommand(name: "lines tone", summary: "Set how lines are said, as Tone does in the line's menu (one Edit › Undo step): neutral, excited, confused, sad, angry, whispered, scared, tender or sarcastic. Tag Speakers never changes a tone set this way.",
                     params: [idsParam,
                              .init(name: "tone", help: "the tone. May also come last: `lines tone 3f2a9c1e angry`"),
                              projectParam],
                     example: "innervoice lines tone 3f2a9c1e angry --json") { args in
            let (entry, document) = try project(args)
            var ids = args.list("ids")
            var raw = args.string("tone")
            if raw == nil, let last = ids.last, Tone(rawValue: last.lowercased()) != nil { raw = ids.removeLast() }
            guard let raw, let tone = Tone(rawValue: raw.lowercased()) else {
                throw AgentError.usage("Give a tone: " + Tone.allCases.map(\.rawValue).joined(separator: ", ") + ". Example: innervoice lines tone 3f2a9c1e angry")
            }
            let picked = try findLines(ids, in: document)
            document.setTone(tone, for: picked.map(\.id), undoManager: entry.undoManager)
            return ["project": entry.name, "tone": tone.rawValue,
                    "lines": picked.compactMap { line($0.id, document) }.map { lineJSON($0, document) }]
        },
        AgentCommand(name: "lines reveal", summary: "Show a line in the manuscript, selected, as clicking it in the Index does.",
                     params: [.init(name: "id", required: true, positional: true, help: "the line's id (see `innervoice lines list`)"), projectParam],
                     example: "innervoice lines reveal 3f2a9c1e --json") { args in
            let (entry, document) = try project(args)
            guard let found = try findLines([try args.required("id")], in: document).first else { throw AgentError.notFound("no such line") }
            entry.workspace.show(paragraph: found.paragraphID, line: found.id)
            return ["project": entry.name, "line": lineJSON(found, document)]
        },
    ] }

    // MARK: Characters

    private static var characters: [AgentCommand] { [
        AgentCommand(name: "characters list", summary: "List the cast, most lines first. Returns `characters` (id, name, aliases, color, lines, words, firstLine, chapters, facts, narrator, showInIndex, voice: engine apple, pocket, orpheus or elevenlabs, name, source, confidence, needsYou).",
                     params: [projectParam],
                     example: "innervoice characters list --json", readOnly: true) { args in
            let (entry, document) = try project(args)
            let index = document.index
            let cast = document.project.characters.sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
            return ["project": entry.name, "total": cast.count, "linesNeedingYou": document.project.linesNeedingYou,
                    "characters": cast.map { characterJSON($0, document) }]
        },
        AgentCommand(name: "characters show", summary: "Show one character: summary facts with their sources (\"Ch 2 ¶4\") and Revealed Late flags, notes, voice notes, narrator, and line and word counts.",
                     params: [characterParam, projectParam],
                     example: "innervoice characters show Mara --json", readOnly: true) { args in
            let (entry, document) = try project(args)
            let c = try character(try args.required("name"), in: document)
            let index = document.index
            let first = index.stats[c.id]?.firstLine
            var json = characterJSON(c, document)
            let order = FactKind.narratorOrder
            json["project"] = entry.name
            json["facts"] = c.facts.sorted { (order.firstIndex(of: $0.kind) ?? 99) < (order.firstIndex(of: $1.kind) ?? 99) }.map { fact -> JSONObject in
                let source = fact.sourceParagraphID.flatMap { index.positions[$0] }
                let late = source.map { s in first.map { s.chapter > $0.chapter } ?? false } ?? false
                return ["id": short(fact.id), "kind": fact.kind.rawValue, "text": fact.text,
                        "source": fact.sourceParagraphID.map { index.reference($0) } ?? NSNull(),
                        "quote": fact.quote, "fromAI": fact.fromAI && !fact.edited, "revealedLate": late]
            }
            json["notes"] = c.notes
            json["voiceNotes"] = c.voiceNotes
            json["voiceCue"] = document.voiceCue(for: c)
            return json
        },
        AgentCommand(name: "characters merge", summary: "Make two characters one, as Merge does in Cast: every line and fact of FROM moves to TO, and FROM's names become TO's aliases. Only reports what it would change until --yes (Edit › Undo can take it back).",
                     params: [.init(name: "from", required: true, positional: true, help: "the character to merge away (a name, alias or id)"),
                              .init(name: "into", required: true, help: "the character to keep"),
                              projectParam],
                     example: "innervoice characters merge Mara --into \"Mara Quinn\" --yes --json", destructive: true) { args in
            let (entry, document) = try project(args)
            let from = try character(try args.required("from"), in: document)
            let into = try character(try args.required("into"), in: document)
            guard from.id != into.id else { throw AgentError.invalid("\(from.name) and \(into.name) are the same character") }
            let lines = document.index.stats[from.id]?.lines ?? 0
            let aliases = ([from.name] + from.aliases).filter { $0 != into.name && !into.aliases.contains($0) }
            guard args.confirmed else {
                return ["project": entry.name, "changed": false, "from": from.name, "into": into.name,
                        "linesToMove": lines, "factsToMove": from.facts.count, "aliasesToAdd": aliases,
                        "hint": "Nothing changed. Add --yes to merge \(from.name) into \(into.name)."]
            }
            document.merge(from.id, into: into.id, undoManager: entry.undoManager)
            let merged = document.project.character(into.id) ?? into
            return ["project": entry.name, "changed": true, "merged": from.name, "into": characterJSON(merged, document)]
        },
        AgentCommand(name: "characters rename", summary: "Rename a character, as editing the name on its Cast page does. To make two characters one, use `characters merge`.",
                     params: [characterParam,
                              .init(name: "to", required: true, help: "the new name"),
                              projectParam],
                     example: "innervoice characters rename Tom --to \"Tom Hale\" --json") { args in
            let (entry, document) = try project(args)
            let c = try character(try args.required("name"), in: document)
            let name = try args.required("to").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !name.isEmpty else { throw AgentError.usage("--to can't be empty") }
            if let other = document.project.characters.first(where: { $0.id != c.id && $0.name.caseInsensitiveCompare(name) == .orderedSame }) {
                throw AgentError.invalid("another character is already called \(other.name). To make them one: innervoice characters merge \"\(c.name)\" --into \"\(other.name)\"")
            }
            guard name != c.name else { return ["project": entry.name, "changed": false, "character": characterJSON(c, document)] }
            document.updateCharacter(c.id, "Rename Character", undoManager: entry.undoManager) { $0.name = name }
            return ["project": entry.name, "changed": true, "was": c.name,
                    "character": characterJSON(document.project.character(c.id) ?? c, document)]
        },
        AgentCommand(name: "characters color", summary: "Change a character's color in the manuscript. Takes auto (the default: blues and greens for men, pinks and purples for women, warm colors when the book doesn't say), #RRGGBB or a palette name (Blue, Pink, Amber…). Reddish colors are refused: red means Who's Speaking?",
                     params: [characterParam,
                              .init(name: "color", required: true, positional: true, help: "auto, #RRGGBB, or a palette name: \(CharacterPalette.swatches.map(\.name).joined(separator: ", "))"),
                              projectParam],
                     example: "innervoice characters color Mara \"#0EA5E9\" --json") { args in
            let (entry, document) = try project(args)
            let c = try character(try args.required("name"), in: document)
            let raw = try args.required("color").trimmingCharacters(in: .whitespaces)
            if ["auto", "automatic"].contains(raw.lowercased()) {
                let was = (c.colorHex, c.colorIsAutomatic)
                document.setColor(c.id, hex: nil, undoManager: entry.undoManager)
                let now = document.project.character(c.id) ?? c
                return ["project": entry.name, "changed": was != (now.colorHex, true), "character": c.name, "color": now.colorHex, "automatic": true]
            }
            let hex: String
            if let swatch = CharacterPalette.swatches.first(where: { $0.name.caseInsensitiveCompare(raw) == .orderedSame }) {
                hex = swatch.hex
            } else {
                let digits = raw.hasPrefix("#") ? String(raw.dropFirst()) : raw
                guard digits.count == 6, UInt32(digits, radix: 16) != nil else {
                    throw AgentError.usage("\"\(raw)\" isn't a color. Give #RRGGBB, like #0EA5E9, or a palette name: \(CharacterPalette.swatches.map(\.name).joined(separator: ", "))")
                }
                hex = "#" + digits.uppercased()
            }
            guard !CharacterPalette.isTooRed(hex) else {
                throw AgentError.invalid("\(hex) is too close to red, which InnerVoice keeps for Who's Speaking? and Unsure lines. Pick another color, like \(CharacterPalette.next(used: document.project.characters.map(\.colorHex)))")
            }
            let changed = c.colorHex.caseInsensitiveCompare(hex) != .orderedSame || c.colorChosen != true
            if changed { document.setColor(c.id, hex: hex, undoManager: entry.undoManager) }
            return ["project": entry.name, "changed": changed, "character": c.name, "color": hex, "automatic": false]
        },
        // Casting Style (docs/CASTING-STYLE.md)
        AgentCommand(name: "casting style", summary: "Show or change the book's Casting Style: one-voice (One Voice, or Just Me when narrating), shared (Narrator + Two Voices, or Three Narrators) or full-cast. Changing it keeps every voice and narrator. Returns `castingStyle` and who reads each character (`readers`).",
                     params: [.init(name: "style", positional: true, help: "one-voice, shared or full-cast (leave out to show the current one)"), projectParam],
                     example: "innervoice casting style shared --json") { args in
            let (entry, document) = try project(args)
            if let raw = args.string("style") {
                let styles: [String: CastingStyle] = ["one-voice": .oneVoice, "one": .oneVoice, "just-me": .oneVoice, "shared": .shared,
                                                      "three-narrators": .shared, "full-cast": .fullCast, "full": .fullCast]
                guard let style = styles[raw.lowercased()] else { throw AgentError.usage("\"\(raw)\" isn't a Casting Style. Give one-voice, shared or full-cast.") }
                document.setCastingStyle(style, undoManager: entry.undoManager)
            }
            let readers = CastingStyleRules.readers(document.project, index: document.index)
            let shades = document.project.showsVoices ? VoiceResolver(project: document.project, index: document.index).shades : document.narrationShades()
            return ["project": entry.name, "castingStyle": document.project.settings.castingStyle?.rawValue ?? NSNull(),
                    "readers": document.project.characters.filter { (document.index.stats[$0.id]?.lines ?? 0) > 0 }.map { c -> JSONObject in
                        ["name": c.name, "readBy": readers[c.id]?.rawValue ?? "own", "shade": shades[c.id]?.cue ?? NSNull()]
                    }]
        },
        AgentCommand(name: "characters gender", summary: "Set a character's gender: male, female, other, or auto (what the book says). Casting Style sorts by it; colors and casting follow it.",
                     params: [characterParam, .init(name: "gender", required: true, positional: true, help: "male, female, other or auto"), projectParam],
                     example: "innervoice characters gender Quinn other --json") { args in
            let (entry, document) = try project(args)
            let c = try character(try args.required("name"), in: document)
            let raw = try args.required("gender").lowercased()
            let g: CharacterGender? = raw == "auto" ? nil : try CharacterGender(rawValue: raw) ?? { throw AgentError.usage("Give male, female, other or auto.") }()
            document.setGender(g, for: [c.id], undoManager: entry.undoManager)
            return ["project": entry.name, "changed": c.gender != g, "character": c.name, "gender": g?.rawValue ?? "auto"]
        },
        AgentCommand(name: "characters main", summary: "Mark a character as a main character (yes), not main (no), or neither (auto). In Full Cast, main characters always have a voice of their own.",
                     params: [characterParam, .init(name: "main", required: true, positional: true, help: "yes, no or auto"), projectParam],
                     example: "innervoice characters main Mara yes --json") { args in
            let (entry, document) = try project(args)
            let c = try character(try args.required("name"), in: document)
            let raw = try args.required("main").lowercased()
            let main: Bool? = ["yes", "true"].contains(raw) ? true : ["no", "false"].contains(raw) ? false : raw == "auto" ? nil
                : try { throw AgentError.usage("Give yes, no or auto.") }()
            document.setMain(main, for: [c.id], undoManager: entry.undoManager)
            return ["project": entry.name, "changed": c.isMain != main, "character": c.name, "main": main.map { $0 as Any } ?? "auto"]
        },
        AgentCommand(name: "characters read-by", summary: "Choose who reads a character under the Casting Style: narrator, men, women, other, own (a voice or narrator of their own), or auto (the style decides).",
                     params: [characterParam, .init(name: "role", required: true, positional: true, help: "narrator, men, women, other, own or auto"), projectParam],
                     example: "innervoice characters read-by Kade own --json") { args in
            let (entry, document) = try project(args)
            let c = try character(try args.required("name"), in: document)
            let raw = try args.required("role").lowercased()
            let role: VoiceRole? = raw == "auto" ? nil : try VoiceRole(rawValue: raw) ?? { throw AgentError.usage("Give narrator, men, women, other, own or auto.") }()
            document.setRole(role, for: c.id, undoManager: entry.undoManager)
            let reader = CastingStyleRules.readers(document.project, index: document.index)[c.id]
            return ["project": entry.name, "changed": c.role != role, "character": c.name, "role": role?.rawValue ?? "auto", "readBy": reader?.rawValue ?? "own"]
        },
        AgentCommand(name: "characters shade", summary: "Set how the narrator shades a character: pitch (-4 to 4 semitones, lower to higher), pace (-2 to 2) and a few words, or --auto to go back to the shade worked out from gender, age and the voice brief.",
                     params: [characterParam,
                              .init(name: "pitch", kind: .integer, help: "-4 (much lower) to 4 (much higher)"),
                              .init(name: "pace", kind: .integer, help: "-2 (slower) to 2 (quicker)"),
                              .init(name: "words", help: "texture for the cue: gravelly, clipped…"),
                              .init(name: "auto", kind: .boolean, help: "use the automatic shade again"),
                              projectParam],
                     example: "innervoice characters shade Tom --pitch -2 --pace -1 --words gravelly --json") { args in
            let (entry, document) = try project(args)
            let c = try character(try args.required("name"), in: document)
            let shade: Shade? = try args.bool("auto") == true ? nil
                : Shade(pitch: try args.int("pitch", default: c.shade?.pitch ?? 0), pace: try args.int("pace", default: c.shade?.pace ?? 0),
                        words: args.string("words") ?? c.shade?.words)
            document.setShade(shade, for: c.id, undoManager: entry.undoManager)
            return ["project": entry.name, "character": c.name, "shade": shade.map { s -> JSONObject in ["pitch": s.pitch, "pace": s.pace, "words": s.words ?? NSNull(), "cue": s.cue ?? NSNull()] } ?? "auto"]
        },
    ] }

    // MARK: AI

    private static var ai: [AgentCommand] { [
        AgentCommand(name: "tag start", summary: "Start Tag Speakers: the on-device model decides who speaks every line the narrator and the rules haven't settled. Runs in the background; follow it with `innervoice tag status`. Lines the narrator assigned are never changed.",
                     params: [.init(name: "chapter", kind: .integer, help: "only this chapter (from 1)"), projectParam],
                     example: "innervoice tag start --json") { args in
            let (entry, document) = try project(args)
            try requireModel()
            guard !document.tagging.isRunning else { throw AgentError.busy("\(entry.name) is already \(jobWords(document)). Wait, or run `innervoice tag stop`") }
            var chapters: [Int]?
            if args.values["chapter"] != nil {
                let n = try args.int("chapter", default: 1)
                guard document.project.chapters.indices.contains(n - 1) else { throw AgentError.notFound("no chapter \(n): this book has \(document.project.chapters.count)") }
                chapters = [n - 1]
            }
            document.tagging.tagSpeakers(undoManager: entry.undoManager, chapters: chapters)
            return taggingJSON(entry, document)
        },
        AgentCommand(name: "tag stop", summary: "Stop Tag Speakers or Build Summaries. What the AI has done so far is kept.",
                     params: [projectParam],
                     example: "innervoice tag stop --json") { args in
            let (entry, document) = try project(args)
            let running = document.tagging.isRunning
            document.tagging.stop()
            return ["project": entry.name, "stopped": running]
        },
        AgentCommand(name: "tag status", summary: "Show what the AI is doing in a project: job, progress (0 to 1), message, the last run's result or failure, and how many lines still need you.",
                     params: [projectParam],
                     example: "innervoice tag status --json", readOnly: true) { args in
            let (entry, document) = try project(args)
            return taggingJSON(entry, document)
        },
        AgentCommand(name: "summaries start", summary: "Start Build Summaries: the model reads about each character (most lines first) and adds facts with their sources. Runs in the background; follow it with `innervoice tag status`.",
                     params: [.init(name: "character", help: "only this character (a name, alias or id)"), projectParam],
                     example: "innervoice summaries start --character Mara --json") { args in
            let (entry, document) = try project(args)
            try requireModel()
            guard !document.tagging.isRunning else { throw AgentError.busy("\(entry.name) is already \(jobWords(document)). Wait, or run `innervoice tag stop`") }
            let only = try args.string("character").map { [try character($0, in: document).id] }
            document.tagging.buildSummaries(undoManager: entry.undoManager, only: only)
            return taggingJSON(entry, document)
        },
    ] }

    // MARK: Pronunciations

    private static var pronunciations: [AgentCommand] { [
        AgentCommand(name: "pronunciations list", summary: "List the pronunciation terms. Returns `pronunciations` (id, term, sayItAs, notes, confirmed, occurrences, first, hasSample).",
                     params: [.init(name: "search", help: "only terms or sayings containing this"),
                              .init(name: "all", kind: .boolean, help: "include terms dismissed as needing no entry"),
                              .init(name: "limit", kind: .integer, help: "at most this many (default 50)"),
                              .init(name: "offset", kind: .integer, help: "skip this many, for the next page"),
                              projectParam],
                     example: "innervoice pronunciations list --json", readOnly: true) { args in
            let (entry, document) = try project(args)
            var terms = document.project.pronunciations
            if try args.bool("all") != true { terms = terms.filter { !$0.dismissed } }
            if let search = args.string("search"), !search.isEmpty {
                terms = terms.filter { $0.term.localizedCaseInsensitiveContains(search) || $0.sayItAs.localizedCaseInsensitiveContains(search) }
            }
            return try page(terms, args, key: "pronunciations", extra: ["project": entry.name]) { pronunciationJSON($0, document) }
        },
        AgentCommand(name: "pronunciations set", summary: "Say how to pronounce a term, as typing in the Pronunciations list does. Adds the term when it isn't there yet.",
                     params: [.init(name: "term", required: true, positional: true, help: "the term (or its id)"),
                              .init(name: "say-it-as", help: "how to say it, like SHEV-on"),
                              .init(name: "notes", help: "your notes"),
                              .init(name: "confirmed", kind: .boolean, help: "checked with the author or rights holder (true or false)"),
                              .init(name: "match-case", kind: .boolean, help: "only when the capitals match (true or false)"),
                              projectParam],
                     example: "innervoice pronunciations set Kerensa --say-it-as \"keh-REN-sah\" --json") { args in
            let (entry, document) = try project(args)
            let term = try args.required("term").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !term.isEmpty else { throw AgentError.usage("the term can't be empty") }
            let sayItAs = args.string("say-it-as"), notes = args.string("notes"), confirmed = try args.bool("confirmed"), matchCase = try args.bool("match-case")
            guard sayItAs != nil || notes != nil || confirmed != nil || matchCase != nil else {
                throw AgentError.usage("give --say-it-as, --notes, --confirmed or --match-case. Example: innervoice pronunciations set Kerensa --say-it-as \"keh-REN-sah\"")
            }
            let existing = document.project.pronunciations.first { $0.term.caseInsensitiveCompare(term) == .orderedSame }
                // An id only when it looks like one (8 characters), so a new term like "Bead" never hits an id.
                ?? (term.count == 8 ? uniquePrefix(term, in: document.project.pronunciations, id: \.id) : nil)
            func apply(_ p: inout Pronunciation) {
                if let sayItAs { p.sayItAs = sayItAs; p.dismissed = false }
                if let notes { p.notes = notes }
                if let confirmed { p.confirmed = confirmed }
                if let matchCase { p.matchCase = matchCase }
            }
            if let existing {
                document.change("Edit Pronunciation", undoManager: entry.undoManager) { p in
                    if let i = p.pronunciations.firstIndex(where: { $0.id == existing.id }) { apply(&p.pronunciations[i]) }
                }
                let now = document.project.pronunciations.first { $0.id == existing.id } ?? existing
                return ["project": entry.name, "added": false, "changed": now != existing, "pronunciation": pronunciationJSON(now, document)]
            }
            var new = Pronunciation(term: term, addedByUser: true)
            apply(&new)
            document.change("Add Term", undoManager: entry.undoManager) { $0.pronunciations.append(new) }
            return ["project": entry.name, "added": true, "changed": true, "pronunciation": pronunciationJSON(new, document)]
        },
    ] }

    // MARK: Export

    private static var output: [AgentCommand] { [
        AgentCommand(name: "export", summary: "Export a Narrator Script, the tagged manuscript, the cast list or the pronunciation guide, as File › Export… does, to a file.",
                     params: [.init(name: "kind", required: true, help: "narrator-script, manuscript, cast or pronunciations"),
                              .init(name: "format", help: "pdf (default) or word; cast and pronunciations also csv"),
                              .init(name: "out", kind: .path, required: true, help: "the file to write (.pdf, .docx or .csv is added when missing)"),
                              .init(name: "narrator", help: "narrator-script only: this narrator's characters (default every character)"),
                              .init(name: "no-cues", kind: .boolean, help: "narrator-script only: leave out the cue line before each line"),
                              .init(name: "no-summaries", kind: .boolean, help: "narrator-script only: leave out the character summaries"),
                              .init(name: "replace", kind: .boolean, help: "replace the file if it exists"),
                              projectParam],
                     example: "innervoice export --kind narrator-script --narrator Ana --format pdf --out ~/Desktop/Ana.pdf --json") { args in
            let (entry, document) = try project(args)
            let kinds: [String: Exporter.Kind] = ["narrator-script": .narratorScript, "manuscript": .manuscript, "cast": .cast, "pronunciations": .pronunciations]
            let rawKind = try args.required("kind").lowercased()
            guard let kind = kinds[rawKind] else { throw AgentError.usage("--kind must be narrator-script, manuscript, cast or pronunciations, not \"\(rawKind)\"") }
            let rawFormat = (args.string("format") ?? "pdf").lowercased()
            guard let format = Exporter.Format.allCases.first(where: { $0.rawValue.lowercased() == rawFormat || (rawFormat == "docx" && $0 == .word) }) else {
                throw AgentError.usage("--format must be pdf, word or csv, not \"\(rawFormat)\"")
            }
            guard kind.formats.contains(format) else {
                throw AgentError.usage("\(kind.title) exports as \(kind.formats.map { $0.rawValue.lowercased() }.joined(separator: " or ")), not \(rawFormat)")
            }
            var options = Exporter.Options(kind: kind, format: format)
            if let name = args.string("narrator") {
                guard kind == .narratorScript else { throw AgentError.usage("--narrator only goes with --kind narrator-script") }
                guard let narrator = document.project.narrators.first(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) else {
                    let names = document.project.narrators.map(\.name)
                    throw AgentError.notFound("no narrator named \(name). " + (names.isEmpty ? "This project has no narrators yet: the person sets them up in File › Export…" : "Narrators: \(names.joined(separator: ", "))"))
                }
                options.narratorID = narrator.id
            }
            options.includeCues = try args.bool("no-cues") != true
            options.includeSummaries = try args.bool("no-summaries") != true
            var url = URL(fileURLWithPath: try args.required("out"))
            let ext = format == .pdf ? "pdf" : format == .word ? "docx" : "csv"
            if url.pathExtension.lowercased() != ext { url = url.appendingPathExtension(ext) }
            var isFolder: ObjCBool = false
            guard FileManager.default.fileExists(atPath: url.deletingLastPathComponent().path, isDirectory: &isFolder), isFolder.boolValue else {
                throw AgentError.notFound("no folder \(url.deletingLastPathComponent().path)")
            }
            if FileManager.default.fileExists(atPath: url.path), try args.bool("replace") != true {
                throw AgentError.invalid("\(url.path) already exists. Add --replace to write over it, or choose another --out")
            }
            do { try Exporter.write(options, document: document, to: url) } catch {
                throw AgentError.invalid("couldn't export: \(error.localizedDescription)")
            }
            let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
            return ["project": entry.name, "kind": rawKind, "format": format.rawValue.lowercased(), "path": url.path, "bytes": size]
        },
    ] }

    // MARK: Model

    private static var models: [AgentCommand] { [
        AgentCommand(name: "model list", summary: "List the models InnerVoice can use, with advice for this Mac's memory. Returns `models` (id, name, size, quality and speed 1 to 5, fit, note, downloaded, selected, recommended, download) and `apple` (Apple's on-device model).",
                     example: "innervoice model list --json", readOnly: true) { _ in
            let manager = ModelManager.shared
            manager.refresh()
            let memory = ModelManager.memoryGB
            let apple = AppleTextModel.availability
            return ["memoryGB": memory, "selection": manager.selectionName, "ready": manager.isReady,
                    "models": ModelCatalog.all.map { m -> JSONObject in
                        let advice = ModelCatalog.advice(for: m, memoryGB: memory)
                        return ["id": m.id, "name": m.name, "size": m.sizeDescription, "quality": m.quality, "speed": m.speed,
                                "fit": advice.fit.title, "note": advice.note, "blurb": m.blurb,
                                "downloaded": manager.downloaded.contains(m.id), "selected": manager.selection == "catalog:" + m.id,
                                "recommended": m.id == manager.recommendedID, "download": downloadJSON(m)]
                    },
                    "apple": ["id": "apple", "available": apple.ok, "why": apple.ok ? NSNull() as Any : apple.why as Any,
                              "selected": manager.selection == "apple"]]
        },
        AgentCommand(name: "model select", summary: "Choose the model Tag Speakers and Build Summaries use, as Use This Model does: a downloaded model's id, \"apple\" for Apple's on-device model, or the path to your own .gguf file.",
                     params: [.init(name: "id", required: true, positional: true, help: "a model id from `innervoice model list`, apple, or a .gguf path")],
                     example: "innervoice model select gemma-4-e4b --json") { args in
            let manager = ModelManager.shared
            manager.refresh()
            let raw = try args.required("id")
            let selection: String
            if raw.lowercased() == "apple" {
                let a = AppleTextModel.availability
                guard a.ok else { throw AgentError.invalid("Apple's on-device model can't be used: \(a.why)") }
                selection = "apple"
            } else if raw.lowercased().hasSuffix(".gguf") {
                let path = (raw as NSString).expandingTildeInPath
                guard path.hasPrefix("/"), FileManager.default.fileExists(atPath: path) else { throw AgentError.notFound("no model file at \(raw) (give the full path)") }
                selection = "file:" + path
            } else {
                guard let m = ModelCatalog.model(id: raw.lowercased()) else {
                    throw AgentError.notFound("no model \(raw). Run `innervoice model list`")
                }
                guard manager.downloaded.contains(m.id) else {
                    throw AgentError.invalid("\(m.name) isn't downloaded yet (\(m.sizeDescription)). Run `innervoice model download \(m.id)`, then select it")
                }
                selection = "catalog:" + m.id
            }
            guard TaggingController.busyReason == nil else { throw AgentError.busy("InnerVoice is \(TaggingController.busyReason!). Stop it first with `innervoice tag stop`") }
            let changed = manager.selection != selection
            if changed { manager.selection = selection }
            return ["changed": changed, "selection": manager.selectionName, "ready": manager.isReady]
        },
        AgentCommand(name: "model download", summary: "Download a model from the list (2.5 to 19 GB from Hugging Face; the only network use besides updates). Only reports the size until --yes. Follow it with `innervoice model list`; when it finishes it becomes the model if none was ready.",
                     params: [.init(name: "id", required: true, positional: true, help: "a model id from `innervoice model list`")],
                     example: "innervoice model download gemma-4-e4b --yes --json", destructive: true,
                     yesHelp: "start the download. Without --yes it only shows the size and the fit for this Mac") { args in
            let manager = ModelManager.shared
            manager.refresh()
            let raw = try args.required("id").lowercased()
            guard let m = ModelCatalog.model(id: raw) else { throw AgentError.notFound("no model \(raw). Run `innervoice model list`") }
            let advice = ModelCatalog.advice(for: m, memoryGB: ModelManager.memoryGB)
            if manager.downloaded.contains(m.id) { return ["id": m.id, "changed": false, "downloaded": true] }
            guard args.confirmed else {
                return ["id": m.id, "changed": false, "size": m.sizeDescription, "fit": advice.fit.title, "note": advice.note,
                        "hint": "Nothing downloaded. Add --yes to download \(m.sizeDescription)."]
            }
            manager.download(m)
            return ["id": m.id, "changed": true, "size": m.sizeDescription, "download": downloadJSON(m)]
        },
    ] }

    // MARK: Params

    private static let projectParam = AgentCommand.Param(name: "project", help: "the project: its name, path or id from `innervoice projects list` (default: the frontmost)")
    private static let characterParam = AgentCommand.Param(name: "name", required: true, positional: true, help: "the character: a name, alias or id")
    private static let idsParam = AgentCommand.Param(name: "ids", required: true, positional: true, multiple: true, help: "line ids from `innervoice lines list`")

    // MARK: Finding things

    static func short(_ id: UUID) -> String { String(id.uuidString.prefix(8)).lowercased() }

    /// The project named by --project, or the frontmost one.
    static func project(_ args: Args) throws -> (OpenProjects.Entry, InnerVoiceDocument) {
        let open = OpenProjects.all
        guard !open.isEmpty else { throw AgentError.noProject }
        let entry: OpenProjects.Entry
        if let raw = args.string("project"), !raw.isEmpty {
            let path = URL(fileURLWithPath: (raw as NSString).expandingTildeInPath).standardizedFileURL
            // By path, then file name, then the book's title, then id.
            let tests: [(OpenProjects.Entry) -> Bool] = [
                { $0.fileURL?.standardizedFileURL == path },
                { $0.name.caseInsensitiveCompare(raw) == .orderedSame },
                { ($0.document?.project.title ?? "").caseInsensitiveCompare(raw) == .orderedSame },
                { raw.count >= 4 && short($0.id).hasPrefix(raw.lowercased()) },
            ]
            let matches = tests.lazy.map { open.filter($0) }.first { !$0.isEmpty } ?? []
            guard let first = matches.first else {
                throw AgentError.notFound("no open project \"\(raw)\". Open: \(open.map(\.name).joined(separator: ", ")). Run `innervoice projects list`")
            }
            guard matches.count == 1 else {
                throw AgentError.usage("\"\(raw)\" matches \(matches.count) open projects. Use the id from `innervoice projects list`")
            }
            entry = first
        } else {
            guard let front = OpenProjects.front else { throw AgentError.noProject }
            entry = front
        }
        guard let document = entry.document else { throw AgentError.noProject }
        guard !document.project.isEmpty else {
            throw AgentError.invalid("\(entry.name) has no manuscript yet. Start a project with `innervoice projects new --manuscript <file>`")
        }
        return (entry, document)
    }

    /// A character by name, alias, id, or the unique start of a name.
    private static func character(_ raw: String, in document: InnerVoiceDocument, hint: String? = nil) throws -> BookCharacter {
        let cast = document.project.characters
        let key = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        func same(_ a: String) -> Bool { a.caseInsensitiveCompare(key) == .orderedSame }
        if let c = cast.first(where: { same($0.name) }) { return c }
        let byAlias = cast.filter { $0.aliases.contains(where: same) }
        if byAlias.count == 1 { return byAlias[0] }
        if key.count >= 4, let c = uniquePrefix(key, in: cast, id: \.id) { return c }
        let started = cast.filter { $0.name.lowercased().hasPrefix(key.lowercased()) }
        if started.count == 1 { return started[0] }
        if started.count > 1 || byAlias.count > 1 {
            let names = (started.isEmpty ? byAlias : started).map(\.name)
            throw AgentError.usage("\"\(raw)\" could be \(names.joined(separator: " or ")). Give the full name")
        }
        throw AgentError.notFound("no character \"\(raw)\"\(hint.map { ", \($0)" } ?? ""). Run `innervoice characters list`")
    }

    /// The one item whose id starts with `raw` (8 characters is what lists show; 4 or more works).
    private static func uniquePrefix<T>(_ raw: String, in items: [T], id: KeyPath<T, UUID>) -> T? {
        let key = raw.lowercased().replacingOccurrences(of: "-", with: "")
        guard key.count >= 4, key.allSatisfy(\.isHexDigit) else { return nil }
        let found = items.filter { $0[keyPath: id].uuidString.lowercased().replacingOccurrences(of: "-", with: "").hasPrefix(key) }
        return found.count == 1 ? found[0] : nil
    }

    private static func line(_ id: UUID, _ document: InnerVoiceDocument) -> Line? {
        document.index.lineIndexByID[id].map { document.project.lines[$0] }
    }

    private static func findLines(_ ids: [String], in document: InnerVoiceDocument) throws -> [Line] {
        guard !ids.isEmpty else { throw AgentError.usage("give at least one line id. Run `innervoice lines list` to find them") }
        return try ids.map { raw in
            guard let found = uniquePrefix(raw, in: document.project.lines, id: \.id) else {
                throw AgentError.notFound("no line with id \(raw). Run `innervoice lines list`")
            }
            return found
        }
    }

    /// In a Debug test copy, only files in /tmp or the temporary folder: never a real project.
    private static func refuseRealFilesInTestRun(_ url: URL) throws {
        guard AgentSocket.isTestRun else { return }
        let path = url.resolvingSymlinksInPath().path
        let temporary = ["/tmp/", "/private/tmp/", "/private/var/folders/", FileManager.default.temporaryDirectory.resolvingSymlinksInPath().path]
        guard temporary.contains(where: { path.hasPrefix($0) }) else {
            throw AgentError.invalid("a test copy (APP_STORE) only opens projects in /tmp, never real ones. Copy it to /tmp first")
        }
    }

    // MARK: Results

    private static func page<T>(_ items: [T], _ args: Args, key: String, extra: JSONObject, _ json: (T) -> JSONObject) throws -> JSONObject {
        let limit = max(1, try args.int("limit", default: 50))
        let offset = max(0, try args.int("offset", default: 0))
        let slice = items.dropFirst(offset).prefix(limit)
        var result = extra
        result[key] = slice.map(json)
        result["total"] = items.count
        if offset + slice.count < items.count { result["nextOffset"] = offset + slice.count }
        return result
    }

    private static func projectJSON(_ entry: OpenProjects.Entry, front: OpenProjects.Entry?) -> JSONObject {
        guard let document = entry.document else { return ["id": short(entry.id), "name": entry.name] }
        let p = document.project
        return ["id": short(entry.id), "name": entry.name, "title": p.title, "author": p.author,
                "path": entry.fileURL?.path ?? NSNull(), "saved": entry.fileURL != nil,
                "edited": entry.nsDocument?.isDocumentEdited ?? (entry.fileURL == nil), "front": entry === front,
                "manuscript": p.sourceFileName, "chapters": p.chapters.count, "words": p.wordCount, "lines": p.lines.count,
                "linesNeedingYou": p.linesNeedingYou, "untagged": p.untaggedCount, "characters": p.characters.count,
                "page": entry.workspace.page.rawValue, "tagging": jobJSON(document),
                // What Edit › Undo in that window would take back, so an agent can tell the person.
                "undo": entry.undoManager.flatMap { $0.canUndo ? $0.undoActionName : nil } ?? NSNull()]
    }

    private static func lineJSON(_ line: Line, _ document: InnerVoiceDocument) -> JSONObject {
        let p = document.project
        return ["id": short(line.id), "ref": document.index.reference(line.paragraphID),
                "speaker": p.character(line.speakerID)?.name ?? NSNull(), "kind": line.kind.rawValue,
                "source": line.source.rawValue, "confidence": (line.confidence * 100).rounded() / 100,
                "needsYou": p.needsYou(line), "text": p.text(of: line, index: document.index),
                "tone": line.tone?.rawValue ?? NSNull(), "toneSource": line.toneSource?.rawValue ?? NSNull(),
                "toneConfidence": line.toneConfidence.map { ($0 * 100).rounded() / 100 } ?? NSNull(), "toneNeedsYou": p.toneNeedsYou(line)]
    }

    private static func characterJSON(_ c: BookCharacter, _ document: InnerVoiceDocument) -> JSONObject {
        let index = document.index
        let stats = index.stats[c.id]
        return ["id": short(c.id), "name": c.name, "aliases": c.aliases, "color": c.colorHex, "colorAutomatic": c.colorIsAutomatic,
                "lines": stats?.lines ?? 0, "words": stats?.words ?? 0,
                "firstLine": stats?.firstLine.map { "Ch \($0.chapter + 1) ¶\($0.paragraph + 1)" } ?? NSNull(),
                "chapters": stats?.chapters.count ?? 0, "facts": c.facts.count,
                "narrator": document.effectiveNarrator(for: c)?.name ?? NSNull(),
                "gender": c.gender?.rawValue ?? "auto", "main": c.isMain.map { $0 as Any } ?? "auto",
                "showInIndex": index.showsInIndex(c),
                "voice": c.voice(for: document.project.engineInUse).map { v -> JSONObject in
                    ["engine": v.choice.engine.rawValue, "name": VoiceCatalog.name(of: v.choice), "source": v.source.rawValue,
                     "confidence": (v.confidence * 100).rounded() / 100, "needsYou": document.project.voiceNeedsYou(c, engine: document.project.engineInUse)]
                } ?? NSNull()]
    }

    private static func pronunciationJSON(_ p: Pronunciation, _ document: InnerVoiceDocument) -> JSONObject {
        ["id": short(p.id), "term": p.term, "sayItAs": p.sayItAs, "notes": p.notes, "confirmed": p.confirmed, "matchCase": p.matchCase,
         "occurrences": p.occurrences, "first": p.firstParagraphID.map { document.index.reference($0) } ?? NSNull(),
         "hasSample": p.audioFile != nil, "dismissed": p.dismissed]
    }

    private static func jobJSON(_ document: InnerVoiceDocument) -> Any {
        guard let s = document.tagging.status else { return NSNull() }
        return ["job": s.job == .tagging ? "tag speakers" : "build summaries", "progress": (s.progress * 100).rounded() / 100, "message": s.message]
    }

    private static func jobWords(_ document: InnerVoiceDocument) -> String {
        document.tagging.status?.job == .summaries ? "building summaries" : "tagging speakers"
    }

    private static func taggingJSON(_ entry: OpenProjects.Entry, _ document: InnerVoiceDocument) -> JSONObject {
        let p = document.project
        return ["project": entry.name, "running": document.tagging.isRunning, "job": jobJSON(document),
                "finished": document.tagging.finished ?? NSNull(), "failure": document.tagging.failure ?? NSNull(),
                "lines": p.lines.count, "linesNeedingYou": p.linesNeedingYou, "untagged": p.untaggedCount,
                "lastTagged": p.lastTagged.map(JSON.date) ?? NSNull(), "summariesBuilt": p.summariesBuilt.map(JSON.date) ?? NSNull(),
                "model": ModelManager.shared.selectionName]
    }

    private static var modelSummary: JSONObject {
        let m = ModelManager.shared
        let load: String = switch m.loadState {
        case .none: "not loaded"
        case .loading: "loading"
        case .ready: "loaded"
        case .failed(let why): "failed: \(why)"
        }
        return ["selection": m.selectionName, "id": m.catalogModel?.id ?? (m.selection == "apple" ? "apple" : NSNull()),
                "ready": m.isReady, "state": load, "recommended": m.recommendedID, "memoryGB": ModelManager.memoryGB]
    }

    private static func downloadJSON(_ m: CatalogModel) -> Any {
        switch ModelManager.shared.downloads[m.id] {
        case .downloading(let progress, _): ["state": "downloading", "progress": (progress * 100).rounded() / 100]
        case .verifying: ["state": "verifying"]
        case .failed(let why): ["state": "failed", "message": why]
        case nil: NSNull()
        }
    }

    private static func requireModel() throws {
        guard ModelManager.shared.isReady else {
            throw AgentError.noModel("no model is ready (\(ModelManager.shared.selectionName)). Run `innervoice model list`, then `innervoice model select <id>` with a downloaded one, or `innervoice model download <id>`")
        }
    }
}
