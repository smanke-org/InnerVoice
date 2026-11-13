import Foundation

/// What a Claude model costs, in US dollars per million tokens.
public struct ClaudePrice: Hashable, Sendable {
    public var input: Double
    public var output: Double
    public init(input: Double, output: Double) {
        self.input = input
        self.output = output
    }
}

public enum ClaudePricing {
    /// Anthropic's list prices for the built-in models. A model that isn't listed (new ones
    /// come from the Models API) shows tokens only.
    public static let perMillion: [String: ClaudePrice] = [
        "claude-fable-5-2": ClaudePrice(input: 8, output: 40),
        "claude-opus-5-5": ClaudePrice(input: 4, output: 20),
        "claude-sonnet-5-5": ClaudePrice(input: 2, output: 10),
        "claude-haiku-5-5": ClaudePrice(input: 0.10, output: 0.50),
    ]

    public static func price(for modelID: String) -> ClaudePrice? { perMillion[modelID] }
}

/// A rough count of what a Claude run sends and gets back, worked out on the Mac from the
/// text it would send. Never asks Anthropic (counting tokens there would send the book).
public struct CostEstimate: Hashable, Sendable {
    public var inputTokens: Int
    public var outputTokens: Int

    public init(inputTokens: Int = 0, outputTokens: Int = 0) {
        self.inputTokens = inputTokens
        self.outputTokens = outputTokens
    }

    public static func + (a: CostEstimate, b: CostEstimate) -> CostEstimate {
        CostEstimate(inputTokens: a.inputTokens + b.inputTokens, outputTokens: a.outputTokens + b.outputTokens)
    }

    public var totalTokens: Int { inputTokens + outputTokens }

    /// nil for a model without a known price.
    public func dollars(modelID: String) -> Double? {
        guard let p = ClaudePricing.price(for: modelID) else { return nil }
        return Double(inputTokens) / 1_000_000 * p.input + Double(outputTokens) / 1_000_000 * p.output
    }

    /// "about $1.20", "less than $0.01", or "about 120,000 tokens" when the price isn't known.
    public func phrase(modelID: String) -> String {
        guard let d = dollars(modelID: modelID) else {
            return "about \(Self.roundTokens(totalTokens).formatted()) tokens"
        }
        if d < 0.01 { return "less than $0.01" }
        return "about " + Self.money(d)
    }

    /// One sentence for a confirmation: "Estimated cost: about $1.20 (about 450,000 tokens).
    /// This is an estimate; your bill may differ."
    public func sentence(modelID: String) -> String {
        let tokens = Self.roundTokens(totalTokens).formatted()
        if ClaudePricing.price(for: modelID) != nil {
            return "Estimated cost: \(phrase(modelID: modelID)) (about \(tokens) tokens). This is an estimate made on your Mac; your bill may differ."
        }
        return "Estimated size: about \(tokens) tokens. InnerVoice doesn’t know this model’s price; check it in the Claude Console. This is an estimate; your bill may differ."
    }

    static func money(_ d: Double) -> String {
        d < 10 ? String(format: "$%.2f", d) : "$" + Int(d.rounded()).formatted()
    }

    static func roundTokens(_ n: Int) -> Int {
        n < 1000 ? max(n, 0) : Int((Double(n) / 1000).rounded()) * 1000
    }
}

public enum CostEstimator {
    /// About four characters of English prose to a token, plus a margin for the newer
    /// tokenizer, which uses more.
    public static func tokens(_ text: String) -> Int {
        Int((Double(text.utf16.count) / 4 * 1.2).rounded(.up))
    }

    public static func tokens(words: Int) -> Int { Int((Double(words) * 1.6).rounded(.up)) }

    /// Claude thinks before it answers, and thinking is billed as output: allow for it.
    public static let thinking = 2.0

    /// Requests whose text is known: every message counts as input, and each reply is about
    /// `output` tokens (before thinking).
    public static func requests(_ messages: [[ChatMessage]], outputEach output: Int) -> CostEstimate {
        let input = messages.reduce(0) { $0 + $1.reduce(0) { $0 + tokens($1.content) + 4 } }
        return CostEstimate(inputTokens: input, outputTokens: Int(Double(output * messages.count) * thinking))
    }

    /// Tag Speakers over these chapters (all when nil): each passage is read twice, once for
    /// speakers and once for tones, with the instructions each time.
    public static func tagSpeakers(project: Project, chapters: [Int]? = nil, passageWords: Int = 2500) -> CostEstimate {
        let picked = (chapters ?? Array(project.chapters.indices)).filter { project.chapters.indices.contains($0) }
        let ids = Set(picked.flatMap { project.chapters[$0].paragraphs.map(\.id) })
        let words = picked.reduce(0) { $0 + project.chapters[$1].wordCount }
        let lines = project.lines.filter { ids.contains($0.paragraphID) }.count
        let passages = picked.reduce(0) { $0 + max(1, Int((Double(project.chapters[$1].wordCount) / Double(passageWords)).rounded(.up))) }
        let overhead = tokens(AttributionPrompt.system) + tokens(TonePrompt.system) + 2 * 400 // instructions + cast list
        let input = 2 * tokens(words: words) + passages * overhead
        let output = 40 * passages * 2 + lines * (34 + 22)
        return CostEstimate(inputTokens: input, outputTokens: Int(Double(output) * thinking))
    }

    /// Build Summaries for `characters` characters: up to four passages of the book about
    /// each (about 2,700 words in all), then a voice brief.
    public static func buildSummaries(project: Project, characters: Int, passageWords: Int = 2500) -> CostEstimate {
        let paragraphs = max(1, project.chapters.reduce(0) { $0 + $1.paragraphs.count })
        let perParagraph = Double(project.wordCount) / Double(paragraphs)
        let wordsEach = min(Double(passageWords + 200), perParagraph * 48)
        let readEach = tokens(words: Int(wordsEach)) + tokens(SummaryPrompt.system) + 150
        let briefEach = tokens(VoiceBriefPrompt.system) + 900
        let input = characters * (readEach + briefEach)
        let output = characters * (700 + 260)
        return CostEstimate(inputTokens: input, outputTokens: Int(Double(output) * thinking))
    }
}
