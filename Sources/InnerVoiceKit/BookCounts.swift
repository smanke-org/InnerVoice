import Foundation

/// The words and billable characters shown beside the book's name, and the cost tooltip.
/// Windows' `BookCounts` says the same thing word for word.
public enum BookCounts {
    public static func label(words: Int, characters: Int) -> String {
        "\(words.formatted()) words · \(characters.formatted()) characters"
    }

    /// What sending the whole book once costs with each cloud engine, the book's own engine
    /// first. `gemini`, `eleven`: the models chosen in Settings.
    public static func costHelp(characters n: Int, engine: VoiceEngine, gemini: GeminiTTS.Model, eleven: ElevenLabsModel? = nil, on date: Date = .now) -> String {
        let money = { (d: Double) in d.formatted(.currency(code: "USD").locale(Locale(identifier: "en_US"))) }
        var lines: [(VoiceEngine, String)] = [
            (.elevenlabs, "ElevenLabs: about \((eleven?.credits(characters: n) ?? n).formatted()) credits" + (eleven.map { " (\($0.name))" } ?? "")),
            (.speechify, "Speechify: about \(money(SpeechifyTTS.estimatedDollars(characters: n))) at pay-as-you-go"),
            (.gemini, "Gemini TTS: about \(money(GeminiTTS.estimatedSeconds(characters: n) * gemini.dollarsPerSecond(on: date))) (\(gemini == .flash ? "Flash" : "Flash-Lite"), paid tier)"),
        ]
        if let i = lines.firstIndex(where: { $0.0 == engine }) { lines.insert(lines.remove(at: i), at: 0) }
        var out = "\(n.formatted()) billable characters: letters, numbers, spaces and punctuation, as cloud voices count them.\n\nThe whole book once with a cloud voice:\n"
            + lines.map { "• " + $0.1 + ($0.0 == engine ? " (this book’s engine)" : "") }.joined(separator: "\n")
        if !engine.isCloud { out += "\n\nThis book’s engine runs on this computer: no charge." }
        out += "\n\nRetakes and changed lines cost again; your plan’s allowance isn’t counted."
        return out
    }
}
