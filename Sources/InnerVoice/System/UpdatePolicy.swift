import Foundation

/// What Inner Voice does when a newer release appears.
enum UpdatePolicy: String, CaseIterable {
    /// Download, verify and install on its own, while the Mac isn't being used.
    case automatic
    /// Say so (menu item and notification) and wait for the user to choose.
    case ask
    /// Don't look for updates; Check for Updates still works.
    case off

    var title: String {
        switch self {
        case .automatic: return "Install automatically"
        case .ask: return "Ask me first"
        case .off: return "Don't check"
        }
    }

    static let key = "updatePolicy"

    static var current: UpdatePolicy {
        get {
            UserDefaults.standard.string(forKey: key).flatMap(UpdatePolicy.init) ?? .ask
        }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: key) }
    }

    /// A version the user chose not to be reminded about (per Mac, not synced).
    static var skippedVersion: String? {
        get { UserDefaults.standard.string(forKey: "skippedUpdateVersion") }
        set { UserDefaults.standard.set(newValue, forKey: "skippedUpdateVersion") }
    }
}
