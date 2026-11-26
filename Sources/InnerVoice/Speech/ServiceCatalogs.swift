import Foundation
import InnerVoiceKit
import Observation

/// Keeps each cloud voice service's models and voices current from its own API: at launch
/// when a day has passed, once a day while the app runs, and on Refresh in Settings ›
/// Voices. Only services that are set up are asked, and only for lists (no book text is
/// sent). A refresh never changes the model the narrator chose; if that model disappears,
/// Settings says so and suggests the closest one.
///
/// To add a service: a `CatalogService` case (Kit) and a branch in `fetch(_:)`.
@Observable @MainActor
final class ServiceCatalogs {
    static let shared = ServiceCatalogs()

    private static let stateKey = "catalog.state"
    private(set) var states: [CatalogService: CatalogState] = [:]
    private(set) var refreshing: Set<CatalogService> = []
    private(set) var failures: [CatalogService: String] = [:]
    @ObservationIgnored private var timer: Task<Void, Never>?

    private init() {
        if !SystemHooks.isTestRun, let data = UserDefaults.standard.data(forKey: Self.stateKey),
           let saved = try? JSONDecoder().decode([String: CatalogState].self, from: data) {
            for (k, v) in saved { if let s = CatalogService(rawValue: k) { states[s] = v } }
        }
    }

    func state(_ s: CatalogService) -> CatalogState { states[s] ?? CatalogState() }

    func isSetUp(_ s: CatalogService) -> Bool {
        switch s {
        case .elevenlabs: ElevenLabs.shared.isSetUp
        case .speechify: SpeechifyVoices.shared.isSetUp
        case .gemini: GeminiVoices.shared.isSetUp
        }
    }

    /// The model this Mac uses for a service.
    func chosenModel(_ s: CatalogService) -> String? {
        switch s {
        case .elevenlabs: ElevenLabs.modelID
        case .gemini: GeminiVoices.shared.modelID
        case .speechify: nil // one model (simba), not listed by Speechify's API
        }
    }

    /// "Eleven v4 is no longer offered by ElevenLabs. Choose another model; Eleven v5 is closest."
    func retiredWarning(_ s: CatalogService) -> String? {
        guard isSetUp(s), let chosen = chosenModel(s), state(s).isRetired(chosen) else { return nil }
        let name: (String) -> String = { id in
            s == .elevenlabs ? (ElevenLabs.shared.models.first { $0.id == id }?.name ?? id) : GeminiTTS.Model.title(for: id)
        }
        var text = "\(name(chosen)) is no longer offered by \(s.title), so it may stop working. Choose another model"
        if let next = ServiceCatalog.suggestion(for: chosen, in: state(s).models) { text += "; \(name(next)) is closest" }
        return text + "."
    }

    /// Starts the daily refresh (not in test runs).
    func start() {
        guard !SystemHooks.isTestRun, timer == nil else { return }
        timer = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refreshAll(force: false)
                try? await Task.sleep(for: .seconds(60 * 60))
            }
        }
    }

    /// Refreshes every set-up service whose lists are a day old (or all, when forced).
    func refreshAll(force: Bool) async {
        for s in CatalogService.allCases where isSetUp(s) && (force || ServiceCatalog.isDue(states[s])) {
            await refresh(s)
        }
    }

    func refresh(_ s: CatalogService) async {
        guard isSetUp(s), !refreshing.contains(s) else { return }
        refreshing.insert(s)
        defer { refreshing.remove(s) }
        do {
            let (models, voices) = try await fetch(s)
            var st = state(s)
            st.record(models: models, voices: voices, at: .now)
            states[s] = st
            failures[s] = nil
            save()
        } catch {
            failures[s] = error.localizedDescription
        }
    }

    /// The narrator has looked at what's new.
    func markSeen(_ s: CatalogService) {
        guard var st = states[s], st.newSummary != nil else { return }
        st.markSeen()
        states[s] = st
        save()
    }

    private func fetch(_ s: CatalogService) async throws -> (models: [String]?, voices: [String]?) {
        switch s {
        case .elevenlabs:
            let eleven = ElevenLabs.shared
            try await eleven.refreshModels()
            let voices = try await eleven.refreshVoices()
            return (eleven.models.map(\.id), voices.map(\.voiceID))
        case .speechify:
            let speechify = SpeechifyVoices.shared
            try await speechify.refreshVoices()
            return (nil, speechify.voices.map(\.voiceID))
        case .gemini:
            // Google has no voice list to read; its 30 voices are built in.
            return (try await GeminiVoices.shared.fetchModels(), nil)
        }
    }

    private func save() {
        guard !SystemHooks.isTestRun else { return }
        let dict = Dictionary(uniqueKeysWithValues: states.map { ($0.key.rawValue, $0.value) })
        if let data = try? JSONEncoder().encode(dict) { UserDefaults.standard.set(data, forKey: Self.stateKey) }
    }
}
