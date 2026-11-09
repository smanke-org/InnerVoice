import AppKit
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

/// Narrator Scripts, the tagged manuscript, the cast list and the pronunciation guide,
/// as PDF, Word or CSV.
enum Exporter {
    enum Kind: String, CaseIterable, Identifiable {
        case narratorScript, manuscript, cast, pronunciations
        var id: String { rawValue }
        var title: String {
            switch self {
            case .narratorScript: "Narrator Script"
            case .manuscript: "Tagged Manuscript"
            case .cast: "Cast List"
            case .pronunciations: "Pronunciation Guide"
            }
        }
        var detail: String {
            switch self {
            case .narratorScript: "One narrator's lines, each with the cue line before it, and their characters' summaries."
            case .manuscript: "The whole book with colors and initials, and a character key. For printing or reading on an iPad."
            case .cast: "Every character with their summary, voice notes, narrator and line counts."
            case .pronunciations: "Every term with how to say it and your notes."
            }
        }
        var formats: [Format] {
            switch self {
            case .narratorScript, .manuscript: [.pdf, .word]
            case .cast, .pronunciations: [.pdf, .word, .csv]
            }
        }
    }

    enum Format: String, CaseIterable, Identifiable {
        case pdf = "PDF", word = "Word", csv = "CSV"
        var id: String { rawValue }
        var type: UTType {
            switch self {
            case .pdf: .pdf
            case .word: UTType(filenameExtension: "docx") ?? .data
            case .csv: .commaSeparatedText
            }
        }
    }

    struct Options {
        var kind: Kind = .narratorScript
        var format: Format = .pdf
        /// nil: every character (no narrators set up yet).
        var narratorID: UUID?
        var includeCues = true
        var includeSummaries = true
    }

    // MARK: Building

    static let body = NSFont(name: "Georgia", size: 11) ?? NSFont.systemFont(ofSize: 11)
    static let small = NSFont.systemFont(ofSize: 8.5)
    static let h1 = NSFont.systemFont(ofSize: 20, weight: .bold)
    static let h2 = NSFont.systemFont(ofSize: 14, weight: .semibold)
    static let h3 = NSFont.systemFont(ofSize: 11, weight: .semibold)

    static func paragraph(spacing: CGFloat = 6, indent: CGFloat = 0) -> NSParagraphStyle {
        let s = NSMutableParagraphStyle()
        s.paragraphSpacing = spacing
        s.lineHeightMultiple = 1.15
        s.headIndent = indent
        s.firstLineHeadIndent = indent
        return s
    }

    static func text(_ s: String, _ font: NSFont, color: NSColor = .black, spacing: CGFloat = 6, indent: CGFloat = 0) -> NSAttributedString {
        NSAttributedString(string: s + "\n", attributes: [.font: font, .foregroundColor: color, .paragraphStyle: paragraph(spacing: spacing, indent: indent)])
    }

    static func build(_ o: Options, document: InnerVoiceDocument) -> NSAttributedString {
        let project = document.project
        let index = document.index
        let out = NSMutableAttributedString()
        let gray = NSColor(white: 0.4, alpha: 1)
        switch o.kind {
        case .narratorScript:
            let narrator = project.narrators.first { $0.id == o.narratorID }
            let cast = project.characters.filter { o.narratorID == nil || $0.narratorID == o.narratorID }
            let ids = Set(cast.map(\.id))
            out.append(text(project.title, h1, spacing: 4))
            out.append(text("Narrator Script" + (narrator.map { " · \($0.name)" } ?? ""), h2, color: gray, spacing: 16))
            if o.includeSummaries {
                for c in cast { out.append(characterBlock(c, document: document)) }
            }
            var lastChapter = -1
            for (i, line) in project.lines.enumerated() where line.speakerID.map(ids.contains) == true {
                let pos = index.positions[line.paragraphID]
                if let pos, pos.chapter != lastChapter {
                    lastChapter = pos.chapter
                    out.append(text(project.chapters[pos.chapter].title, h2, spacing: 8))
                }
                if o.includeCues, i > 0 {
                    let cue = project.lines[i - 1]
                    if cue.speakerID.map(ids.contains) != true {
                        let who = project.character(cue.speakerID)?.name ?? "?"
                        out.append(text("Cue · \(who): “\(project.text(of: cue, index: index))”", small, color: gray, spacing: 2, indent: 18))
                    }
                }
                let c = project.character(line.speakerID)
                let head = NSMutableAttributedString(string: "\(index.reference(line.paragraphID))  \(c?.name.uppercased() ?? "")\(line.kind == .spoken ? "" : " (\(line.kind.title.lowercased()))")\n",
                                                     attributes: [.font: h3, .foregroundColor: c.map { darker(NSColor(hexString: $0.colorHex)) } ?? .black,
                                                                  .paragraphStyle: paragraph(spacing: 1)])
                out.append(head)
                out.append(text("“\(project.text(of: line, index: index))”", body, spacing: 10))
            }
        case .manuscript:
            out.append(text(project.title, h1, spacing: 4))
            out.append(text("Character key", h2, color: gray, spacing: 6))
            for c in project.characters.sorted(by: { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }) {
                let line = NSMutableAttributedString(string: "  \(c.initials)  ", attributes: [.font: h3, .backgroundColor: NSColor(hexString: c.colorHex).withAlphaComponent(0.3)])
                line.append(NSAttributedString(string: "  \(c.name)\n", attributes: [.font: body, .paragraphStyle: paragraph(spacing: 2)]))
                out.append(line)
            }
            out.append(text("", body, spacing: 12))
            for chapter in project.chapters {
                out.append(text(chapter.title, h2, spacing: 10))
                for p in chapter.paragraphs {
                    let s = NSMutableAttributedString(string: p.text, attributes: [.font: body, .foregroundColor: NSColor.black, .paragraphStyle: paragraph(spacing: 7)])
                    var tags: [String] = []
                    for li in index.linesByParagraph[p.id] ?? [] {
                        let line = project.lines[li]
                        guard line.range.end <= s.length else { continue }
                        let needs = project.needsYou(line)
                        let color = needs ? NSColor.attention : NSColor(hexString: project.character(line.speakerID)?.colorHex ?? "#888888")
                        s.addAttribute(.backgroundColor, value: color.withAlphaComponent(0.28), range: line.range.ns)
                        if line.kind == .thought { s.addAttribute(.underlineStyle, value: NSUnderlineStyle.patternDash.rawValue | NSUnderlineStyle.single.rawValue, range: line.range.ns) }
                        let tag = needs ? "?" : (project.character(line.speakerID)?.initials ?? "?")
                        if !tags.contains(tag) { tags.append(tag) }
                    }
                    if !tags.isEmpty {
                        s.insert(NSAttributedString(string: "[\(tags.joined(separator: ", "))] ", attributes: [.font: small, .foregroundColor: gray]), at: 0)
                    }
                    s.append(NSAttributedString(string: "\n"))
                    out.append(s)
                }
            }
        case .cast:
            out.append(text(project.title, h1, spacing: 4))
            out.append(text("Cast List", h2, color: gray, spacing: 16))
            for c in project.characters.sorted(by: { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }) {
                out.append(characterBlock(c, document: document))
            }
        case .pronunciations:
            out.append(text(project.title, h1, spacing: 4))
            out.append(text("Pronunciation Guide", h2, color: gray, spacing: 16))
            for p in project.pronunciations.filter({ !$0.dismissed }).sorted(by: { $0.term.localizedCaseInsensitiveCompare($1.term) == .orderedAscending }) {
                let s = NSMutableAttributedString(string: p.term, attributes: [.font: h3, .paragraphStyle: paragraph(spacing: 1)])
                s.append(NSAttributedString(string: p.sayItAs.isEmpty ? "  —" : "  \(p.sayItAs)", attributes: [.font: body]))
                if p.confirmed { s.append(NSAttributedString(string: "  ✓ confirmed", attributes: [.font: small, .foregroundColor: gray])) }
                s.append(NSAttributedString(string: "\n"))
                out.append(s)
                if !p.notes.isEmpty { out.append(text(p.notes, small, color: gray, spacing: 8, indent: 12)) }
            }
        }
        return out
    }

    static func characterBlock(_ c: BookCharacter, document: InnerVoiceDocument) -> NSAttributedString {
        let out = NSMutableAttributedString()
        let stats = document.index.stats[c.id]
        let head = NSMutableAttributedString(string: "■ ", attributes: [.font: h2, .foregroundColor: NSColor(hexString: c.colorHex)])
        head.append(NSAttributedString(string: c.name + "\n", attributes: [.font: h2, .paragraphStyle: paragraph(spacing: 2)]))
        out.append(head)
        var meta = "\(stats?.lines ?? 0) lines · \(stats?.words ?? 0) words"
        if let first = stats?.firstLine { meta += " · first line Ch \(first.chapter + 1)" }
        if !c.aliases.isEmpty { meta += " · also \(c.aliases.joined(separator: ", "))" }
        if let n = document.project.narrators.first(where: { $0.id == c.narratorID }) { meta += " · read by \(n.name)" }
        out.append(text(meta, small, color: NSColor(white: 0.4, alpha: 1), spacing: 4))
        if !c.voiceNotes.isEmpty { out.append(text("Voice: " + c.voiceNotes, body, spacing: 4)) }
        for kind in FactKind.narratorOrder {
            let facts = c.facts.filter { $0.kind == kind }
            guard !facts.isEmpty else { continue }
            out.append(text("\(kind.title): " + facts.map(\.text).joined(separator: "; "), body, spacing: 2, indent: 0))
        }
        if !c.notes.isEmpty { out.append(text("Notes: " + c.notes, body, spacing: 2)) }
        out.append(text("", body, spacing: 10))
        return out
    }

    static func darker(_ c: NSColor) -> NSColor { c.blended(withFraction: 0.35, of: .black) ?? c }

    // MARK: Writing

    static func write(_ o: Options, document: InnerVoiceDocument, to url: URL) throws {
        switch o.format {
        case .csv:
            try csv(o, document: document).data(using: .utf8)!.write(to: url)
        case .word:
            let a = build(o, document: document)
            let data = try a.data(from: NSRange(location: 0, length: a.length), documentAttributes: [.documentType: NSAttributedString.DocumentType.officeOpenXML])
            try data.write(to: url)
        case .pdf:
            try pdf(build(o, document: document), to: url)
        }
    }

    /// Lays the text out on US Letter pages with TextKit and prints it to a PDF.
    static func pdf(_ text: NSAttributedString, to url: URL) throws {
        let info = NSPrintInfo()
        info.paperSize = NSSize(width: 612, height: 792)
        info.topMargin = 54
        info.bottomMargin = 54
        info.leftMargin = 60
        info.rightMargin = 60
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        info.jobDisposition = .save
        info.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = url
        let width = info.paperSize.width - info.leftMargin - info.rightMargin
        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 100))
        view.textStorage?.setAttributedString(text)
        view.isVerticallyResizable = true
        view.backgroundColor = .white
        view.drawsBackground = false
        view.sizeToFit()
        let op = NSPrintOperation(view: view, printInfo: info)
        op.showsPrintPanel = false
        op.showsProgressPanel = false
        guard op.run() else { throw CocoaError(.fileWriteUnknown) }
    }

    static func csv(_ o: Options, document: InnerVoiceDocument) -> String {
        func q(_ s: String) -> String { "\"" + s.replacingOccurrences(of: "\"", with: "\"\"") + "\"" }
        let project = document.project
        switch o.kind {
        case .pronunciations:
            var rows = ["Term,Say It As,Notes,Confirmed,Count"]
            for p in project.pronunciations where !p.dismissed {
                rows.append([q(p.term), q(p.sayItAs), q(p.notes), p.confirmed ? "yes" : "no", "\(p.occurrences)"].joined(separator: ","))
            }
            return rows.joined(separator: "\n")
        default:
            var rows = ["Name,Also Called,Narrator,Lines,Words,First Chapter," + FactKind.narratorOrder.map(\.title).joined(separator: ",") + ",Voice Notes,Notes"]
            for c in project.characters {
                let s = document.index.stats[c.id]
                let facts = FactKind.narratorOrder.map { k in q(c.facts.filter { $0.kind == k }.map(\.text).joined(separator: "; ")) }
                rows.append(([q(c.name), q(c.aliases.joined(separator: ", ")),
                              q(project.narrators.first { $0.id == c.narratorID }?.name ?? ""),
                              "\(s?.lines ?? 0)", "\(s?.words ?? 0)", s?.firstLine.map { "\($0.chapter + 1)" } ?? ""] + facts
                             + [q(c.voiceNotes), q(c.notes)]).joined(separator: ","))
            }
            return rows.joined(separator: "\n")
        }
    }
}

/// File › Export…
struct ExportSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    @Binding var isPresented: Bool
    @Environment(\.undoManager) private var undoManager
    @State private var options = Exporter.Options()
    @State private var newNarrator = ""
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Text("Export").font(.title2.weight(.semibold))
            Picker("Export", selection: $options.kind) {
                ForEach(Exporter.Kind.allCases.filter { $0 != .narratorScript || document.project.showsNarrationTools }) { Text($0.title).tag($0) }
            }
            .pickerStyle(.radioGroup)
            .onAppear {
                // Narrator Scripts belong to narration projects.
                if !document.project.showsNarrationTools, options.kind == .narratorScript { options.kind = .manuscript }
            }
            .labelsHidden()
            .onChange(of: options.kind) { if !options.kind.formats.contains(options.format) { options.format = .pdf } }
            Text(options.kind.detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Form {
                Picker("Format", selection: $options.format) {
                    ForEach(options.kind.formats) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                if options.kind == .narratorScript {
                    Picker("Narrator", selection: $options.narratorID) {
                        Text("Every Character").tag(UUID?.none)
                        ForEach(document.project.narrators) { Text($0.name).tag(UUID?.some($0.id)) }
                    }
                    Toggle("Include cue lines", isOn: $options.includeCues)
                    Toggle("Include character summaries", isOn: $options.includeSummaries)
                    narrators
                }
            }
            .formStyle(.grouped)
            if let error { Text(error).font(.callout).foregroundStyle(.orange) }
            HStack {
                HelpButton(topic: .narratorsAndScripts)
                Spacer()
                Button("Cancel") { isPresented = false }.keyboardShortcut(.cancelAction)
                Button("Export…") { export() }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
            }
        }
        .padding(Space.xl)
        .frame(width: 520)
        .noInitialFocus()
    }

    /// Narrators are set up here and on each character's page.
    private var narrators: some View {
        Section("Narrators") {
            ForEach(document.project.narrators) { n in
                HStack {
                    Text(n.name)
                    Spacer()
                    Text("\(document.project.characters.filter { $0.narratorID == n.id }.count) characters")
                        .foregroundStyle(.secondary)
                    Button { document.change("Remove Narrator", undoManager: undoManager) { p in
                        p.narrators.removeAll { $0.id == n.id }
                        for i in p.characters.indices where p.characters[i].narratorID == n.id { p.characters[i].narratorID = nil }
                    } } label: { Image(systemName: "minus.circle") }
                        .buttonStyle(.borderless)
                        .help("Remove \(n.name)")
                }
            }
            HStack {
                TextField("Add a narrator", text: $newNarrator).onSubmit(addNarrator)
                Button("Add", action: addNarrator).disabled(newNarrator.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            Text("Cast characters to a narrator on each character's page in Cast.").font(.caption).foregroundStyle(.secondary)
        }
    }

    private func addNarrator() {
        let name = newNarrator.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        document.change("Add Narrator", undoManager: undoManager) { $0.narrators.append(Narrator(name: name)) }
        newNarrator = ""
    }

    private func export() {
        let panel = NSSavePanel()
        let narrator = document.project.narrators.first { $0.id == options.narratorID }?.name
        panel.nameFieldStringValue = "\(document.project.title) - \(options.kind.title)\(narrator.map { " - \($0)" } ?? "")"
        panel.allowedContentTypes = [options.format.type]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try Exporter.write(options, document: document, to: url)
            isPresented = false
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } catch {
            self.error = "Unable to export: \(error.localizedDescription)"
        }
    }
}
