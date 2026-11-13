// Ported from Sources/InnerVoice/Agent/AgentCore.swift. The command list (AgentCommands.cs)
// is read by the CLI, its help and the MCP server; the app runs each command
// (InnerVoice.App/Agent), on the UI thread, so the windows update live and Undo works.

namespace InnerVoice.Agent;

public enum ParamKind
{
    String,
    Integer,
    Boolean,
    /// A file or folder. The CLI makes it absolute against the shell's folder, since the
    /// app's own working folder is elsewhere.
    Path,
}

public static class ParamKinds
{
    public static string Raw(this ParamKind k) => k.ToString().ToLowerInvariant();
    public static string JsonType(this ParamKind k) => k == ParamKind.Path ? "string" : k.Raw();
}

/// `Positional`: given without its --name, right after the command: `characters show Mara`.
/// `Multiple`: a positional param that takes every remaining word: `lines assign 3f2a 9c1e`;
/// the values reach the command comma-separated (MCP clients send them that way too).
public sealed record Param(string Name, ParamKind Kind = ParamKind.String, bool Required = false, bool Positional = false,
                           bool Multiple = false, string Help = "");

/// One thing an agent can do. Names are "<noun> <verb>": "lines assign".
public sealed record AgentCommand(string Name, string Summary, string Example)
{
    public Param[] Params { get; init; } = [];
    /// Only reads. Agents may run it without asking.
    public bool ReadOnly { get; init; }
    /// Removes data or cannot be undone. Without --yes it only reports what it would do.
    public bool Destructive { get; init; }
    /// What --yes does, when only part of the command needs it.
    public string? YesHelp { get; init; }
}

/// The values a command was called with.
public sealed class Args(Dictionary<string, string> values)
{
    public Dictionary<string, string> Values { get; } = values;

    public string? String(string name) => Values.TryGetValue(name, out var v) ? v : null;

    public bool Has(string name) => Values.ContainsKey(name);

    public string Required(string name) =>
        Values.TryGetValue(name, out var v) && v.Length > 0 ? v : throw AgentError.Usage($"missing --{name}");

    public int Int(string name, int fallback)
    {
        if (!Values.TryGetValue(name, out var raw)) return fallback;
        return int.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw AgentError.Usage($"--{name} must be a whole number, not \"{raw}\"");
    }

    public bool? Bool(string name)
    {
        if (!Values.TryGetValue(name, out var raw)) return null;
        return raw.ToLowerInvariant() switch
        {
            "" or "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw AgentError.Usage($"--{name} must be true or false, not \"{raw}\""),
        };
    }

    /// A comma-separated list (a `Multiple` param): `3f2a,9c1e`.
    public List<string> List(string name) =>
        (String(name) ?? "").Split(',').Select(s => s.Trim(' ', '\t')).Where(s => s.Length > 0).ToList();

    public bool Confirmed
    {
        get
        {
            try { return Bool("yes") == true; }
            catch (AgentError) { return false; }
        }
    }
}

/// An error with a stable code an agent can branch on, a message that says how to fix it,
/// and the exit code the CLI returns.
public sealed class AgentError(string code, string message, int exit) : Exception(message)
{
    public string Code { get; } = code;
    public int Exit { get; } = exit;

    public static AgentError Usage(string message) => new("usage", message, 2);
    public static AgentError NotFound(string message) => new("not_found", message, 3);
    public static AgentError Invalid(string message) => new("invalid", message, 1);
    public static AgentError Unavailable(string message) => new("app_unavailable", message, 4);
    /// Tag Speakers or Build Summaries is already running on that project.
    public static AgentError Busy(string message) => new("busy", message, 1);
    /// No model is downloaded and chosen, so the AI can't run.
    public static AgentError NoModel(string message) => new("no_model", message, 1);
    /// Windows only: a Mac feature the Windows app doesn't have yet (AI voices).
    public static AgentError NotYet(string message) => new("not_available", message, 1);
    /// No project window is open.
    public static AgentError NoProject => new("no_project", "No project is open. Open one with `innervoice projects open <path>`, "
                                              + "or start one with `innervoice projects new --manuscript <file>`", 3);

    /// The CLI's exit code for an error code that came back from the app.
    public static int ExitFor(string code) => code switch
    {
        "usage" => 2,
        "not_found" or "no_project" => 3,
        "app_unavailable" => 4,
        _ => 1,
    };

    public JObject Json => new() { ["ok"] = false, ["error"] = new JObject { ["code"] = Code, ["message"] = Message } };
}
