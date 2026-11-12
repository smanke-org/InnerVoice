import InnerVoiceKit
import SwiftUI

/// Casting Style edits (docs/CASTING-STYLE.md). Each is one undo step. The style only
/// decides who reads each character: no voice is deleted when it changes.
@MainActor extension InnerVoiceDocument {
    func setCastingStyle(_ style: CastingStyle?, undoManager: UndoManager?) {
        guard style != project.settings.castingStyle else { return }
        updateSettings("Change Casting Style", undoManager: undoManager) { $0.castingStyle = style }
        // Men, Women and Other need voices the first time the style uses them.
        VoiceCastingRun.castIfNeeded(document: self)
    }

    /// Narrator + Two Voices: whose voice reads the narration (nil: the narrator's own).
    func setNarrationRole(_ role: VoiceRole?, undoManager: UndoManager?) {
        updateSettings("Change Who Reads the Narration", undoManager: undoManager) { $0.narrationRole = role == .narrator ? nil : role }
    }

    /// Full Cast: who reads characters not marked main.
    func setMinorVoices(_ minor: MinorVoices, undoManager: UndoManager?) {
        updateSettings("Change Minor Characters", undoManager: undoManager) { $0.minorVoices = minor == .narrator ? nil : minor }
        VoiceCastingRun.castIfNeeded(document: self)
    }

    /// Who reads a character; nil goes back to the style's choice.
    func setRole(_ role: VoiceRole?, for id: UUID, undoManager: UndoManager?) {
        updateCharacter(id, "Change Who Reads", undoManager: undoManager) { $0.role = role }
        // Own Voice with no voice yet: casting gives them one.
        if role == .own { VoiceCastingRun.castIfNeeded(document: self) }
    }

    /// Who reads several characters at once, in one undo step.
    func setRole(_ role: VoiceRole?, for ids: [UUID], undoManager: UndoManager?) {
        guard ids.count != 1 else { return setRole(role, for: ids[0], undoManager: undoManager) }
        change("Change Who Reads \(ids.count) Characters", undoManager: undoManager) { p in
            for id in ids { if let i = p.characterIndex(id) { p.characters[i].role = role } }
        }
        if role == .own { VoiceCastingRun.castIfNeeded(document: self) }
    }

    /// Marks a main character (true), not main (false), or neither (nil).
    func setMain(_ main: Bool?, for ids: [UUID], undoManager: UndoManager?) {
        change(main == true ? "Mark as Main Character" : "Unmark Main Character", undoManager: undoManager) { p in
            for id in ids { if let i = p.characterIndex(id) { p.characters[i].isMain = main } }
        }
        VoiceCastingRun.castIfNeeded(document: self)
    }

    /// The narrator's choice of gender; nil goes back to what the book says. Automatic
    /// colors follow it.
    func setGender(_ gender: CharacterGender?, for ids: [UUID], undoManager: UndoManager?) {
        change(ids.count == 1 ? "Set Gender" : "Set Gender of \(ids.count) Characters", undoManager: undoManager) { p in
            for id in ids { if let i = p.characterIndex(id) { p.characters[i].gender = gender } }
        }
        recolorAutomatic()
    }

    /// How far characters the narrator plays move from the narrator's voice.
    func setShadeStrength(_ strength: ShadeStrength, undoManager: UndoManager?) {
        updateSettings("Change Shading", undoManager: undoManager) { $0.shadeStrength = strength == .clear ? nil : strength }
    }

    /// A character's shade; nil goes back to the automatic one.
    func setShade(_ shade: Shade?, for id: UUID, undoManager: UndoManager?) {
        updateCharacter(id, "Change Shade", undoManager: undoManager) { $0.shade = shade }
    }

    /// Narration Tools, Just Me: the narrator's own voice (characters of the same gender
    /// aren't shaded).
    func setNarratorGender(_ gender: CharacterGender?, undoManager: UndoManager?) {
        updateSettings("Set Your Voice", undoManager: undoManager) { $0.narratorGender = gender }
    }

    /// Three Narrators: a narrator for the narration, the men and the women, made the first
    /// time (existing narrators keep their names; one with no part yet takes the narration).
    func setUpThreeNarrators(undoManager: UndoManager?) {
        change("Set Up Three Narrators", undoManager: undoManager) { p in
            if !p.narrators.contains(where: { $0.role == .narrator }), let i = p.narrators.firstIndex(where: { $0.role == nil }) {
                p.narrators[i].role = .narrator
            }
            for (role, name) in [(VoiceRole.narrator, "Narration"), (.men, "Men’s Narrator"), (.women, "Women’s Narrator")]
            where !p.narrators.contains(where: { $0.role == role }) {
                p.narrators.append(Narrator(name: name, role: role))
            }
        }
    }

    /// The part a narrator reads (Three Narrators); nil: characters one by one.
    func setNarratorRole(_ role: VoiceRole?, for narratorID: UUID, undoManager: UndoManager?) {
        change("Change Narrator’s Part", undoManager: undoManager) { p in
            for i in p.narrators.indices {
                if p.narrators[i].id == narratorID { p.narrators[i].role = role }
                else if role != nil, p.narrators[i].role == role { p.narrators[i].role = nil }  // one narrator per part
            }
        }
    }

    /// Narration Tools: who reads a character now (their own assignment, else their part's).
    @MainActor func effectiveNarrator(for c: BookCharacter) -> Narrator? {
        let gender = c.gender ?? (CastingStyleRules.usesGender(project.settings)
            ? VoiceCasting.profile(c, project: project, index: index).gender.map(CharacterGender.init) : nil)
        return CastingStyleRules.narrator(for: c, in: project, gender: gender)
    }

    /// The voice a shared role (Men, Women, Other) speaks in: the narrator's choice.
    func setRoleVoice(_ choice: VoiceChoice, for role: VoiceRole, undoManager: UndoManager?) {
        let key: VoiceEngine = project.engineInUse == .mixed ? .mixed : choice.engine
        updateSettings("Choose \(role.title) Voice", undoManager: undoManager) {
            $0.setRoleVoice(CastVoice(choice: choice, confidence: 1, source: .user), role, for: key)
        }
    }
}
