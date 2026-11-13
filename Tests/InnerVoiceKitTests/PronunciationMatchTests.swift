import Testing
import Foundation
@testable import InnerVoiceKit

@Test func pronunciationCoversPossessivesPluralsAndCase() {
    let s = [SpeechScript.Saying("Barnaby", "BAR-nuh-bee")]
    func say(_ t: String) -> String { SpeechScript.apply(s, to: t) }
    #expect(say("Barnaby ran.") == "BAR-nuh-bee ran.")
    #expect(say("Barnaby's hat") == "BAR-nuh-bee's hat")
    #expect(say("Barnaby’s hat") == "BAR-nuh-bee’s hat")
    #expect(say("the Barnabys’ house") == "the BAR-nuh-bees’ house")
    #expect(say("BARNABY!") == "BAR-nuh-bee!")
    #expect(say("Barnabyish and SirBarnaby") == "Barnabyish and SirBarnaby")
    #expect(SpeechScript.apply([.init("Ross", "RAWSS")], to: "the Rosses") == "the RAWSSes")
    #expect(SpeechScript.apply([.init("Jam", "jahm")], to: "James") == "James")
    #expect(SpeechScript.apply([.init("Will", "Vill", matchCase: true)], to: "Will will go.") == "Vill will go.")
    #expect(SpeechScript.apply([.init("Ann", "$1 an")], to: "Ann") == "$1 an")
}

@Test func matchCaseRoundTripsAndOldFilesRead() throws {
    let old = #"{"id":"9C1B5C1E-6C47-4E43-9B39-2F9E5C2B8A11","term":"X","sayItAs":"","notes":"","confirmed":false,"occurrences":0,"dismissed":false,"addedByUser":false}"#
    let p = try JSONDecoder().decode(Pronunciation.self, from: Data(old.utf8))
    #expect(!p.matchCase)
    var q = p; q.matchCase = true
    let back = try JSONDecoder().decode(Pronunciation.self, from: JSONEncoder().encode(q))
    #expect(back.matchCase)
}
