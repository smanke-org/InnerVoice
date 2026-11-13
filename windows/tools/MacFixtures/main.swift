// Writes the Mac-made fixtures for the Pocket TTS port into windows/Fixtures, with the Mac's
// own Kit (Sources/InnerVoiceKit, compiled into this tool by make-mac-fixtures.sh, so nothing
// outside windows/ changes):
//   sample-mac.innervoice          the Mac's sample project as Tests/InnerVoiceKitTests/
//                                  WindowsFixtures.swift writes it since Pocket TTS (engine
//                                  "pocket", stock voices), from the same markdown
//   legacy-qwen.json               the old book from PocketTTSTests.oldProject() (engine "qwen")
//   legacy-qwen-migrated-mac.json  that book after the Mac's LegacyVoices.migrate
//   sample-windows-migrated-mac.json  the Windows-saved Qwen3-TTS sample after the Mac's migrate
// The Windows tests migrate the same inputs and compare field by field.

import Foundation

let folder = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "windows/Fixtures")
let sorted = JSONEncoder()
sorted.outputFormatting = [.sortedKeys]
let pretty = JSONEncoder()
pretty.outputFormatting = [.sortedKeys, .prettyPrinted]

// The sample story, as the current fixture holds it (Original/Sample Story.md).
let oldSample = try ProjectArchive.read(Data(contentsOf: folder.appending(path: "sample-mac.innervoice")))
let markdown = String(decoding: oldSample.original!.data, as: UTF8.self)

func sampleProject() -> Project {
    let blocks = PlainTextReader.blocks(from: markdown, markdown: true)
    let chapters = ChapterDetector.chapters(from: blocks, fallbackTitle: "Sample Story")
    return ProjectBuilder.make(from: ImportedBook(title: "Sample Story", author: "A. N. Example", chapters: chapters),
                               fileName: "Sample Story.md")
}

// MARK: sample-mac.innervoice (WindowsFixtures.writeFixtures, part 2, at 9d41ee9)

do {
    var p = sampleProject()
    let tom = p.characters.first { $0.name.hasPrefix("Tom") }!.id
    let narrator = Narrator(name: "Sample Narrator", notes: "Reads the men.", role: .men)
    p.narrators = [narrator]
    if let i = p.characters.firstIndex(where: { $0.id == tom }) {
        p.characters[i].narratorID = narrator.id
        p.characters[i].facts = [Fact(kind: .age, text: "About sixty", sourceParagraphID: p.chapters[0].paragraphs[8].id,
                                      quote: "sixty if he was a day", fromAI: true)]
        p.characters[i].voiceNotes = "Gravel, Highland burr."
        p.characters[i].setVoice(CastVoice(choice: VoiceChoice(engine: .pocket, voiceID: "stock:dan", pitch: 0.89),
                                           confidence: 0.8, source: .user), for: .pocket)
        p.characters[i].pinned = true
        p.characters[i].gender = .male
        p.characters[i].isMain = true
        p.characters[i].role = .own
        p.characters[i].shade = Shade(pitch: -2, pace: -1, words: "gravelly")
    }
    p.settings.castingStyle = .shared
    p.settings.narrationRole = .narrator
    p.settings.minorVoices = .byGender
    p.settings.shadeStrength = .subtle
    p.settings.setRoleVoice(CastVoice(choice: VoiceChoice(engine: .pocket, voiceID: "stock:leo"), confidence: 0.9, source: .auto),
                            .men, for: .pocket)
    p.lines[0].aiModel = "gemma-4-e2b"
    p.settings.voiceEngine = .pocket
    p.settings.purpose = .aiVoices
    p.settings.cloudVoicesAllowed = Date(timeIntervalSinceReferenceDate: 800_000_000.25)
    p.settings.stepsDone = ["manuscript"]
    p.settings.setNarratorVoice(CastVoice(choice: VoiceChoice(engine: .apple, voiceID: "com.apple.voice.premium.en-US.Zoe"),
                                          confidence: 1, source: .user), for: .apple)
    p.tonesTagged = Date(timeIntervalSinceReferenceDate: 800_000_100.5)
    p.voicesCast = Date(timeIntervalSinceReferenceDate: 800_000_200.125)
    p.importedAt = Date(timeIntervalSinceReferenceDate: 799_999_999.123456)
    p.recorded = [p.chapters[0].paragraphs[1].id, p.chapters[0].paragraphs[2].id]
    p.retakes = [Retake(paragraphID: p.chapters[0].paragraphs[3].id, note: "Breath", created: Date(timeIntervalSinceReferenceDate: 800_000_300))]
    p.pronunciations = [Pronunciation(term: "Kerensa", sayItAs: "keh-REN-sah", audioFile: "Kerensa.m4a", occurrences: 3,
                                      firstParagraphID: p.chapters[1].paragraphs[5].id)]
    p.revisions = [RevisionRecord(date: Date(timeIntervalSinceReferenceDate: 800_000_400), fileName: "Sample Story v2.md",
                                  added: [], changed: [p.chapters[1].paragraphs[0].id],
                                  removed: [.init(text: "An old line.", chapterTitle: "Chapter 1: The Storm")], unchanged: 40)]
    p.lastTagged = Date(timeIntervalSinceReferenceDate: 800_000_500)
    let archive = try ProjectArchive.write(.init(
        projectJSON: try sorted.encode(p),
        original: .init(name: "Sample Story.md", data: Data(markdown.utf8)),
        audio: ["Kerensa.m4a": Data([0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70])]))
    try archive.write(to: folder.appending(path: "sample-mac.innervoice"))
}

// MARK: legacy-qwen.json and its migration (PocketTTSTests.oldProject, at 9d41ee9)

func book(_ paragraphs: [String]) -> ImportedBook {
    ImportedBook(title: "Test", chapters: [Chapter(title: "Chapter 1", paragraphs: paragraphs.map { Paragraph(text: $0) })])
}

func oldProjectJSON() throws -> Data {
    var p = ProjectBuilder.make(from: book([
        "“Morning,” said Edda Voss.", "“Morning,” said Mara Quinn.", "“Tea?” said Tom Hale.",
        "“Please,” Mara said.", "“Two sugars,” said Edda.", "“Coming,” said Pip.",
    ]), fileName: "t.txt")
    for i in p.characters.indices {
        switch p.characters[i].name {
        case "Edda Voss": p.characters[i].facts = [Fact(kind: .gender, text: "Woman"), Fact(kind: .age, text: "About 60")]
        case "Tom Hale": p.characters[i].facts = [Fact(kind: .gender, text: "Man")]
        case "Pip": p.characters[i].facts = [Fact(kind: .gender, text: "Boy"), Fact(kind: .age, text: "Eight")]
        default: break
        }
    }
    p.voicesCast = Date(timeIntervalSinceReferenceDate: 700_000_000)
    var json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(p)) as! [String: Any]
    func voice(_ id: String, design: String? = nil, source: String = "auto", pitch: Double? = nil) -> [String: Any] {
        var choice: [String: Any] = ["engine": "qwen", "voiceID": id]
        if let design { choice["design"] = design }
        if let pitch { choice["pitch"] = pitch }
        return ["choice": choice, "confidence": 0.7, "source": source]
    }
    var characters = json["characters"] as! [[String: Any]]
    for i in characters.indices {
        let orpheus: [String: Any] = ["choice": ["engine": "orpheus", "voiceID": "leo"], "confidence": 1, "source": "user"]
        switch characters[i]["name"] as! String {
        case "Edda Voss": characters[i]["voices"] = ["qwen": voice("preset:grandmother", source: "user", pitch: 0.95)]
        case "Mara Quinn": characters[i]["voices"] = ["qwen": voice("design", design: "A woman in her thirties with a warm voice.")]
        case "Tom Hale": characters[i]["voices"] = ["qwen": voice("design", design: "A gruff old sailor.", source: "user"), "orpheus": orpheus]
        case "Pip": characters[i]["voices"] = ["qwen": voice("clone:ABC", source: "user")]
        default: break
        }
    }
    json["characters"] = characters
    var settings = json["settings"] as! [String: Any]
    settings["voiceEngine"] = "qwen"
    settings["narratorVoices"] = ["qwen": voice("preset:narrator", source: "user")]
    settings["roleVoices"] = ["men": ["qwen": voice("preset:baritone")], "women": ["qwen": voice("design", design: "A woman.", source: "user")]]
    json["settings"] = settings
    return try JSONSerialization.data(withJSONObject: json, options: [.sortedKeys])
}

let old = try oldProjectJSON()
try old.write(to: folder.appending(path: "legacy-qwen.json"))
var migrated = try JSONDecoder().decode(Project.self, from: old)
precondition(LegacyVoices.migrate(&migrated))
try pretty.encode(migrated).write(to: folder.appending(path: "legacy-qwen-migrated-mac.json"))

// MARK: the Windows-saved sample (Qwen3-TTS) after the Mac's migrate

let windowsSample = try ProjectArchive.read(Data(contentsOf: folder.appending(path: "sample-windows.innervoice")))
var w = try JSONDecoder().decode(Project.self, from: windowsSample.projectJSON)
precondition(LegacyVoices.migrate(&w))
try pretty.encode(w).write(to: folder.appending(path: "sample-windows-migrated-mac.json"))
print("Wrote sample-mac.innervoice, legacy-qwen.json, legacy-qwen-migrated-mac.json and sample-windows-migrated-mac.json to \(folder.path)")
