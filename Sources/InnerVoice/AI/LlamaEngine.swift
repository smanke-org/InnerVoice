import InnerVoiceKit
import Foundation
import llama

/// A loaded GGUF model and one llama.cpp context, used from a single serial queue.
///
/// Consecutive passages share the same instructions, so the engine keeps the tokens in
/// the KV cache and only evaluates what changed.
nonisolated final class LlamaEngine: @unchecked Sendable {
    enum EngineError: LocalizedError {
        case loadFailed(String), contextFailed, loraFailed(String), tokenizeFailed, decodeFailed

        var errorDescription: String? {
            switch self {
            case .loadFailed(let path): "Couldn't load the model at \(path)."
            case .contextFailed: "Couldn't create a model context. The Mac may be low on memory."
            case .loraFailed(let path): "Couldn't load the LoRA adapter at \(path)."
            case .tokenizeFailed: "Couldn't tokenize the prompt."
            case .decodeFailed: "The model failed while evaluating the prompt."
            }
        }
    }

    struct Options: Equatable {
        var contextLength: UInt32 = 8192
        var loraPath: String?
        var loraScale: Float = 1.0
    }

    let queue = DispatchQueue(label: "InnerVoice.llama", qos: .userInitiated)

    private(set) var modelPath: String?
    private(set) var template: ChatTemplate = .plain
    private(set) var closeThinking = false
    private(set) var description = ""

    private var model: OpaquePointer?
    private var ctx: OpaquePointer?
    private var lora: OpaquePointer?
    private var options = Options()
    /// Tokens whose state is in the KV cache, in order from position 0.
    private var cached: [llama_token] = []
    private var batchSize: Int32 = 512

    /// Bumped by `cancel()`; a generation stops when it no longer matches.
    private let cancelLock = NSLock()
    private var generation = 0

    static let backendOnce: Void = {
        llama_log_set({ level, text, _ in
            // Only errors are worth the noise; llama.cpp logs every tensor at load otherwise.
            guard level == GGML_LOG_LEVEL_ERROR, let text else { return }
            NSLog("llama: %@", String(cString: text).trimmingCharacters(in: .whitespacesAndNewlines))
        }, nil)
        llama_backend_init()
    }()

    deinit { unloadLocked() }

    var isLoaded: Bool { ctx != nil }

    /// Loads a model, replacing the current one. Call on `queue`.
    func load(path: String, options: Options) throws {
        _ = Self.backendOnce
        unloadLocked()
        var mp = llama_model_default_params()
        mp.n_gpu_layers = 999
        guard let m = llama_model_load_from_file(path, mp) else { throw EngineError.loadFailed(path) }
        model = m
        var cp = llama_context_default_params()
        cp.n_ctx = options.contextLength
        cp.n_batch = 512
        cp.n_ubatch = 512
        // Sliding-window caches can't drop a tail of positions; the full-size cache can,
        // which keeps prefix reuse working on Gemma.
        cp.swa_full = true
        let threads = Int32(max(2, min(8, ProcessInfo.processInfo.activeProcessorCount - 2)))
        cp.n_threads = threads
        cp.n_threads_batch = threads
        guard let c = llama_init_from_model(m, cp) else {
            unloadLocked()
            throw EngineError.contextFailed
        }
        ctx = c
        batchSize = Int32(cp.n_batch)
        if let loraPath = options.loraPath, !loraPath.isEmpty {
            guard let adapter = llama_adapter_lora_init(m, loraPath) else {
                unloadLocked()
                throw EngineError.loraFailed(loraPath)
            }
            lora = adapter
            var adapters: [OpaquePointer?] = [adapter]
            var scales: [Float] = [options.loraScale]
            _ = llama_set_adapters_lora(c, &adapters, 1, &scales)
        }
        let tmpl = llama_model_chat_template(m, nil).map { String(cString: $0) }
        template = ChatTemplate.detect(template: tmpl)
        closeThinking = ChatTemplate.hasThinking(template: tmpl)
        var buf = [CChar](repeating: 0, count: 256)
        llama_model_desc(m, &buf, buf.count)
        description = String(cString: buf)
        modelPath = path
        self.options = options
        cached = []

    }

    func unload() { queue.sync { unloadLocked() } }

    private func unloadLocked() {
        if let ctx { llama_free(ctx) }
        if let lora { llama_adapter_lora_free(lora) }
        if let model { llama_model_free(model) }
        ctx = nil
        lora = nil
        model = nil
        modelPath = nil
        cached = []
    }

    /// Stops any generation in flight at its next token.
    func cancel() {
        cancelLock.lock()
        generation += 1
        cancelLock.unlock()
    }

    private var currentGeneration: Int {
        cancelLock.lock()
        defer { cancelLock.unlock() }
        return generation
    }

    struct Sampling {
        var temperature: Float = 0.1
        var topK: Int32 = 40
        var topP: Float = 0.9
        var seed: UInt32 = 42
    }

    /// Evaluates `prompt` and generates up to `maxTokens`. Returns nil when cancelled.
    /// `shouldStop` sees the text so far and can end generation early. Call on `queue`.
    func generate(prompt: String, maxTokens: Int, sampling: Sampling = Sampling(), grammar: String? = nil,
                  shouldStop: ((String) -> Bool)? = nil) throws -> String? {
        guard let ctx, let model else { return nil }
        let myGeneration = currentGeneration
        let vocab = llama_model_get_vocab(model)
        var tokens = try tokenize(prompt, vocab: vocab)
        let limit = Int(llama_n_ctx(ctx)) - maxTokens - 8
        if tokens.count > limit {
            // Keep the start (template and instructions) and the end (the question).
            let head = tokens.prefix(limit / 3)
            let tail = tokens.suffix(limit - head.count)
            tokens = Array(head) + Array(tail)
        }

        // Reuse the shared prefix; always re-evaluate at least the last token for fresh logits.
        var common = zip(cached, tokens).prefix(while: { $0 == $1 }).count
        if common == tokens.count { common -= 1 }
        let mem = llama_get_memory(ctx)
        if common < cached.count {
            if !llama_memory_seq_rm(mem, 0, llama_pos(common), -1) {
                llama_memory_clear(mem, true)
                common = 0
            }
            cached.removeSubrange(common...)
        }
        var pos = common
        while pos < tokens.count {
            if currentGeneration != myGeneration { return nil }
            let n = min(Int(batchSize), tokens.count - pos)
            var chunk = Array(tokens[pos..<(pos + n)])
            let result = chunk.withUnsafeMutableBufferPointer { buf in
                llama_decode(ctx, llama_batch_get_one(buf.baseAddress, Int32(n)))
            }
            guard result == 0 else {
                llama_memory_clear(mem, true)
                cached = []
                throw EngineError.decodeFailed
            }
            cached.append(contentsOf: chunk)
            pos += n
        }

        let sampler = makeSampler(sampling, vocab: vocab, grammar: grammar)
        defer { llama_sampler_free(sampler) }
        var output = ""
        var pending = [CChar]()
        for _ in 0..<maxTokens {
            if currentGeneration != myGeneration { return nil }
            let token = llama_sampler_sample(sampler, ctx, -1)
            if llama_vocab_is_eog(vocab, token) { break }
            pending.append(contentsOf: piece(token, vocab: vocab))
            // A multi-byte character can span tokens; only flush complete UTF-8.
            if let text = String(validatingCString: pending + [0]) {
                output += text
                pending.removeAll()
            }
            var one = token
            guard llama_decode(ctx, llama_batch_get_one(&one, 1)) == 0 else { break }
            cached.append(token)
            if shouldStop?(output) == true { break }
        }
        return output
    }

    private func makeSampler(_ s: Sampling, vocab: OpaquePointer?, grammar: String?) -> UnsafeMutablePointer<llama_sampler> {
        let chain = llama_sampler_chain_init(llama_sampler_chain_default_params())!
        // The grammar only lets through tokens that keep the reply valid JSON of the right shape.
        if let grammar, let g = llama_sampler_init_grammar(vocab, grammar, "root") {
            llama_sampler_chain_add(chain, g)
        }
        llama_sampler_chain_add(chain, llama_sampler_init_penalties(llama_vocab_n_tokens(vocab), 64, 1.1, 0, 0))
        if s.temperature <= 0 {
            llama_sampler_chain_add(chain, llama_sampler_init_greedy())
        } else {
            llama_sampler_chain_add(chain, llama_sampler_init_top_k(s.topK))
            llama_sampler_chain_add(chain, llama_sampler_init_top_p(s.topP, 1))
            llama_sampler_chain_add(chain, llama_sampler_init_temp(s.temperature))
            llama_sampler_chain_add(chain, llama_sampler_init_dist(s.seed))
        }
        return chain
    }

    private func tokenize(_ text: String, vocab: OpaquePointer?) throws -> [llama_token] {
        let utf8 = Array(text.utf8CString)
        let byteCount = Int32(utf8.count - 1)
        var tokens = [llama_token](repeating: 0, count: Int(byteCount) + 16)
        var n = llama_tokenize(vocab, utf8, byteCount, &tokens, Int32(tokens.count), true, true)
        if n < 0 {
            tokens = [llama_token](repeating: 0, count: Int(-n))
            n = llama_tokenize(vocab, utf8, byteCount, &tokens, Int32(tokens.count), true, true)
        }
        guard n >= 0 else { throw EngineError.tokenizeFailed }
        return Array(tokens.prefix(Int(n)))
    }

    private func piece(_ token: llama_token, vocab: OpaquePointer?) -> [CChar] {
        var buf = [CChar](repeating: 0, count: 64)
        var n = llama_token_to_piece(vocab, token, &buf, Int32(buf.count), 0, false)
        if n < 0 {
            buf = [CChar](repeating: 0, count: Int(-n))
            n = llama_token_to_piece(vocab, token, &buf, Int32(buf.count), 0, false)
        }
        return Array(buf.prefix(max(0, Int(n))))
    }
}
