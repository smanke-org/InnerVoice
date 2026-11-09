import AppKit

/// InnerVoice was called "Inner Voice" (with a space) until 0.1.3.
///
/// The updater in 0.1.2 and earlier installs a new version into the bundle that is running,
/// so a copy updated from 0.1.2 arrives as `Inner Voice.app`. At its first launch it renames
/// itself to `InnerVoice.app` and reopens from there. The bundle ID stays
/// `com.smanke.InnerVoice`, so settings, recent projects, window restoration and the
/// downloaded model (in Application Support/Inner Voice, which keeps its name) carry over.
///
/// A copy installed fresh beside an old `Inner Voice.app` puts the old one in the Trash
/// instead, so there's only one, and only one answers the `innervoice` command.
@MainActor
enum AppRename {
    static let oldBundleName = "Inner Voice.app"
    static let newBundleName = "InnerVoice.app"

    /// Renames this copy if it's still `Inner Voice.app`. True when it has handed over to the
    /// renamed copy and is quitting, so launch should go no further.
    static func renameIfNeeded() -> Bool {
        #if DEBUG
        // Debug builds only when testing this: a test copy must never move the real app.
        guard ProcessInfo.processInfo.environment["INNERVOICE_RENAME_TEST"] != nil else { return false }
        #endif
        let bundle = Bundle.main.bundleURL
        guard bundle.lastPathComponent == oldBundleName else {
            trashOldCopy(besides: bundle)
            relinkCommand(to: bundle, from: bundle.deletingLastPathComponent().appending(path: oldBundleName))
            #if DEBUG
            // `INNERVOICE_RENAME_TEST=<file>`: say where this copy runs from, then quit.
            let report = ProcessInfo.processInfo.environment["INNERVOICE_RENAME_TEST"] ?? ""
            try? "running from \(bundle.path)\n".write(toFile: report, atomically: true, encoding: .utf8)
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { exit(0) }
            return true
            #else
            return false
            #endif
        }
        let destination = bundle.deletingLastPathComponent().appending(path: newBundleName)
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            NSLog("InnerVoice: \(destination.path) already exists; staying at \(bundle.path)")
            return false
        }
        do {
            try FileManager.default.moveItem(at: bundle, to: destination)
        } catch {
            // No permission to rename in this folder (a standard user in /Applications, say):
            // it keeps working under the old file name.
            NSLog("InnerVoice: couldn't rename \(bundle.path): \(error.localizedDescription)")
            return false
        }
        NSLog("InnerVoice: renamed \(bundle.path) to \(destination.path); reopening")
        relinkCommand(to: destination, from: bundle)
        reopen(destination)
        return true
    }

    /// Opens the renamed copy once this process has gone, then quits.
    private static func reopen(_ app: URL) {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        var open = ["/usr/bin/open"]
        #if DEBUG
        // A new copy even while the person's own InnerVoice is running.
        open.append("-n")
        // A test copy hands its test settings on; `open` doesn't pass the environment.
        for (key, value) in ProcessInfo.processInfo.environment where key.hasPrefix("INNERVOICE_") || key.hasPrefix("APP_") {
            open += ["--env", "\(key)=\(value)"]
        }
        #endif
        task.arguments = ["-c", #"while kill -0 "$1" 2>/dev/null; do sleep 0.2; done; shift 2; "$@""#, "sh",
                          "\(ProcessInfo.processInfo.processIdentifier)", app.path] + open + [app.path]
        do {
            try task.run()
        } catch {
            NSLog("InnerVoice: couldn't reopen \(app.path): \(error.localizedDescription)")
        }
        exit(0)
    }

    /// The `innervoice` command is a link to the app's binary in ~/.local/bin. A link into
    /// the old bundle name points at the new one instead.
    private static func relinkCommand(to app: URL, from old: URL) {
        let link = FileManager.default.homeDirectoryForCurrentUser.appending(path: ".local/bin/innervoice")
        guard let target = try? FileManager.default.destinationOfSymbolicLink(atPath: link.path),
              target.hasPrefix(old.path + "/") else { return }
        let binary = app.appending(path: "Contents/MacOS/InnerVoice").path
        do {
            try FileManager.default.removeItem(at: link)
            try FileManager.default.createSymbolicLink(atPath: link.path, withDestinationPath: binary)
        } catch {
            NSLog("InnerVoice: couldn't relink \(link.path): \(error.localizedDescription)")
        }
    }

    /// An old `Inner Voice.app` (this app, an older version) in the same folder goes to the
    /// Trash, where it can still be put back.
    private static func trashOldCopy(besides bundle: URL) {
        let old = bundle.deletingLastPathComponent().appending(path: oldBundleName)
        guard let info = NSDictionary(contentsOf: old.appending(path: "Contents/Info.plist")),
              info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              let version = info["CFBundleShortVersionString"] as? String,
              !UpdateController.isNewer(version, than: AppInfo.version) else { return }
        // Not while it's running; it would just come back next time.
        guard !NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            .contains(where: { $0.bundleURL?.standardizedFileURL == old.standardizedFileURL }) else { return }
        NSWorkspace.shared.recycle([old]) { _, error in
            NSLog("InnerVoice: old copy \(old.path) to the Trash: \(error?.localizedDescription ?? "done")")
        }
    }
}
