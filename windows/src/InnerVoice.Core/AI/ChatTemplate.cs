// Ported from Sources/InnerVoiceKit/AI/ChatTemplate.swift.

using System.Text;

namespace InnerVoice.Core.AI;

public enum ChatRole { System, User, Assistant }

public readonly record struct ChatMessage(ChatRole Role, string Content);

/// Renders chat messages in a model family's turn format. Detected from the Jinja template
/// stored in the GGUF, so the prefill can be appended right after the assistant header,
/// which a generic template engine would not allow.
public enum ChatTemplate { Gemma4, Gemma, ChatML, Llama3, Phi, Plain }

public static class ChatTemplates
{
    public static ChatTemplate Detect(string? template)
    {
        if (template is null) return ChatTemplate.Plain;
        if (template.Contains("<|turn>")) return ChatTemplate.Gemma4;
        if (template.Contains("<start_of_turn>")) return ChatTemplate.Gemma;
        if (template.Contains("<|im_start|>")) return ChatTemplate.ChatML;
        if (template.Contains("<|start_header_id|>")) return ChatTemplate.Llama3;
        if (template.Contains("<|assistant|>") && template.Contains("<|end|>")) return ChatTemplate.Phi;
        return ChatTemplate.Plain;
    }

    /// Whether the template opens a reasoning block that must be closed up front (Qwen 3).
    public static bool HasThinking(string? template) => template?.Contains("<think>") == true;

    /// Text that ends a turn, for trimming output when the end token is printed as text.
    public static string[] StopStrings(this ChatTemplate t) => t switch
    {
        ChatTemplate.Gemma4 => ["<turn|>", "<|turn>"],
        ChatTemplate.Gemma => ["<end_of_turn>", "<start_of_turn>"],
        ChatTemplate.ChatML => ["<|im_end|>", "<|im_start|>"],
        ChatTemplate.Llama3 => ["<|eot_id|>", "<|start_header_id|>"],
        ChatTemplate.Phi => ["<|end|>", "<|user|>"],
        _ => [],
    };

    static string Raw(ChatRole r) => r switch { ChatRole.System => "system", ChatRole.User => "user", _ => "assistant" };

    /// The prompt text without a BOS token; tokenize it with special tokens on so the
    /// tokenizer adds BOS where the model expects one.
    public static string Render(this ChatTemplate t, IEnumerable<ChatMessage> messages, string prefill, bool closeThinking = false)
    {
        var o = new StringBuilder();
        switch (t)
        {
            case ChatTemplate.Gemma4:
                foreach (var m in messages) o.Append("<|turn>").Append(m.Role == ChatRole.Assistant ? "model" : Raw(m.Role)).Append('\n').Append(m.Content).Append("<turn|>\n");
                o.Append("<|turn>model\n");
                break;
            case ChatTemplate.Gemma:
                var pending = "";
                foreach (var m in messages)
                {
                    switch (m.Role)
                    {
                        case ChatRole.System: pending = m.Content + "\n\n"; break;
                        case ChatRole.User: o.Append("<start_of_turn>user\n").Append(pending).Append(m.Content).Append("<end_of_turn>\n"); pending = ""; break;
                        case ChatRole.Assistant: o.Append("<start_of_turn>model\n").Append(m.Content).Append("<end_of_turn>\n"); break;
                    }
                }
                o.Append("<start_of_turn>model\n");
                break;
            case ChatTemplate.ChatML:
                foreach (var m in messages) o.Append("<|im_start|>").Append(Raw(m.Role)).Append('\n').Append(m.Content).Append("<|im_end|>\n");
                o.Append("<|im_start|>assistant\n");
                if (closeThinking) o.Append("<think>\n\n</think>\n\n");
                break;
            case ChatTemplate.Llama3:
                foreach (var m in messages) o.Append("<|start_header_id|>").Append(Raw(m.Role)).Append("<|end_header_id|>\n\n").Append(m.Content).Append("<|eot_id|>");
                o.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
                break;
            case ChatTemplate.Phi:
                foreach (var m in messages) o.Append("<|").Append(Raw(m.Role)).Append("|>").Append(m.Content).Append("<|end|>");
                o.Append("<|assistant|>");
                break;
            default:
                foreach (var m in messages.Where(m => m.Role != ChatRole.Assistant)) o.Append(m.Content).Append("\n\n");
                break;
        }
        return o.Append(prefill).ToString();
    }
}
