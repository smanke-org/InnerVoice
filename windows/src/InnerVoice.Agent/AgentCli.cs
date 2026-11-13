// Ported from Sources/InnerVoice/Agent/AgentCLI.swift.
//   innervoice help                                   every command, with examples
//   innervoice lines assign 3f2a9c1e --to Mara        run one command
//   innervoice lines list --needs-you --json          one JSON object on stdout
//   innervoice mcp                                    an MCP server on stdin and stdout
//   innervoice                                        open InnerVoice
//   innervoice <file or innervoice:// link>           open it in InnerVoice
// Exit codes: 0 ok, 1 error, 2 usage, 3 not found, 4 app not running.

using System.Collections;
using System.Globalization;

namespace InnerVoice.Agent;

public static class AgentCli
{
    public static string Version => typeof(AgentCli).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    public static int Run(string[] arguments, TextWriter stdout, TextWriter stderr, TextReader stdin)
    {
        var words = arguments.ToList();
        bool json = words.Contains("--json");
        words.RemoveAll(w => w == "--json");
        try
        {
            var first = words.FirstOrDefault();
            switch (first)
            {
                case null:
                    return Activate([], stdout, json);
                case "help" or "--help" or "-h":
                    return Help(words.Skip(1).ToList(), json, stdout);
                case "--version" or "version":
                    stdout.WriteLine(json ? Json.Write(new JObject { ["version"] = Version }) : $"innervoice {Version}");
                    return 0;
                case "mcp":
                    AgentMcp.Serve(stdin, stdout, request => AgentPipe.Send(request, launch: true));
                    return 0;
            }
            // A project, a manuscript or an innervoice:// link opens in the app, as the Mac's
            // binary does with a path from Finder.
            if (words.Count == 1 && (first.StartsWith(AgentUrl.Scheme + ":", StringComparison.OrdinalIgnoreCase)
                                     || (first.IndexOfAny(['/', '\\', '.']) >= 0 && File.Exists(first))))
            {
                return Activate([first.Contains("://") ? first : Absolute(first)], stdout, json);
            }
            return Command(words, json, stdout);
        }
        catch (AgentError e)
        {
            Fail(e, json, stdout, stderr);
            return e.Exit;
        }
        catch (Exception e)
        {
            Fail(AgentError.Invalid(e.Message), json, stdout, stderr);
            return 1;
        }
    }

    /// The longest command name that matches the first words: "characters merge".
    public static AgentCommand? Find(IReadOnlyList<string> words) =>
        AgentCommands.All.Where(c => Starts(words, c.Name.Split(' '))).MaxBy(c => c.Name.Length);

    static bool Starts(IReadOnlyList<string> words, string[] name) =>
        words.Count >= name.Length && name.Select((w, i) => words[i] == w).All(x => x);

    static int Command(List<string> words, bool json, TextWriter stdout)
    {
        var command = Find(words)
            ?? throw AgentError.Usage($"unknown command \"{string.Join(" ", words.Take(2))}\". Run `innervoice help`");
        var rest = words.Skip(command.Name.Split(' ').Length).ToList();
        if (rest.Contains("--help") || rest.Contains("-h"))
        {
            stdout.WriteLine(Usage(command));
            return 0;
        }
        bool launch = !rest.Contains("--no-launch");
        rest.RemoveAll(w => w == "--no-launch");
        var values = Parse(rest, command);
        var request = Json.Write(new JObject { ["command"] = command.Name, ["args"] = values.ToDictionary(kv => kv.Key, kv => (object?)kv.Value) });
        var reply = AgentPipe.Send(request, launch);
        if (Json.Parse(reply) is not JObject o) throw AgentError.Invalid("the app sent no answer");
        if (o.TryGetValue("ok", out var ok) && ok is true)
        {
            var result = o.TryGetValue("result", out var r) ? r : new JObject();
            stdout.WriteLine(json ? Json.Write(result) : Human(result));
            return 0;
        }
        var error = o.TryGetValue("error", out var e) && e is JObject eo ? eo : new JObject();
        var code = error.TryGetValue("code", out var c) && c is string cs ? cs : "error";
        throw new AgentError(code, error.TryGetValue("message", out var m) && m is string ms ? ms : "unknown error", AgentError.ExitFor(code));
    }

    /// Opens InnerVoice (starting it when needed), with a file or link to open.
    static int Activate(List<string> items, TextWriter stdout, bool json)
    {
        var reply = AgentPipe.Send(Json.Write(new JObject { ["activate"] = items }), launch: true);
        if (Json.Parse(reply) is JObject o && o.TryGetValue("ok", out var ok) && ok is true)
        {
            if (json) stdout.WriteLine(Json.Write(o.TryGetValue("result", out var r) ? r : new JObject()));
            return 0;
        }
        var error = Json.Parse(reply) is JObject eo && eo.TryGetValue("error", out var e) && e is JObject err ? err : new JObject();
        var code = error.TryGetValue("code", out var c) && c is string cs ? cs : "error";
        throw new AgentError(code, error.TryGetValue("message", out var m) && m is string ms ? ms : "unknown error", AgentError.ExitFor(code));
    }

    /// --name value, --flag, and positional values, checked against the params.
    public static Dictionary<string, string> Parse(IReadOnlyList<string> words, AgentCommand command)
    {
        var values = new Dictionary<string, string>();
        var positional = command.Params.Where(p => p.Positional).ToList();
        int index = 0;
        while (index < words.Count)
        {
            var word = words[index];
            if (word.StartsWith("--"))
            {
                var name = word[2..];
                if (name == "yes" && command.Destructive) { values["yes"] = "true"; index++; continue; }
                var param = command.Params.FirstOrDefault(p => p.Name == name)
                    ?? throw AgentError.Usage($"{command.Name} has no option --{name}. Run `innervoice {command.Name} --help`");
                var next = index + 1 < words.Count ? words[index + 1] : null;
                if (param.Kind == ParamKind.Boolean && (next is null || next.StartsWith("--") || !new[] { "true", "false", "yes", "no" }.Contains(next.ToLowerInvariant())))
                {
                    values[name] = "true";
                    index++;
                }
                else
                {
                    if (next is null) throw AgentError.Usage($"--{name} needs a value");
                    values[name] = param.Kind == ParamKind.Path ? Absolute(next) : next;
                    index += 2;
                }
            }
            else
            {
                var param = positional.FirstOrDefault()
                    ?? throw AgentError.Usage($"unexpected \"{word}\". Run `innervoice {command.Name} --help`");
                var value = param.Kind == ParamKind.Path ? Absolute(word) : word;
                if (param.Multiple)
                {
                    // Takes every positional word that follows; keep the param for the next one.
                    values[param.Name] = values.TryGetValue(param.Name, out var before) ? before + "," + value : value;
                }
                else
                {
                    values[param.Name] = value;
                    positional.RemoveAt(0);
                }
                index++;
            }
        }
        foreach (var param in command.Params.Where(p => p.Required && !values.ContainsKey(p.Name)))
            throw AgentError.Usage($"missing {(param.Positional ? $"<{param.Name}>" : $"--{param.Name}")}. Example: {command.Example}");
        return values;
    }

    /// A path as the app needs it: ~ expanded, and relative to the shell's folder.
    public static string Absolute(string path)
    {
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, Environment.CurrentDirectory));
    }

    // MARK: Help

    static int Help(List<string> words, bool json, TextWriter stdout)
    {
        if (json)
        {
            stdout.WriteLine(Json.Write(new JObject { ["commands"] = AgentCommands.All.Select(Schema).ToList() }));
            return 0;
        }
        var name = string.Join(" ", words);
        if (AgentCommands.All.FirstOrDefault(c => c.Name == name) is { } command)
        {
            stdout.WriteLine(Usage(command));
            return 0;
        }
        stdout.WriteLine(HelpText());
        return 0;
    }

    public static string HelpText()
    {
        var lines = new List<string>
        {
            "innervoice: drive InnerVoice from the shell. Every command takes --json,",
            "and --project <name or path> where it works on one project (default: the frontmost).", "",
        };
        int width = AgentCommands.All.Max(c => c.Name.Length) + 2;
        foreach (var command in AgentCommands.All)
        {
            // The first sentence: up to ". " (not the dot in ".innervoice" or "2.5 GB").
            int dot = command.Summary.IndexOf(". ", StringComparison.Ordinal);
            var first = dot >= 0 ? command.Summary[..dot] + "." : command.Summary;
            lines.Add("  " + Pad(command.Name, width) + first);
        }
        lines.AddRange(["", "More: innervoice help <command>, innervoice help --json (every command as a schema).",
                        "MCP server for Claude Desktop and other clients: innervoice mcp.",
                        "Exit codes: 0 ok, 1 error, 2 usage, 3 not found, 4 app not running."]);
        return string.Join("\n", lines);
    }

    /// Swift's `padding(toLength:withPad:startingAt:)`: pads, or cuts a longer string.
    static string Pad(string s, int length) => s.Length >= length ? s[..length] : s.PadRight(length);

    public static string Usage(AgentCommand command)
    {
        var synopsis = "  innervoice " + command.Name + string.Concat(command.Params.Select(p =>
        {
            var shown = p.Positional ? $"<{p.Name}>{(p.Multiple ? "..." : "")}"
                : p.Kind == ParamKind.Boolean ? $"--{p.Name}" : $"--{p.Name} <{p.Kind.Raw()}>";
            return " " + (p.Required ? shown : $"[{shown}]");
        })) + (command.Destructive ? " [--yes]" : "");
        var lines = new List<string> { command.Summary, "", synopsis };
        if (command.Params.Length > 0) lines.Add("");
        foreach (var p in command.Params)
            lines.Add("  " + Pad(p.Positional ? p.Name : "--" + p.Name, 14) + p.Help);
        if (command.Destructive)
            lines.Add("  --yes         " + (command.YesHelp ?? "do it. Without --yes it only shows what it would change"));
        lines.AddRange(["", $"Example: {command.Example}"]);
        return string.Join("\n", lines);
    }

    public static JObject Schema(AgentCommand command)
    {
        var properties = new JObject();
        foreach (var p in command.Params)
        {
            var help = p.Multiple ? p.Help + " (several: comma-separated)" : p.Help;
            properties[p.Name] = new JObject { ["type"] = p.Kind.JsonType(), ["description"] = help };
        }
        if (command.Destructive)
        {
            properties["yes"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = command.YesHelp is { } y ? $"Like the CLI's --yes: {y}." : "Do it. Without yes it only shows what it would change.",
            };
        }
        return new JObject
        {
            ["name"] = command.Name, ["description"] = command.Summary + " Example: " + command.Example,
            ["readOnly"] = command.ReadOnly, ["destructive"] = command.Destructive,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = command.Params.Where(p => p.Required).Select(p => p.Name).ToList(),
            },
        };
    }

    // MARK: Output

    /// Short plain lines for people; agents use --json. A result holding lists prints its
    /// other fields on one line first, then each list under its name, one line per item.
    public static string Human(object? result)
    {
        if (result is not JObject o) return Text(result);
        var lists = o.Keys.OrderBy(k => k, StringComparer.Ordinal).Where(k => IsObjectList(o[k])).ToList();
        if (lists.Count == 0) return Line(o);
        var rest = o.Where(kv => !lists.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var lines = rest.Count == 0 ? new List<string>() : [Line(rest)];
        foreach (var key in lists)
        {
            var items = ((IList)o[key]!).Cast<JObject>().ToList();
            lines.Add(items.Count == 0 ? $"{key}: none" : $"{key}:");
            lines.AddRange(items.Select(i => "  " + Line(i)));
        }
        return string.Join("\n", lines);
    }

    /// A list of objects, or an empty list (the Mac's `is [JSONObject]`).
    static bool IsObjectList(object? value) => value is IList list && list.Cast<object?>().All(x => x is JObject);

    static readonly string[] FirstKeys = ["id", "ref", "number", "name", "term", "title", "speaker", "kind"];

    static string Line(JObject o)
    {
        static int Rank(string key) => Array.IndexOf(FirstKeys, key) is var i && i >= 0 ? i : key == "text" ? 99 : 50;
        return string.Join("  ", o.Keys.OrderBy(Rank).ThenBy(k => k, StringComparer.Ordinal).Select(k => $"{k}: {Text(o[k])}"));
    }

    /// One value on one line: nested objects as {key: value, …}, lists comma-separated.
    public static string Text(object? value) => value switch
    {
        bool b => b ? "yes" : "no",
        null => "none",
        string s => s,
        JObject o => "{" + string.Join(", ", o.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}: {Text(o[k])}")) + "}",
        IList list when list.Count == 0 => "none",
        IList list => string.Join(list[0] is JObject ? "; " : ", ", list.Cast<object?>().Select(Text)),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    static void Fail(AgentError error, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (json) stdout.WriteLine(Json.Write(error.Json));
        else stderr.WriteLine($"innervoice: {error.Message}");
    }
}
