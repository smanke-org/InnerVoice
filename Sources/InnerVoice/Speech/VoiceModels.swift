import AppKit
import HuggingFace
import InnerVoiceKit
import MLXAudioCore
import Observation

/// The speech models Qwen3-TTS and Orpheus need, downloaded from Hugging Face into
/// Application Support. Downloading is the only time they touch the network; the book's
/// text never leaves the Mac.
nonisolated struct VoicePack: Sendable {
    var engine: VoiceEngine
    /// Hugging Face repositories, in download order.
    var repos: [String]
    var bytes: Int64
    var summary: String

    var sizeDescription: String { ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file) }

    static let orpheus = VoicePack(
        engine: .orpheus,
        repos: ["mlx-community/orpheus-3b-0.1-ft-8bit", "mlx-community/snac_24khz"],
        bytes: 3_620_000_000,
        summary: "Eight natural American voices that act out the words, with sighs, laughs and gasps from the speech tags.")

    static let qwen = VoicePack(
        engine: .qwen,
        repos: [VoicePack.qwenDesignRepo, VoicePack.qwenCloneRepo],
        bytes: 6_180_000_000,
        summary: "A voice designed for each character from their summary and Voice Notes, kept the same on every line and acted in each tone.")

    /// Designs a voice from a description and acts in a tone.
    static let qwenDesignRepo = "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-8bit"
    /// Speaks every line in a designed voice.
    static let qwenCloneRepo = "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-8bit"

    static func pack(for engine: VoiceEngine) -> VoicePack? {
        switch engine {
        case .apple, .mixed, .elevenlabs: nil
        case .orpheus: orpheus
        case .qwen: qwen
        }
    }

    /// Where the models live. `APP_VOICE_MODELS` points test runs somewhere else.
    static var folder: URL {
        let base = ProcessInfo.processInfo.environment["APP_VOICE_MODELS"].map { URL(fileURLWithPath: $0) }
            ?? URL.applicationSupportDirectory.appending(path: "Inner Voice/Voice Models")
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base
    }

    static var cache: HubCache { HubCache(cacheDirectory: folder) }

    /// The folder one repository downloads into (mlx-audio's layout).
    static func directory(_ repo: String) -> URL {
        folder.appending(path: "mlx-audio/" + repo.replacingOccurrences(of: "/", with: "_"))
    }

    static func isDownloaded(_ repo: String) -> Bool {
        let dir = directory(repo)
        guard FileManager.default.fileExists(atPath: dir.appending(path: "config.json").path),
              let files = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.fileSizeKey]) else { return false }
        return files.contains { $0.pathExtension == "safetensors" && ((try? $0.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) > 0 }
    }

    var isInstalled: Bool { repos.allSatisfy(Self.isDownloaded) }
}

/// Downloads and removes the voice packs; Settings › Voices shows its state.
@Observable
@MainActor
final class VoiceModelManager {
    static let shared = VoiceModelManager()

    enum State: Equatable {
        case downloading(Double)
        case failed(String)
    }

    private(set) var states: [VoiceEngine: State] = [:]
    /// Bumped when a pack is installed or removed, so views that ask `isInstalled` update.
    private(set) var revision = 0
    @ObservationIgnored private var tasks: [VoiceEngine: Task<Void, Never>] = [:]

    func isInstalled(_ engine: VoiceEngine) -> Bool {
        _ = revision
        if engine == .elevenlabs { return ElevenLabs.shared.isSetUp }
        return engine == .apple || engine == .mixed || (VoicePack.pack(for: engine)?.isInstalled ?? false)
    }

    func download(_ engine: VoiceEngine) {
        guard let pack = VoicePack.pack(for: engine) else { return }
        download(pack)
    }

    /// Downloads a pack's repositories (the self-test passes a small one).
    func download(_ pack: VoicePack) {
        let engine = pack.engine
        guard tasks[engine] == nil else { return }
        let free = (try? VoicePack.folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?
            .volumeAvailableCapacityForImportantUsage ?? Int64.max
        if free < pack.bytes + 1_000_000_000 {
            states[engine] = .failed("Not enough free space: \(pack.sizeDescription) needed.")
            return
        }
        states[engine] = .downloading(0)
        tasks[engine] = Task {
            defer { tasks[engine] = nil }
            do {
                let cache = VoicePack.cache
                let client = HubClient(cache: cache)
                // Each repository's share of the bar, by size (the codec is tiny).
                let weights = pack.repos.map { $0.contains("snac") ? 0.02 : 1.0 }
                let total = weights.reduce(0, +)
                var done = 0.0
                for (i, repo) in pack.repos.enumerated() {
                    try Task.checkCancellation()
                    guard let id = Repo.ID(rawValue: repo) else { continue }
                    let before = done
                    let share = weights[i] / total
                    _ = try await ModelUtils.resolveOrDownloadModel(
                        client: client, cache: cache, repoID: id, requiredExtension: "safetensors",
                        progressHandler: { [weak self] p in
                            guard let self, case .downloading = self.states[engine] else { return }
                            self.states[engine] = .downloading(min(1, before + share * p.fractionCompleted))
                        })
                    done += share
                }
                states[engine] = nil
                revision += 1
            } catch is CancellationError {
                states[engine] = nil
            } catch {
                states[engine] = Task.isCancelled ? nil : .failed(Self.describe(error))
            }
        }
    }

    /// Waits for a download to finish (self-test).
    func waitForDownload(_ engine: VoiceEngine) async {
        await tasks[engine]?.value
    }

    func cancel(_ engine: VoiceEngine) {
        tasks[engine]?.cancel()
        tasks[engine] = nil
        states[engine] = nil
    }

    /// Moves the pack's models to the Trash (they can come back from there).
    func remove(_ engine: VoiceEngine) {
        guard let pack = VoicePack.pack(for: engine) else { return }
        remove(pack)
    }

    func remove(_ pack: VoicePack) {
        Task { await ModelSpeech.unload() }
        // Test runs delete outright, so their downloads don't land in the person's Trash.
        func trash(_ url: URL) {
            if SystemHooks.isTestRun { try? FileManager.default.removeItem(at: url) } else { try? FileManager.default.trashItem(at: url, resultingItemURL: nil) }
        }
        for repo in pack.repos {
            // The model folder and the Hugging Face cache it was cloned from.
            trash(VoicePack.directory(repo))
            let hub = VoicePack.folder.appending(path: "models--" + repo.replacingOccurrences(of: "/", with: "--"))
            trash(hub)
        }
        if pack.engine == .qwen {
            trash(VoicePack.folder.appending(path: "Samples"))
            QwenSamples.forget()
        }
        revision += 1
    }

    static func describe(_ error: Error) -> String {
        let ns = error as NSError
        if ns.domain == NSURLErrorDomain { return "Unable to download: check the internet connection, then try again." }
        return "Unable to download: \(error.localizedDescription)"
    }
}
