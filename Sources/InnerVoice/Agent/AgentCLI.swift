import Foundation

/// The app binary is also its CLI. With a command after it, it runs that
/// command against the running app and exits; without one, it opens the app.
///   innervoice help                                   every command, with examples
///   innervoice lines assign 3f2a9c1e --to Mara        run one command
///   innervoice lines list --needs-you --json          one JSON object on stdout
///   innervoice mcp                                    an MCP server on stdin and stdout
/// Exit codes: 0 ok, 1 error, 2 usage, 3 not found, 4 app not running.
enum AgentCLI {
    /// nil when the app should open normally.
    static func runIfRequested() -> Int32? {
        var words = Array(CommandLine.arguments.dropFirst())
        // macOS and Xcode pass flags like -NSDocumentRevisionsDebugMode, and the self-test is
        // --selftest. Not ours.
        guard let first = words.first, !first.hasPrefix("-") || ["--help", "-h", "--version"].contains(first) else { return nil }
        // Finder and `open` hand a document over as a path: that opens the app too.
        guard !first.hasPrefix("/") else { return nil }
        let json = words.contains("--json")
        words.removeAll { $0 == "--json" }
        do {
            switch first {
            case "help", "--help", "-h":
                return help(Array(words.dropFirst()), json: json)
            case "--version", "version":
                print(json ? JSON.string(["version": version]) : "innervoice \(version)")
                return 0
            case "mcp":
                AgentMCP.serve()
                return 0
            default:
                return try run(words, json: json)
            }
        } catch let error as AgentError {
            fail(error, json: json)
            return error.exit
        } catch {
            fail(.invalid(error.localizedDescription), json: json)
            return 1
        }
    }

    /// Through the ~/.local/bin link, Bundle.main cannot find Info.plist. Follow the link.
    static var version: String {
        let plist = AgentBinary.url.deletingLastPathComponent().deletingLastPathComponent().appending(path: "Info.plist")
        return NSDictionary(contentsOf: plist)?["CFBundleShortVersionString"] as? String ?? "dev"
    }

    private static func run(_ words: [String], json: Bool) throws -> Int32 {
        // The longest command name that matches the first words: "characters merge".
        guard let command = AgentCommands.all
            .filter({ words.starts(with: $0.name.split(separator: " ").map(String.init)) })
            .max(by: { $0.name.count < $1.name.count }) else {
            throw AgentError.usage("unknown command \"\(words.prefix(2).joined(separator: " "))\". Run `innervoice help`")
        }
        var rest = Array(words.dropFirst(command.name.split(separator: " ").count))
        if rest.contains("--help") || rest.contains("-h") {
            print(usage(command))
            return 0
        }
        let launch = !rest.contains("--no-launch")
        rest.removeAll { $0 == "--no-launch" }
        let values = try parse(rest, for: command)
        let request = JSON.data(["command": command.name, "args": values])
        let reply = try AgentSocket.send(request, launch: launch)
        guard let object = try? JSONSerialization.jsonObject(with: reply) as? JSONObject else {
            throw AgentError.invalid("the app sent no answer")
        }
        if object["ok"] as? Bool == true {
            let result = object["result"] ?? [:]
            print(json ? JSON.string(result) : human(result))
            return 0
        }
        let error = object["error"] as? JSONObject ?? [:]
        let code = error["code"] as? String ?? "error"
        throw AgentError(code: code, message: error["message"] as? String ?? "unknown error",
                         exit: ["usage": 2, "not_found": 3, "no_project": 3, "app_unavailable": 4][code] ?? 1)
    }

    /// --name value, --flag, and positional values, checked against the params.
    static func parse(_ words: [String], for command: AgentCommand) throws -> [String: String] {
        var values: [String: String] = [:]
        var positional = command.params.filter(\.positional)
        var index = 0
        while index < words.count {
            let word = words[index]
            if word.hasPrefix("--") {
                let name = String(word.dropFirst(2))
                if name == "yes", command.destructive { values["yes"] = "true"; index += 1; continue }
                guard let param = command.params.first(where: { $0.name == name }) else {
                    throw AgentError.usage("\(command.name) has no option --\(name). Run `innervoice \(command.name) --help`")
                }
                let next = index + 1 < words.count ? words[index + 1] : nil
                if param.kind == .boolean, next == nil || next!.hasPrefix("--") || !["true", "false", "yes", "no"].contains(next!.lowercased()) {
                    values[name] = "true"
                    index += 1
                } else {
                    guard let next else { throw AgentError.usage("--\(name) needs a value") }
                    values[name] = param.kind == .path ? absolute(next) : next
                    index += 2
                }
            } else {
                guard let param = positional.first else {
                    throw AgentError.usage("unexpected \"\(word)\". Run `innervoice \(command.name) --help`")
                }
                let value = param.kind == .path ? absolute(word) : word
                if param.multiple {
                    // Takes every positional word that follows; keep the param for the next one.
                    values[param.name] = [values[param.name], value].compactMap { $0 }.joined(separator: ",")
                } else {
                    values[param.name] = value
                    positional.removeFirst()
                }
                index += 1
            }
        }
        for param in command.params where param.required && values[param.name] == nil {
            throw AgentError.usage("missing \(param.positional ? "<\(param.name)>" : "--\(param.name)"). Example: \(command.example)")
        }
        return values
    }

    /// A path as the app needs it: ~ expanded, and relative to the shell's folder.
    private static func absolute(_ path: String) -> String {
        let expanded = (path as NSString).expandingTildeInPath
        return URL(fileURLWithPath: expanded, relativeTo: URL.currentDirectory()).standardizedFileURL.path
    }

    // MARK: Help

    private static func help(_ words: [String], json: Bool) -> Int32 {
        if json {
            print(JSON.string(["commands": AgentCommands.all.map(schema)]))
            return 0
        }
        let name = words.joined(separator: " ")
        if let command = AgentCommands.all.first(where: { $0.name == name }) {
            print(usage(command))
            return 0
        }
        var lines = ["innervoice: drive InnerVoice from the shell. Every command takes --json,",
                     "and --project <name or path> where it works on one project (default: the frontmost).", ""]
        let width = (AgentCommands.all.map(\.name.count).max() ?? 0) + 2
        for command in AgentCommands.all {
            // The first sentence: up to ". " (not the dot in ".innervoice" or "2.5 GB").
            let first = command.summary.range(of: ". ").map { String(command.summary[..<$0.lowerBound]) + "." } ?? command.summary
            lines.append("  " + command.name.padding(toLength: width, withPad: " ", startingAt: 0) + first)
        }
        lines += ["", "More: innervoice help <command>, innervoice help --json (every command as a schema).",
                  "MCP server for Claude Desktop and other clients: innervoice mcp.",
                  "Exit codes: 0 ok, 1 error, 2 usage, 3 not found, 4 app not running."]
        print(lines.joined(separator: "\n"))
        return 0
    }

    static func usage(_ command: AgentCommand) -> String {
        var lines = ["\(command.summary)", "", "  innervoice \(command.name)" + command.params.map { param in
            let shown = param.positional ? "<\(param.name)>\(param.multiple ? "..." : "")"
                : param.kind == .boolean ? "--\(param.name)" : "--\(param.name) <\(param.kind.rawValue)>"
            return " " + (param.required ? shown : "[\(shown)]")
        }.joined() + (command.destructive ? " [--yes]" : "")]
        if !command.params.isEmpty { lines.append("") }
        for param in command.params {
            lines.append("  " + (param.positional ? param.name : "--" + param.name).padding(toLength: 14, withPad: " ", startingAt: 0) + param.help)
        }
        if command.destructive {
            lines.append("  --yes         " + (command.yesHelp ?? "do it. Without --yes it only shows what it would change"))
        }
        lines += ["", "Example: \(command.example)"]
        return lines.joined(separator: "\n")
    }

    static func schema(_ command: AgentCommand) -> JSONObject {
        var properties: JSONObject = [:]
        for param in command.params {
            let help = param.multiple ? param.help + " (several: comma-separated)" : param.help
            properties[param.name] = ["type": param.kind.jsonType, "description": help]
        }
        if command.destructive {
            properties["yes"] = ["type": "boolean", "description": command.yesHelp.map { "Like the CLI's --yes: \($0)." }
                                 ?? "Do it. Without yes it only shows what it would change."]
        }
        return ["name": command.name, "description": command.summary + " Example: " + command.example,
                "readOnly": command.readOnly, "destructive": command.destructive,
                "inputSchema": ["type": "object", "properties": properties,
                                "required": command.params.filter(\.required).map(\.name)]]
    }

    // MARK: Output

    /// Short plain lines for people. Agents use --json. A result holding lists prints its
    /// other fields on one line first, then each list under its name, one line per item.
    private static func human(_ result: Any) -> String {
        guard let object = result as? JSONObject else { return text(result) }
        let lists = object.keys.sorted().filter { object[$0] is [JSONObject] }
        guard !lists.isEmpty else { return line(object) }
        var rest = object
        for key in lists { rest[key] = nil }
        var lines = rest.isEmpty ? [] : [line(rest)]
        for key in lists {
            let items = object[key] as? [JSONObject] ?? []
            lines.append(items.isEmpty ? "\(key): none" : "\(key):")
            lines += items.map { "  " + line($0) }
        }
        return lines.joined(separator: "\n")
    }

    private static func line(_ object: JSONObject) -> String {
        let first = ["id", "ref", "number", "name", "term", "title", "speaker", "kind"]
        let last = ["text"]
        func rank(_ key: String) -> Int { first.firstIndex(of: key) ?? (last.contains(key) ? 99 : 50) }
        return object.keys.sorted { a, b in (rank(a), a) < (rank(b), b) }
            .map { "\($0): \(text(object[$0] ?? NSNull()))" }
            .joined(separator: "  ")
    }

    /// One value on one line: nested objects as {key: value, …}, lists comma-separated.
    private static func text(_ value: Any) -> String {
        // JSON booleans arrive as NSNumber; show them as yes and no.
        if let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() { return number.boolValue ? "yes" : "no" }
        if value is NSNull { return "none" }
        if let list = value as? [Any] {
            guard !list.isEmpty else { return "none" }
            let parts = list.map(text)
            return list.first is JSONObject ? parts.joined(separator: "; ") : parts.joined(separator: ", ")
        }
        if let object = value as? JSONObject {
            return "{" + object.keys.sorted().map { "\($0): \(text(object[$0] ?? NSNull()))" }.joined(separator: ", ") + "}"
        }
        return "\(value)"
    }

    private static func fail(_ error: AgentError, json: Bool) {
        if json {
            print(JSON.string(error.json))
        } else {
            FileHandle.standardError.write(Data("innervoice: \(error.message)\n".utf8))
        }
    }
}
