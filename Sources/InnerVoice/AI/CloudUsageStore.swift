import Foundation
import InnerVoiceKit
import Observation

/// Cloud usage on this Mac (Claude tokens, ElevenLabs characters), saved in Application
/// Support/Inner Voice/Cloud Usage.json. Numbers only, never book text. Test runs keep it in
/// memory.
@MainActor @Observable
final class CloudUsageStore {
    static let shared = CloudUsageStore()

    private(set) var records: [CloudUsageRecord] = []
    /// Claude's per-minute token allowance left after the last request (from Anthropic's
    /// rate-limit headers), per model.
    private(set) var claudeTokensLeft: [String: Int] = [:]

    private let file: URL? = SystemHooks.isTestRun ? nil
        : URL.applicationSupportDirectory.appending(path: "Inner Voice/Cloud Usage.json")
    @ObservationIgnored private var saveTask: Task<Void, Never>?

    private init() {
        if let file, let data = try? Data(contentsOf: file),
           let saved = try? JSONDecoder().decode([CloudUsageRecord].self, from: data) {
            records = saved
        }
    }

    func add(_ record: CloudUsageRecord) {
        CloudUsage.merge(record, into: &records)
        scheduleSave()
    }

    func noteClaudeLimit(model: String, tokensLeft: Int) { claudeTokensLeft[model] = tokensLeft }

    /// Test hook: replaces everything (snapshots seed made-up numbers).
    func replaceForTesting(_ list: [CloudUsageRecord]) { records = list }

    private func scheduleSave() {
        guard let file else { return }
        saveTask?.cancel()
        let snapshot = records
        saveTask = Task {
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled, let data = try? JSONEncoder().encode(snapshot) else { return }
            try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? data.write(to: file, options: .atomic)
        }
    }
}
