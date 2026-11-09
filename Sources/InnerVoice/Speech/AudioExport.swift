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
    static func write(_ parts: [Part], to folder: URL, format: Format, resolver: VoiceResolver,
                      progress: (Int, Int, String) -> Void) async throws -> [URL] {
        let total = parts.reduce(0) { $0 + $1.segments.count }
        var done = 0
        var exported = 0.0
        defer { ActivityStore.shared.record(ActivityKey.secondsExported, count: Int(exported.rounded())) }
        var urls: [URL] = []
        let fileFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 44_100, channels: 1, interleaved: false)!
        for part in parts where !part.segments.isEmpty {
            let url = folder.appending(path: safeName(part.name) + "." + format.fileExtension)
            try? FileManager.default.removeItem(at: url)
            let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
            guard let converter = AVAudioConverter(from: SpeechAudio.format, to: fileFormat) else { throw SpeechError.failed("unsupported audio format") }
            for seg in part.segments {
                try Task.checkCancellation()
                progress(done, total, part.name)
                if let request = resolver.request(for: seg) {
                    let audio = SpeechAudio.trimmed(try await SpeechRenderer.shared.render(request))
                    try write(audio, to: file, converter: converter, format: fileFormat)
                    exported += Double(audio.frameLength) / audio.format.sampleRate
                }
                let gap = seg.pause == .none ? seg.pauseAfter : PauseSettings.current.seconds(seg.pause)
                if gap > 0 {
                    try write(SpeechAudio.silence(gap), to: file, converter: converter, format: fileFormat)
                }
                done += 1
            }
            urls.append(url)
        }
        progress(total, total, "")
        return urls
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
    @Binding var isPresented: Bool

    enum Scope: String, CaseIterable, Identifiable {
        case book, chapter, character
        var id: String { rawValue }
        var title: String {
            switch self {
            case .book: "The whole book"
            case .chapter: "One chapter"
            case .character: "One character or the narrator"
            }
        }
    }

    /// The Character menu's narrator choice.
    static let narratorTag = UUID(uuidString: "00000000-0000-0000-0000-00000000AAAA")!

    @State private var scope = Scope.book
    @State private var chapter = 0
    @State private var characterID: UUID?
    @AppStorage("voices.readNarration") private var narration = true
    @AppStorage("audioExport.perChapter") private var perChapter = true
    @AppStorage("audioExport.format") private var formatRaw = AudioExporter.Format.m4a.rawValue
    @State private var running: Task<Void, Never>?
    @State private var progress = 0.0
    @State private var status = ""
    @State private var error: String?

    private var format: AudioExporter.Format { AudioExporter.Format(rawValue: formatRaw) ?? .m4a }

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
                if scope == .chapter {
                    Picker("Chapter", selection: $chapter) {
                        ForEach(Array(document.project.chapters.enumerated()), id: \.offset) { i, c in Text(c.title).tag(i) }
                    }
                }
                if scope == .character {
                    Picker("Character", selection: $characterID) {
                        Text("Narrator (narration only)").tag(UUID?.some(Self.narratorTag))
                        Divider()
                        ForEach(cast) { Text($0.name).tag(UUID?.some($0.id)) }
                    }
                } else {
                    Toggle("Read narration too", isOn: $narration)
                }
                if scope == .book {
                    Picker("Files", selection: $perChapter) {
                        Text("One file per chapter").tag(true)
                        Text("One file").tag(false)
                    }
                }
                Picker("Format", selection: $formatRaw) {
                    ForEach(AudioExporter.Format.allCases) { Text($0.rawValue).tag($0.rawValue) }
                }
                .pickerStyle(.segmented)
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
                        .disabled(scope == .character && characterID == nil)
                }
            }
        }
        .padding(Space.xl)
        .frame(width: 520)
        .noInitialFocus()
        .onAppear {
            characterID = characterID ?? cast.first?.id
            VoiceCastingRun.castIfNeeded(document: document)
        }
    }

    private func parts() -> [AudioExporter.Part] {
        let project = document.project
        let index = document.index
        let title = project.title
        switch scope {
        case .book:
            if perChapter {
                return project.chapters.indices.map { c in
                    AudioExporter.Part(name: String(format: "%02d ", c + 1) + project.chapters[c].title,
                                       segments: SpeechScript.segments(chapter: c, project: project, index: index, includeNarration: narration))
                }
            }
            return [AudioExporter.Part(name: title, segments: SpeechScript.segments(chapter: 0, chapters: project.chapters.count, project: project,
                                                                                    index: index, includeNarration: narration))]
        case .chapter:
            guard project.chapters.indices.contains(chapter) else { return [] }
            return [AudioExporter.Part(name: "\(title) - \(project.chapters[chapter].title)",
                                       segments: SpeechScript.segments(chapter: chapter, project: project, index: index, includeNarration: narration))]
        case .character:
            guard let id = characterID else { return [] }
            if id == Self.narratorTag {
                // Everything the narrator's voice reads, in order.
                let resolver = VoiceResolver(project: project)
                let all = SpeechScript.segments(chapter: 0, chapters: project.chapters.count, project: project, index: index, includeNarration: true)
                return [AudioExporter.Part(name: "\(title) - Narrator", segments: all.filter { resolver.choice(for: $0) == resolver.narrator })]
            }
            guard let c = project.character(id) else { return [] }
            return [AudioExporter.Part(name: "\(title) - \(c.name)",
                                       segments: SpeechScript.segments(chapter: 0, chapters: project.chapters.count, project: project, index: index,
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
        let resolver = VoiceResolver(project: document.project)
        let format = self.format
        error = nil
        progress = 0
        running = Task {
            defer { running = nil }
            do {
                let urls = try await AudioExporter.write(parts, to: folder, format: format, resolver: resolver) { done, total, what in
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
