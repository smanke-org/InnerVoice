import Foundation

/// A small MCP server over stdin and stdout (JSON-RPC, one message per line).
/// Every AgentCommand becomes a tool ("lines assign" -> "lines_assign"), and each
/// call goes to the running app, like the CLI. Register it once:
///   claude mcp add --scope user innervoice -- "/Applications/InnerVoice.app/Contents/MacOS/InnerVoice" mcp
/// Optional: Claude Code drives the CLI fine without it. Use it for Claude
/// Desktop and other clients that have no shell.
enum AgentMCP {
    static func serve() {
        while let line = readLine(strippingNewline: true) {
            guard let data = line.data(using: .utf8),
                  let message = try? JSONSerialization.jsonObject(with: data) as? JSONObject else { continue }
            // Notifications have no id and get no answer.
            guard let id = message["id"] else { continue }
            let method = message["method"] as? String ?? ""
            let params = message["params"] as? JSONObject ?? [:]
            var reply: JSONObject = ["jsonrpc": "2.0", "id": id]
            switch method {
            case "initialize":
                reply["result"] = [
                    "protocolVersion": params["protocolVersion"] as? String ?? "2025-06-18",
                    "capabilities": ["tools": [:]],
                    "serverInfo": ["name": "innervoice", "version": AgentCLI.version],
                    "instructions": "Tools that drive InnerVoice, the audiobook narrator's app on this Mac: see the open projects, chapters, lines and cast, assign lines to characters, change a line's kind, rename, recolor and merge characters, run Tag Speakers and Build Summaries with the on-device model, set pronunciations, choose the model, and export narrator scripts, the tagged manuscript, the cast list or the pronunciation guide. Changes show in the project window at once and Edit › Undo works on them. Most tools take project (a name or path; default the frontmost window). Line ids come from lines_list. Destructive tools only report what they would change unless yes is true (the CLI's --yes). Error code no_model means no model is ready: model_list, then model_select.",
                ]
            case "ping":
                reply["result"] = [:] as JSONObject
            case "tools/list":
                reply["result"] = ["tools": AgentCommands.all.map(tool)]
            case "tools/call":
                reply["result"] = call(params)
            default:
                reply["error"] = ["code": -32601, "message": "unknown method \(method)"]
            }
            print(JSON.string(reply))
            fflush(stdout)
        }
    }

    private static func tool(_ command: AgentCommand) -> JSONObject {
        var schema = AgentCLI.schema(command)
        schema["name"] = command.name.replacingOccurrences(of: " ", with: "_")
        schema["annotations"] = ["readOnlyHint": command.readOnly, "destructiveHint": command.destructive]
        schema["readOnly"] = nil
        schema["destructive"] = nil
        return schema
    }

    /// One argument as the CLI would pass it: lists comma-separated, booleans as true/false.
    private static func value(_ any: Any) -> String {
        if let list = any as? [Any] { return list.map(value).joined(separator: ",") }
        if let number = any as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() { return number.boolValue ? "true" : "false" }
        return "\(any)"
    }

    private static func call(_ params: JSONObject) -> JSONObject {
        let name = (params["name"] as? String ?? "").replacingOccurrences(of: "_", with: " ")
        let arguments = (params["arguments"] as? JSONObject ?? [:]).mapValues(value)
        do {
            guard AgentCommands.all.contains(where: { $0.name == name }) else { throw AgentError.usage("unknown tool \(name)") }
            let reply = try AgentSocket.send(JSON.data(["command": name, "args": arguments]), launch: true)
            let object = (try? JSONSerialization.jsonObject(with: reply) as? JSONObject) ?? [:]
            let ok = object["ok"] as? Bool == true
            return ["content": [["type": "text", "text": JSON.string(ok ? object["result"] ?? [:] : object["error"] ?? [:])]],
                    "isError": !ok]
        } catch let error as AgentError {
            return ["content": [["type": "text", "text": JSON.string(error.json)]], "isError": true]
        } catch {
            return ["content": [["type": "text", "text": error.localizedDescription]], "isError": true]
        }
    }
}
