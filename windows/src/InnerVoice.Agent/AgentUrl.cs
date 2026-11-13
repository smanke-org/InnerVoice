// innervoice:// links. The words after innervoice:// name a command, as on the command line,
// and the query gives its options; words after the command fill its positional params:
//   innervoice://                                     bring InnerVoice to the front
//   innervoice://open/cast?character=Mara             open Mara on the Cast page
//   innervoice://open/review?project=Harbor           the Review page of the open project Harbor
//   innervoice://lines/reveal/3f2a9c1e                show that line in the manuscript
//   innervoice://projects/open?path=C:\Books\Harbor.innervoice
// A link can be clicked anywhere (a web page, a document), so it only shows things: no
// command that edits, saves, exports, runs the AI or downloads works from a link.

namespace InnerVoice.Agent;

public sealed record AgentUrlRequest(string? Command, Dictionary<string, string> Args);

public static class AgentUrl
{
    public const string Scheme = "innervoice";

    /// The commands a link may run.
    public static readonly string[] Allowed = ["open", "lines reveal", "projects open"];

    public static bool IsLink(string text) => text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static AgentUrlRequest Parse(string url)
    {
        if (!IsLink(url)) throw AgentError.Usage($"\"{url}\" isn't an innervoice:// link");
        var rest = url[(Scheme.Length + 1)..].TrimStart('/');
        var fragment = rest.IndexOf('#');
        if (fragment >= 0) rest = rest[..fragment];
        var question = rest.IndexOf('?');
        var path = question >= 0 ? rest[..question] : rest;
        var query = question >= 0 ? rest[(question + 1)..] : "";

        var words = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToList();
        var args = new Dictionary<string, string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Unescape(eq >= 0 ? pair[..eq] : pair);
            if (key.Length > 0) args[key] = eq >= 0 ? Unescape(pair[(eq + 1)..]) : "true";
        }
        if (words.Count == 0)
        {
            if (args.Count > 0) throw AgentError.Usage("give a command after innervoice://, like innervoice://open/cast");
            return new AgentUrlRequest(null, args);
        }

        var lowered = words.Select(w => w.ToLowerInvariant()).ToList();
        var command = AgentCommands.All
            .Where(c => c.Name.Split(' ') is var name && lowered.Count >= name.Length && name.Select((w, i) => lowered[i] == w).All(x => x))
            .MaxBy(c => c.Name.Length)
            ?? throw AgentError.Usage($"unknown command \"{string.Join(" ", words.Take(2))}\" in the link");
        if (!Allowed.Contains(command.Name))
            throw AgentError.Usage($"a link can't run `{command.Name}`. Links only show things: {string.Join(", ", Allowed.Select(a => Scheme + "://" + a.Replace(' ', '/')))}");

        var positional = command.Params.Where(p => p.Positional).ToList();
        foreach (var word in words.Skip(command.Name.Split(' ').Length))
        {
            var param = positional.FirstOrDefault() ?? throw AgentError.Usage($"unexpected \"{word}\" in the link");
            if (param.Multiple) args[param.Name] = args.TryGetValue(param.Name, out var before) ? before + "," + word : word;
            else { args[param.Name] = word; positional.RemoveAt(0); }
        }
        foreach (var key in args.Keys)
        {
            var param = command.Params.FirstOrDefault(p => p.Name == key)
                ?? throw AgentError.Usage($"{command.Name} has no option {key}. Run `innervoice {command.Name} --help`");
            if (param.Kind == ParamKind.Path && !Path.IsPathFullyQualified(args[key]))
                throw AgentError.Usage($"{key} in a link must be a full path, not \"{args[key]}\"");
        }
        foreach (var param in command.Params.Where(p => p.Required && !args.ContainsKey(p.Name)))
            throw AgentError.Usage($"the link is missing {param.Name}");
        return new AgentUrlRequest(command.Name, args);
    }

    static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
}
