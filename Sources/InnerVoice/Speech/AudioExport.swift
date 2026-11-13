@preconcurrency import AVFoundation
import AppKit
import InnerVoiceKit
import SwiftUI

/// Writes the voices to audio files: the whole book, a chapter or one character's lines,
/// as M4A or WAV, one file per chapter or all in one.
@MainActor
enum AudioExporter {
    enum Format: String, CaseIterable, Identifiable {
        case m4a = "M4A", wav = "WAV"
        var id: String { rawValue }
        var fileExtension: String { rawValue.lowercased() }

        var settings: [String: Any] {
            switch self {
            case .m4a: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 44_100, AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: 128_000]
            case .wav: [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 44_100, AVNumberOfChannelsKey: 1,
                        AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false, AVLinearPCMIsBigEndianKey: false]
            }
        }
    }

    /// One file to write: its name and what goes in it.
    struct Part {
        var name: String
        var segments: [SpeechSegment]
    }

    /// Renders and writes each part. `progress` hears (done, total, what) after each segment.
    /// With `stem`, each part is written Separate: one file per voice (named by `stem`, such
    /// as "Narrator" or "Men’s Voice"), all the same length, with silence wherever another
    /// voice speaks, so they line up when mixed.
    static func write(_ parts: [Part], to folder: URL, format: Format, resolver: VoiceResolver,
                      stem: ((SpeechSegment) -> String)? = nil,
                      progress: (Int, Int, String) -> Void) async throws -> [URL] {
        let total = parts.reduce(0) { $0 + $1.segments.count }
        var done = 0
        var exported = 0.0
        defer { ActivityStore.shared.record(ActivityKey.secondsExported, count: Int(exported.rounded())) }
        var urls: [URL] = []
        let fileFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 44_100, channels: 1, interleaved: false)!
        // Each file is written in a local temporary folder and moved into place when it's
        // finished. A cloud folder (OneDrive, iCloud Drive, Dropbox) takes a file over while
        // it's still being written, and the next write fails (avfaudio error -40).
        let staging = FileManager.default.temporaryDirectory.appending(path: "InnerVoice Export \(UUID().uuidString)")
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: staging) }
        for part in parts where !part.segments.isEmpty {
            // One file (Combined), or one per voice in this part (Separate), in order of first use.
            var names: [String] = []
            for seg in part.segments {
                let n = stem?(seg) ?? ""
                if !names.contains(n) { names.append(n) }
            }
            var files: [String: (AVAudioFile, AVAudioConverter)] = [:]
            var moves: [(from: URL, to: URL)] = []
            for n in names {
                let fileName = safeName(n.isEmpty ? part.name : part.name + " - " + n) + "." + format.fileExtension
                let temp = staging.appending(path: fileName)
                try? FileManager.default.removeItem(at: temp)
                let file = try AVAudioFile(forWriting: temp, settings: format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
                guard let converter = AVAudioConverter(from: SpeechAudio.format, to: fileFormat) else { throw SpeechError.failed("unsupported audio format") }
                files[n] = (file, converter)
                moves.append((temp, folder.appending(path: fileName)))
            }
            func put(_ buffer: AVAudioPCMBuffer, in name: String) throws {
                for (n, (file, converter)) in files {
                    try write(n == name ? buffer : SpeechAudio.silence(Double(buffer.frameLength) / buffer.format.sampleRate),
                              to: file, converter: converter, format: fileFormat)
                }
            }
            for seg in part.segments {
                try Task.checkCancellation()
                progress(done, total, part.name)
                if let request = resolver.request(for: seg) {
                    let audio = SpeechAudio.trimmed(try await SpeechRenderer.shared.render(request))
                    try put(audio, in: stem?(seg) ?? "")
                    exported += Double(audio.frameLength) / audio.format.sampleRate
                }
                let gap = seg.pause == .none ? seg.pauseAfter : PauseSettings.current.seconds(seg.pause)
                if gap > 0 {
                    // Silence goes in every file alike.
                    try put(SpeechAudio.silence(gap), in: "\u{0}")
                }
                done += 1
            }
            // Close the files (which finishes them), then move each into the chosen folder.
            for (file, _) in files.values { file.close() }
            files = [:]
            for move in moves {
                if FileManager.default.fileExists(atPath: move.to.path) {
                    _ = try FileManager.default.replaceItemAt(move.to, withItemAt: move.from)
                } else {
                    try FileManager.default.moveItem(at: move.from, to: move.to)
                }
                urls.append(move.to)
            }
        }
        progress(total, total, "")
        return urls
    }

    /// Separate: which file a segment goes in. Everything the narrator's voice reads is
    /// "Narrator"; a shared voice (Casting Style) is its name; anyone else is their own.
    static func stems(project: Project, index: BookIndex, resolver: VoiceResolver) -> (SpeechSegment) -> String {
        let readers = CastingStyleRules.readers(project, index: index)
        let names = Dictionary(uniqueKeysWithValues: project.characters.map { ($0.id, $0.name) })
        return { seg in
            guard let id = seg.speakerID, resolver.choice(for: seg) != resolver.narrator || readers[id].map(VoiceRole.sharedRoles.contains) == true else {
                return "Narrator"
            }
            if let r = readers[id], VoiceRole.sharedRoles.contains(r) { return r.voiceTitle }
            return names[id] ?? "Narrator"
        }
    }

    private static func write(_ buffer: AVAudioPCMBuffer, to file: AVAudioFile, converter: AVAudioConverter, format: AVAudioFormat) throws {
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * format.sampleRate / buffer.format.sampleRate + 1024)
        guard let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { return }
        var fed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if fed {
                status.pointee = .noDataNow
                return nil
            }
            fed = true
            status.pointee = .haveData
            return buffer
        }
        if let error { throw error }
        if out.frameLength > 0 { try file.write(from: out) }
    }

    static func safeName(_ s: String) -> String {
        let bad = CharacterSet(charactersIn: "/:\\?%*|\"<>")
        return s.components(separatedBy: bad).joined(separator: "-").trimmingCharacters(in: .whitespaces)
    }
}

struct AudioExportSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    var workspace: Workspace
    @Binding var isPresented: Bool

    enum Scope: String, CaseIterable, Identifiable {
        case book, character
        var id: String { rawValue }
        var title: String {
            switch self {
            case .book: "All voices"
            case .character: "One character or the narrator"
            }
        }
    }

    /// The Character menu's narrator choice.
    static let narratorTag = UUID(uuidString: "00000000-0000-0000-0000-00000000AAAA")!

    @State private var scope = Scope.book
    /// The chapter clicked last, for ⇧-click ranges.
    @State private var anchor: Int?
    @State private var characterID: UUID?
    @AppStorage("voices.readNarration") private var narration = true
    @AppStorage("audioExport.perChapter") private var perChapter = true
    /// Say each chapter's recap ("Previously…") after its title.
    @AppStorage("audioExport.recaps") private var recaps = false
    /// Separate: a file per voice (Narrator, Men’s Voice, a character…), lined up for mixing.
    @AppStorage("audioExport.separate") private var separate = false
    @AppStorage("audioExport.format") private var formatRaw = AudioExporter.Format.m4a.rawValue
    @State private var running: Task<Void, Never>?
    @State private var progress = 0.0
    @State private var status = ""
    @State private var error: String?

    private var format: AudioExporter.Format { AudioExporter.Format(rawValue: formatRaw) ?? .m4a }

    /// The picked chapters, by index, in book order.
    private var picked: [Int] {
        let all = document.project.chapters.indices
        guard let set = workspace.audioExportChapters else { return Array(all) }
        return all.filter { set.contains($0) }
    }

    private var everyChapter: Bool { picked.count == document.project.chapters.count }

    private func pick(_ i: Int, _ on: Bool) {
        var set = Set(picked)
        // ⇧-click sets every chapter from the last one clicked to this one.
        let range = NSEvent.modifierFlags.contains(.shift) && anchor != nil ? Array(min(anchor!, i)...max(anchor!, i)) : [i]
        for c in range { if on { set.insert(c) } else { set.remove(c) } }
        anchor = i
        workspace.audioExportChapters = set.count == document.project.chapters.count ? nil : set
    }

    private var chapterToggles: [Binding<Bool>] {
        let set = Set(picked)
        return document.project.chapters.indices.map { i in
            Binding(get: { set.contains(i) }, set: { pick(i, $0) })
        }
    }

    private func chapterTitle(_ i: Int) -> String {
        let t = document.project.chapters[i].title.trimmingCharacters(in: .whitespaces)
        return t.isEmpty ? (i == 0 ? "Opening" : "Untitled") : t
    }

    @ViewBuilder private var chapterList: some View {
        let toggles = chapterToggles
        HStack {
            Toggle(sources: toggles, isOn: \.self) { Text("Select All") }
                .toggleStyle(.checkbox)
            Spacer()
            Text(everyChapter ? "All \(toggles.count.formatted()) chapters" : "\(picked.count.formatted()) of \(toggles.count.formatted()) chapters")
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
                            Text(chapterTitle(i)).lineLimit(1).truncationMode(.tail)
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

    private var cast: [BookCharacter] {
        let index = document.index
        return document.project.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Text("Export Audio").font(.title2.weight(.semibold))
            Text("Each line is read in its character’s voice and tone, with \(document.project.engineInUse.title).")
                .font(.callout)
                .foregroundStyle(.secondary)
            if document.project.settings.cloudVoicesAllowed != nil, document.project.engineInUse == .elevenlabs || document.project.engineInUse == .mixed {
                Label("ElevenLabs voices are made in the cloud: exporting sends the text they speak to ElevenLabs, and uses your plan’s credits.", systemImage: "icloud.and.arrow.up")
                    .font(.callout)
                    .foregroundStyle(.orange)
            }
            Form {
                Picker("Export", selection: $scope) {
                    ForEach(Scope.allCases) { Text($0.title).tag($0) }
                }
                if scope == .character {
                    Picker("Character", selection: $characterID) {
                        Text("Narrator (narration only)").tag(UUID?.some(Self.narratorTag))
                        Divider()
                        ForEach(cast) { Text($0.name).tag(UUID?.some($0.id)) }
                    }
                } else {
                    Toggle("Read narration too", isOn: $narration)
                    if narration, picked.contains(where: { document.project.chapters.indices.contains($0) && document.project.chapters[$0].recapText != nil }) {
                        Toggle("Say chapter recaps (“Previously…”)", isOn: $recaps)
                            .help("The narrator reads each chapter’s recap after its title. Recaps are in the inspector next to the chapter.")
                    }
                }
                if scope == .book, picked.count > 1 {
                    Picker("Files", selection: $perChapter) {
                        Text("One file per chapter").tag(true)
                        Text("One file").tag(false)
                    }
                }
                if scope != .character {
                    Picker("Voices", selection: $separate) {
                        Text("Combined").tag(false)
                        Text("Separate, a file per voice").tag(true)
                    }
                    .help("Separate saves the narrator, each shared voice and each character with a voice of their own in files of their own. They’re all the same length, so they line up in an audio editor.")
                }
                Picker("Format", selection: $formatRaw) {
                    ForEach(AudioExporter.Format.allCases) { Text($0.rawValue).tag($0.rawValue) }
                }
                .pickerStyle(.segmented)
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
                        .disabled(picked.isEmpty || (scope == .character && characterID == nil))
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
            characterID = characterID ?? cast.first?.id
            VoiceCastingRun.castIfNeeded(document: document)
        }
    }

    private func parts() -> [AudioExporter.Part] {
        let project = document.project
        let index = document.index
        let title = project.title
        let chapters = picked
        // "Book - Chapters 1–3, 5" when only some chapters go out.
        let which = everyChapter ? "" : (scope == .character ? ", " : " - ") + SpeechScript.chapterLabel(chapters)
        switch scope {
        case .book:
            if chapters.count == 1, let c = chapters.first {
                return [AudioExporter.Part(name: "\(title) - \(chapterTitle(c))",
                                           segments: SpeechScript.segments(chapter: c, project: project, index: index, includeNarration: narration, recaps: recaps))]
            }
            if perChapter {
                return chapters.map { c in
                    AudioExporter.Part(name: String(format: "%02d ", c + 1) + chapterTitle(c),
                                       segments: SpeechScript.segments(chapter: c, project: project, index: index, includeNarration: narration, recaps: recaps))
                }
            }
            return [AudioExporter.Part(name: title + which,
                                       segments: SpeechScript.segments(chapters: chapters, project: project, index: index, includeNarration: narration, recaps: recaps))]
        case .character:
            guard let id = characterID else { return [] }
            if id == Self.narratorTag {
                // Everything the narrator's voice reads, in order.
                let resolver = VoiceResolver(project: project, index: index)
                let all = SpeechScript.segments(chapters: chapters, project: project, index: index, includeNarration: true)
                return [AudioExporter.Part(name: "\(title) - Narrator" + which, segments: all.filter { resolver.choice(for: $0) == resolver.narrator })]
            }
            guard let c = project.character(id) else { return [] }
            return [AudioExporter.Part(name: "\(title) - \(c.name)" + which,
                                       segments: SpeechScript.segments(chapters: chapters, project: project, index: index,
                                                                       includeNarration: false, only: id))]
        }
    }

    private func export() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.prompt = "Export Here"
        panel.message = "Choose a folder for the audio files."
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        let parts = parts()
        let resolver = VoiceResolver(project: document.project, index: document.index)
        let stem = separate && scope != .character ? AudioExporter.stems(project: document.project, index: document.index, resolver: resolver) : nil
        let format = self.format
        error = nil
        progress = 0
        running = Task {
            defer { running = nil }
            do {
                let urls = try await AudioExporter.write(parts, to: folder, format: format, resolver: resolver, stem: stem) { done, total, what in
                    progress = total == 0 ? 1 : Double(done) / Double(total)
                    status = what.isEmpty ? "Finishing…" : "Reading \(what) (\(done.formatted()) of \(total.formatted()))…"
                }
                isPresented = false
                NSWorkspace.shared.activateFileViewerSelecting(urls)
            } catch is CancellationError {
                status = "Stopped."
            } catch {
                if Task.isCancelled { status = "Stopped." } else { self.error = error.localizedDescription }
            }
        }
    }
}
