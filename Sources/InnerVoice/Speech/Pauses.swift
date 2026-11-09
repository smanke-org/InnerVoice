import Foundation
import InnerVoiceKit
import Observation

/// Settings › Voices › Pauses: how long each gap is, and how fast Play plays. Kept per Mac.
/// Changes apply to the next gap, even mid-play, since the player looks each one up as it
/// goes; the audio itself isn't remade.
@Observable
@MainActor
final class PauseStore {
    static let shared = PauseStore()
    private static let key = "voices.pauses"
    private static let speedKey = "voices.playbackSpeed"

    var settings: PauseSettings {
        didSet {
            PauseSettings.current = settings
            // Test runs share the person's preferences: never save theirs.
            if !SystemHooks.isTestRun, let data = try? JSONEncoder().encode(settings) { UserDefaults.standard.set(data, forKey: Self.key) }
        }
    }

    /// Play's speed, 0.5× to 2×. Pauses scale with it. Export Audio is always 1×.
    var speed: Double {
        didSet {
            SpeechPlayer.shared.speed = speed
            if !SystemHooks.isTestRun { UserDefaults.standard.set(speed, forKey: Self.speedKey) }
        }
    }

    private init() {
        let saved = UserDefaults.standard.data(forKey: Self.key).flatMap { try? JSONDecoder().decode(PauseSettings.self, from: $0) }
        settings = SystemHooks.isTestRun ? .standard : saved ?? .standard
        speed = SystemHooks.isTestRun ? 1 : (UserDefaults.standard.object(forKey: Self.speedKey) as? Double ?? 1)
        PauseSettings.current = settings
    }

    /// The preset these settings match, if any.
    var preset: String? {
        switch settings {
        case .tight: "Tight"
        case .standard: "Natural"
        case .relaxed: "Relaxed"
        default: nil
        }
    }

    static let presets: [(String, PauseSettings)] = [("Tight", .tight), ("Natural", .standard), ("Relaxed", .relaxed)]
}
