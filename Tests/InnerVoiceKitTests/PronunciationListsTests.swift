import Foundation
import Testing
@testable import InnerVoiceKit

/// This Book's and This Mac's pronunciations (`PronunciationLists`, `PronunciationFile`).
@Suite struct PronunciationListsTests {
    static func project(_ own: [(String, String)] = []) -> Project {
        var p = Project(title: "T")
        p.pronunciations = own.map { Pronunciation(term: $0.0, sayItAs: $0.1, addedByUser: true) }
        return p
    }

    static let mac = PronunciationList(listID: "MAC", entries: [
        ListedPronunciation(term: "Kerensa", sayItAs: "keh-REN-sa"),
        ListedPronunciation(term: "Siobhan", sayItAs: "shi-VAWN"),
    ])

    @Test func theBookWinsAndTheComputerFillsIn() {
        let p = Self.project([("Kerensa", "KER-en-sa")])
        let say = PronunciationLists.sayings(p, computer: Self.mac)
        #expect(say.first { $0.term == "Kerensa" }?.say == "KER-en-sa")
        #expect(say.first { $0.term == "Siobhan" }?.say == "shi-VAWN")
        #expect(say.count == 2)
        #expect(SpeechScript.apply(say, to: "Siobhan met Kerensa.") == "shi-VAWN met KER-en-sa.")
    }

    @Test func aBookCanTurnTheComputerListOff() {
        var p = Self.project()
        p.settings.usesComputerPronunciations = false
        #expect(PronunciationLists.sayings(p, computer: Self.mac).isEmpty)
        #expect(p.settings.computerPronunciationsOff == true)
        p.settings.usesComputerPronunciations = true
        #expect(p.settings.computerPronunciationsOff == nil)
    }

    @Test func theBookKeepsACopyThatPlaysOnAnotherComputer() throws {
        var p = Self.project()
        p.computerPronunciations = PronunciationLists.copyForSaving(p, computer: Self.mac)
        #expect(p.computerPronunciations?.count == 2 && p.computerPronunciations?.allSatisfy { $0.list == "MAC" } == true)
        // Saved and read back.
        let data = try JSONEncoder().encode(p)
        let read = try JSONDecoder().decode(Project.self, from: data)
        // Another computer with its own list: its words win, the book's copy fills in.
        let pc = PronunciationList(listID: "PC", entries: [ListedPronunciation(term: "Siobhan", sayItAs: "sha-VAHN")])
        let there = PronunciationLists.sayings(read, computer: pc)
        #expect(there.first { $0.term == "Siobhan" }?.say == "sha-VAHN")
        #expect(there.first { $0.term == "Kerensa" }?.say == "keh-REN-sa")
        // No list there at all: the copy is used as it is.
        #expect(PronunciationLists.sayings(read, computer: nil).count == 2)
        // Saved there: the copy keeps the Mac's word (still from the Mac) and the PC's.
        let again = PronunciationLists.copyForSaving(read, computer: pc) ?? []
        #expect(again.first { $0.term == "Kerensa" }?.list == "MAC" && again.first { $0.term == "Siobhan" }?.list == "PC")
    }

    @Test func aWordDeletedOnThisComputerIsNotBroughtBack() {
        var p = Self.project()
        p.computerPronunciations = PronunciationLists.copyForSaving(p, computer: Self.mac)
        var mac = Self.mac
        mac.entries.removeAll { $0.term == "Siobhan" }
        let say = PronunciationLists.sayings(p, computer: mac)
        #expect(say.map(\.term) == ["Kerensa"])
        #expect(PronunciationLists.copyForSaving(p, computer: mac)?.map(\.term) == ["Kerensa"])
    }

    @Test func olderBooksReadUnchanged() throws {
        let p = Self.project([("Kerensa", "KER-en-sa")])
        let json = String(decoding: try JSONEncoder().encode(p), as: UTF8.self)
        #expect(!json.contains("computerPronunciations"))
        let read = try JSONDecoder().decode(Project.self, from: Data(json.utf8))
        #expect(read.settings.usesComputerPronunciations && read.computerPronunciations == nil)
        #expect(PronunciationLists.copyForSaving(read, computer: nil) == nil)
    }

    @Test func mergingAsksOnlyAboutRealConflicts() {
        var list = Self.mac
        let incoming = [
            ListedPronunciation(term: "kerensa", sayItAs: "KER-en-sa"),       // conflict
            ListedPronunciation(term: "Siobhan", sayItAs: "shi-VAWN", notes: "Irish"), // same
            ListedPronunciation(term: "Aoife", sayItAs: "EE-fa"),             // new
            ListedPronunciation(term: "", sayItAs: "x"),                       // skipped
        ]
        #expect(PronunciationLists.conflicts(incoming, in: list).map(\.term) == ["kerensa"])
        var kept = list
        let k = PronunciationLists.merge(incoming, into: &kept, conflict: .keepExisting)
        #expect(k == .init(added: 1, replaced: 0, kept: 1, unchanged: 1))
        #expect(kept.entry("Kerensa")?.sayItAs == "keh-REN-sa" && kept.entry("Siobhan")?.notes == "Irish")
        let r = PronunciationLists.merge(incoming, into: &list, conflict: .replace)
        #expect(r.replaced == 1 && list.entry("Kerensa")?.sayItAs == "KER-en-sa" && list.entries.count == 3)
    }

    @Test func csvRoundTripsQuotesCommasAndCase() throws {
        let entries = [
            ListedPronunciation(term: "Will", sayItAs: "Will", notes: "The name, \"not\" the verb", matchCase: true),
            ListedPronunciation(term: "Llanfair, PG", sayItAs: "HLAN-vire"),
        ]
        let csv = PronunciationFile.export(entries, as: .csv)
        #expect(String(decoding: csv, as: UTF8.self).hasPrefix("Word,Say It As,Match Case,Notes"))
        let back = try PronunciationFile.read(csv, fileName: "list.csv")
        #expect(back.map(\.term) == ["Llanfair, PG", "Will"])
        #expect(back.first { $0.term == "Will" }?.matchCase == true && back.first { $0.term == "Will" }?.notes == "The name, \"not\" the verb")
    }

    @Test func jsonRoundTripsAndPlainTextReads() throws {
        let json = PronunciationFile.export(Self.mac.entries, as: .json)
        #expect(String(decoding: json, as: UTF8.self).contains("InnerVoice Pronunciations"))
        #expect(try PronunciationFile.read(json).count == 2)
        let text = "# names\nKerensa = keh-REN-sa\nSiobhan → shi-VAWN\nAoife\tEE-fa\nnothing here\n"
        let read = try PronunciationFile.read(Data(text.utf8), fileName: "names.txt")
        #expect(read.map(\.term) == ["Kerensa", "Siobhan", "Aoife"])
        #expect(throws: PronunciationFile.ReadError.empty) { try PronunciationFile.read(Data("  \n".utf8)) }
        #expect(throws: PronunciationFile.ReadError.self) { try PronunciationFile.read(Data("{\"a\":1}".utf8)) }
    }

    @Test func csvWithoutAHeaderAndWithOtherColumnNames() throws {
        let plain = try PronunciationFile.read(Data("Word,Say It As\nKerensa,keh-REN-sa\n".utf8), fileName: "a.csv")
        #expect(plain.map(\.sayItAs) == ["keh-REN-sa"])
        let other = try PronunciationFile.read(Data("Notes,Pronunciation,Term\nnone,shi-VAWN,Siobhan\n".utf8), fileName: "b.csv")
        #expect(other.first?.term == "Siobhan" && other.first?.sayItAs == "shi-VAWN" && other.first?.notes == "none")
    }
}
