import Foundation

/// The per-day counts InnerVoice keeps for Settings › Activity: numbers only, never anything
/// from a book. Days are "yyyy-MM-dd" in the Mac's time zone. Each count has a key from
/// `ActivityKey`; AI accuracy is kept per model as "aiKept|<model>" and "aiChanged|<model>".
public struct ActivityLog: Codable, Equatable, Sendable {
    public var firstDay: String?
    /// Day → key → count.
    public var days: [String: [String: Int]]
    /// Key → count since counting began (old days are trimmed).
    public var lifetime: [String: Int]

    public init(firstDay: String? = nil, days: [String: [String: Int]] = [:], lifetime: [String: Int] = [:]) {
        self.firstDay = firstDay
        self.days = days
        self.lifetime = lifetime
    }

    public static func key(for date: Date, calendar: Calendar = .current) -> String {
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    public static func date(for key: String, calendar: Calendar = .current) -> Date? {
        let parts = key.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3 else { return nil }
        return calendar.date(from: DateComponents(year: parts[0], month: parts[1], day: parts[2]))
    }

    public mutating func record(_ key: String, count: Int = 1, at date: Date = Date(), calendar: Calendar = .current) {
        guard count > 0 else { return }
        let day = Self.key(for: date, calendar: calendar)
        lifetime[key, default: 0] += count
        days[day, default: [:]][key, default: 0] += count
        if firstDay.map({ day < $0 }) ?? true { firstDay = day }
    }

    /// Keeps about 400 days; lifetime totals and `firstDay` stay.
    public mutating func trim(keepDays: Int = 400, now: Date = Date(), calendar: Calendar = .current) {
        guard let cutoff = calendar.date(byAdding: .day, value: -keepDays, to: calendar.startOfDay(for: now)) else { return }
        let cutoffKey = Self.key(for: cutoff, calendar: calendar)
        days = days.filter { $0.key >= cutoffKey }
    }

    /// Totals per key since `start` (nil: since counting began).
    public func totals(since start: Date? = nil, calendar: Calendar = .current) -> [String: Int] {
        guard let start else { return lifetime }
        let startKey = Self.key(for: start, calendar: calendar)
        var result: [String: Int] = [:]
        for (key, perKey) in days where key >= startKey {
            for (k, n) in perKey { result[k, default: 0] += n }
        }
        return result
    }

    /// Per-day totals of the given keys, for the graph.
    public func counts(keys: Set<String>, calendar: Calendar = .current) -> [Date: Int] {
        var result: [Date: Int] = [:]
        for (key, perKey) in days {
            guard let date = Self.date(for: key, calendar: calendar) else { continue }
            let total = perKey.filter { keys.contains($0.key) }.values.reduce(0, +)
            if total > 0 { result[date, default: 0] += total }
        }
        return result
    }

    /// Several Macs' logs added together (All Macs). Each Mac's counts are whole-day totals,
    /// so adding them never counts anything twice.
    public static func combined(_ logs: [ActivityLog]) -> ActivityLog {
        var result = ActivityLog()
        for log in logs {
            for (day, perKey) in log.days {
                for (k, n) in perKey { result.days[day, default: [:]][k, default: 0] += n }
            }
            for (k, n) in log.lifetime { result.lifetime[k, default: 0] += n }
            if let f = log.firstDay, result.firstDay.map({ f < $0 }) ?? true { result.firstDay = f }
        }
        return result
    }
}

/// What InnerVoice counts.
public enum ActivityKey {
    // Lines
    /// Lines given a speaker by the rules ("said Tom") or the back-and-forth.
    public static let linesRules = "linesRules"
    /// Lines given a speaker by the AI.
    public static let linesAI = "linesAI"
    /// Lines the narrator assigned that nothing had assigned.
    public static let linesByHand = "linesByHand"
    /// Reviewing a guess: the speaker the rules or the AI picked was kept, or changed.
    public static let guessesKept = "guessesKept", guessesChanged = "guessesChanged"
    /// Tones read by the AI, and tones the narrator kept or changed.
    public static let tonesAI = "tonesAI", tonesKept = "tonesKept", tonesChanged = "tonesChanged"
    // Narration tools
    public static let facts = "facts", pronunciations = "pronunciations"
    public static let paragraphsRecorded = "paragraphsRecorded", retakesMarked = "retakesMarked", retakesCleared = "retakesCleared"
    /// Lines whose tags carried over to a revised manuscript.
    public static let linesCarriedOver = "linesCarriedOver"
    // AI voices
    public static let secondsPlayed = "secondsPlayed", secondsExported = "secondsExported"
    public static let voicesKept = "voicesKept", voicesChanged = "voicesChanged"
    public static let elevenLabsCharacters = "elevenLabsCharacters"
    // Per model
    public static func aiKept(_ model: String) -> String { "aiKept|" + model }
    public static func aiChanged(_ model: String) -> String { "aiChanged|" + model }

    /// Lines tagged for the narrator: by the rules or the AI.
    public static let taggedForYou: Set<String> = [linesRules, linesAI]
    /// About how long finding and marking one line's speaker by hand takes, for the
    /// time-saved estimate (labeled as an estimate wherever it shows).
    public static let secondsPerLineByHand = 6.0
}

/// The AI's accuracy on one model: of the guesses the narrator reviewed, how many were kept.
public struct ModelAccuracy: Hashable, Identifiable, Sendable {
    public var model: String
    public var kept: Int
    public var changed: Int
    public var id: String { model }
    public var reviewed: Int { kept + changed }
    public var rate: Double { reviewed == 0 ? 0 : Double(kept) / Double(reviewed) }

    /// Models from a log's totals, most reviewed first.
    public static func all(in totals: [String: Int]) -> [ModelAccuracy] {
        var byModel: [String: ModelAccuracy] = [:]
        for (k, n) in totals {
            if k.hasPrefix("aiKept|") {
                let m = String(k.dropFirst(7)); byModel[m, default: ModelAccuracy(model: m, kept: 0, changed: 0)].kept += n
            } else if k.hasPrefix("aiChanged|") {
                let m = String(k.dropFirst(10)); byModel[m, default: ModelAccuracy(model: m, kept: 0, changed: 0)].changed += n
            }
        }
        return byModel.values.sorted { $0.reviewed > $1.reviewed }
    }
}

/// One Mac's activity, as shared with the person's other Macs through iCloud Drive. Each Mac
/// writes only its own file, with whole-day totals, so a stale or repeated file never counts
/// anything twice.
public struct DeviceActivity: Codable, Equatable, Sendable {
    public var deviceID: String
    public var deviceName: String
    public var updatedAt: Date
    public var log: ActivityLog

    public init(deviceID: String, deviceName: String, updatedAt: Date, log: ActivityLog) {
        self.deviceID = deviceID
        self.deviceName = deviceName
        self.updatedAt = updatedAt
        self.log = log
    }
}
