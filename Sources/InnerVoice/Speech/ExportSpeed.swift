import InnerVoiceKit
import SwiftUI

/// How many lines Export Audio makes at once, chosen per engine (Settings › Voices and the
/// Export Audio sheet). One at a time is slowest and always right; more at once is faster
/// but might not work with every voice.
enum ExportSpeed: String, CaseIterable, Identifiable {
    case slowest, faster, fastest
    var id: String { rawValue }

    var title: String {
        switch self {
        case .slowest: "Slowest but most reliable"
        case .faster: "Faster"
        case .fastest: "Fastest (may not work with every voice)"
        }
    }

    /// Lines at once.
    func linesAtOnce(_ engine: VoiceEngine) -> Int {
        switch self {
        case .slowest: 1
        case .faster: 2
        case .fastest: engine == .apple ? 3 : 4
        }
    }

    static func key(_ engine: VoiceEngine) -> String { "audioExport.speed." + engine.rawValue }

    /// ElevenLabs gains the most (it waits on the network) and works at full speed; the
    /// voices on this Mac gain little, so they start one at a time.
    static func standard(_ engine: VoiceEngine) -> ExportSpeed { engine == .elevenlabs ? .fastest : .slowest }

    static func current(_ engine: VoiceEngine) -> ExportSpeed {
        UserDefaults.standard.string(forKey: key(engine)).flatMap(ExportSpeed.init(rawValue:)) ?? standard(engine)
    }

    static let engines: [VoiceEngine] = [.apple, .pocket, .orpheus, .elevenlabs]

    /// The speed settings go to the person's other Macs with their other settings.
    static func registerSync() {
        SettingsSync.register(Dictionary(uniqueKeysWithValues: engines.map { (key($0), standard($0).rawValue as Any) }))
    }
}

/// One picker per engine: in Settings › Voices, and in Export Audio for the book's engines.
struct ExportSpeedPicker: View {
    let engine: VoiceEngine
    @AppStorage private var raw: String

    init(engine: VoiceEngine) {
        self.engine = engine
        _raw = AppStorage(wrappedValue: ExportSpeed.standard(engine).rawValue, ExportSpeed.key(engine))
    }

    var body: some View {
        Picker(engine.title, selection: $raw) {
            ForEach(ExportSpeed.allCases) { Text($0.title).tag($0.rawValue) }
        }
        .help("How many lines Export Audio makes at once with \(engine.title). Slowest makes one at a time.")
    }
}
