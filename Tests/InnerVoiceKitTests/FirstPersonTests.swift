import Foundation
import Testing
@testable import InnerVoiceKit

@Suite struct FirstPersonTests {
    @Test func tellerGetsOwnRoleOthersByGender() {
        let me = BookCharacter(name: "Mara", colorHex: "#2F7FD0")
        let tom = BookCharacter(name: "Tom", colorHex: "#2F7FD0")
        var s = ProjectSettings()
        s.castingStyle = .firstPerson
        s.povCharacterID = me.id
        #expect(CastingStyleRules.role(for: me, settings: s, gender: .female, anyMainMarked: false) == .own)
        #expect(CastingStyleRules.role(for: tom, settings: s, gender: .male, anyMainMarked: false) == .men)
        #expect(CastingStyleRules.sharedRolesNeeded(s) == VoiceRole.sharedRoles)
    }

    @Test func chapterNarratorsOverrideTheBook() {
        let me = UUID(), other = UUID(), ch1 = UUID(), ch2 = UUID()
        var s = ProjectSettings()
        s.povCharacterID = me
        var fp = FirstPersonSettings()
        fp.chapterNarrators = [ch2: other]
        s.firstPerson = fp
        #expect(FirstPersonRules.teller(ofChapter: ch1, settings: s) == me)
        #expect(FirstPersonRules.teller(ofChapter: ch2, settings: s) == other)
        #expect(FirstPersonRules.tellers(s) == [me, other])
    }

    @Test func defaultsAreVariantNarratorReadsNarrationThoughtsVoiced() {
        let fp = FirstPersonSettings()
        #expect(fp.voiceMode == .variant)
        #expect(fp.variantShade == FirstPersonSettings.defaultShade)
        #expect(fp.readsNarration == nil && fp.voicesThoughts == nil)
    }

    @Test func roundTripsThroughJSON() throws {
        var s = ProjectSettings()
        var fp = FirstPersonSettings()
        fp.voice = .different
        fp.readsNarration = true
        fp.chapterNarrators = [UUID(): UUID()]
        s.firstPerson = fp
        s.castingStyle = .firstPerson
        let back = try JSONDecoder().decode(ProjectSettings.self, from: JSONEncoder().encode(s))
        #expect(back.firstPerson == fp && back.castingStyle == .firstPerson)
    }
}
