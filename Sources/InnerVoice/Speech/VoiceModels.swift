import AppKit
import HuggingFace
import InnerVoiceKit
import MLXAudioCore
import Observation

/// The speech models Chatterbox and Orpheus need, downloaded from Hugging Face into
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

    static let chatterbox = VoicePack(
        engine: .chatterbox,
        repos: [VoicePack.chatterboxRepo, VoicePack.chatterboxTokenizerRepo],
        bytes: 3_192_000_000,
        summary: "Nine stock voices and the voices you clone on this Mac, kept the same on every line and acted in each tone. Uses about 4 GB of memory while it speaks.")

    /// Chatterbox (Resemble AI, MIT): clones a voice from a short clip and speaks the lines.
    static let chatterboxRepo = "mlx-community/Chatterbox-TTS-fp16"
    /// The speech tokenizer Chatterbox reads a clip with (part of Chatterbox's design; it
    /// comes from CosyVoice 2).
    static let chatterboxTokenizerRepo = "mlx-community/S3TokenizerV2"

    static func pack(for engine: VoiceEngine) -> VoicePack? {
        switch engine {
        case .apple, .mixed, .elevenlabs: nil
        case .orpheus: orpheus
        case .chatterbox: chatterbox
        }
    }

    /// mlx-audio loads Chatterbox's speech tokenizer from its default Hugging Face cache, which
    /// is ~/.cache/huggingface unless `HF_HUB_CACHE` says otherwise: point it at the voice
    /// models, so it finds the copy downloaded with the pack and never fetches one elsewhere.
    /// Called at launch, before anything reads the default cache.
    static func pinHubCache() {
        let base = ProcessInfo.processInfo.environment["APP_VOICE_MODELS"].map { URL(fileURLWithPath: $0) }
            ?? URL.applicationSupportDirectory.appending(path: "Inner Voice/Voice Models")
        setenv("HF_HUB_CACHE", base.path, 1)
    }

    /// Qwen3-TTS (retired 2026-11-13): its models and voice samples, if this Mac still has them.
    static var oldQwenFiles: [URL] {
        let fm = FileManager.default
        var out: [URL] = []
        let models = folder.appending(path: "mlx-audio")
        for name in (try? fm.contentsOfDirectory(atPath: models.path)) ?? [] where name.hasPrefix("mlx-community_Qwen3-TTS") {
            out.append(models.appending(path: name))
        }
        for name in (try? fm.contentsOfDirectory(atPath: folder.path)) ?? [] where name.hasPrefix("models--mlx-community--Qwen3-TTS") {
            out.append(folder.appending(path: name))
        }
        let samples = folder.appending(path: "Samples")
        if fm.fileExists(atPath: samples.path) { out.append(samples) }
        return out
    }

    /// How much room the old files take.
    static func size(of urls: [URL]) -> Int64 {
        var total: Int64 = 0
        for url in urls {
            guard let e = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.totalFileAllocatedSizeKey]) else { continue }
            for case let f as URL in e {
                total += Int64((try? f.resourceValues(forKeys: [.totalFileAllocatedSizeKey]))?.totalFileAllocatedSize ?? 0)
            }
        }
        return total
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
        if engine == .apple || engine == .mixed { return true }
        // Checking lists the model folders on disk, and views ask on every redraw: the answer
        // is kept until a pack is downloaded or removed (`revision` changes).
        if let hit = installedCache[engine], hit.revision == revision { return hit.value }
        let value = VoicePack.pack(for: engine)?.isInstalled ?? false
        installedCache[engine] = (revision, value)
        return value
    }

    @ObservationIgnored private var installedCache: [VoiceEngine: (revision: Int, value: Bool)] = [:]

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
        if pack.engine == .chatterbox { ChatterboxClips.forget() }
        revision += 1
    }

    /// The files Qwen3-TTS left (Settings › Voices › Remove Old Voice Files), worked out once
    /// per change: the Settings row asks on every redraw.
    private(set) var oldFiles: (urls: [URL], bytes: Int64)?

    func checkOldFiles() {
        let urls = VoicePack.oldQwenFiles
        oldFiles = urls.isEmpty ? nil : (urls, VoicePack.size(of: urls))
    }

    /// Moves Qwen3-TTS's models and voice samples to the Trash. Test runs touch only a
    /// test folder (APP_VOICE_MODELS), never the person's own, and delete outright there.
    func removeOldFiles() {
        if SystemHooks.isTestRun, ProcessInfo.processInfo.environment["APP_VOICE_MODELS"] == nil { return }
        for url in VoicePack.oldQwenFiles {
            if SystemHooks.isTestRun { try? FileManager.default.removeItem(at: url) } else { try? FileManager.default.trashItem(at: url, resultingItemURL: nil) }
        }
        checkOldFiles()
    }

    static func describe(_ error: Error) -> String {
        let ns = error as NSError
        if ns.domain == NSURLErrorDomain { return "Unable to download: check the internet connection, then try again." }
        return "Unable to download: \(error.localizedDescription)"
    }
}
