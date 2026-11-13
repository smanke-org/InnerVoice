// Ported from Sources/InnerVoice/Agent/AgentMCP.swift. A small MCP server over stdin and
// stdout (JSON-RPC, one message per line). Every command becomes a tool ("lines assign" →
// "lines_assign"), and each call goes to the running app, like the CLI. Register it once:
//   claude mcp add --scope user innervoice -- innervoice mcp
// Optional: Claude Code drives the CLI fine without it. Use it for Claude Desktop and other
// clients that have no shell.

using System.Collections;
using System.Globalization;

namespace InnerVoice.Agent;

public static class AgentMcp
{
    public const string Instructions = "Tools that drive InnerVoice, the audiobook narrator's app on this PC: see the open projects, chapters, lines and cast, assign lines to characters, change a line's kind, rename, recolor and merge characters, run Tag Speakers and Build Summaries with the on-device model, set pronunciations, choose the model, and export narrator scripts, the tagged manuscript, the cast list or the pronunciation guide. Changes show in the project window at once and Edit › Undo works on them. Most tools take project (a name or path; default the frontmost window). Line ids come from lines_list. Destructive tools only report what they would change unless yes is true (the CLI's --yes). Error code no_model means no model is ready: model_list, then model_select.";

    /// `send` takes one request line and returns the app's answer (AgentPipe.Send).
    public static void Serve(TextReader input, TextWriter output, Func<string, string> send)
    {
        while (input.ReadLine() is { } line)
        {
            if (Json.Parse(line) is not JObject message) continue;
            // Notifications have no id and get no answer.
            if (!message.TryGetValue("id", out var id)) continue;
            var method = message.TryGetValue("method", out var m) && m is string ms ? ms : "";
            var parameters = message.TryGetValue("params", out var p) && p is JObject po ? po : new JObject();
            var reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = id };
            switch (method)
            {
                case "initialize":
                    reply["result"] = new JObject
                    {
                        ["protocolVersion"] = parameters.TryGetValue("protocolVersion", out var v) && v is string vs ? vs : "2025-06-18",
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "innervoice", ["version"] = AgentCli.Version },
                        ["instructions"] = Instructions,
                    };
                    break;
                case "ping":
                    reply["result"] = new JObject();
                    break;
                case "tools/list":
                    reply["result"] = new JObject { ["tools"] = AgentCommands.All.Select(Tool).ToList() };
                    break;
                case "tools/call":
                    reply["result"] = Call(parameters, send);
                    break;
                default:
                    reply["error"] = new JObject { ["code"] = -32601, ["message"] = $"unknown method {method}" };
                    break;
            }
            output.WriteLine(Json.Write(reply));
            output.Flush();
        }
    }

    public static JObject Tool(AgentCommand command)
    {
        var schema = AgentCli.Schema(command);
        schema["name"] = command.Name.Replace(' ', '_');
        schema["annotations"] = new JObject { ["readOnlyHint"] = command.ReadOnly, ["destructiveHint"] = command.Destructive };
        schema.Remove("readOnly");
        schema.Remove("destructive");
        return schema;
    }

    /// One argument as the CLI would pass it: lists comma-separated, booleans as true/false.
    static string Value(object? any) => any switch
    {
        null => "",
        bool b => b ? "true" : "false",
        string s => s,
        JObject o => Json.Write(o),
        IList list => string.Join(",", list.Cast<object?>().Select(Value)),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => any.ToString() ?? "",
    };

    static JObject Call(JObject parameters, Func<string, string> send)
    {
        var name = (parameters.TryGetValue("name", out var n) && n is string ns ? ns : "").Replace('_', ' ');
        var arguments = (parameters.TryGetValue("arguments", out var a) && a is JObject ao ? ao : new JObject())
            .ToDictionary(kv => kv.Key, kv => (object?)Value(kv.Value));
        try
        {
            if (AgentCommands.Named(name) is null) throw AgentError.Usage($"unknown tool {name}");
            var reply = send(Json.Write(new JObject { ["command"] = name, ["args"] = arguments }));
            var o = Json.Parse(reply) as JObject ?? new JObject();
            bool ok = o.TryGetValue("ok", out var okv) && okv is true;
            var body = ok ? (o.TryGetValue("result", out var r) ? r : new JObject()) : (o.TryGetValue("error", out var e) ? e : new JObject());
            return Content(Json.Write(body ?? new JObject()), !ok);
        }
        catch (AgentError e) { return Content(Json.Write(e.Json), true); }
        catch (Exception e) { return Content(e.Message, true); }
    }

    static JObject Content(string text, bool error) => new()
    {
        ["content"] = new List<object?> { new JObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = error,
    };
}
