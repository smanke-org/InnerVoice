@preconcurrency import AVFoundation
import AppKit
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

/// EPUB with Audio: the book's text and its voices in one EPUB 3 file, with Media Overlays
/// so each sentence highlights as it's read in reading apps that play read-along books.
@MainActor
enum EpubExporter {
    /// Reads the picked chapters (with narration) and writes the book to `url`. Everything is
    /// made in a local temporary folder and moved into place at the end (cloud folders).
    static func write(project: Project, index: BookIndex, chapters picked: [Int], to url: URL, resolver: VoiceResolver,
                      progress: @escaping (Int, Int, String) -> Void) async throws {
        let staging = FileManager.default.temporaryDirectory.appending(path: "InnerVoice EPUB \(UUID().uuidString)")
        let audioFolder = staging.appending(path: "Audio")
        try FileManager.default.createDirectory(at: audioFolder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: staging) }

        let language = ReadAlongEpub.language(of: project)
        let chapters = Set(picked).sorted().filter { project.chapters.indices.contains($0) }
        var pages: [ReadAlongEpub.Page] = []
        var parts: [AudioExporter.Part] = []
        for (n, c) in chapters.enumerated() {
            let segments = SpeechScript.segments(chapter: c, project: project, index: index, includeNarration: true)
            pages.append(ReadAlongEpub.page(chapter: project.chapters[c], number: n + 1, segments: segments, language: language))
            parts.append(AudioExporter.Part(name: String(format: "c%03d", n + 1), segments: segments))
        }
        var timing: [[String: (begin: Double, end: Double)]] = Array(repeating: [:], count: parts.count)
        let titles = chapters.map { title(project, $0) }
        let urls = try await AudioExporter.write(parts, to: audioFolder, format: .m4a, resolver: resolver,
                                                 timing: { p, seg, begin, end in timing[p][seg.id] = (begin, end) }) { done, total, part in
            let what = Int(part.dropFirst()).map { titles.indices.contains($0 - 1) ? titles[$0 - 1] : part } ?? part
            progress(done, total, what)
        }
        let audioByName = Dictionary(urls.map { ($0.deletingPathExtension().lastPathComponent, $0) }, uniquingKeysWith: { a, _ in a })

        var files: [ReadAlongEpub.ChapterFile] = []
        for (n, page) in pages.enumerated() {
            let audio = audioByName[parts[n].name]
            let duration = audio.map(Self.duration) ?? 0
            let clips = audio == nil ? [] : ReadAlongEpub.clips(fragments: page.fragments, timing: timing[n], duration: duration)
            files.append(ReadAlongEpub.ChapterFile(title: titles[n], xhtml: page.xhtml, audio: clips.isEmpty ? nil : audio,
                                                   duration: duration, clips: clips))
        }
        let book = ReadAlongEpub.Book(title: project.title, author: project.author, language: language,
                                      identifier: ReadAlongEpub.identifier(title: project.title, author: project.author),
                                      chapters: files)
        progress(1, 1, "")
        let out = staging.appending(path: "book.epub")
        try await Task.detached(priority: .userInitiated) { try ReadAlongEpub.write(book, to: out) }.value
        if FileManager.default.fileExists(atPath: url.path) {
            _ = try FileManager.default.replaceItemAt(url, withItemAt: out)
        } else {
            try FileManager.default.moveItem(at: out, to: url)
        }
    }

    static func title(_ project: Project, _ c: Int) -> String {
        let t = project.chapters[c].title.trimmingCharacters(in: .whitespaces)
        return t.isEmpty ? (c == 0 ? "Opening" : "Untitled") : t
    }

    /// Seconds of audio in a file, as written.
    static func duration(_ url: URL) -> Double {
        guard let file = try? AVAudioFile(forReading: url) else { return 0 }
        return Double(file.length) / file.processingFormat.sampleRate
    }
}

struct EpubExportSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    var workspace: Workspace
    @Binding var isPresented: Bool

    @State private var anchor: Int?
    @State private var running: Task<Void, Never>?
    @State private var progress = 0.0
    @State private var status = ""
    @State private var error: String?

    /// The picked chapters, by index, in book order (shared with Export Audio).
    private var picked: [Int] {
        let all = document.project.chapters.indices
        guard let set = workspace.audioExportChapters else { return Array(all) }
        return all.filter { set.contains($0) }
    }

    private func pick(_ i: Int, _ on: Bool) {
        var set = Set(picked)
        // ⇧-click sets every chapter from the last one clicked to this one.
        let range = NSEvent.modifierFlags.contains(.shift) && anchor != nil ? Array(min(anchor!, i)...max(anchor!, i)) : [i]
        for c in range { if on { set.insert(c) } else { set.remove(c) } }
        anchor = i
        workspace.audioExportChapters = set.count == document.project.chapters.count ? nil : set
    }

    @ViewBuilder private var chapterList: some View {
        let set = Set(picked)
        let toggles = document.project.chapters.indices.map { i in Binding(get: { set.contains(i) }, set: { pick(i, $0) }) }
        HStack {
            Toggle(sources: toggles, isOn: \.self) { Text("Select All") }
                .toggleStyle(.checkbox)
            Spacer()
            Text(picked.count == toggles.count ? "All \(toggles.count.formatted()) chapters" : "\(picked.count.formatted()) of \(toggles.count.formatted()) chapters")
                .font(.callout)
                .foregroundStyle(.secondary)
                .monospacedDigit()
        }
        ScrollView {
            VStack(alignment: .leading, spacing: Space.xs) {
                ForEach(toggles.indices, id: \.self) { i in
                    Toggle(isOn: toggles[i]) {
                        HStack(spacing: Space.sm) {
                            Text("\(i + 1)").monospacedDigit().foregroundStyle(.secondary).frame(minWidth: 22, alignment: .trailing)
                            Text(EpubExporter.title(document.project, i)).lineLimit(1).truncationMode(.tail)
                        }
                    }
                    .toggleStyle(.checkbox)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(.vertical, Space.xs)
        }
        .frame(height: min(CGFloat(toggles.count) * 22 + 8, 200))
        .help("Click a chapter to leave it in or out. ⇧-click picks every chapter between it and the last one you clicked.")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Text("Export EPUB with Audio").font(.title2.weight(.semibold))
            Text("The book’s text with every line read in its character’s voice and tone, with \(document.project.engineInUse.title). In a reading app that plays read-along books, each sentence is highlighted as it’s read.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if document.project.settings.cloudVoicesAllowed != nil, document.project.engineInUse == .elevenlabs || document.project.engineInUse == .mixed {
                Label("ElevenLabs voices are made in the cloud: exporting sends the text they speak to ElevenLabs, and uses your plan’s credits.", systemImage: "icloud.and.arrow.up")
                    .font(.callout)
                    .foregroundStyle(.orange)
            }
            Form {
                Section("Chapters") { chapterList }
            }
            .formStyle(.grouped)
            .disabled(running != nil)
            if running != nil {
                VStack(alignment: .leading, spacing: Space.xs) {
                    ProgressView(value: progress)
                    Text(status).font(.caption).foregroundStyle(.secondary)
                }
            }
            if let error { Text(error).font(.callout).foregroundStyle(.orange) }
            HStack {
                HelpButton(topic: .voices)
                Spacer()
                if running != nil {
                    Button("Stop") { running?.cancel() }.keyboardShortcut(.cancelAction)
                } else {
                    Button("Cancel") { isPresented = false }.keyboardShortcut(.cancelAction)
                    Button("Export…") { export() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                        .disabled(picked.isEmpty)
                }
            }
        }
        .padding(Space.xl)
        .frame(width: 520)
        .noInitialFocus()
        .onAppear {
            // Chapters added or deleted since the last export: start again from every chapter.
            if let set = workspace.audioExportChapters, set.contains(where: { $0 >= document.project.chapters.count }) {
                workspace.audioExportChapters = nil
            }
            VoiceCastingRun.castIfNeeded(document: document)
        }
    }

    private func export() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.epub]
        panel.canCreateDirectories = true
        let title = document.project.title.isEmpty ? "Book" : document.project.title
        let which = picked.count == document.project.chapters.count ? "" : " - " + SpeechScript.chapterLabel(picked)
        panel.nameFieldStringValue = AudioExporter.safeName(title + which) + ".epub"
        panel.prompt = "Export"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let project = document.project
        let index = document.index
        let picked = self.picked
        let resolver = VoiceResolver(project: project, index: index)
        error = nil
        progress = 0
        running = Task {
            defer { running = nil }
            do {
                try await EpubExporter.write(project: project, index: index, chapters: picked, to: url, resolver: resolver) { done, total, what in
                    progress = total == 0 ? 1 : Double(done) / Double(total)
                    status = what.isEmpty ? "Putting the book together…" : "Reading \(what) (\(done.formatted()) of \(total.formatted()))…"
                }
                isPresented = false
                NSWorkspace.shared.activateFileViewerSelecting([url])
            } catch is CancellationError {
                status = "Stopped."
            } catch {
                if Task.isCancelled { status = "Stopped." } else { self.error = error.localizedDescription }
            }
        }
    }
}
