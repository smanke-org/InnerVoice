// Writes the Mac-made pronunciation fixtures into windows/Fixtures/pronunciations with the Mac's
// own Kit (PronunciationLists.swift, compiled into this tool by make-pronunciation-fixtures.sh),
// and reads back the files the Windows app exported there, so a file made on either computer
// opens on the other:
//   mac-export.csv / mac-export.json   PronunciationFile.export of `entries`
//   mac-list.json                      This Mac's list as the app keeps it ({listID, entries})
//   mac-project.json                   a small book with its own entry, its copy of This Mac's
//                                      list (each entry with `list`) and the list turned off
//   windows-export.csv / .json         written by the Windows tests (PronunciationFileTests);
//                                      this tool checks the Mac reads them as `entries`

import Foundation

let folder = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "windows/Fixtures")
    .appending(path: "pronunciations")
try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
let sorted = JSONEncoder()
sorted.outputFormatting = [.sortedKeys]

func id(_ n: Int) -> UUID { UUID(uuidString: String(format: "00000000-0000-4000-8000-%012d", n))! }

// Placeholder words only. Quotes, commas, a line break, Match Case, a non-ASCII letter, and a
// book-copy entry whose `list` an export leaves out.
let entries = [
    ListedPronunciation(id: id(1), term: "Kerensa", sayItAs: "keh-REN-sa", notes: "Cornish"),
    ListedPronunciation(id: id(2), term: "Will", sayItAs: "Will", notes: "The name, \"not\" the verb", matchCase: true),
    ListedPronunciation(id: id(3), term: "Llanfair, PG", sayItAs: "HLAN-vire"),
    ListedPronunciation(id: id(4), term: "Siobhan", sayItAs: "shi-VAWN", notes: "Irish\nTwo lines"),
    ListedPronunciation(id: id(5), term: "Zoë", sayItAs: "ZO-ee"),
    ListedPronunciation(id: id(6), term: "aoife", sayItAs: "EE-fa", list: "OTHER"),
]

try PronunciationFile.export(entries, as: .csv).write(to: folder.appending(path: "mac-export.csv"))
try PronunciationFile.export(entries, as: .json).write(to: folder.appending(path: "mac-export.json"))

let list = PronunciationList(listID: "MAC-LIST", entries: Array(entries.prefix(3)))
try sorted.encode(list).write(to: folder.appending(path: "mac-list.json"))

var p = Project(title: "Pronunciation Fixture", importedAt: Date(timeIntervalSinceReferenceDate: 800_000_000))
p.pronunciations = [Pronunciation(id: id(10), term: "Kerensa", sayItAs: "KER-en-sa", addedByUser: true)]
p.computerPronunciations = PronunciationLists.copyForSaving(p, computer: list)
p.settings.usesComputerPronunciations = false
try sorted.encode(p).write(to: folder.appending(path: "mac-project.json"))
print("Wrote mac-export.csv, mac-export.json, mac-list.json and mac-project.json to \(folder.path)")

// What the Mac should read from any export of `entries`.
func plain(_ e: [ListedPronunciation]) -> [String] {
    e.map { "\($0.term)|\($0.sayItAs)|\($0.notes)|\($0.matchCase)|\($0.list ?? "-")" }.sorted()
}
let expected = plain(entries.map { var e = $0; e.list = nil; return e })
var failed = false
for name in ["mac-export.csv", "mac-export.json", "windows-export.csv", "windows-export.json"] {
    let url = folder.appending(path: name)
    guard FileManager.default.fileExists(atPath: url.path) else { print("\(name): not there yet"); continue }
    let read = plain(try PronunciationFile.read(Data(contentsOf: url), fileName: name))
    if read == expected { print("\(name): the Mac reads it") } else { print("\(name): MISMATCH\n  \(read)\n  \(expected)"); failed = true }
}
if failed { exit(1) }
