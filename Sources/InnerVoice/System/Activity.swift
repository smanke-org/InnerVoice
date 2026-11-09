import AppKit
import Foundation
import InnerVoiceKit
import Observation

/// What InnerVoice has done for the narrator, per day, for Settings › Activity and the AI
/// accuracy in Choose a Model. Numbers only, never anything from a book. Kept in
/// Application Support/Inner Voice/activity.json; with settings sync on, each Mac also
/// writes its own copy to iCloud Drive/InnerVoice/Activity/<device>.json and reads the
/// others' for All Macs. Test runs count in memory only.
@Observable
@MainActor
final class ActivityStore {
    static let shared = ActivityStore()

    private(set) var log = ActivityLog()
    /// The other Macs' logs, by device ID (All Macs).
    private(set) var remotes: [String: DeviceActivity] = [:]
    @ObservationIgnored private var saveTask: Task<Void, Never>?

    private var url: URL { URL.applicationSupportDirectory.appending(path: "Inner Voice/activity.json") }
    private var shareFolder: URL { SettingsSync.file.deletingLastPathComponent().appending(path: "Activity") }

    static var deviceID: String {
        if let id = UserDefaults.standard.string(forKey: "activity.deviceID") { return id }
        let id = UUID().uuidString
        if !SystemHooks.isTestRun { UserDefaults.standard.set(id, forKey: "activity.deviceID") }
        return id
    }

    static var deviceName: String { Host.current().localizedName ?? ProcessInfo.processInfo.hostName }

    private init() {
        guard !SystemHooks.isTestRun else { return }
        if let data = try? Data(contentsOf: url), let saved = try? JSONDecoder().decode(ActivityLog.self, from: data) { log = saved }
        loadRemotes()
    }

    func record(_ key: String, count: Int = 1) {
        guard count > 0 else { return }
        log.record(key, count: count)
        scheduleSave()
    }

    /// The narrator reviewed lines the rules or the AI had assigned: kept or changed, and
    /// for AI lines, per model.
    func recordReview(before: [Line], newSpeaker: UUID?) {
        for line in before {
            switch line.source {
            case .none: record(ActivityKey.linesByHand)
            case .user: break
            case .rule, .exchange, .ai:
                let kept = line.speakerID != nil && line.speakerID == newSpeaker
                record(kept ? ActivityKey.guessesKept : ActivityKey.guessesChanged)
                if line.source == .ai, let m = line.aiModel { record(kept ? ActivityKey.aiKept(m) : ActivityKey.aiChanged(m)) }
            }
        }
    }

    // MARK: Totals

    var allMacsAvailable: Bool { SettingsSync.shared.isEnabled }

    func combined(allMacs: Bool) -> ActivityLog {
        allMacs ? ActivityLog.combined([log] + remotes.values.map(\.log)) : log
    }

    var otherMacNames: [String] { remotes.values.map(\.deviceName).sorted() }

    // MARK: Saving and sharing

    private func scheduleSave() {
        guard !SystemHooks.isTestRun else { return }
        saveTask?.cancel()
        saveTask = Task {
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled else { return }
            save()
        }
    }

    private func save() {
        log.trim()
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(log).write(to: url, options: .atomic)
        } catch {
            NSLog("InnerVoice: couldn't save activity: \(error.localizedDescription)")
        }
        share()
    }

    /// This Mac's counts to iCloud Drive, when settings sync is on.
    private func share() {
        guard SettingsSync.shared.isEnabled, SettingsSync.shared.iCloudDriveAvailable else { return }
        let entry = DeviceActivity(deviceID: Self.deviceID, deviceName: Self.deviceName, updatedAt: .now, log: log)
        let folder = shareFolder
        let file = folder.appending(path: Self.deviceID + ".json")
        Task.detached(priority: .utility) {
            let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
            guard let data = try? encoder.encode(entry) else { return }
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            var error: NSError?
            NSFileCoordinator().coordinate(writingItemAt: file, options: .forReplacing, error: &error) { url in
                try? data.write(to: url, options: .atomic)
            }
        }
    }

    /// The other Macs' counts from iCloud Drive.
    func loadRemotes() {
        guard SettingsSync.shared.isEnabled, SettingsSync.shared.iCloudDriveAvailable else { remotes = [:]; return }
        let folder = shareFolder
        let me = Self.deviceID
        Task.detached(priority: .utility) {
            let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
            let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
            var found: [String: DeviceActivity] = [:]
            for f in files where f.pathExtension == "json" && f.deletingPathExtension().lastPathComponent != me {
                var error: NSError?
                NSFileCoordinator().coordinate(readingItemAt: f, options: [], error: &error) { url in
                    if let data = try? Data(contentsOf: url), let a = try? decoder.decode(DeviceActivity.self, from: data) { found[a.deviceID] = a }
                }
            }
            await MainActor.run { ActivityStore.shared.remotes = found }
        }
    }

    /// Test runs start from nothing.
    func resetForTest() {
        guard SystemHooks.isTestRun else { return }
        log = ActivityLog()
    }

    /// Made-up numbers over the last month, for snapshots only.
    func seedForSnapshot() {
        guard SystemHooks.isTestRun else { return }
        var l = ActivityLog()
        let cal = Calendar.current
        for d in 0..<30 {
            guard let day = cal.date(byAdding: .day, value: -d, to: .now), d % 3 != 1 else { continue }
            let n = 40 + (d * 37) % 160
            l.record(ActivityKey.linesAI, count: n, at: day)
            l.record(ActivityKey.linesRules, count: n / 3, at: day)
            l.record(ActivityKey.guessesKept, count: n / 4, at: day)
            l.record(ActivityKey.guessesChanged, count: n / 40 + 1, at: day)
            l.record(ActivityKey.aiKept("Gemma 4 31B"), count: n / 4, at: day)
            l.record(ActivityKey.aiChanged("Gemma 4 31B"), count: n / 40 + 1, at: day)
            l.record(ActivityKey.linesByHand, count: 3, at: day)
            l.record(ActivityKey.tonesAI, count: n / 5, at: day)
            l.record(ActivityKey.tonesKept, count: n / 12, at: day)
            l.record(ActivityKey.tonesChanged, count: 2, at: day)
            l.record(ActivityKey.paragraphsRecorded, count: n / 2, at: day)
            l.record(ActivityKey.secondsPlayed, count: n * 3, at: day)
        }
        l.record(ActivityKey.aiKept("Gemma 4 E4B"), count: 210)
        l.record(ActivityKey.aiChanged("Gemma 4 E4B"), count: 34)
        l.record(ActivityKey.facts, count: 312)
        l.record(ActivityKey.pronunciations, count: 48)
        l.record(ActivityKey.retakesMarked, count: 22)
        l.record(ActivityKey.retakesCleared, count: 19)
        l.record(ActivityKey.linesCarriedOver, count: 1830)
        l.record(ActivityKey.secondsExported, count: 5400)
        l.record(ActivityKey.voicesKept, count: 14)
        l.record(ActivityKey.voicesChanged, count: 5)
        log = l
    }
}
