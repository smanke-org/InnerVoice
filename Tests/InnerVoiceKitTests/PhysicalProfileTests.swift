import Foundation
import Testing
@testable import InnerVoiceKit

private let reply = #"{"summary":"A tall, wiry woman with cropped gray hair.","summaryInferred":true,"age":{"text":"about sixty","inferred":true},"height":{"text":"Tall","inferred":false},"build":{"text":"","inferred":false},"hair":{"text":"Long and gray","inferred":false},"eyes":{"text":"unknown","inferred":false},"skin":{"text":"","inferred":false},"face":{"text":"","inferred":false},"marks":{"text":"Scar on her left hand","inferred":false},"clothing":{"text":"","inferred":false},"other":{"text":"","inferred":false},"changes":[{"chapter":3,"field":"hair","text":"cuts it short","inferred":false},{"chapter":99,"field":"marks","text":"Burned","inferred":true}],"flags":[{"field":"eyes","text":"Blue in chapter 1, green in chapter 4"},{"field":"wings","text":"x"}]}"#

@Test func physicalProfileParsesWithInferredLabelsChangesAndFlags() throws {
    let p = try #require(PhysicalProfilePrompt.parse(reply, chapters: 10))
    #expect(p.summaryInferred)
    #expect(p.value(.age).text == "About sixty" && p.value(.age).inferred)
    #expect(p.value(.height).inferred == false)
    #expect(p.values[.eyes] == nil && p.values[.build] == nil)  // "unknown" and "" are left empty
    #expect(p.changes.count == 2)
    #expect(p.changes[0].chapter == 2 && p.changes[0].text == "Cuts it short")
    #expect(p.changes[1].chapter == nil)  // past the last chapter
    #expect(p.flags.count == 1 && p.flags[0].field == .eyes)
    #expect(PhysicalProfilePrompt.parse("no json", chapters: 3) == nil)
}

@Test func physicalProfileIsReplacedOnlyWhileUnedited() throws {
    let p = try #require(PhysicalProfilePrompt.parse(reply, chapters: 10))
    var c = BookCharacter(name: "Edda", colorHex: "#10B981")
    #expect(PhysicalProfile.put(p, into: &c))
    #expect(!PhysicalProfile.isEdited(c))
    var fresh = p; fresh.summary = "Rewritten."
    #expect(PhysicalProfile.put(fresh, into: &c) && c.physical?.summary == "Rewritten.")
    c.physical?.set(.eyes, .init(text: "Gray"))
    #expect(PhysicalProfile.isEdited(c))
    var again = p; again.summary = "Third."
    #expect(!PhysicalProfile.put(again, into: &c))
    #expect(c.physical?.value(.eyes).text == "Gray")
}

@Test func physicalProfileRoundTripsAsAJSONObject() throws {
    var c = BookCharacter(name: "Edda", colorHex: "#10B981")
    c.physical = try #require(PhysicalProfilePrompt.parse(reply, chapters: 10))
    let data = try JSONEncoder().encode(c)
    let json = String(decoding: data, as: UTF8.self)
    #expect(json.contains(#""hair":{"#))  // fields keyed by name, same as Windows
    let back = try JSONDecoder().decode(BookCharacter.self, from: data)
    #expect(back.physical == c.physical)
    // Older books have no profile.
    let old = try JSONDecoder().decode(BookCharacter.self, from: try JSONEncoder().encode(BookCharacter(name: "A", colorHex: "#10B981")))
    #expect(old.physical == nil)
}

@Test func characterProfilesMarkInferredItems() throws {
    var proj = ProjectBuilder.make(from: ImportedBook(title: "T", chapters: [Chapter(title: "Chapter 1", paragraphs: [Paragraph(text: "“Hello,” said Edda.", style: .body, italic: [])])]), fileName: "t.txt")
    let i = try #require(proj.characters.firstIndex { $0.name == "Edda" })
    proj.characters[i].physical = try #require(PhysicalProfilePrompt.parse(reply, chapters: 10))
    let md = CharacterProfiles.markdown(proj, index: BookIndex(proj))
    #expect(md.contains("## Edda") && md.contains("### Physical Description"))
    #expect(md.contains("- **Apparent Age:** About sixty *(inferred)*"))
    #expect(md.contains("**Continuity to check**"))
}
