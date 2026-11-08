import AppKit

/// Starts the CLI when there is a command after the app's name, else the app.
///   innervoice <command>      run it against the running app and exit (Agent/AgentCLI.swift)
///   Inner Voice.app           the app (App.swift)
@main
enum Main {
    static func main() {
        if let code = AgentCLI.runIfRequested() { exit(code) }
        if AgentSocket.isTestRun { AgentDemo.isolateTestRun() }
        InnerVoiceApp.main()
    }
}

/// Debug test copies for the agent CLI (APP_STORE=/tmp/<name>): they never reopen or keep
/// real projects, and can open the sample book to have something to drive.
enum AgentDemo {
    /// No window restoration (it would reopen the person's real projects in the test copy),
    /// and a plain `pkill` (SIGTERM) closes the sample book without saving or autosaving it.
    /// Set in the argument domain, which lives in memory only, never in the real preferences.
    static func isolateTestRun() {
        var arguments = UserDefaults.standard.volatileDomain(forName: UserDefaults.argumentDomain)
        arguments["ApplePersistenceIgnoreState"] = true
        arguments["NSQuitAlwaysKeepsWindows"] = false
        UserDefaults.standard.setVolatileDomain(arguments, forName: UserDefaults.argumentDomain)

        signal(SIGTERM, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        source.setEventHandler {
            MainActor.assumeIsolated {
                for doc in NSDocumentController.shared.documents {
                    doc.updateChangeCount(.changeCleared)
                    doc.close()
                }
                exit(0)
            }
        }
        source.resume()
        termination = source
    }

    private static var termination: DispatchSourceSignal?

    /// INNERVOICE_DEMO=1 on a Debug test copy opens the sample book at launch.
    static func openIfRequested() {
        guard AgentSocket.isTestRun, ProcessInfo.processInfo.environment["INNERVOICE_DEMO"] == "1" else { return }
        pending = Task {
            for _ in 0..<40 where AppState.newDocument == nil { try? await Task.sleep(for: .seconds(0.25)) }
            _ = try? await openSample()
        }
    }

    /// The sample book still opening at launch: commands wait for it, so the first one finds it.
    static var pending: Task<Void, Never>?

    /// Opens SeedData's sample book in a new window and waits for the window to register.
    static func openSample() async throws -> OpenProjects.Entry {
        guard let newDocument = AppState.newDocument else {
            throw AgentError.unavailable("Inner Voice has no window open yet to start a project from. Try again in a second")
        }
        let before = Set(OpenProjects.all.map(\.id))
        let sample = SeedData.project()
        newDocument { InnerVoiceDocument(project: sample) }
        return try await AgentWait.newProject(after: before)
    }
}

/// Waiting for SwiftUI to open a project window and register it.
enum AgentWait {
    static func newProject(after before: Set<UUID>, seconds: Double = 10) async throws -> OpenProjects.Entry {
        let steps = Int(seconds / 0.1)
        for _ in 0..<steps {
            if let entry = OpenProjects.all.last(where: { !before.contains($0.id) }) { return entry }
            try? await Task.sleep(for: .seconds(0.1))
        }
        throw AgentError.invalid("the project window didn't open within \(Int(seconds)) seconds. Run `innervoice projects list` to see if it did later")
    }
}
