import AppKit
import InnerVoiceKit

/// A book whose engine is a cloud one (ElevenLabs, Gemini TTS) speaks only once the book
/// allows its text to go to that company. Every play, preview and export asks here first:
/// the existing permission question, and if the narrator says no, nothing plays and they're
/// told how to play on this Mac instead. Don't Send is the default button.
@MainActor
enum CloudGate {
    /// Whether the book may speak with its engine now (asking when it hasn't said yet).
    static func allows(_ document: InnerVoiceDocument) -> Bool {
        let project = document.project
        let e = project.engineInUse
        guard project.showsVoices, e.isCloud, !project.settings.allowsCloud(e) else { return true }
        if SystemHooks.isTestRun { return false }
        let company = e.cloudCompany ?? "the cloud"
        let ask = NSAlert()
        ask.messageText = "Send This Book to \(company)?"
        ask.informativeText = "“\(project.title)” speaks with \(e.title), which makes its voices on \(company == "ElevenLabs" ? "ElevenLabs’" : company + "’s") servers. Every line \(e == .elevenlabs ? "an" : "a") \(e.title) voice speaks is sent there as text; a table read or an exported book sends the whole manuscript. Allow it only if you have the right to share this book."
        ask.addButton(withTitle: "Don’t Send")
        ask.addButton(withTitle: "Send to \(company)")
        if ask.runModal() == .alertSecondButtonReturn {
            let undo = OpenProjects.all.first { $0.document === document }?.undoManager
            document.setCloudAllowed(e, true, undoManager: undo)
            return true
        }
        let info = NSAlert()
        info.messageText = "Nothing Will Play"
        info.informativeText = "“\(project.title)” speaks with \(e.title), which needs your permission to send its text to \(company). To play on this Mac instead, open the Voices page and choose an engine that runs on this Mac, such as Apple Voices, Pocket TTS or Qwen3-TTS, from the engine menu in the toolbar. To allow \(company) later, play again and choose Send."
        info.addButton(withTitle: "OK")
        info.runModal()
        return false
    }
}
