import Foundation

/// Find in the Manuscript: every place a phrase appears, in reading order. Case and
/// diacritics are ignored, and straight and curly quotes match each other.
public enum TextSearch {
    public struct Match: Hashable, Sendable {
        public var chapter: Int
        public var paragraphID: UUID
        /// In the paragraph's text (UTF-16, like NSString).
        public var range: TextSpan
    }

    public static func normalized(_ s: String) -> String {
        s.replacingOccurrences(of: "\u{2019}", with: "'").replacingOccurrences(of: "\u{2018}", with: "'")
            .replacingOccurrences(of: "\u{201C}", with: "\"").replacingOccurrences(of: "\u{201D}", with: "\"")
    }

    public static func matches(of query: String, in project: Project, limit: Int = 5000) -> [Match] {
        let q = normalized(query.trimmingCharacters(in: .whitespacesAndNewlines))
        guard !q.isEmpty else { return [] }
        var out: [Match] = []
        for (c, chapter) in project.chapters.enumerated() {
            for p in chapter.paragraphs {
                // Same length after normalizing (each quote is one UTF-16 unit both ways).
                let text = normalized(p.text) as NSString
                var from = 0
                while from < text.length {
                    let r = text.range(of: q, options: [.caseInsensitive, .diacriticInsensitive],
                                       range: NSRange(location: from, length: text.length - from))
                    guard r.location != NSNotFound, r.length > 0 else { break }
                    out.append(Match(chapter: c, paragraphID: p.id, range: TextSpan(location: r.location, length: r.length)))
                    if out.count >= limit { return out }
                    from = NSMaxRange(r)
                }
            }
        }
        return out
    }
}
