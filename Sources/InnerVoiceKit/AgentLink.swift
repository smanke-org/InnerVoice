import Foundation

/// innervoice:// links. The words after innervoice:// name an agent command, as on the command
/// line, and the query gives its options; words after the command fill its positional params:
///   innervoice://                                     bring InnerVoice to the front
///   innervoice://open/cast?character=Mara             open Mara on the Cast page
///   innervoice://open/review?project=Harbor           the Review page of the open project Harbor
///   innervoice://lines/reveal/3f2a9c1e                show that line in the manuscript
///   innervoice://projects/open?path=/Users/me/Books/Harbor.innervoice
/// A link can be clicked anywhere (a web page, a document), so it only shows things: no command
/// that edits, saves, exports, runs the AI or downloads works from a link. The Windows app reads
/// links the same way (windows/src/InnerVoice.Agent/AgentUrl.cs).
public enum AgentLink {
    public static let scheme = "innervoice"

    /// The commands a link may run.
    public static let allowed = ["open", "lines reveal", "projects open"]

    /// What the parser needs to know of an agent command (the app's AgentCommand).
    public struct Command: Sendable {
        public struct Param: Sendable {
            public var name: String
            public var required: Bool
            public var positional: Bool
            public var multiple: Bool
            /// A file or folder: a link must give it as a full path.
            public var isPath: Bool

            public init(name: String, required: Bool = false, positional: Bool = false, multiple: Bool = false, isPath: Bool = false) {
                self.name = name
                self.required = required
                self.positional = positional
                self.multiple = multiple
                self.isPath = isPath
            }
        }

        public var name: String
        public var params: [Param]

        public init(name: String, params: [Param] = []) {
            self.name = name
            self.params = params
        }
    }

    /// The command to run (nil: only bring InnerVoice forward) and its options.
    public struct Request: Equatable, Sendable {
        public var command: String?
        public var args: [String: String]
    }

    /// Why a link can't be followed, said so the person can fix it.
    public struct Refusal: Error, Equatable, LocalizedError, Sendable {
        public var message: String
        public var errorDescription: String? { message }
    }

    public static func isLink(_ text: String) -> Bool {
        text.lowercased().hasPrefix(scheme + ":")
    }

    public static func parse(_ url: String, commands: [Command]) throws -> Request {
        guard isLink(url) else { throw Refusal(message: "\"\(url)\" isn't an innervoice:// link") }
        var rest = Substring(url.dropFirst(scheme.count + 1))
        while rest.first == "/" { rest = rest.dropFirst() }
        if let hash = rest.firstIndex(of: "#") { rest = rest[..<hash] }
        let path: Substring
        let query: Substring
        if let question = rest.firstIndex(of: "?") {
            path = rest[..<question]
            query = rest[rest.index(after: question)...]
        } else {
            path = rest
            query = ""
        }

        let words = path.split(separator: "/").map { unescape($0) }.filter { !$0.isEmpty }
        var args: [String: String] = [:]
        for pair in query.split(separator: "&") {
            let parts = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            let key = unescape(parts[0])
            if !key.isEmpty { args[key] = parts.count > 1 ? unescape(parts[1]) : "true" }
        }
        if words.isEmpty {
            guard args.isEmpty else { throw Refusal(message: "give a command after innervoice://, like innervoice://open/cast") }
            return Request(command: nil, args: args)
        }

        let lowered = words.map { $0.lowercased() }
        let matches = commands.filter { command in
            let name = command.name.split(separator: " ").map(String.init)
            return lowered.count >= name.count && zip(name, lowered).allSatisfy { $0 == $1 }
        }
        guard let command = matches.max(by: { $0.name.count < $1.name.count }) else {
            throw Refusal(message: "unknown command \"\(words.prefix(2).joined(separator: " "))\" in the link")
        }
        guard allowed.contains(command.name) else {
            let list = allowed.map { scheme + "://" + $0.replacingOccurrences(of: " ", with: "/") }.joined(separator: ", ")
            throw Refusal(message: "a link can't run `\(command.name)`. Links only show things: \(list)")
        }

        var positional = command.params.filter(\.positional)
        for word in words.dropFirst(command.name.split(separator: " ").count) {
            guard let param = positional.first else { throw Refusal(message: "unexpected \"\(word)\" in the link") }
            if param.multiple {
                args[param.name] = args[param.name].map { $0 + "," + word } ?? word
            } else {
                args[param.name] = word
                positional.removeFirst()
            }
        }
        for key in args.keys.sorted() {
            guard let param = command.params.first(where: { $0.name == key }) else {
                throw Refusal(message: "\(command.name) has no option \(key). Run `innervoice \(command.name) --help`")
            }
            if param.isPath, let value = args[key], !value.hasPrefix("/") {
                throw Refusal(message: "\(key) in a link must be a full path, not \"\(value)\"")
            }
        }
        if let missing = command.params.first(where: { $0.required && args[$0.name] == nil }) {
            throw Refusal(message: "the link is missing \(missing.name)")
        }
        return Request(command: command.name, args: args)
    }

    private static func unescape(_ text: Substring) -> String {
        let spaced = text.replacingOccurrences(of: "+", with: " ")
        return spaced.removingPercentEncoding ?? spaced
    }
}
