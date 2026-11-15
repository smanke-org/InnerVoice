import InnerVoiceKit
import SwiftUI

/// Settings › Voices › Engines: the one voice engine. With a book open (one that uses AI
/// voices), it's that book's engine and changes only that book; with none open, it's the
/// default every new book starts with.
struct EngineChoice: View {
    private var switches: EngineSwitches { .shared }
    /// Re-read the open book when Settings comes forward.
    @State private var tick = 0

    var body: some View {
        let _ = tick
        let entry = OpenProjects.front.flatMap { e in e.document?.project.showsVoices == true ? e : nil }
        let choices = switches.choices
        Group {
            if let entry, let document = entry.document {
                Picker("Voice engine for “\(document.project.title)”", selection: Binding(
                    get: { document.project.engineInUse },
                    set: { e in
                        if e.isCloud, !document.project.settings.allowsCloud(e) {
                            document.setVoiceEngine(e, undoManager: entry.undoManager)
                            _ = CloudGate.allows(document)
                        } else {
                            document.setVoiceEngine(e, undoManager: entry.undoManager)
                        }
                        tick += 1
                    })) {
                    ForEach(choices) { Text($0.title).tag($0) }
                }
                .help("This book’s engine. It changes only this book; close every book to change the default for new ones.")
            } else {
                Picker("Default engine for new books", selection: Binding(get: { switches.defaultEngine }, set: { switches.setDefault($0); tick += 1 })) {
                    ForEach(choices) { Text($0.title).tag($0) }
                }
                .help("New books start with this engine. To change an open book’s engine, use this menu while it’s open, or its Voices page.")
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didBecomeKeyNotification)) { _ in tick += 1 }
    }
}
