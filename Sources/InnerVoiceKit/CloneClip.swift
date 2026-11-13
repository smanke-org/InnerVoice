import Foundation

/// The part of a recording a voice cloned on this Mac learns from. Pocket TTS clones well
/// from about 5 to 15 seconds of one person speaking (Kyutai's own voice clips are 10 s),
/// so a longer recording is cut at a pause between words. The words it says are kept with
/// it, for the narrator to check.
public enum CloneClip {
    /// The longest clip the clone model is given.
    public static let longest: Double = 15
    /// Shorter than this carries too little of the voice.
    public static let shortest: Double = 4

    /// One recognized word: when it's said, and where it ends in the transcript (UTF-16).
    public struct Word: Hashable, Sendable {
        public var start: Double
        public var end: Double
        public var textEnd: Int

        public init(start: Double, end: Double, textEnd: Int) {
            self.start = start
            self.end = end
            self.textEnd = textEnd
        }
    }

    public struct Span: Hashable, Sendable {
        /// Seconds into the recording.
        public var start: Double
        public var end: Double
        /// The transcript up to here is what the clip says.
        public var textEnd: Int
        public var seconds: Double { end - start }
    }

    /// From the first word up to `limit` seconds, ending at the longest pause after at least
    /// 8 seconds (or after `shortest`, for a short recording). nil when there are no words.
    public static func span(_ words: [Word], limit: Double = longest) -> Span? {
        guard let first = words.first else { return nil }
        let start = max(0, first.start - 0.15)
        let fits = words.indices.filter { words[$0].end - start <= limit }
        guard let last = fits.last else { return nil }
        func cut(_ i: Int) -> Span {
            let after = i + 1 < words.count ? words[i + 1].start : words[i].end + 0.25
            return Span(start: start, end: min(words[i].end + 0.25, max(words[i].end, after)), textEnd: words[i].textEnd)
        }
        // Everything fits: use it all.
        if last == words.count - 1 { return cut(last) }
        func gap(_ i: Int) -> Double { words[i + 1].start - words[i].end }
        for minimum in [8.0, shortest] {
            let choices = fits.filter { words[$0].end - start >= minimum }
            if let best = choices.max(by: { gap($0) != gap($1) ? gap($0) < gap($1) : $0 < $1 }) { return cut(best) }
        }
        return cut(last)
    }

    /// The transcript the span says, without trailing spaces.
    public static func text(_ transcript: String, upTo end: Int) -> String {
        let ns = transcript as NSString
        return ns.substring(to: min(max(0, end), ns.length)).trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
