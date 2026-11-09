import Foundation

/// Version details read from the bundle, so the number shown in About, Settings and the
/// menu bar menu always matches what was actually built rather than a hardcoded copy.
enum AppInfo {
    /// The name people see, the executable and the repository ("Inner Voice" with a space
    /// until 0.1.3).
    static let name = "InnerVoice"

    static var version: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "—"
    }

    static var build: String {
        Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "—"
    }

    /// "v0.1.0", or "v0.1.0 (202611081830)" when the build number differs from the
    /// marketing version (build.sh stamps the date into local builds).
    static var displayVersion: String {
        (version == build ? "v\(version)" : "v\(version) (\(build))") + (stage.map { " \($0)" } ?? "")
    }

    /// "0.1.3 beta", for people to read.
    static var labeledVersion: String {
        stage.map { "\(version) \($0)" } ?? version
    }

    /// "beta" while InnerVoice is in beta (InnerVoiceReleaseStage in Info.plist).
    static var stage: String? {
        Bundle.main.infoDictionary?["InnerVoiceReleaseStage"] as? String
    }

    /// The About window: the version and the last 10 releases' notes.
    static func showAbout() {
        AboutWindow.shared.show()
    }
}

import SwiftUI

extension View {
    /// The line under the book's name at the top of the window: the page's own words (a
    /// chapter, "2 of 2 done"), then the version, so it's always clear which InnerVoice is open.
    func pageSubtitle(_ text: String = "") -> some View {
        navigationSubtitle(text.isEmpty ? "v" + AppInfo.labeledVersion : text + "  ·  v" + AppInfo.labeledVersion)
    }
}
