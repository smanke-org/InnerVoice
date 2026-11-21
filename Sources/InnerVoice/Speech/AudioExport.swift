@preconcurrency import AVFoundation
import AppKit
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

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
    struct Part: Sendable {
        var name: String
        var segments: [SpeechSegment]
    }

    /// Renders and writes each part. `progress` hears (done, total, what) after each segment.
    /// With `stem`, each part is written Separate: one file per voice (named by `stem`, such
    /// as "Narrator" or "Men’s Voice"), all the same length, with silence wherever another
    /// voice speaks, so they line up when mixed. `timing` hears where each spoken segment
    /// starts and ends in its part's file, in seconds (part index, segment, start, end).
    static func write(_ parts: [Part], to folder: URL, format: Format, resolver: VoiceResolver,
                      stem: ((SpeechSegment) -> String)? = nil,
                      timing: ((Int, SpeechSegment, Double, Double) -> Void)? = nil,
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
        slowedLines = 0
        var oneAtATime = false
        for (partIndex, part) in parts.enumerated() where !part.segments.isEmpty {
            var position = 0.0
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
            // Lines are made ahead, several at once, and written in book order: the
            // network (ElevenLabs) and the system voices (Apple) work in parallel, and the
            // on-device models overlap their GPU work with the trimming and writing here.
            let requests = part.segments.map { resolver.request(for: $0) }
            var ahead: [Int: Task<AVAudioPCMBuffer, Error>] = [:]
            var next = 0
            defer { for t in ahead.values { t.cancel() } }
            func fill(from i: Int) {
                next = max(next, i)
                while next < requests.count {
                    guard let request = requests[next] else { next += 1; continue }
                    guard ahead.count < (oneAtATime ? 1 : renderAhead(request.choice.engine)) else { break }
                    ahead[next] = Task { try await renderWithRetry(request) }
                    next += 1
                }
            }
            for (i, seg) in part.segments.enumerated() {
                try Task.checkCancellation()
                progress(done, total, part.name)
                fill(from: i)
                if requests[i] != nil {
                    let task = ahead.removeValue(forKey: i) ?? Task { try await renderWithRetry(requests[i]!) }
                    let rendered: AVAudioPCMBuffer
                    do {
                        rendered = try await task.value
                    } catch where !Task.isCancelled && !oneAtATime && renderAhead(requests[i]!.choice.engine) > 1 {
                        // Trouble while several lines were being made: make this one again on
                        // its own, and the rest of the export one at a time.
                        for t in ahead.values { t.cancel() }
                        ahead = [:]
                        next = i + 1
                        oneAtATime = true
                        slowedLines += 1
                        SpeechLog.write("export: a line failed with several at once (\(error)); going one at a time")
                        rendered = try await renderWithRetry(requests[i]!)
                    }
                    fill(from: i + 1)
                    let audio = SpeechAudio.trimmed(rendered)
                    try put(audio, in: stem?(seg) ?? "")
                    let seconds = Double(audio.frameLength) / audio.format.sampleRate
                    timing?(partIndex, seg, position, position + seconds)
                    position += seconds
                    exported += seconds
                }
                let gap = seg.pause == .none ? seg.pauseAfter : PauseSettings.current.seconds(seg.pause)
                if gap > 0 {
                    // Silence goes in every file alike.
                    try put(SpeechAudio.silence(gap), in: "\u{0}")
                    position += gap
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

    /// Lines the last export made again on their own after trouble at a faster speed.
    static var slowedLines = 0

    /// Self-test: lines at once for every engine.
    static var testAhead: Int?
    /// Self-test: the next renders that fail on purpose.
    static var testFailures = 0

    /// How many lines are made at once for an engine.
    static func renderAhead(_ engine: VoiceEngine) -> Int {
        // Self-test timing: one at a time, as before, to compare.
        if SystemHooks.isTestRun, ProcessInfo.processInfo.environment["INNERVOICE_EXPORT_SERIAL"] != nil { return 1 }
        if SystemHooks.isTestRun, let n = testAhead { return n }
        return ExportSpeed.current(engine == .mixed ? .pocket : engine).linesAtOnce(engine)
    }

    /// ElevenLabs answers "busy" when too many requests run at once (another app, or a
    /// smaller plan), and Gemini TTS when a per-minute limit is reached or Google is
    /// overloaded: wait and try again rather than stopping the export. Gemini's waits come
    /// from GeminiPace (as long as Google asks, then spaced to the key's limit).
    static func renderWithRetry(_ request: SpeechRequest) async throws -> AVAudioPCMBuffer {
        if SystemHooks.isTestRun, testFailures > 0 {
            testFailures -= 1
            throw SpeechError.failed("self-test failure")
        }
        var wait = retryWait
        var attempt = 0
        while true {
            do {
                return try await SpeechRenderer.shared.render(request)
            } catch ElevenLabsError.busy where attempt < 5 {
                try await Task.sleep(for: .seconds(wait))
            } catch GeminiError.busy where attempt < 8 {
                // GeminiPace holds the next try until Google's wait is over and spaces the
                // lines to the key's limit, so a short pause is enough here.
                SpeechLog.write("export: Gemini TTS busy, trying again (attempt \(attempt + 1))")
                try await Task.sleep(for: .seconds(min(wait, 4)))
            } catch SpeechifyError.busy(let after) where attempt < 8 {
                SpeechLog.write("export: Speechify busy, trying again (attempt \(attempt + 1))")
                try await Task.sleep(for: .seconds(min(after ?? wait, 30)))
            }
            attempt += 1
            wait = min(wait * 2, 60)
        }
    }

    /// The first wait before trying a busy engine again, in seconds; it doubles each time
    /// (up to a minute). Test runs shorten it.
    static var retryWait: Double = 1

    static func safeName(_ s: String) -> String {
        let bad = CharacterSet(charactersIn: "/:\\?%*|\"<>")
        return s.components(separatedBy: bad).joined(separator: "-").trimmingCharacters(in: .whitespaces)
    }
}

struct AudioExportSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    var workspace: Workspace
    @Binding var isPresented: Bool

    nonisolated enum Scope: String, CaseIterable, Identifiable, Sendable {
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
    /// Gemini TTS: how long Google asked to wait (its per-minute limit), while it lasts.
    @State private var waitNote: String?
    @State private var error: String?
    /// ElevenLabs credits this export would use, and what the plan has left.
    @State private var credits: (needed: Int, lines: Int, left: Int?)?
    /// Characters of Gemini TTS lines this export would send (for a cost estimate).
    @State private var geminiCharacters: Int?
    /// Characters of Speechify lines this export would send (Speechify bills by character).
    @State private var speechifyCharacters: Int?

    /// The engines this book's voices use, for the Export Speed pickers.
    private var speedEngines: [VoiceEngine] {
        let engine = document.project.engineInUse
        guard engine == .mixed else { return [engine] }
        let used = Set(document.project.characters.compactMap { $0.voice(for: .mixed)?.choice.engine })
            .union(document.project.settings.narratorVoice(for: .mixed).map { [$0.choice.engine] } ?? [])
        return ExportSpeed.engines.filter { used.contains($0) }
    }

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
            if document.project.settings.googleVoicesAllowed != nil, document.project.engineInUse == .gemini || document.project.engineInUse == .mixed && EngineSwitches.shared.isOn(.gemini) {
                Label("Gemini TTS voices are made in the cloud: exporting sends the text they speak to Google, billed to your Gemini API key.", systemImage: "icloud.and.arrow.up")
                    .font(.callout)
                    .foregroundStyle(.orange)
            }
            if document.project.settings.speechifyVoicesAllowed != nil, document.project.engineInUse == .speechify {
                Label("Speechify voices are made in the cloud: exporting sends the text they speak to Speechify, counted against your Speechify API plan.", systemImage: "icloud.and.arrow.up")
                    .font(.callout)
                    .foregroundStyle(.orange)
            }
            if let credits, credits.needed > 0 {
                creditsNote(credits)
            }
            if let geminiCharacters, geminiCharacters > 0 {
                let dollars = GeminiTTS.estimatedSeconds(characters: geminiCharacters) * GeminiVoices.shared.model.dollarsPerSecond()
                Label("About \(dollars.formatted(.currency(code: "USD"))) of Gemini TTS for the lines not made yet, at Google’s paid-tier prices.", systemImage: "creditcard")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            if let speechifyCharacters, speechifyCharacters > 0 {
                let dollars = SpeechifyTTS.estimatedDollars(characters: speechifyCharacters)
                Label("About \(speechifyCharacters.formatted()) Speechify characters for the lines not made yet: about \(dollars.formatted(.currency(code: "USD"))) at Speechify’s pay-as-you-go price, less whatever your plan’s monthly allowance still covers.", systemImage: "creditcard")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
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
                ForEach(speedEngines, id: \.self) { ExportSpeedPicker(engine: $0, label: "Export speed (\($0.title))") }
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
                    if let waitNote { Text(waitNote).font(.caption).foregroundStyle(.secondary).monospacedDigit() }
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
        .task(id: creditsKey) { await estimateCredits() }
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
        spec.parts(resolver: VoiceResolver(project: document.project, index: document.index))
    }

    /// What goes into the files, as plain values, so the lines can be worked out off the
    /// main thread (the estimate) as well as on it (the export).
    nonisolated struct PartsSpec: Sendable {
        var project: Project
        var index: BookIndex
        var picked: [Int]
        var scope: Scope
        var characterID: UUID?
        var narration: Bool
        var recaps: Bool
        var perChapter: Bool

        private func chapterTitle(_ i: Int) -> String {
            let t = project.chapters[i].title.trimmingCharacters(in: .whitespaces)
            return t.isEmpty ? (i == 0 ? "Opening" : "Untitled") : t
        }

        func parts(resolver: VoiceResolver) -> [AudioExporter.Part] {
        let title = project.title
        let chapters = picked
        let everyChapter = picked.count == project.chapters.count
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
            if id == AudioExportSheet.narratorTag {
                // Everything the narrator's voice reads, in order.
                let all = SpeechScript.segments(chapters: chapters, project: project, index: index, includeNarration: true)
                return [AudioExporter.Part(name: "\(title) - Narrator" + which, segments: all.filter { resolver.choice(for: $0) == resolver.narrator })]
            }
            guard let c = project.character(id) else { return [] }
            return [AudioExporter.Part(name: "\(title) - \(c.name)" + which,
                                       segments: SpeechScript.segments(chapters: chapters, project: project, index: index,
                                                                       includeNarration: false, only: id))]
        }
        }
    }

    private var spec: PartsSpec {
        PartsSpec(project: document.project, index: document.index, picked: picked, scope: scope, characterID: characterID,
                  narration: narration, recaps: recaps, perChapter: perChapter)
    }

    private func creditsNote(_ c: (needed: Int, lines: Int, left: Int?)) -> some View {
        let short = c.left.map { c.needed > $0 } ?? false
        return VStack(alignment: .leading, spacing: 2) {
            Label("About \(c.needed.formatted()) ElevenLabs credits for \(c.lines.formatted()) " + (c.lines == 1 ? "line" : "lines") + " not made yet"
                  + (c.left.map { ". Your plan has \($0.formatted()) left." } ?? "."), systemImage: "creditcard")
            if short {
                Text("That’s more than your plan has left: the export would stop when the credits run out. Lines already made aren’t charged again, so you could export fewer chapters now and the rest later.")
                    .foregroundStyle(Color(nsColor: .attention))
            }
        }
        .font(.callout)
        .foregroundStyle(short ? Color(nsColor: .attention) : .secondary)
    }

    /// What the estimate depends on: the choices in the sheet.
    private var creditsKey: String {
        "\(scope)|\(characterID?.uuidString ?? "")|\(narration)|\(recaps)|\(picked)|\(document.tagRevision)"
    }

    /// Counts the characters of the ElevenLabs lines not in the audio cache (ElevenLabs
    /// charges about a credit a character), then asks ElevenLabs what the plan has left.
    /// Only the plan's numbers come back; no book text is sent.
    private func estimateCredits() async {
        guard ElevenLabs.shared.isSetUp || GeminiVoices.shared.isSetUp || SpeechifyVoices.shared.isSetUp else {
            credits = nil; geminiCharacters = nil; speechifyCharacters = nil; return
        }
        // Settle first: a run of clicks makes one estimate, not one each.
        try? await Task.sleep(for: .milliseconds(250))
        if Task.isCancelled { return }
        // The whole book's lines and a disk check for each: off the main thread, so the
        // sheet keeps up with the clicks.
        let spec = spec
        let resolver = VoiceResolver(project: document.project, index: document.index)
        let folder = SpeechRenderer.shared.cacheFolder
        let work = Task.detached(priority: .userInitiated) { () -> (needed: Int, lines: Int, gemini: Int, speechify: Int)? in
            var needed = 0, lines = 0, gemini = 0, speechify = 0
            for (n, seg) in spec.parts(resolver: resolver).flatMap(\.segments).enumerated() {
                if n % 200 == 0, Task.isCancelled { return nil }
                guard let r = resolver.request(for: seg), r.choice.engine.isCloud, r.cloudAllowed,
                      !FileManager.default.fileExists(atPath: folder.appending(path: r.cacheKey + ".caf").path) else { continue }
                let count = r.text.trimmingCharacters(in: .whitespacesAndNewlines).count
                if r.choice.engine == .gemini { gemini += count; continue }
                if r.choice.engine == .speechify { speechify += count; continue }
                guard r.choice.engine == .elevenlabs else { continue }
                needed += count
                lines += 1
            }
            return (needed, lines, gemini, speechify)
        }
        guard let (needed, lines, gemini, speechify) = await withTaskCancellationHandler(operation: { await work.value }, onCancel: { work.cancel() }),
              !Task.isCancelled else { return }
        geminiCharacters = gemini
        speechifyCharacters = speechify
        guard ElevenLabs.shared.isSetUp, needed > 0 else { credits = nil; return }
        credits = (needed, lines, credits?.left)
        guard needed > 0, credits?.left == nil, let account = try? await ElevenLabs.shared.account(), !Task.isCancelled else { return }
        credits = (needed, lines, max(0, account.limit - account.used))
    }

    private func export() {
        guard CloudGate.allows(document) else { return }
        // Anything paid goes out only after saying what: how many chapters, and the cost.
        // Cancel is the default button, so a stray Return never starts a costly export.
        let paidCredits = credits?.needed ?? 0
        let paidGemini = geminiCharacters ?? 0
        let paidSpeechify = speechifyCharacters ?? 0
        if paidCredits > 0 || paidGemini > 0 || paidSpeechify > 0 {
            let count = picked.count
            var costs: [String] = []
            if paidGemini > 0 {
                let dollars = GeminiTTS.estimatedSeconds(characters: paidGemini) * GeminiVoices.shared.model.dollarsPerSecond()
                costs.append("about \(dollars.formatted(.currency(code: "USD"))) of Gemini TTS")
            }
            if paidCredits > 0 { costs.append("about \(paidCredits.formatted()) ElevenLabs credits") }
            if paidSpeechify > 0 {
                let dollars = SpeechifyTTS.estimatedDollars(characters: paidSpeechify)
                costs.append("about \(paidSpeechify.formatted()) Speechify characters (about \(dollars.formatted(.currency(code: "USD"))) beyond your plan’s allowance)")
            }
            let alert = NSAlert()
            alert.messageText = everyChapter && count > 1
                ? "Export the Whole Book (\(count.formatted()) Chapters)?"
                : "Export \(count == 1 ? "1 Chapter" : "\(count.formatted()) Chapters")?"
            alert.informativeText = "This uses \(costs.formatted(.list(type: .and))) for the lines not made yet."
                + (count == 1 ? " Chapter: \(chapterTitle(picked[0]))." : "")
            alert.addButton(withTitle: "Cancel")
            alert.addButton(withTitle: count == 1 ? "Export 1 Chapter" : "Export \(count.formatted()) Chapters")
            guard alert.runModal() == .alertSecondButtonReturn else { return }
        }
        if let c = credits, let left = c.left, c.needed > left {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "Not Enough ElevenLabs Credits"
            alert.informativeText = "This export needs about \(c.needed.formatted()) credits and your plan has \(left.formatted()) left. It will stop when they run out; lines already made are kept and aren’t charged again."
            alert.addButton(withTitle: "Cancel")
            alert.addButton(withTitle: "Export Anyway")
            guard alert.runModal() == .alertSecondButtonReturn else { return }
        }
        var parts = parts()
        let separateFiles = separate && scope != .character
        let format = self.format
        // One file: name it. Several (a file per chapter or per voice): name the folder
        // they go in.
        let single = parts.filter { !$0.segments.isEmpty }.count == 1 && !separateFiles
        let panel = NSSavePanel()
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        panel.prompt = "Export"
        if single, let first = parts.firstIndex(where: { !$0.segments.isEmpty }) {
            panel.message = "Name the audio file and choose where to save it."
            panel.nameFieldLabel = "File name:"
            panel.allowedContentTypes = [UTType(filenameExtension: format.fileExtension) ?? .audio]
            panel.nameFieldStringValue = AudioExporter.safeName(parts[first].name) + "." + format.fileExtension
            guard panel.runModal() == .OK, let url = panel.url else { return }
            parts[first].name = url.deletingPathExtension().lastPathComponent
            exportParts(parts, to: url.deletingLastPathComponent(), format: format)
        } else {
            panel.message = "Name the folder for the audio files and choose where to save it."
            panel.nameFieldLabel = "Folder name:"
            panel.nameFieldStringValue = AudioExporter.safeName(document.project.title) + " Audio"
            guard panel.runModal() == .OK, let url = panel.url else { return }
            do {
                try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            } catch {
                self.error = error.localizedDescription
                return
            }
            exportParts(parts, to: url, format: format)
        }
    }

    private func exportParts(_ parts: [AudioExporter.Part], to folder: URL, format: AudioExporter.Format) {
        let resolver = VoiceResolver(project: document.project, index: document.index)
        let stem = separate && scope != .character ? AudioExporter.stems(project: document.project, index: document.index, resolver: resolver) : nil
        error = nil
        progress = 0
        let started = Date()
        running = Task {
            defer { running = nil }
            do {
                // While Google makes us wait, say so: the bar stands still for up to a minute.
                let waits = Task {
                    while !Task.isCancelled {
                        try? await Task.sleep(for: .seconds(1))
                        waitNote = await GeminiPace.shared.secondsWaiting().map { "Waiting \($0) s for Google’s per-minute limit on Gemini TTS…" }
                    }
                }
                defer { waits.cancel(); waitNote = nil }
                let urls = try await AudioExporter.write(parts, to: folder, format: format, resolver: resolver, stem: stem) { done, total, what in
                    progress = total == 0 ? 1 : Double(done) / Double(total)
                    status = what.isEmpty ? "Finishing…" : "Reading \(what) (\(done.formatted()) of \(total.formatted()))" + (TimeLeft.estimate(done: done, total: total, elapsed: Date().timeIntervalSince(started)).map { ", \($0)…" } ?? "…")
                }
                if AudioExporter.slowedLines > 0 {
                    let alert = NSAlert()
                    alert.messageText = "Export Finished One Line at a Time"
                    alert.informativeText = "A line had trouble while several were being made at once, so the rest of the export went one line at a time. The audio is complete. If this keeps happening with \(document.project.engineInUse.title), choose a slower Export Speed in Settings › Voices."
                    alert.runModal()
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
