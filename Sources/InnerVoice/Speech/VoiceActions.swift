import InnerVoiceKit
import SwiftUI

/// The narrator's voice and tone edits. Each is one undo step.
@MainActor extension InnerVoiceDocument {
    func setTone(_ tone: Tone, for lineIDs: [UUID], undoManager: UndoManager?) {
        for l in project.lines where lineIDs.contains(l.id) && (l.toneSource == .ai || l.toneSource == .rule) {
            ActivityStore.shared.record(l.tone == tone ? ActivityKey.tonesKept : ActivityKey.tonesChanged)
        }
        change(lineIDs.count == 1 ? "Set Tone" : "Set Tone of \(lineIDs.count) Lines", undoManager: undoManager) { p in
            let set = Set(lineIDs)
            for i in p.lines.indices where set.contains(p.lines[i].id) {
                p.lines[i].tone = tone
                p.lines[i].toneConfidence = 1
                p.lines[i].toneSource = .user
            }
        }
    }

    /// Gives a character (or, with nil, the narrator) a voice on its engine. The
    /// narrator's choice: casting never changes it.
    func setVoice(_ choice: VoiceChoice, for characterID: UUID?, undoManager: UndoManager?) {
        let voice = CastVoice(choice: choice, confidence: 1, source: .user)
        if let id = characterID, let old = project.character(id)?.voice(for: project.engineInUse == .mixed ? .mixed : choice.engine), old.source == .auto {
            ActivityStore.shared.record(old.choice == choice ? ActivityKey.voicesKept : ActivityKey.voicesChanged)
        }
        // Mixing engines: the voice is kept with the project's mixed casting, whatever its engine.
        let key: VoiceEngine = project.engineInUse == .mixed ? .mixed : choice.engine
        change(characterID == nil ? "Choose Narrator Voice" : "Choose Voice", undoManager: undoManager) { p in
            if let id = characterID, let i = p.characterIndex(id) {
                p.characters[i].setVoice(voice, for: key)
            } else if characterID == nil {
                p.settings.setNarratorVoice(voice, for: key)
            }
            if p.voicesCast == nil { p.voicesCast = .now }
        }
    }

    /// Keeps the voice casting picked, as if the narrator had chosen it.
    func confirmVoice(for characterID: UUID, undoManager: UndoManager?) {
        let engine = project.engineInUse
        guard let v = project.character(characterID)?.voice(for: engine) else { return }
        setVoice(v.choice, for: characterID, undoManager: undoManager)
    }

    /// Lets this book's text go to ElevenLabs (the narrator said yes), or stops it.
    func setCloudVoicesAllowed(_ allowed: Bool, undoManager: UndoManager?) {
        setCloudAllowed(.elevenlabs, allowed, undoManager: undoManager)
    }

    /// Lets this book's text go to a cloud engine's company (the narrator said yes), or stops
    /// it. Each company has its own yes.
    func setCloudAllowed(_ engine: VoiceEngine, _ allowed: Bool, undoManager: UndoManager?) {
        guard let company = engine.cloudCompany else { return }
        updateSettings(allowed ? "Allow \(company)" : "Stop Sending to \(company)", undoManager: undoManager) {
            let date: Date? = allowed ? .now : nil
            if engine == .gemini { $0.googleVoicesAllowed = date } else { $0.cloudVoicesAllowed = date }
        }
        if allowed { VoiceCastingRun.castIfNeeded(document: self) }
    }

    /// Lets this book's text go to Claude for the AI features (the narrator said yes), or stops it.
    func setCloudTextAllowed(_ allowed: Bool, undoManager: UndoManager?) {
        updateSettings(allowed ? "Allow Claude" : "Stop Sending to Claude", undoManager: undoManager) { $0.cloudTextAllowed = allowed ? .now : nil }
    }

    func setVoiceEngine(_ engine: VoiceEngine, undoManager: UndoManager?) {
        updateSettings("Change Voice Engine", undoManager: undoManager) { $0.voiceEngine = engine == .apple ? nil : engine }
        VoiceCastingRun.castIfNeeded(document: self)
        ModelSpeech.prepare(engine)
    }

    /// Casts every voice again from the summaries; the narrator's choices stay.
    func castVoicesAgain(undoManager: UndoManager?) {
        let engine = project.engineInUse
        let voices = ElevenLabs.shared.castable(VoiceCatalog.voices(for: engine, project: project))
        let index = self.index
        change("Cast Voices", undoManager: undoManager) { p in
            VoiceCasting.cast(&p, index: index, engine: engine, voices: voices)
        }
    }
}

/// Casting that happens on its own: after Tag Speakers and Build Summaries, and when the
/// engine changes. It only fills in what has no voice yet (characters, the narrator, the
/// shared roles); every voice there is stays, so switching engines (or turning one off and
/// on) brings back each engine's voices as they were. After Tag Speakers and Build Summaries
/// (`recastUnsure`) it also decides again the characters' automatic voices it was unsure of,
/// on the engine in use only. Not an undo step of its own: it rides along with the run that
/// caused it.
@MainActor
enum VoiceCastingRun {
    static func castIfNeeded(document: InnerVoiceDocument, recastUnsure: Bool = false) {
        let project = document.project
        let engine = project.engineInUse
        let index = document.index
        let speaking = project.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
        let open = speaking.filter { c in
            guard let v = c.voice(for: engine) else { return true }
            // A voice from an engine that's off stays: it's back when the engine is (until
            // then the narrator's voice reads them).
            guard recastUnsure, EngineSwitches.shared.usable(v.choice.engine) else { return false }
            return v.source == .auto && v.confidence < project.settings.unsureBelow
        }
        // Casting Style: a shared role (Men, Women, Other) the style uses with no voice yet.
        let missingRole = CastingStyleRules.sharedRolesNeeded(project.settings).contains { project.settings.roleVoice($0, for: engine) == nil }
        guard !open.isEmpty || missingRole || project.settings.narratorVoice(for: engine) == nil else { return }
        let voices = ElevenLabs.shared.castable(VoiceCatalog.voices(for: engine, project: project))
        guard !voices.isEmpty else { return }
        let sure = Set(speaking.filter { !open.contains($0) }.map(\.id))
        document.applyQuietly { p in
            VoiceCasting.cast(&p, index: index, engine: engine, voices: voices, keep: sure, keepNarratorAndRoles: true)
        }
    }
}
