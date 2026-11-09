import InnerVoiceKit
import Observation
import SwiftUI

/// Which voice engines are turned on (Settings › Voices). A turned-off engine isn't
/// offered on the Voices page, and a project set to it uses the first engine that's on,
/// without changing the project, so turning it back on brings its casting back. At least
/// one engine stays on. Kept per Mac, like the downloaded models.
@Observable
@MainActor
final class EngineSwitches {
    static let shared = EngineSwitches()
    static let key = "voices.offEngines"

    private(set) var off: Set<VoiceEngine>

    private init() {
        // Test runs share the person's preferences: they start with every engine on and
        // never write the choice back.
        if SystemHooks.isTestRun {
            off = []
        } else {
            let raw = UserDefaults.standard.string(forKey: Self.key) ?? ""
            off = Set(raw.split(separator: ",").compactMap { VoiceEngine(rawValue: String($0)) })
            if off.count >= VoiceEngine.allCases.count { off = [] }
        }
    }

    func isOn(_ engine: VoiceEngine) -> Bool { !off.contains(engine) }

    var enabled: [VoiceEngine] { VoiceEngine.allCases.filter(isOn) }

    /// The last engine that's on can't be turned off.
    func canTurnOff(_ engine: VoiceEngine) -> Bool { isOn(engine) && enabled.count > 1 }

    func set(_ engine: VoiceEngine, on: Bool) {
        if on { off.remove(engine) } else {
            guard canTurnOff(engine) else { return }
            off.insert(engine)
            // Free the memory a turned-off model engine was using.
            if engine != .apple { Task { await ModelSpeech.unload() } }
        }
        if !SystemHooks.isTestRun {
            UserDefaults.standard.set(VoiceEngine.allCases.filter { off.contains($0) }.map(\.rawValue).joined(separator: ","), forKey: Self.key)
        }
    }

    func binding(_ engine: VoiceEngine) -> Binding<Bool> {
        Binding(get: { self.isOn(engine) }, set: { self.set(engine, on: $0) })
    }

    /// The engine a project speaks with: its own choice while that's on.
    func inUse(_ project: Project) -> VoiceEngine {
        let chosen = project.settings.engine
        return isOn(chosen) ? chosen : enabled.first ?? chosen
    }
}

extension Project {
    /// The engine this project speaks with now (see `EngineSwitches`).
    @MainActor var engineInUse: VoiceEngine { EngineSwitches.shared.inUse(self) }
}
