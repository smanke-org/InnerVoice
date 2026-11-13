import Foundation
import Testing
@testable import InnerVoiceKit

// Casting Style (docs/CASTING-STYLE.md): who reads each character, shades, genders to review.

@Suite struct CastingStyleTests {
    static func character(_ name: String, gender: CharacterGender? = nil, isMain: Bool? = nil, role: VoiceRole? = nil) -> BookCharacter {
        var c = BookCharacter(name: name, colorHex: "#2F7FD0")
        c.gender = gender
        c.isMain = isMain
        c.role = role
        return c
    }

    static func settings(_ style: CastingStyle?, minor: MinorVoices? = nil, narration: VoiceRole? = nil) -> ProjectSettings {
        var s = ProjectSettings()
        s.castingStyle = style
        s.minorVoices = minor
        s.narrationRole = narration
        return s
    }

    static func role(_ c: BookCharacter, _ s: ProjectSettings, anyMain: Bool = false) -> VoiceRole {
        CastingStyleRules.role(for: c, settings: s, gender: c.gender, anyMainMarked: anyMain)
    }

    // MARK: File format

    @Test func oldProjectsOpenAndSaveUnchanged() throws {
        var p = Project(title: "Old")
        p.characters = [Self.character("Kade")]
        p.narrators = [Narrator(name: "Sam")]
        let json = String(decoding: try JSONEncoder().encode(p), as: UTF8.self)
        for key in ["castingStyle", "narrationRole", "minorVoices", "shadeStrength", "roleVoices", "\"gender\"", "isMain", "\"role\"", "\"shade\""] {
            #expect(!json.contains(key), "an unset \(key) must not be written (Windows round-trip)")
        }
        let back = try JSONDecoder().decode(Project.self, from: Data(json.utf8))
        #expect(back.settings.castingStyle == nil)
        #expect(back.characters[0].gender == nil && back.characters[0].isMain == nil && back.characters[0].shade == nil)
        #expect(back.narrators[0].role == nil)
    }

    @Test func newFieldsRoundTrip() throws {
        var p = Project(title: "New")
        var c = Self.character("Lena", gender: .female, isMain: true, role: .own)
        c.shade = Shade(pitch: 2, pace: 1, words: "breathy")
        p.characters = [c]
        p.narrators = [Narrator(name: "Jo", role: .women)]
        p.settings.castingStyle = .shared
        p.settings.narrationRole = .women
        p.settings.minorVoices = .byGender
        p.settings.shadeStrength = .strong
        p.settings.setRoleVoice(CastVoice(choice: VoiceChoice(engine: .pocket, voiceID: "stock:dan"), confidence: 0.9, source: .auto), .men, for: .pocket)
        let back = try JSONDecoder().decode(Project.self, from: JSONEncoder().encode(p))
        #expect(back == p)
        #expect(back.settings.roleVoice(.men, for: .pocket)?.choice.voiceID == "stock:dan")
        #expect(back.settings.roleVoice(.women, for: .pocket) == nil)
    }

    @Test func clearingTheLastRoleVoiceLeavesNothingBehind() {
        var s = ProjectSettings()
        let v = CastVoice(choice: VoiceChoice(engine: .apple, voiceID: "x"), confidence: 1, source: .user)
        s.setRoleVoice(v, .other, for: .apple)
        s.setRoleVoice(nil, .other, for: .apple)
        #expect(s.roleVoices == nil)
    }

    // MARK: Roles

    @Test func notChosenYetPlaysAsToday() {
        for c in [Self.character("A", gender: .male), Self.character("B", role: .men), Self.character("C", isMain: false)] {
            #expect(Self.role(c, Self.settings(nil), anyMain: true) == .own)
        }
    }

    @Test func oneVoiceReadsEveryoneAsTheNarratorUnlessTheyHaveTheirOwn() {
        let s = Self.settings(.oneVoice)
        #expect(Self.role(Self.character("Kade", gender: .male), s) == .narrator)
        #expect(Self.role(Self.character("Lena", gender: .female), s) == .narrator)
        #expect(Self.role(Self.character("Kade", role: .own), s) == .own)
        #expect(Self.role(Self.character("Kade", role: .men), s) == .narrator)
    }

    @Test func sharedSortsByGenderWithAGroupForEveryoneElse() {
        let s = Self.settings(.shared)
        #expect(Self.role(Self.character("Kade", gender: .male), s) == .men)
        #expect(Self.role(Self.character("Lena", gender: .female), s) == .women)
        #expect(Self.role(Self.character("Quinn", gender: .other), s) == .other)
        #expect(Self.role(Self.character("Stranger"), s) == .other)
        #expect(Self.role(Self.character("Kade", gender: .male, role: .own), s) == .own)
        #expect(Self.role(Self.character("Lena", gender: .female, role: .narrator), s) == .narrator)
    }

    @Test func fullCastGivesMainCharactersTheirOwnVoice() {
        // Nobody marked main: everyone has their own voice, as before.
        #expect(Self.role(Self.character("Tomas", gender: .male), Self.settings(.fullCast)) == .own)
        // Someone marked main: the rest follow minorVoices (the narrator unless chosen).
        let main = Self.character("Kade", gender: .male, isMain: true)
        let minor = Self.character("Tomas", gender: .male)
        let notMain = Self.character("Rosa", gender: .female, isMain: false)
        #expect(Self.role(main, Self.settings(.fullCast), anyMain: true) == .own)
        #expect(Self.role(minor, Self.settings(.fullCast), anyMain: true) == .narrator)
        #expect(Self.role(notMain, Self.settings(.fullCast), anyMain: true) == .narrator)
        #expect(Self.role(minor, Self.settings(.fullCast, minor: .byGender), anyMain: true) == .men)
        #expect(Self.role(notMain, Self.settings(.fullCast, minor: .byGender), anyMain: true) == .women)
        #expect(Self.role(Self.character("Quinn"), Self.settings(.fullCast, minor: .byGender), anyMain: true) == .other)
        #expect(Self.role(minor, Self.settings(.fullCast, minor: .own), anyMain: true) == .own)
        // The narrator's own choice still wins.
        #expect(Self.role(Self.character("Tomas", role: .own), Self.settings(.fullCast), anyMain: true) == .own)
    }

    @Test func anyMainMarkedCountsOnlyYes() {
        var p = Project(title: "T")
        p.characters = [Self.character("A", isMain: false), Self.character("B")]
        #expect(!CastingStyleRules.anyMainMarked(p))
        p.characters.append(Self.character("C", isMain: true))
        #expect(CastingStyleRules.anyMainMarked(p))
    }

    @Test func aTwoPersonBookReadsTheNarrationInOneOfTheirVoices() {
        #expect(CastingStyleRules.reader(of: .narrator, settings: Self.settings(.shared)) == .narrator)
        #expect(CastingStyleRules.reader(of: .narrator, settings: Self.settings(.shared, narration: .women)) == .women)
        #expect(CastingStyleRules.reader(of: .men, settings: Self.settings(.shared, narration: .women)) == .men)
        // Only the shared style has a narration role.
        #expect(CastingStyleRules.reader(of: .narrator, settings: Self.settings(.oneVoice, narration: .women)) == .narrator)
    }

    @Test func changingStylesKeepsEveryVoiceAndAssignment() throws {
        var p = Project(title: "T")
        var c = Self.character("Kade", gender: .male)
        c.setVoice(CastVoice(choice: VoiceChoice(engine: .pocket, voiceID: "stock:zac"), confidence: 1, source: .user), for: .pocket)
        c.narratorID = UUID()
        p.characters = [c]
        let before = p.characters
        for style in CastingStyle.allCases + [nil] {
            p.settings.castingStyle = style
            _ = CastingStyleRules.role(for: p.characters[0], in: p, gender: .male)
        }
        #expect(p.characters == before)
    }

    // MARK: Shades

    static func profile(age: VoiceAge? = nil, timbre: [String] = [], pitchLean: Int = 0, paceLean: Int = 0) -> CastingProfile {
        var p = CastingProfile()
        p.age = age
        p.timbre = timbre
        p.pitchLean = pitchLean
        p.paceLean = paceLean
        return p
    }

    static func shade(_ g: CharacterGender?, narrator: CharacterGender?, _ strength: ShadeStrength? = nil,
                      profile: CastingProfile = profile(), own: Shade? = nil) -> Shade {
        var c = character("X", gender: g)
        c.shade = own
        return CastingStyleRules.shade(for: c, profile: profile, gender: g, strength: strength, narratorGender: narrator)
    }

    @Test func anotherGenderMovesByTheStrength() {
        #expect(Self.shade(.male, narrator: .female).pitch == -2)
        #expect(Self.shade(.male, narrator: .female, .subtle).pitch == -1)
        #expect(Self.shade(.male, narrator: .female, .strong).pitch == -3)
        #expect(Self.shade(.female, narrator: .male).pitch == 2)
        // The narrator's own gender, and Other, stay put.
        #expect(Self.shade(.female, narrator: .female).isNeutral)
        #expect(Self.shade(.male, narrator: .male).isNeutral)
        #expect(Self.shade(.other, narrator: .male).isNeutral)
        #expect(Self.shade(nil, narrator: .male).isNeutral)
        // Narrator's gender unknown: men down, women up.
        #expect(Self.shade(.male, narrator: nil).pitch == -2)
        #expect(Self.shade(.female, narrator: nil).pitch == 2)
    }

    @Test func ageAndTheBriefAdjustTheShade() {
        let child = Self.shade(.female, narrator: .female, profile: Self.profile(age: .child))
        #expect(child.pitch == 2 && child.pace == 1)
        let elder = Self.shade(.male, narrator: .female, profile: Self.profile(age: .older))
        #expect(elder.pitch == -3 && elder.pace == -1)
        let brief = Self.shade(.male, narrator: .male, profile: Self.profile(pitchLean: -1, paceLean: 1))
        #expect(brief.pitch == -1 && brief.pace == 1)
        // Never past the limits.
        let deep = Self.shade(.male, narrator: .female, .strong, profile: Self.profile(age: .older, pitchLean: -1, paceLean: -1))
        #expect(deep.pitch == -4 && deep.pace == -2)
    }

    @Test func timbreBecomesTextureButNotPitchWords() {
        let s = Self.shade(.male, narrator: .female, profile: Self.profile(timbre: ["deep", "gravelly", "rough", "slow"]))
        #expect(s.words == "gravelly, rough")
        #expect(s.cue == "lower, gravelly, rough")
        #expect(s.directions == "a little lower, gravelly, rough")
    }

    @Test func theNarratorsShadeWins() {
        let own = Shade(pitch: 1, pace: 0, words: "clipped")
        #expect(Self.shade(.male, narrator: .female, own: own) == own)
    }

    @Test func shadeClampsAndReadsAsACue() {
        #expect(Shade(pitch: 9, pace: -7).pitch == 4)
        #expect(Shade(pitch: 9, pace: -7).pace == -2)
        #expect(Shade(pitch: -3).cue == "much lower")
        #expect(Shade(pitch: 3, pace: 1).cue == "much higher, quicker")
        #expect(Shade().cue == nil)
        #expect(Shade(words: "  ").words == nil)
    }

    // MARK: Genders

    @Test func theNarratorsGenderComesFirst() {
        // "Mrs." would make her female; the narrator said otherwise.
        let p = WindowsFixtures.sampleProject()
        let index = BookIndex(p)
        var c = Self.character("Mrs. Example", gender: .male)
        #expect(VoiceCasting.profile(c, project: p, index: index).gender == .male)
        #expect(VoiceCasting.profile(c, project: p, index: index).evidence.contains("Gender: you chose"))
        c.gender = .other
        #expect(VoiceCasting.profile(c, project: p, index: index).gender == .neutral)
        c.gender = nil
        #expect(VoiceCasting.profile(c, project: p, index: index).gender == .female)
    }

    @Test func genderToReviewListsGuessesAndUnknowns() {
        var unknown = CastingProfile()
        unknown.gender = nil
        var guess = CastingProfile()
        guess.gender = .male
        guess.genderGuessed = true
        var sure = CastingProfile()
        sure.gender = .female
        let c = Self.character("Quinn")
        #expect(CastingStyleRules.needsGenderReview(c, profile: unknown))
        #expect(CastingStyleRules.needsGenderReview(c, profile: guess))
        #expect(!CastingStyleRules.needsGenderReview(c, profile: sure))
        // Once the narrator chooses, it's settled, Other included.
        #expect(!CastingStyleRules.needsGenderReview(Self.character("Quinn", gender: .other), profile: unknown))
        #expect(CastingStyleRules.gender(of: c, profile: guess) == .male)
        #expect(CastingStyleRules.gender(of: Self.character("Q", gender: .other), profile: sure) == .other)
    }

    /// Kade speaks 6 lines, Lena 3, Tomas 1; "said Name" tags only, so no pronouns.
    static func talkingBook() -> Project {
        let texts = Array(repeating: "“The tide was wrong,” said Kade.", count: 6)
            + Array(repeating: "“Then wait,” said Lena.", count: 3)
            + ["“Boxes,” said Tomas."]
        let book = ImportedBook(title: "Test", chapters: [Chapter(title: "Chapter 1", paragraphs: texts.map { Paragraph(text: $0) })])
        return ProjectBuilder.make(from: book, fileName: "Test.md")
    }

    @Test func suggestedMainCharactersAreOnlyASuggestion() throws {
        var p = Self.talkingBook()
        let index = BookIndex(p)
        let names = CastingStyleRules.suggestedMain(p, index: index).compactMap { p.character($0)?.name }
        // Over 15% of the lines, most lines first: Kade (60%), Lena (30%), not Tomas (10%).
        #expect(names == ["Kade", "Lena"])
        // Marking someone either way takes them off the suggestions; nothing is marked for them.
        let kade = try #require(p.characters.firstIndex { $0.name == "Kade" })
        #expect(p.characters.allSatisfy { $0.isMain == nil })
        p.characters[kade].isMain = false
        #expect(CastingStyleRules.suggestedMain(p, index: BookIndex(p)).compactMap { p.character($0)?.name } == ["Lena"])
    }

    @Test func genderToReviewFiltersTheUnsettled() throws {
        var p = Self.talkingBook()
        // No pronouns, no summaries: nobody's gender is settled, most lines first.
        #expect(CastingStyleRules.genderToReview(p, index: BookIndex(p)).compactMap { p.character($0)?.name } == ["Kade", "Lena", "Tomas"])
        let lena = try #require(p.characters.firstIndex { $0.name == "Lena" })
        p.characters[lena].gender = .female
        #expect(CastingStyleRules.genderToReview(p, index: BookIndex(p)).compactMap { p.character($0)?.name } == ["Kade", "Tomas"])
    }

    // MARK: Shared voices

    static let catalog: [VoiceInfo] = [
        VoiceInfo(engine: .apple, voiceID: "n", name: "Narrator", gender: .neutral, accent: "American", quality: 3),
        VoiceInfo(engine: .apple, voiceID: "m1", name: "Man", gender: .male, accent: "American", quality: 2),
        VoiceInfo(engine: .apple, voiceID: "m2", name: "Boy", gender: .male, age: .child, accent: "American", quality: 3),
        VoiceInfo(engine: .apple, voiceID: "w1", name: "Woman", gender: .female, accent: "American", quality: 2),
        VoiceInfo(engine: .apple, voiceID: "x1", name: "Neutral", gender: .neutral, accent: "American", quality: 1),
    ]

    @Test func sharedRolesGetDistinctFittingVoices() {
        var p = Project(title: "T")
        p.settings.castingStyle = .shared
        p.settings.setNarratorVoice(CastVoice(choice: VoiceChoice(engine: .apple, voiceID: "n"), confidence: 1, source: .user), for: .apple)
        #expect(VoiceCasting.castRoles(&p, engine: .apple, voices: Self.catalog) == 3)
        // An adult man over a higher-quality boy; never the narrator's voice.
        #expect(p.settings.roleVoice(.men, for: .apple)?.choice.voiceID == "m1")
        #expect(p.settings.roleVoice(.women, for: .apple)?.choice.voiceID == "w1")
        #expect(p.settings.roleVoice(.other, for: .apple)?.choice.voiceID == "x1")
        // A voice the narrator chose stays; automatic ones are kept only when asked.
        p.settings.setRoleVoice(CastVoice(choice: VoiceChoice(engine: .apple, voiceID: "m2"), confidence: 1, source: .user), .men, for: .apple)
        VoiceCasting.castRoles(&p, engine: .apple, voices: Self.catalog)
        #expect(p.settings.roleVoice(.men, for: .apple)?.choice.voiceID == "m2")
        #expect(p.settings.roleVoice(.women, for: .apple)?.source == .auto)
    }

    @Test func onlyStylesThatShareVoicesCastThem() {
        var p = Project(title: "T")
        p.settings.castingStyle = .oneVoice
        #expect(VoiceCasting.castRoles(&p, engine: .apple, voices: Self.catalog) == 0)
        p.settings.castingStyle = .fullCast
        #expect(VoiceCasting.castRoles(&p, engine: .apple, voices: Self.catalog) == 0)
        p.settings.minorVoices = .byGender
        #expect(VoiceCasting.castRoles(&p, engine: .apple, voices: Self.catalog) == 3)
    }

    @Test func readersFollowTheStyle() {
        var p = Self.talkingBook()
        let index = BookIndex(p)
        func reader(_ name: String) -> VoiceRole? {
            CastingStyleRules.readers(p, index: index)[p.characters.first { $0.name == name }!.id]
        }
        #expect(reader("Kade") == .own)
        p.settings.castingStyle = .shared
        // No genders in this book yet: everyone is Other until the narrator chooses.
        #expect(reader("Kade") == .other)
        p.characters[p.characters.firstIndex { $0.name == "Kade" }!].gender = .male
        #expect(reader("Kade") == .men)
        p.settings.castingStyle = .oneVoice
        #expect(reader("Kade") == .narrator)
    }

    // MARK: Narration Tools

    @Test func narratorsFollowTheirPart() {
        let narration = Narrator(name: "Sam", role: .narrator)
        let men = Narrator(name: "Alex", role: .men)
        let women = Narrator(name: "Jo", role: .women)
        var p = Project(title: "T")
        p.narrators = [narration, men, women]
        p.settings.castingStyle = .shared
        let kade = Self.character("Kade", gender: .male)
        let lena = Self.character("Lena", gender: .female)
        let quinn = Self.character("Quinn")
        p.characters = [kade, lena, quinn]
        #expect(CastingStyleRules.narrator(for: kade, in: p, gender: .male)?.id == men.id)
        #expect(CastingStyleRules.narrator(for: lena, in: p, gender: .female)?.id == women.id)
        // Nobody has the Other part: the narration narrator reads them.
        #expect(CastingStyleRules.narrator(for: quinn, in: p, gender: nil)?.id == narration.id)
        // A two-person book: the women's narrator reads the narration too.
        p.settings.narrationRole = .women
        p.narrators = [men, women]
        let toNarrator = Self.character("Hale", gender: .male, role: .narrator)
        #expect(CastingStyleRules.narrator(for: toNarrator, in: p, gender: .male)?.id == women.id)
        // The narrator's own assignment wins.
        var assigned = kade
        assigned.narratorID = women.id
        #expect(CastingStyleRules.narrator(for: assigned, in: p, gender: .male)?.id == women.id)
    }

    @Test func narrationNarratorFollowsTheTwoPersonChoice() {
        var p = Project(title: "T")
        let sam = Narrator(name: "Sam", role: .narrator), jo = Narrator(name: "Jo", role: .women)
        p.narrators = [sam, jo]
        p.settings.castingStyle = .shared
        #expect(CastingStyleRules.narrationNarrator(p)?.id == sam.id)
        p.settings.narrationRole = .women
        #expect(CastingStyleRules.narrationNarrator(p)?.id == jo.id)
    }

    @Test func justMeShadesForThePersonsOwnVoice() {
        var p = Self.talkingBook()
        p.settings.castingStyle = .oneVoice
        let kade = p.characters.firstIndex { $0.name == "Kade" }!, lena = p.characters.firstIndex { $0.name == "Lena" }!
        p.characters[kade].gender = .male
        p.characters[lena].gender = .female
        let index = BookIndex(p)
        // A woman narrating: Kade goes lower, Lena stays in her voice.
        let her = CastingStyleRules.shades(p, index: index, narratorGender: .female)
        #expect(her[p.characters[kade].id]?.pitch == -2)
        #expect(her[p.characters[lena].id] == nil)
        // A man narrating: the other way round.
        let him = CastingStyleRules.shades(p, index: index, narratorGender: .male)
        #expect(him[p.characters[kade].id] == nil)
        #expect(him[p.characters[lena].id]?.pitch == 2)
        // No style chosen: no shades at all.
        p.settings.castingStyle = nil
        #expect(CastingStyleRules.shades(p, index: index, narratorGender: .male).isEmpty)
    }

    @Test func justMeIsTheOnlyNarrator() {
        var p = Project(title: "T")
        let me = Narrator(name: "Me")
        p.narrators = [me]
        p.settings.castingStyle = .oneVoice
        #expect(CastingStyleRules.narrator(for: Self.character("Kade", gender: .male), in: p, gender: .male)?.id == me.id)
        // Full Cast needs each character assigned.
        p.settings.castingStyle = .fullCast
        #expect(CastingStyleRules.narrator(for: Self.character("Kade", gender: .male), in: p, gender: .male) == nil)
    }
}
