import Foundation

public struct ChatMessage: Sendable, Equatable {
    public enum Role: String, Sendable { case system, user, assistant }
    public var role: Role
    public var content: String
    public init(_ role: Role, _ content: String) {
        self.role = role
        self.content = content
    }
}


/// Renders chat messages in a model family's turn format. Detected from the Jinja
/// template stored in the GGUF, so the prefill can be appended right after the
/// assistant header — which a generic template engine would not allow.
public enum ChatTemplate: String, Sendable, Equatable {
    case gemma4, gemma, chatml, llama3, phi, mistral, harmony, plain

    public static func detect(template: String?) -> ChatTemplate {
        guard let t = template else { return .plain }
        if t.contains("<|turn>") { return .gemma4 }
        if t.contains("<start_of_turn>") { return .gemma }
        if t.contains("<|im_start|>") { return .chatml }
        if t.contains("<|start_header_id|>") { return .llama3 }
        if t.contains("<|channel|>") { return .harmony }
        if t.contains("<|assistant|>") && t.contains("<|end|>") { return .phi }
        if t.contains("[INST]") { return .mistral }
        return .plain
    }

    /// Whether the template opens a reasoning block that must be closed up front so the
    /// model writes the continuation straight away (Qwen 3 hybrid models).
    public static func hasThinking(template: String?) -> Bool {
        template?.contains("<think>") == true
    }

    /// Text sequences that end a turn, for trimming output when the vocab's end-of-generation
    /// token is printed as text.
    public var stopStrings: [String] {
        switch self {
        case .gemma4: ["<turn|>", "<|turn>"]
        case .gemma: ["<end_of_turn>", "<start_of_turn>"]
        case .chatml: ["<|im_end|>", "<|im_start|>"]
        case .llama3: ["<|eot_id|>", "<|start_header_id|>"]
        case .phi: ["<|end|>", "<|user|>"]
        case .mistral: ["</s>", "[INST]"]
        case .harmony: ["<|return|>", "<|end|>", "<|start|>"]
        case .plain: []
        }
    }

    /// The prompt text without a BOS token; tokenize it with special tokens on so the
    /// tokenizer adds BOS where the model expects one.
    public func render(_ messages: [ChatMessage], prefill: String, closeThinking: Bool = false) -> String {
        var out = ""
        switch self {
        case .gemma4:
            for m in messages {
                let role = m.role == .assistant ? "model" : m.role.rawValue
                out += "<|turn>\(role)\n\(m.content)<turn|>\n"
            }
            out += "<|turn>model\n"
        case .gemma:
            // Gemma 3 has no system role; its own template folds the system text into the first user turn.
            var pending = ""
            for m in messages {
                switch m.role {
                case .system: pending = m.content + "\n\n"
                case .user:
                    out += "<start_of_turn>user\n\(pending)\(m.content)<end_of_turn>\n"
                    pending = ""
                case .assistant: out += "<start_of_turn>model\n\(m.content)<end_of_turn>\n"
                }
            }
            out += "<start_of_turn>model\n"
        case .chatml:
            for m in messages { out += "<|im_start|>\(m.role.rawValue)\n\(m.content)<|im_end|>\n" }
            out += "<|im_start|>assistant\n"
            if closeThinking { out += "<think>\n\n</think>\n\n" }
        case .llama3:
            // <|begin_of_text|> is the BOS token, added by the tokenizer.
            for m in messages {
                out += "<|start_header_id|>\(m.role.rawValue)<|end_header_id|>\n\n\(m.content)<|eot_id|>"
            }
            out += "<|start_header_id|>assistant<|end_header_id|>\n\n"
        case .phi:
            for m in messages { out += "<|\(m.role.rawValue)|>\(m.content)<|end|>" }
            out += "<|assistant|>"
        case .mistral:
            // Mistral Small 3.x (Tekken): the system prompt has its own tags; <s> is the BOS token.
            for m in messages {
                switch m.role {
                case .system: out += "[SYSTEM_PROMPT]\(m.content)[/SYSTEM_PROMPT]"
                case .user: out += "[INST]\(m.content)[/INST]"
                case .assistant: out += "\(m.content)</s>"
                }
            }
        case .harmony:
            // gpt-oss: system text goes in the developer turn; the answer is started on the final
            // channel so the model skips its reasoning, like closing a <think> block.
            for m in messages {
                switch m.role {
                case .system: out += "<|start|>developer<|message|># Instructions\n\n\(m.content)<|end|>"
                case .user: out += "<|start|>user<|message|>\(m.content)<|end|>"
                case .assistant: out += "<|start|>assistant<|channel|>final<|message|>\(m.content)<|end|>"
                }
            }
            out += "<|start|>assistant<|channel|>final<|message|>"
        case .plain:
            for m in messages where m.role != .assistant { out += m.content + "\n\n" }
        }
        return out + prefill
    }
}
