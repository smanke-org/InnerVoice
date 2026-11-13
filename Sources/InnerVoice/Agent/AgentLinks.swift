import AppKit
import InnerVoiceKit

/// Follows innervoice:// links (read by `AgentLink` in the Kit): runs the one agent command a link
/// names, only the ones that show things, then brings its window forward. A link that can't be
/// followed says why in an alert. The Windows app does the same (AgentRouter.OpenLink).
///
/// The handler is an Apple Event handler installed before launch finishes, and links that
/// arrive then wait for launch: the commands reach NSDocumentController, which SwiftUI must
/// create first (CLAUDE.md).
@MainActor
enum AgentLinks {
    /// Called from applicationWillFinishLaunching, so a link that launched the app reaches us.
    static func install() {
        NSAppleEventManager.shared().setEventHandler(Handler.shared, andSelector: #selector(Handler.handle(_:reply:)),
                                                     forEventClass: AEEventClass(kInternetEventClass), andEventID: AEEventID(kAEGetURL))
    }

    /// Called from applicationDidFinishLaunching: follows the links that came while launching.
    static func launched() {
        let early = waiting
        waiting = []
        // A test copy launched by a link opens the sample book through SwiftUI's newDocument,
        // which comes with the first window, and a link launch shows none.
        if !early.isEmpty, AgentDemo.pending != nil, AppState.newDocument == nil { AppState.showStart() }
        for link in early { follow(link) }
    }

    private static var waiting: [String] = []

    /// The app's commands, as the link parser sees them.
    static var commands: [AgentLink.Command] {
        AgentCommands.all.map { command in
            AgentLink.Command(name: command.name, params: command.params.map {
                AgentLink.Command.Param(name: $0.name, required: $0.required, positional: $0.positional, multiple: $0.multiple, isPath: $0.kind == .path)
            })
        }
    }

    static func follow(_ link: String) {
        guard AppState.launched else {
            waiting.append(link)
            return
        }
        Task {
            await AgentDemo.pending?.value
            do {
                _ = try await run(link)
            } catch {
                bringForward(nil)
                await tell(link, error)
            }
        }
    }

    /// Runs a link's command and brings its project's window forward. Throws AgentError.
    @discardableResult
    static func run(_ link: String) async throws -> Any {
        let request: AgentLink.Request
        do {
            request = try AgentLink.parse(link, commands: commands)
        } catch let refusal as AgentLink.Refusal {
            throw AgentError.usage(refusal.message)
        }
        guard let name = request.command else {
            bringForward(nil)
            return JSONObject()
        }
        guard let command = AgentCommands.all.first(where: { $0.name == name }) else {
            throw AgentError.usage("unknown command \"\(name)\"")
        }
        let args = Args(values: request.args)
        let result = try await command.run(args)
        bringForward(entry(for: name, args))
        return result
    }

    /// The project window a command showed.
    private static func entry(for command: String, _ args: Args) -> OpenProjects.Entry? {
        if command == "projects open", let path = args.string("path") {
            let url = URL(fileURLWithPath: path).standardizedFileURL
            return OpenProjects.all.first { $0.fileURL?.standardizedFileURL == url }
        }
        return try? AgentCommands.project(args).0
    }

    private static func bringForward(_ entry: OpenProjects.Entry?) {
        NSApp.activate()
        if let entry, let window = window(of: entry) {
            if window.isMiniaturized { window.deminiaturize(nil) }
            window.makeKeyAndOrderFront(nil)
        } else if entry == nil, !NSApp.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) {
            AppState.showStart()
        }
    }

    private static func window(of entry: OpenProjects.Entry) -> NSWindow? {
        if let undo = entry.undoManager, let window = NSApp.windows.first(where: { $0.undoManager === undo && $0.canBecomeMain }) {
            return window
        }
        return entry.nsDocument?.windowControllers.first?.window
    }

    /// A sheet on the window in front (the Welcome window when nothing else is open), so the
    /// agent socket keeps answering; an app-modal alert would hold the main actor.
    private static func tell(_ link: String, _ error: Error) async {
        let message = (error as? AgentError)?.message ?? error.localizedDescription
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Couldn’t Follow the Link"
        alert.informativeText = "\(link): \(message)."
        func front() -> NSWindow? {
            let shown = NSApp.windows.filter { $0.isVisible && $0.canBecomeMain && $0.attachedSheet == nil }
            return [NSApp.keyWindow, NSApp.mainWindow].compactMap { $0 }.first { shown.contains($0) } ?? shown.first
        }
        // The Welcome window may still be on its way.
        for _ in 0..<20 where front() == nil { try? await Task.sleep(for: .seconds(0.1)) }
        if let window = front() {
            alert.beginSheetModal(for: window) { _ in }
        } else {
            alert.runModal()
        }
    }

    /// The Apple Event target (an @objc selector needs an object).
    private final class Handler: NSObject {
        static let shared = Handler()

        @objc func handle(_ event: NSAppleEventDescriptor, reply: NSAppleEventDescriptor) {
            guard let link = event.paramDescriptor(forKeyword: AEKeyword(keyDirectObject))?.stringValue else { return }
            MainActor.assumeIsolated { AgentLinks.follow(link) }
        }
    }
}
