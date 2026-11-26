import Foundation

/// What each cloud voice service offers (its models and voices), read from the service's
/// own API so new models and voices appear without an InnerVoice update. This keeps what
/// was seen and when, and works out what's new and whether a chosen model was retired.
/// A refresh only updates the lists: the model a narrator chose is never changed for them.
public enum CatalogService: String, CaseIterable, Codable, Hashable, Identifiable, Sendable {
    case elevenlabs, speechify, gemini

    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .elevenlabs: "ElevenLabs"
        case .speechify: "Speechify"
        case .gemini: "Gemini TTS"
        }
    }
}

/// One service's last refresh.
public struct CatalogState: Codable, Equatable, Sendable {
    public var lastRefreshed: Date?
    public var models: [String] = []
    public var voices: [String] = []
    /// Added since the refresh before; cleared once the narrator has seen them.
    public var newModels: [String] = []
    public var newVoices: [String] = []

    public init() {}

    /// Records a refresh. `nil` means that list wasn't fetched (left as it was). The first
    /// refresh of a list marks nothing new: everything was already there.
    public mutating func record(models fetchedModels: [String]?, voices fetchedVoices: [String]?, at date: Date) {
        if let m = fetchedModels {
            if lastRefreshed != nil, !models.isEmpty {
                let old = Set(models)
                newModels = Self.merge(newModels.filter { m.contains($0) }, m.filter { !old.contains($0) })
            }
            models = m
        }
        if let v = fetchedVoices {
            if lastRefreshed != nil, !voices.isEmpty {
                let old = Set(voices)
                newVoices = Self.merge(newVoices.filter { v.contains($0) }, v.filter { !old.contains($0) })
            }
            voices = v
        }
        lastRefreshed = date
    }

    public mutating func markSeen() { newModels = []; newVoices = [] }

    private static func merge(_ a: [String], _ b: [String]) -> [String] {
        var seen = Set<String>()
        return (a + b).filter { seen.insert($0).inserted }
    }

    /// "2 new models, 5 new voices", or nil.
    public var newSummary: String? {
        var parts: [String] = []
        if !newModels.isEmpty { parts.append("\(newModels.count) new model\(newModels.count == 1 ? "" : "s")") }
        if !newVoices.isEmpty { parts.append("\(newVoices.count) new voice\(newVoices.count == 1 ? "" : "s")") }
        return parts.isEmpty ? nil : parts.joined(separator: ", ")
    }

    /// The chosen model is retired when a refresh listed models and it isn't among them.
    public func isRetired(_ chosen: String) -> Bool {
        lastRefreshed != nil && !models.isEmpty && !models.contains(chosen)
    }
}

public enum ServiceCatalog {
    /// Lists are refreshed once a day (and on the narrator's click).
    public static let interval: TimeInterval = 24 * 60 * 60

    public static func isDue(_ state: CatalogState?, now: Date = .now) -> Bool {
        guard let last = state?.lastRefreshed else { return true }
        return now.timeIntervalSince(last) >= interval || now < last
    }

    /// The closest model to suggest when the chosen one is retired: the newest one that
    /// shares its family name (text before the version), else the first listed.
    public static func suggestion(for retired: String, in models: [String]) -> String? {
        let family = retired.replacingOccurrences(of: #"[_-]?v?\d[\w.]*$"#, with: "", options: .regularExpression)
        return models.first { !family.isEmpty && $0.hasPrefix(family) } ?? models.first
    }

    /// Google's text-to-speech model IDs from `GET v1beta/models` (names like
    /// "models/gemini-3.8-flash-tts"), in Google's order.
    public static func geminiTTSModels(_ data: Data) -> [String]? {
        struct Reply: Decodable { struct M: Decodable { var name: String }; var models: [M]? }
        guard let r = try? JSONDecoder().decode(Reply.self, from: data) else { return nil }
        let ids = (r.models ?? []).map { $0.name.hasPrefix("models/") ? String($0.name.dropFirst(7)) : $0.name }
            .filter { $0.hasSuffix("-tts") || $0.contains("-tts-") }
        return ids
    }
}
