import InnerVoiceKit
import Observation
import SwiftUI

/// The voice engines this Mac can speak with, and the default for new books (Settings ›
/// Voices). Each book speaks with one engine (`ProjectSettings.voiceEngine`): previews, table
/// reads and exports all use it. Mix Engines is gone (2026-11-15): a book saved with it asks
/// which one engine to use when it opens. A local engine is available once downloaded, a
/// cloud engine once it's set up (a key and the narrator's yes). Kept per Mac.
@Observable
@MainActor
final class EngineSwitches {
    static let shared = EngineSwitches()
    static let defaultKey = "voices.defaultEngine"
    /// Before 2026-11-15: engines the narrator had switched off (read once, to pick a default).
    static let oldOffKey = "voices.offEngines"

    /// The engine new books start with.
    private(set) var defaultEngine: VoiceEngine

    private init() {
        if SystemHooks.isTestRun {
            defaultEngine = .apple
        } else if let raw = UserDefaults.standard.string(forKey: Self.defaultKey), let e = VoiceEngine(rawValue: raw), e != .mixed {
            defaultEngine = e
        } else {
            // First run of this version: the best engine that was switched on and downloaded.
            let off = Set((UserDefaults.standard.string(forKey: Self.oldOffKey) ?? "").split(separator: ",").compactMap { VoiceEngine(rawValue: String($0)) })
            defaultEngine = [VoiceEngine.qwen, .pocket, .chatterbox, .orpheus, .apple].first { !off.contains($0) && ModelSpeech.isInstalled($0) } ?? .apple
        }
    }

    func setDefault(_ engine: VoiceEngine) {
        guard engine != .mixed else { return }
        defaultEngine = engine
        if !SystemHooks.isTestRun { UserDefaults.standard.set(engine.rawValue, forKey: Self.defaultKey) }
    }

    /// Whether the engine can be chosen: a cloud engine only once it's set up.
    func isOn(_ engine: VoiceEngine) -> Bool {
        switch engine {
        case .mixed: return false
        case .elevenlabs: return ElevenLabs.shared.isSetUp
        case .gemini: return GeminiVoices.shared.isSetUp
        case .speechify: return SpeechifyVoices.shared.isSetUp
        default: return true
        }
    }

    var enabled: [VoiceEngine] { VoiceEngine.engines.filter(isOn) }

    /// Available and downloaded: its voices can be used now.
    func usable(_ engine: VoiceEngine) -> Bool { isOn(engine) && ModelSpeech.isInstalled(engine) }

    /// What engine menus offer: one engine per book, never a mix.
    var choices: [VoiceEngine] { VoiceEngine.engines.filter(usable) }

    /// Kept for setup code that "turns an engine on": every set-up engine is available now.
    func set(_ engine: VoiceEngine, on: Bool) {}

    /// The engine a book speaks with: its own. A book saved with Mix Engines uses the
    /// default until the narrator picks one (the window asks); a cloud engine that's no
    /// longer set up falls back to the default (or Apple Voices). A cloud engine the book
    /// hasn't allowed stays chosen: playing asks for permission first (`CloudGate`).
    func inUse(_ project: Project) -> VoiceEngine {
        let chosen = project.settings.engine
        let fallback = defaultEngine.isCloud && !isOn(defaultEngine) ? .apple : defaultEngine
        if chosen == .mixed { return fallback }
        return isOn(chosen) ? chosen : (fallback.isCloud ? .apple : fallback)
    }
}

extension Project {
    /// The engine this project speaks with now (see `EngineSwitches`).
    @MainActor var engineInUse: VoiceEngine { EngineSwitches.shared.inUse(self) }
}
