import Foundation
import InnerVoiceKit
import Observation

/// Claude, Anthropic's cloud model, as a choice for the AI features (Tag Speakers, Build
/// Summaries). Book text leaves the Mac only with a key, the narrator's yes in Settings, AND
/// the book's own `cloudTextAllowed`; `ClaudeTextModel` checks all three before every request.
@MainActor @Observable
final class ClaudeAccount {
    static let shared = ClaudeAccount()

    nonisolated static let host = URL(string: "https://api.anthropic.com")!
    nonisolated static let apiVersion = "2026-10-21"
    static let keysPage = URL(string: "https://platform.claude.com/settings/keys")!
    static let privacyHelp = URL(string: "https://privacy.anthropic.com/en/articles/7996868-is-my-data-used-for-model-training")!
    static let warning = "Claude runs in the cloud: the book text you send goes to Anthropic. Anthropic doesn't train on API data unless your organization has opted in. Check your organization's privacy settings in the Claude Console."
    private static let consentKey = "claude.consent"
    private static let modelsKey = "claude.models"

    struct Model: Codable, Hashable, Sendable {
        var id: String
        var name: String
    }

    /// Built in, so the menu works before the first fetch; `refreshModels` adds new ones.
    nonisolated static let builtIn: [Model] = [
        Model(id: "claude-fable-5-2", name: "Claude Fable 5.2"),
        Model(id: "claude-opus-5-5", name: "Claude Opus 5.5"),
        Model(id: "claude-sonnet-5-5", name: "Claude Sonnet 5.5"),
        Model(id: "claude-haiku-5-5", name: "Claude Haiku 5.5"),
    ]
    nonisolated static let defaultModelID = "claude-sonnet-5-5"

    private(set) var models: [Model] = ClaudeAccount.builtIn
    private(set) var revision = 0

    /// Requests go through this session; the self-test swaps in a fake server.
    @ObservationIgnored var session: URLSession = .shared
    @ObservationIgnored private var testKey: String?
    @ObservationIgnored private var testConsent: Date?
    @ObservationIgnored private var cachedKey: String??
    @ObservationIgnored private var cachedConsent: Date??

    private init() {
        if !SystemHooks.isTestRun, let data = UserDefaults.standard.data(forKey: Self.modelsKey),
           let saved = try? JSONDecoder().decode([Model].self, from: data), !saved.isEmpty {
            models = saved
        }
    }

    func name(of id: String) -> String { models.first { $0.id == id }?.name ?? id }

    var isSetUp: Bool {
        _ = revision
        return apiKey != nil && consented != nil
    }

    var consented: Date? {
        _ = revision
        if SystemHooks.isTestRun { return testConsent }
        if let c = cachedConsent { return c }
        let c = UserDefaults.standard.object(forKey: Self.consentKey) as? Date
        cachedConsent = .some(c)
        return c
    }

    /// Cached: views ask `isSetUp` on every redraw, and a keychain read is a round trip.
    var apiKey: String? {
        _ = revision
        if SystemHooks.isTestRun { return testKey }
        if let k = cachedKey { return k }
        let k = Keychain.read(account: "anthropic")
        cachedKey = .some(k)
        return k
    }

    func save(key: String, consent: Bool) {
        let key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        if SystemHooks.isTestRun {
            testKey = key.isEmpty ? nil : key
            testConsent = consent ? .now : nil
        } else {
            if key.isEmpty { Keychain.delete(account: "anthropic") } else { Keychain.write(key, account: "anthropic") }
            let date = consent ? Date() : nil
            UserDefaults.standard.set(date, forKey: Self.consentKey)
            cachedKey = .some(key.isEmpty ? nil : key)
            cachedConsent = .some(date)
        }
        revision += 1
    }

    /// Forgets the key and the yes. Books keep their permission, but nothing is sent until
    /// Claude is set up again.
    func disconnect() {
        save(key: "", consent: false)
        if ModelManager.shared.selection.hasPrefix("claude:") { ModelManager.shared.selection = "" }
    }

    /// Asks Anthropic which models the key can use (no book text is sent).
    func refreshModels() async throws {
        guard let key = apiKey else { throw ClaudeError.notSetUp }
        var request = URLRequest(url: Self.host.appending(path: "v1/models").appending(queryItems: [URLQueryItem(name: "limit", value: "100")]))
        Self.headers(&request, key: key)
        let (data, response) = try await session.data(for: request)
        try Self.throwIfFailed(response, data)
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = json["data"] as? [[String: Any]] else { throw ClaudeError.badReply }
        let found = list.compactMap { m -> Model? in
            guard let id = m["id"] as? String, id.hasPrefix("claude-") else { return nil }
            return Model(id: id, name: (m["display_name"] as? String) ?? id)
        }
        guard !found.isEmpty else { return }
        models = found
        if !SystemHooks.isTestRun, let data = try? JSONEncoder().encode(found) {
            UserDefaults.standard.set(data, forKey: Self.modelsKey)
        }
    }

    nonisolated static func headers(_ request: inout URLRequest, key: String) {
        request.setValue(key, forHTTPHeaderField: "x-api-key")
        request.setValue(apiVersion, forHTTPHeaderField: "anthropic-version")
        request.setValue("application/json", forHTTPHeaderField: "content-type")
    }

    nonisolated static func throwIfFailed(_ response: URLResponse, _ data: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw ClaudeError.badReply }
        guard !(200..<300).contains(http.statusCode) else { return }
        let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        let message = (json?["error"] as? [String: Any])?["message"] as? String
        switch http.statusCode {
        case 401, 403: throw ClaudeError.badKey
        case 429: throw ClaudeError.rateLimited
        case 529: throw ClaudeError.overloaded
        default: throw ClaudeError.server(http.statusCode, message ?? "")
        }
    }
}

enum ClaudeError: LocalizedError {
    case notSetUp, bookNotAllowed, badKey, rateLimited, overloaded, refused, badReply
    case server(Int, String)
    var errorDescription: String? {
        switch self {
        case .notSetUp: "Set up Claude in Settings › AI first."
        case .bookNotAllowed: "This book hasn't been allowed to use Claude. Nothing was sent."
        case .badKey: "Anthropic didn't accept the API key. Check it in Settings › AI."
        case .rateLimited: "Anthropic asked InnerVoice to slow down. Try again in a minute."
        case .overloaded: "Claude is busy right now. Try again in a few minutes."
        case .refused: "Claude declined this passage."
        case .badReply: "Claude's reply couldn't be read."
        case .server(let code, let m): m.isEmpty ? "Anthropic answered \(code)." : "Anthropic answered \(code): \(m)"
        }
    }
}

/// One Claude model answering the AI features' prompts. Grammars are local-model only and
/// ignored here; replies go through the same parsers.
nonisolated final class ClaudeTextModel: TextModel, @unchecked Sendable {
    let modelID: String
    let displayName: String
    /// Claude takes far more than a local model; bigger passages give it more context.
    let passageWords = 2500
    private let key: String
    private let book: String
    private let session: URLSession
    private let lock = NSLock()
    private var tasks: [URLSessionTask] = []

    /// Throws unless Claude is set up AND this book allows it.
    /// `book`: the book's `usageKey`, for Cloud Usage.
    @MainActor init(modelID: String, bookAllowed: Bool, book: String? = nil) throws {
        let account = ClaudeAccount.shared
        guard let key = account.apiKey, account.consented != nil else { throw ClaudeError.notSetUp }
        guard bookAllowed else { throw ClaudeError.bookNotAllowed }
        self.modelID = modelID
        self.book = book ?? ""
        self.key = key
        displayName = account.name(of: modelID)
        session = account.session
    }

    func complete(_ messages: [ChatMessage], grammar: String?, maxTokens: Int) async throws -> String {
        let system = messages.filter { $0.role == .system }.map(\.content).joined(separator: "\n\n")
        let turns: [[String: Any]] = messages.filter { $0.role != .system }.map {
            ["role": $0.role == .assistant ? "assistant" : "user", "content": $0.content]
        }
        var body: [String: Any] = [
            "model": modelID,
            // Local-model budgets are tight; give Claude room so a reply is never cut short.
            "max_tokens": max(4096, maxTokens * 4),
            "messages": turns,
            "output_config": ["effort": "low"],
        ]
        if !system.isEmpty {
            // The system prompt repeats for every passage of a book: cache it.
            body["system"] = [["type": "text", "text": system, "cache_control": ["type": "ephemeral"]]]
        }
        var request = URLRequest(url: ClaudeAccount.host.appending(path: "v1/messages"))
        request.httpMethod = "POST"
        request.timeoutInterval = 600
        ClaudeAccount.headers(&request, key: key)
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (data, response) = try await send(request)
        try ClaudeAccount.throwIfFailed(response, data)
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw ClaudeError.badReply }
        recordUsage(json, response: response)
        if json["stop_reason"] as? String == "refusal" { throw ClaudeError.refused }
        let blocks = json["content"] as? [[String: Any]] ?? []
        return blocks.filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }.joined()
    }

    private func send(_ request: URLRequest) async throws -> (Data, URLResponse) {
        try await withCheckedThrowingContinuation { continuation in
            let task = session.dataTask(with: request) { data, response, error in
                if let error { continuation.resume(throwing: (error as? URLError)?.code == .cancelled ? CancellationError() : error) }
                else if let data, let response { continuation.resume(returning: (data, response)) }
                else { continuation.resume(throwing: ClaudeError.badReply) }
            }
            lock.withLock { tasks.append(task) }
            task.resume()
        }
    }

    /// Counts what the request used for Cloud Usage (numbers only).
    private func recordUsage(_ json: [String: Any], response: URLResponse) {
        guard let usage = json["usage"] as? [String: Any] else { return }
        var r = CloudUsageRecord(day: CloudUsageRecord.day(), book: book, service: .claude, model: modelID)
        r.inputTokens = usage["input_tokens"] as? Int ?? 0
        r.outputTokens = usage["output_tokens"] as? Int ?? 0
        r.cacheReadTokens = usage["cache_read_input_tokens"] as? Int ?? 0
        r.cacheWriteTokens = usage["cache_creation_input_tokens"] as? Int ?? 0
        r.requests = 1
        let left = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "anthropic-ratelimit-tokens-remaining").flatMap(Int.init)
        let model = modelID
        Task { @MainActor in
            CloudUsageStore.shared.add(r)
            if let left { CloudUsageStore.shared.noteClaudeLimit(model: model, tokensLeft: left) }
        }
    }

    func cancel() {
        lock.withLock { tasks.forEach { $0.cancel() }; tasks.removeAll() }
    }
}
