import Foundation

/// What the cloud services used, per day, book and model: numbers only, never book text.
public struct CloudUsageRecord: Codable, Hashable, Sendable {
    public enum Service: String, Codable, Sendable { case claude, elevenlabs }

    /// "yyyy-MM-dd" in the Mac's time zone.
    public var day: String
    /// `Project.usageKey` of the book it was for.
    public var book: String
    public var service: Service
    public var model: String
    public var inputTokens = 0
    public var outputTokens = 0
    public var cacheReadTokens = 0
    public var cacheWriteTokens = 0
    /// ElevenLabs bills by characters.
    public var characters = 0
    public var requests = 0

    public init(day: String, book: String, service: Service, model: String) {
        self.day = day
        self.book = book
        self.service = service
        self.model = model
    }

    public var totalTokens: Int { inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens }

    /// Dollars at Anthropic's list prices (cache reads 0.1×, cache writes 1.25× the input
    /// price). nil for ElevenLabs and for models without a known price.
    public var cost: Double? {
        guard service == .claude, let p = ClaudePricing.price(for: model) else { return nil }
        let input = Double(inputTokens) + Double(cacheReadTokens) * 0.1 + Double(cacheWriteTokens) * 1.25
        return (input * p.input + Double(outputTokens) * p.output) / 1_000_000
    }

    public mutating func add(_ other: CloudUsageRecord) {
        inputTokens += other.inputTokens
        outputTokens += other.outputTokens
        cacheReadTokens += other.cacheReadTokens
        cacheWriteTokens += other.cacheWriteTokens
        characters += other.characters
        requests += other.requests
    }

    public static func day(_ date: Date = .now) -> String {
        let f = DateFormatter()
        f.calendar = Calendar(identifier: .gregorian)
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd"
        return f.string(from: date)
    }
}

public enum CloudUsage {
    /// Adds a request to the list, folding it into the same day, book and model.
    public static func merge(_ new: CloudUsageRecord, into list: inout [CloudUsageRecord]) {
        if let i = list.firstIndex(where: { $0.day == new.day && $0.book == new.book && $0.service == new.service && $0.model == new.model }) {
            list[i].add(new)
        } else {
            list.append(new)
        }
    }

    /// One total per service and model, for one book (or all books with nil).
    public static func totals(_ list: [CloudUsageRecord], book: String?) -> [CloudUsageRecord] {
        var out: [CloudUsageRecord] = []
        for r in list where book == nil || r.book == book {
            var t = r
            t.day = ""
            t.book = book ?? ""
            merge(t, into: &out)
        }
        return out.sorted { ($0.service.rawValue, $0.model) < ($1.service.rawValue, $1.model) }
    }

    /// Per day and model, for the chart, oldest first.
    public static func byDay(_ list: [CloudUsageRecord], book: String?) -> [CloudUsageRecord] {
        var out: [CloudUsageRecord] = []
        for r in list where book == nil || r.book == book {
            var t = r
            t.book = book ?? ""
            merge(t, into: &out)
        }
        return out.sorted { ($0.day, $0.model) < ($1.day, $1.model) }
    }
}

public extension Project {
    /// Names this book in the usage records without changing the file: the title and when
    /// it was imported, which don't change.
    var usageKey: String { "\(title)|\(Int(importedAt.timeIntervalSince1970))" }
}
