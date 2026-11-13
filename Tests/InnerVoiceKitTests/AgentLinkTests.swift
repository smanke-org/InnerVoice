import Foundation
import Testing
@testable import InnerVoiceKit

// innervoice:// links (AgentLink), the same cases as the Windows app's LinksOnlyShowThings
// (windows/tests/InnerVoice.Core.Tests/AgentTests.cs). The self-test checks the app's real
// command list against the same rules.

@Suite struct AgentLinkTests {
    typealias P = AgentLink.Command.Param
    static let project = P(name: "project")
    /// The commands the links below name, with the app's params.
    static let commands: [AgentLink.Command] = [
        .init(name: "open", params: [P(name: "page", positional: true), P(name: "character"), project]),
        .init(name: "projects open", params: [P(name: "path", required: true, positional: true, isPath: true)]),
        .init(name: "projects list"),
        .init(name: "lines reveal", params: [P(name: "id", required: true, positional: true), project]),
        .init(name: "lines assign", params: [P(name: "ids", required: true, positional: true, multiple: true), P(name: "to", required: true), project]),
        .init(name: "model download", params: [P(name: "id", required: true, positional: true), P(name: "yes")]),
        .init(name: "export", params: [P(name: "kind", required: true), P(name: "out", isPath: true), project]),
    ]

    func parse(_ url: String) throws -> AgentLink.Request { try AgentLink.parse(url, commands: Self.commands) }

    func refusal(_ url: String) -> String {
        do {
            _ = try parse(url)
            return "followed"
        } catch {
            return (error as? AgentLink.Refusal)?.message ?? "\(error)"
        }
    }

    @Test func linksOnlyShowThings() throws {
        var r = try parse("innervoice://")
        #expect(r.command == nil)
        #expect(r.args.isEmpty)

        r = try parse("innervoice://open/cast?character=Mara%20Quinn&project=Harbor+Book")
        #expect(r.command == "open")
        #expect(r.args == ["page": "cast", "character": "Mara Quinn", "project": "Harbor Book"])

        r = try parse("InnerVoice://Lines/Reveal/3f2a9c1e/")
        #expect(r.command == "lines reveal")
        #expect(r.args["id"] == "3f2a9c1e")

        let full = FileManager.default.temporaryDirectory.appending(path: "Harbor Book.innervoice").path
        r = try parse("innervoice://projects/open?path=" + full.addingPercentEncoding(withAllowedCharacters: .alphanumerics)!)
        #expect(r.command == "projects open")
        #expect(r.args["path"] == full)

        r = try parse("innervoice://open/review?project=Harbor#ignored")
        #expect(r.args == ["page": "review", "project": "Harbor"])

        #expect(refusal("innervoice://lines/assign/3f2a9c1e?to=Mara").contains("a link can't run `lines assign`"))
        #expect(refusal("innervoice://model/download/gemma-4-e4b?yes=true").contains("a link can't run `model download`"))
        #expect(refusal("innervoice://export?kind=cast").contains("a link can't run `export`"))
        #expect(refusal("innervoice://projects/list").contains("a link can't run `projects list`"))
        #expect(refusal("innervoice://projects/open?path=Harbor.innervoice").contains("must be a full path"))
        // A path in the link's words is one word: its slashes are escaped.
        #expect(try parse("innervoice://projects/open/%2FUsers%2Fme%2FHarbor.innervoice").args["path"] == "/Users/me/Harbor.innervoice")
        #expect(refusal("innervoice://projects/open/~%2FHarbor.innervoice").contains("must be a full path"))
        #expect(refusal("innervoice://open/cast?yes=true").contains("has no option yes"))
        #expect(refusal("innervoice://lines/reveal").contains("missing id"))
        #expect(refusal("innervoice://lines/reveal/3f2a/9c1e").contains("unexpected \"9c1e\""))
        #expect(refusal("innervoice://nothing").contains("unknown command"))
        #expect(refusal("innervoice://?project=Harbor").contains("give a command"))
        #expect(refusal("https://example.com/open/cast").contains("isn't an innervoice:// link"))
    }
}
