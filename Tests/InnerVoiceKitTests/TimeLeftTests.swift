import Testing
@testable import InnerVoiceKit

@Suite struct TimeLeftTests {
    @Test func waitsForEnoughToGoOn() {
        #expect(TimeLeft.estimate(done: 4, total: 100, elapsed: 60) == nil)
        #expect(TimeLeft.estimate(done: 10, total: 100, elapsed: 10) == nil)
        #expect(TimeLeft.estimate(done: 100, total: 100, elapsed: 600) == nil)
    }
    @Test func scalesTheRateSoFar() {
        #expect(TimeLeft.estimate(done: 10, total: 100, elapsed: 60) == "about 9 min left")
        #expect(TimeLeft.estimate(done: 10, total: 1000, elapsed: 60) == "about 1 hr 39 min left")
        #expect(TimeLeft.text(30) == "less than a minute left")
        #expect(TimeLeft.text(7200) == "about 2 hr left")
    }
}
