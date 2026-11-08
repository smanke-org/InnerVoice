import Foundation

/// Version details read from the bundle, so the number shown in About, Settings and the
/// menu bar menu always matches what was actually built rather than a hardcoded copy.
enum AppInfo {
    /// The name people see ("Inner Voice"); the executable and repository are "InnerVoice".
    static let name = "Inner Voice"

    static var version: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "—"
    }

    static var build: String {
        Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "—"
    }

    /// "v0.1.0", or "v0.1.0 (202611081830)" when the build number differs from the
    /// marketing version (build.sh stamps the date into local builds).
    static var displayVersion: String {
        version == build ? "v\(version)" : "v\(version) (\(build))"
    }

    /// The About window: the version and the last 10 releases' notes.
    static func showAbout() {
        AboutWindow.shared.show()
    }
}
