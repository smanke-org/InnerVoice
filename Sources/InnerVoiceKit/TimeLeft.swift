import Foundation

/// Export Audio's "about 12 min left": the time so far per line, times the lines to go.
/// Quiet until it has something to go on (5 lines and 15 seconds), so the first guess
/// isn't wild. Lines already in the saved audio come back at once, so an export that
/// starts with them guesses low at first and corrects as it goes.
public enum TimeLeft {
    public static func estimate(done: Int, total: Int, elapsed: TimeInterval) -> String? {
        guard done >= 5, elapsed >= 15, total > done else { return nil }
        let seconds = elapsed / Double(done) * Double(total - done)
        return text(seconds)
    }

    public static func text(_ seconds: TimeInterval) -> String {
        if seconds < 60 { return "less than a minute left" }
        let minutes = Int((seconds / 60).rounded())
        if minutes < 60 { return "about \(minutes) min left" }
        let h = minutes / 60, m = minutes % 60
        return m == 0 ? "about \(h) hr left" : "about \(h) hr \(m) min left"
    }
}
