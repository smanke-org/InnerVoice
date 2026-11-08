import AppKit
import CryptoKit
import Foundation
import FoundationModels
import InnerVoiceKit
import Observation

/// Something that answers a prompt: a llama.cpp model or Apple's on-device model.
protocol TextModel: Sendable {
    var displayName: String { get }
    /// Words per passage this model can take in comfortably.
    var passageWords: Int { get }
    func complete(_ messages: [ChatMessage], grammar: String?, maxTokens: Int) async throws -> String
    func cancel()
}

enum ModelError: LocalizedError {
    case noModel, notDownloaded, appleUnavailable(String)
    var errorDescription: String? {
        switch self {
        case .noModel: "Choose a model in Settings › AI first."
        case .notDownloaded: "The chosen model isn't downloaded yet."
        case .appleUnavailable(let why): why
        }
    }
}

/// A GGUF model run by llama.cpp on the Mac's GPU.
nonisolated final class LlamaTextModel: TextModel, @unchecked Sendable {
    let engine: LlamaEngine
    let displayName: String
    let passageWords = 900
    init(engine: LlamaEngine, name: String) {
        self.engine = engine
        displayName = name
    }

    func complete(_ messages: [ChatMessage], grammar: String?, maxTokens: Int) async throws -> String {
        let engine = self.engine
        return try await withCheckedThrowingContinuation { continuation in
            engine.queue.async {
                do {
                    let prompt = engine.template.render(messages, prefill: "", closeThinking: engine.closeThinking)
                    let text = try engine.generate(prompt: prompt, maxTokens: maxTokens, grammar: grammar) ?? ""
                    var out = text
                    for stop in engine.template.stopStrings { if let r = out.range(of: stop) { out = String(out[..<r.lowerBound]) } }
                    continuation.resume(returning: out)
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    func cancel() { engine.cancel() }
}

/// Apple's on-device model (Apple Intelligence). No download, smaller context.
nonisolated final class AppleTextModel: TextModel, @unchecked Sendable {
    let displayName = "Apple On-Device Model"
    let passageWords = 600

    static var availability: (ok: Bool, why: String) {
        switch SystemLanguageModel.default.availability {
        case .available: return (true, "")
        case .unavailable(.deviceNotEligible): return (false, "This Mac can't run Apple Intelligence.")
        case .unavailable(.appleIntelligenceNotEnabled): return (false, "Turn on Apple Intelligence in System Settings to use it.")
        case .unavailable(.modelNotReady): return (false, "Apple's model is still downloading. Try again later.")
        case .unavailable: return (false, "Apple's on-device model isn't available.")
        }
    }

    func complete(_ messages: [ChatMessage], grammar: String?, maxTokens: Int) async throws -> String {
        let system = messages.filter { $0.role == .system }.map(\.content).joined(separator: "\n")
        let user = messages.filter { $0.role == .user }.map(\.content).joined(separator: "\n\n")
        let session = LanguageModelSession(instructions: system)
        let response = try await session.respond(to: user, options: GenerationOptions(temperature: 0.1, maximumResponseTokens: maxTokens))
        return response.content
    }

    func cancel() {}
}

/// Downloads, lists and loads models. The choice is saved as "catalog:<id>",
/// "file:<path>" or "apple".
@Observable
final class ModelManager {
    static let shared = ModelManager()

    enum DownloadState: Equatable {
        case downloading(Double, Int64)
        case verifying
        case failed(String)
    }

    enum LoadState: Equatable { case none, loading, ready, failed(String) }

    private(set) var downloads: [String: DownloadState] = [:]
    private(set) var loadState: LoadState = .none
    private(set) var downloaded: Set<String> = []
    var selection: String {
        didSet {
            // A test copy (APP_STORE, agent CLI tests) shares the real app's preferences: its
            // choice lasts only until it quits.
            if !SystemHooks.isTestRun { UserDefaults.standard.set(selection, forKey: "ai.model") }
            unload()
        }
    }

    private let engine = LlamaEngine()
    private var loadedPath: String?
    private var downloaders: [String: Downloader] = [:]

    static var memoryGB: Int { Int((Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824).rounded()) }

    nonisolated static var modelsFolder: URL {
        let base = ProcessInfo.processInfo.environment["APP_MODELS"].map { URL(fileURLWithPath: $0) }
            ?? URL.applicationSupportDirectory.appending(path: "Inner Voice/Models")
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base
    }

    private init() {
        selection = ProcessInfo.processInfo.environment["APP_MODEL"] ?? UserDefaults.standard.string(forKey: "ai.model") ?? ""
        refresh()
    }

    func refresh() {
        downloaded = Set(ModelCatalog.all.filter {
            FileManager.default.fileExists(atPath: Self.modelsFolder.appending(path: $0.file).path)
        }.map(\.id))
    }

    var recommendedID: String { ModelCatalog.recommendedID(memoryGB: Self.memoryGB) }

    var selectionName: String {
        if selection == "apple" { return "Apple On-Device Model" }
        if selection.hasPrefix("catalog:"), let m = ModelCatalog.model(id: String(selection.dropFirst(8))) { return m.name }
        if selection.hasPrefix("file:") { return URL(fileURLWithPath: String(selection.dropFirst(5))).deletingPathExtension().lastPathComponent }
        return "No model"
    }

    /// True when Tag Speakers can run without asking the narrator to pick a model.
    var isReady: Bool {
        if selection == "apple" { return AppleTextModel.availability.ok }
        if selection.hasPrefix("catalog:") { return downloaded.contains(String(selection.dropFirst(8))) }
        if selection.hasPrefix("file:") { return FileManager.default.fileExists(atPath: String(selection.dropFirst(5))) }
        return false
    }

    var catalogModel: CatalogModel? {
        selection.hasPrefix("catalog:") ? ModelCatalog.model(id: String(selection.dropFirst(8))) : nil
    }

    // MARK: Loading

    /// Loads the chosen model (once) and returns it.
    func model() async throws -> TextModel {
        if selection == "apple" {
            let a = AppleTextModel.availability
            guard a.ok else { throw ModelError.appleUnavailable(a.why) }
            return AppleTextModel()
        }
        var path: String?
        if let m = catalogModel {
            guard downloaded.contains(m.id) else { throw ModelError.notDownloaded }
            path = Self.modelsFolder.appending(path: m.file).path
        } else if selection.hasPrefix("file:") {
            path = String(selection.dropFirst(5))
        }
        guard let path else { throw ModelError.noModel }
        let name = selectionName
        if loadedPath == path, loadState == .ready { return LlamaTextModel(engine: engine, name: name) }
        loadState = .loading
        let engine = self.engine
        do {
            try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
                engine.queue.async {
                    do {
                        try engine.load(path: path, options: LlamaEngine.Options(contextLength: 8192))
                        c.resume()
                    } catch {
                        c.resume(throwing: error)
                    }
                }
            }
            loadedPath = path
            loadState = .ready
            return LlamaTextModel(engine: engine, name: name)
        } catch {
            loadState = .failed(error.localizedDescription)
            throw error
        }
    }

    func unload() {
        engine.cancel()
        loadedPath = nil
        loadState = .none
        engine.queue.async { [engine] in engine.unloadOnQueue() }
    }

    /// Frees GPU memory before the app quits; ggml aborts if it's freed during exit.
    func unloadNow() {
        engine.cancel()
        engine.unload()
    }

    // MARK: Files

    func chooseFile() {
        let panel = NSOpenPanel()
        panel.title = "Choose a GGUF Model"
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.allowsOtherFileTypes = true
        guard panel.runModal() == .OK, let url = panel.url, url.pathExtension.lowercased() == "gguf" else { return }
        selection = "file:" + url.path
    }

    func delete(_ m: CatalogModel) {
        if selection == "catalog:" + m.id { unload() }
        try? FileManager.default.trashItem(at: Self.modelsFolder.appending(path: m.file), resultingItemURL: nil)
        refresh()
    }

    // MARK: Downloading

    func download(_ m: CatalogModel) {
        guard downloaders[m.id] == nil else { return }
        let free = (try? URL.applicationSupportDirectory.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?
            .volumeAvailableCapacityForImportantUsage ?? Int64.max
        if free < m.bytes + 1_000_000_000 {
            downloads[m.id] = .failed("Not enough free space: \(m.sizeDescription) needed.")
            return
        }
        downloads[m.id] = .downloading(0, 0)
        let destination = Self.modelsFolder.appending(path: m.file)
        let d = Downloader(model: m, destination: destination) { [weak self] event in
            Task { @MainActor in self?.downloadEvent(m, event) }
        }
        downloaders[m.id] = d
        d.start()
    }

    func cancelDownload(_ m: CatalogModel) {
        downloaders[m.id]?.cancel()
        downloaders[m.id] = nil
        downloads[m.id] = nil
    }

    private func downloadEvent(_ m: CatalogModel, _ event: Downloader.Event) {
        switch event {
        case .progress(let p, let bytes): downloads[m.id] = .downloading(p, bytes)
        case .verifying: downloads[m.id] = .verifying
        case .finished:
            downloads[m.id] = nil
            downloaders[m.id] = nil
            refresh()
            if !isReady { selection = "catalog:" + m.id }
        case .failed(let message):
            downloads[m.id] = .failed(message)
            downloaders[m.id] = nil
        }
    }
}

/// One model download: URLSession to a temp file, resumable, SHA-256 check, then into place.
nonisolated final class Downloader: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    enum Event { case progress(Double, Int64), verifying, finished, failed(String) }

    let model: CatalogModel
    let destination: URL
    let report: (Event) -> Void
    private var session: URLSession?
    private var task: URLSessionDownloadTask?
    private var lastReport = Date.distantPast
    private var resumeURL: URL { destination.appendingPathExtension("resume") }

    init(model: CatalogModel, destination: URL, report: @escaping (Event) -> Void) {
        self.model = model
        self.destination = destination
        self.report = report
    }

    func start() {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForResource = 12 * 3600
        let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
        self.session = session
        // Pick up where a canceled or interrupted download left off.
        if let data = try? Data(contentsOf: resumeURL) {
            task = session.downloadTask(withResumeData: data)
        } else {
            task = session.downloadTask(with: model.downloadURL)
        }
        task?.resume()
    }

    func cancel() {
        let url = resumeURL
        task?.cancel { data in
            if let data { try? data.write(to: url) }
        }
        session?.finishTasksAndInvalidate()
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard Date().timeIntervalSince(lastReport) > 0.25 else { return }
        lastReport = Date()
        let total = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : model.bytes
        report(.progress(Double(totalBytesWritten) / Double(max(1, total)), totalBytesWritten))
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        if let http = downloadTask.response as? HTTPURLResponse, http.statusCode != 200 {
            report(.failed("The server answered \(http.statusCode)."))
            return
        }
        try? FileManager.default.removeItem(at: resumeURL)
        let staged = destination.appendingPathExtension("part")
        try? FileManager.default.removeItem(at: staged)
        do {
            try FileManager.default.moveItem(at: location, to: staged)
        } catch {
            report(.failed(error.localizedDescription))
            return
        }
        report(.verifying)
        guard let digest = Self.sha256(of: staged), digest == model.sha256 else {
            try? FileManager.default.removeItem(at: staged)
            report(.failed("The download was damaged. Try again."))
            return
        }
        try? FileManager.default.removeItem(at: destination)
        do {
            try FileManager.default.moveItem(at: staged, to: destination)
            report(.finished)
        } catch {
            report(.failed(error.localizedDescription))
        }
        session.finishTasksAndInvalidate()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error else { return }
        let ns = error as NSError
        if let data = ns.userInfo[NSURLSessionDownloadTaskResumeData] as? Data { try? data.write(to: resumeURL) }
        if ns.code == NSURLErrorCancelled { return }
        if ns.code == NSURLErrorNotConnectedToInternet || ns.code == NSURLErrorCannotFindHost {
            report(.failed("Can't reach Hugging Face. Check your connection, then click Download to continue."))
        } else {
            report(.failed(error.localizedDescription))
        }
    }

    static func sha256(of url: URL) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try? handle.read(upToCount: 8 * 1_048_576), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}
