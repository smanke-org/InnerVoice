using JObject = System.Collections.Generic.Dictionary<string, object?>;
using System.Runtime.CompilerServices;
using InnerVoice.Agent;

namespace InnerVoice.Core.Tests;

/// The agent CLI's own parts, without the app: help that matches the Mac's word for word
/// (windows/Fixtures/agent: `innervoice help`, `help <command>` and `help --json` from the
/// Mac build of the same commit), the parser, the output, the MCP server, the named pipe and
/// innervoice:// links. AgentAppTests (App.Tests) runs the commands in the app.
/// The pipe tests time how fast a second listener gives up; other test classes running at
/// the same time (audio rendering in the voices tests) slowed that past its limit, so these
/// run on their own.
[CollectionDefinition("Pipes", DisableParallelization = true)]
public class PipesCollection { }

[Collection("Pipes")]
public class AgentTests
{
    /// No test here can reach a real InnerVoice: a pipe nobody else uses, and not a test copy
    /// (that would add `projects demo` to the help).
    [ModuleInitializer]
    internal static void Isolate()
    {
        Environment.SetEnvironmentVariable("APP_STORE", null);
        Environment.SetEnvironmentVariable("APP_AGENT_PIPE", $"InnerVoice.coretest.{Guid.NewGuid():N}"[..36]);
    }

    static string Windows(string mac) =>
        AgentCommands.WindowsWording.Aggregate(mac, (s, w) => s.Replace(w.Mac, w.Windows).Replace(Json.Write(w.Mac)[1..^1], Json.Write(w.Windows)[1..^1]));

    /// A Mac help fixture, with Unix line ends even where Git checked it out with Windows ones.
    static string MacFixture(string name) => File.ReadAllText(Fixtures.PathOf("agent/" + name)).Replace("\r\n", "\n");

    static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        int exit = AgentCli.Run(args, stdout, stderr, TextReader.Null);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void HelpMatchesTheMac()
    {
        var mac = MacFixture("mac-help.txt").TrimEnd('\n');
        Assert.Equal(Windows(mac), AgentCli.HelpText());
        var (exit, output, _) = Cli("help");
        Assert.Equal(0, exit);
        Assert.Equal(Windows(mac) + "\n", output);
        // Every Windows wording change is used: none is left over from an old Mac text.
        foreach (var (m, _) in AgentCommands.WindowsWording)
            Assert.Contains(m, MacFixture("mac-help.json").Replace("\\\"", "\""));
    }

    [Fact]
    public void EveryCommandsUsageMatchesTheMac()
    {
        var blocks = MacFixture("mac-usage.txt").Split("=== ", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(blocks.Length, AgentCommands.All.Count);
        foreach (var block in blocks)
        {
            var newline = block.IndexOf('\n');
            var name = block[..newline];
            var command = AgentCommands.Named(name);
            Assert.True(command is not null, $"no command {name}");
            Assert.Equal(Windows(block[(newline + 1)..].TrimEnd('\n')), AgentCli.Usage(command!));
            var (exit, output, _) = Cli(["help", .. name.Split(' ')]);
            Assert.Equal(0, exit);
            Assert.Equal(AgentCli.Usage(command!) + "\n", output);
        }
    }

    [Fact]
    public void SchemaMatchesTheMac()
    {
        var mac = Json.Parse(Windows(MacFixture("mac-help.json")));
        var (exit, output, _) = Cli("help", "--json");
        Assert.Equal(0, exit);
        Assert.Equal(Json.Write(mac), output.TrimEnd('\n'));
        Assert.Equal(Json.Write(mac), Json.Write(new JObject { ["commands"] = AgentCommands.All.Select(AgentCli.Schema).ToList() }));
    }

    [Fact]
    public void ErrorsHaveCodesAndExitCodes()
    {
        var (exit, output, err) = Cli("no-such-command", "--json");
        Assert.Equal(2, exit);
        Assert.Equal("{\"error\":{\"code\":\"usage\",\"message\":\"unknown command \\\"no-such-command\\\". Run `innervoice help`\"},\"ok\":false}\n", output);
        Assert.Equal("", err);

        (exit, output, err) = Cli("no-such-command");
        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal("innervoice: unknown command \"no-such-command\". Run `innervoice help`\n", err);

        (exit, output, _) = Cli("lines", "assign", "--json");
        Assert.Equal(2, exit);
        Assert.Contains("missing <ids>. Example: innervoice lines assign 3f2a9c1e 7b0d44e2 --to Mara --json", output);

        // Nothing listens on this test's pipe: app not running, exit 4.
        (exit, output, _) = Cli("status", "--no-launch", "--json");
        Assert.Equal(4, exit);
        Assert.Contains("\"code\":\"app_unavailable\"", output);
        Assert.Contains("InnerVoice is not running. Open it, or leave out --no-launch", output);

        (exit, output, _) = Cli("--version");
        Assert.Equal(0, exit);
        Assert.Matches(@"^innervoice \d+\.\d+\.\d+\n$", output);
        Assert.Equal(AgentCli.Version, Json.Parse(Cli("version", "--json").Out) is JObject v ? v["version"] : null);

        Assert.Equal(2, AgentError.ExitFor("usage"));
        Assert.Equal(3, AgentError.ExitFor("not_found"));
        Assert.Equal(3, AgentError.ExitFor("no_project"));
        Assert.Equal(4, AgentError.ExitFor("app_unavailable"));
        Assert.Equal(1, AgentError.ExitFor("not_available"));
        Assert.Equal(1, AgentError.ExitFor("busy"));
    }

    [Fact]
    public void ParsesLikeTheMac()
    {
        var assign = AgentCommands.Named("lines assign")!;
        var values = AgentCli.Parse(["3f2a9c1e", "7b0d44e2", "--to", "Mara", "--new"], assign);
        Assert.Equal("3f2a9c1e,7b0d44e2", values["ids"]);
        Assert.Equal("Mara", values["to"]);
        Assert.Equal("true", values["new"]);

        // A boolean takes a value only when it is one.
        values = AgentCli.Parse(["3f2a9c1e", "--clear", "false", "--yes"], assign);
        Assert.Equal("false", values["clear"]);
        Assert.Equal("true", values["yes"]);
        values = AgentCli.Parse(["3f2a9c1e", "--new", "--to", "Mara"], assign);
        Assert.Equal("true", values["new"]);

        Assert.Equal("characters show has no option --yes. Run `innervoice characters show --help`",
            Assert.Throws<AgentError>(() => AgentCli.Parse(["Mara", "--yes"], AgentCommands.Named("characters show")!)).Message);
        var e = Assert.Throws<AgentError>(() => AgentCli.Parse(["3f2a9c1e", "--to"], assign));
        Assert.Equal("--to needs a value", e.Message);
        Assert.Equal("usage", e.Code);
        Assert.Equal("unexpected \"extra\". Run `innervoice characters show --help`",
            Assert.Throws<AgentError>(() => AgentCli.Parse(["Mara", "extra"], AgentCommands.Named("characters show")!)).Message);
        Assert.Equal("missing --into. Example: innervoice characters merge Mara --into \"Mara Quinn\" --yes --json",
            Assert.Throws<AgentError>(() => AgentCli.Parse(["Mara"], AgentCommands.Named("characters merge")!)).Message);

        // Paths become absolute against the shell's folder, ~ is the home folder.
        values = AgentCli.Parse(["--manuscript", "book.docx"], AgentCommands.Named("projects new")!);
        Assert.Equal(Path.GetFullPath("book.docx"), values["manuscript"]);
        values = AgentCli.Parse([Path.Combine("~", "Harbor.innervoice")], AgentCommands.Named("projects open")!);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Harbor.innervoice"), values["path"]);

        // The longest name wins: "characters merge", not a "characters" command.
        Assert.Equal("characters merge", AgentCli.Find(["characters", "merge", "Mara"])?.Name);
        Assert.Null(AgentCli.Find(["characters"]));
    }

    [Fact]
    public void ArgsReadValuesLikeTheMac()
    {
        var args = new Args(new() { ["n"] = "12", ["bad"] = "twelve", ["on"] = "yes", ["off"] = "0", ["list"] = " a, b ,,c ", ["yes"] = "true" });
        Assert.Equal(12, args.Int("n", 1));
        Assert.Equal(1, args.Int("missing", 1));
        Assert.Equal("--bad must be a whole number, not \"twelve\"", Assert.Throws<AgentError>(() => args.Int("bad", 1)).Message);
        Assert.True(args.Bool("on"));
        Assert.False(args.Bool("off"));
        Assert.Null(args.Bool("missing"));
        Assert.Equal(["a", "b", "c"], args.List("list"));
        Assert.True(args.Confirmed);
        Assert.Equal("missing --gone", Assert.Throws<AgentError>(() => args.Required("gone")).Message);
    }

    [Fact]
    public void HumanOutputLikeTheMac()
    {
        var result = new JObject
        {
            ["project"] = "Harbor", ["total"] = 2, ["nextOffset"] = null,
            ["lines"] = new List<object?>
            {
                new JObject { ["text"] = "Hello.", ["id"] = "3f2a9c1e", ["speaker"] = "Mara", ["needsYou"] = false, ["confidence"] = 0.95, ["ref"] = "Ch 1 ¶2" },
                new JObject { ["id"] = "7b0d44e2", ["speaker"] = null, ["needsYou"] = true, ["aliases"] = new List<object?> { "Tom", "T" } },
            },
            ["characters"] = new List<object?>(),
        };
        Assert.Equal(string.Join("\n",
            "nextOffset: none  project: Harbor  total: 2",
            "characters: none",
            "lines:",
            "  id: 3f2a9c1e  ref: Ch 1 ¶2  speaker: Mara  confidence: 0.95  needsYou: no  text: Hello.",
            "  id: 7b0d44e2  speaker: none  aliases: Tom, T  needsYou: yes"), AgentCli.Human(result));
        Assert.Equal("changed: yes  into: {id: 1234abcd, name: Mara}", AgentCli.Human(new JObject { ["changed"] = true, ["into"] = new JObject { ["name"] = "Mara", ["id"] = "1234abcd" } }));
        Assert.Equal("a; b", AgentCli.Text(new List<object?> { new JObject { ["x"] = "a" }, new JObject { ["x"] = "b" } }).Replace("{x: ", "").Replace("}", ""));
    }

    [Fact]
    public void JsonLikeTheMac()
    {
        Assert.Equal("{\"a\":1,\"b\":[true,null,0.5,1],\"c\":\"a/b \\\"q\\\" é\\n\\u0001\"}",
            Json.Write(new JObject { ["c"] = "a/b \"q\" é\n\u0001", ["b"] = new List<object?> { true, null, 0.5, 1.0 }, ["a"] = 1 }));
        Assert.Equal(0.12, Json.Hundredths(0.1234));
        Assert.Equal(0.95, Json.Hundredths(0.949999));
        Assert.Equal("2001-01-01T00:00:00Z", Json.Date(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var parsed = Json.Parse("{\"n\":3,\"f\":0.5,\"l\":[\"x\"],\"o\":{\"k\":false}}") as JObject;
        Assert.Equal(3L, (long)parsed!["n"]!);
        Assert.Equal(0.5, (double)parsed["f"]!);
        Assert.Null(Json.Parse("not json"));
    }

    [Fact]
    public void McpListsEveryCommandAndCallsTheApp()
    {
        var sent = new List<string>();
        string Send(string request)
        {
            sent.Add(request);
            return "{\"ok\":true,\"result\":{\"changed\":true}}";
        }
        var input = new StringReader(string.Join("\n",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"check\",\"version\":\"1\"}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"three\",\"method\":\"tools/call\",\"params\":{\"name\":\"lines_assign\",\"arguments\":{\"ids\":[\"3f2a9c1e\",\"7b0d44e2\"],\"to\":\"Mara\",\"new\":true}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"no_such_tool\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"nope\"}"));
        var output = new StringWriter { NewLine = "\n" };
        AgentMcp.Serve(input, output, Send);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JObject)Json.Parse(l)!).ToList();
        Assert.Equal(5, replies.Count);

        var init = (JObject)replies[0]["result"]!;
        Assert.Equal("2025-06-18", init["protocolVersion"]);
        Assert.NotNull(((JObject)init["capabilities"]!)["tools"]);
        Assert.Equal("innervoice", ((JObject)init["serverInfo"]!)["name"]);

        var tools = ((List<object?>)((JObject)replies[1]["result"]!)["tools"]!).Cast<JObject>().ToList();
        Assert.Equal(AgentCommands.All.Count, tools.Count);
        foreach (var tool in tools)
        {
            Assert.Matches("^[A-Za-z0-9_-]{1,64}$", (string)tool["name"]!);
            Assert.True(((JObject)tool["annotations"]!).ContainsKey("readOnlyHint"));
            Assert.False(tool.ContainsKey("readOnly"));
        }

        Assert.Equal("three", replies[2]["id"]);
        Assert.Equal("{\"args\":{\"ids\":\"3f2a9c1e,7b0d44e2\",\"new\":\"true\",\"to\":\"Mara\"},\"command\":\"lines assign\"}", Assert.Single(sent));
        var call = (JObject)replies[2]["result"]!;
        Assert.Equal(false, call["isError"]);
        Assert.Equal("{\"changed\":true}", ((JObject)((List<object?>)call["content"]!)[0]!)["text"]);

        Assert.Equal(true, ((JObject)replies[3]["result"]!)["isError"]);
        Assert.Equal(-32601L, (long)((JObject)replies[4]["error"]!)["code"]!);
    }

    [Fact]
    public async Task PipeCarriesOneLineEachWay()
    {
        var name = $"InnerVoice.pipetest.{Guid.NewGuid():N}"[..36];
        using var stop = new CancellationTokenSource();
        var heard = new List<string>();
        var listening = AgentPipe.Listen(request =>
        {
            lock (heard) heard.Add(request);
            return Task.FromResult(Json.Parse(request) is JObject o && o.ContainsKey("activate")
                ? "{\"ok\":true,\"result\":{}}" : "{\"ok\":true,\"result\":{\"echo\":" + request + "}}");
        }, stop.Token, name);

        string? reply = null;
        for (int i = 0; i < 50 && reply is null; i++)
        {
            reply = await Task.Run(() => AgentPipe.Exchange("{\"command\":\"status\",\"args\":{\"é\":\"ünïcode\"}}", connectMs: 100, name: name));
            if (reply is null) await Task.Delay(50);
        }
        Assert.Equal("{\"ok\":true,\"result\":{\"echo\":{\"command\":\"status\",\"args\":{\"é\":\"ünïcode\"}}}}", reply);

        // Another InnerVoice can't serve the same pipe: it stops listening at once. Only Windows
        // pipes refuse a second first instance reliably; .NET's Unix sockets on the Mac don't.
        if (OperatingSystem.IsWindows())
        {
            var second = AgentPipe.Listen(_ => Task.FromResult("{\"ok\":true,\"result\":\"second\"}"), stop.Token, name);
            Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))));
        }

        // A second copy of the app hands over its files and links.
        Assert.True(await Task.Run(() => AgentPipe.Forward(["innervoice://open/cast"], name)));
        lock (heard) Assert.Contains("{\"activate\":[\"innervoice://open/cast\"]}", heard);

        stop.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
        // Nobody listening: no answer, and nothing to hand over to.
        Assert.Null(await Task.Run(() => AgentPipe.Exchange("{}", connectMs: 100, name: name)));
        Assert.False(await Task.Run(() => AgentPipe.Forward([], name)));
    }

    [Fact]
    public void TestCopiesGetTheirOwnPipe()
    {
        var before = (Environment.GetEnvironmentVariable("APP_STORE"), Environment.GetEnvironmentVariable("APP_AGENT_PIPE"));
        try
        {
            Environment.SetEnvironmentVariable("APP_AGENT_PIPE", null);
            Environment.SetEnvironmentVariable("APP_STORE", null);
            Assert.False(AgentPipe.IsTestRun);
            Assert.StartsWith("InnerVoice.Agent.", AgentPipe.Name);
            var real = AgentPipe.Name;

            Environment.SetEnvironmentVariable("APP_STORE", Path.Combine(Path.GetTempPath(), "iv-cli-a"));
            Assert.True(AgentPipe.IsTestRun);
            var a = AgentPipe.Name;
            Assert.StartsWith("InnerVoice.test.", a);
            Assert.NotEqual(real, a);
            Assert.Contains(AgentCommands.All, c => c.Name == "projects demo");
            Assert.Equal("projects demo", AgentCommands.All[AgentCommands.All.ToList().FindIndex(c => c.Name == "projects save") + 1].Name);

            Environment.SetEnvironmentVariable("APP_STORE", Path.Combine(Path.GetTempPath(), "iv-cli-b"));
            Assert.NotEqual(a, AgentPipe.Name);

            Environment.SetEnvironmentVariable("APP_AGENT_PIPE", "custom");
            Assert.Equal("custom", AgentPipe.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_STORE", before.Item1);
            Environment.SetEnvironmentVariable("APP_AGENT_PIPE", before.Item2);
        }
    }

    [Fact]
    public void LinksOnlyShowThings()
    {
        var r = AgentUrl.Parse("innervoice://");
        Assert.Null(r.Command);
        Assert.Empty(r.Args);

        r = AgentUrl.Parse("innervoice://open/cast?character=Mara%20Quinn&project=Harbor+Book");
        Assert.Equal("open", r.Command);
        Assert.Equal(new Dictionary<string, string> { ["page"] = "cast", ["character"] = "Mara Quinn", ["project"] = "Harbor Book" }, r.Args);

        r = AgentUrl.Parse("InnerVoice://Lines/Reveal/3f2a9c1e/");
        Assert.Equal("lines reveal", r.Command);
        Assert.Equal("3f2a9c1e", r.Args["id"]);

        var full = Path.Combine(Path.GetTempPath(), "Harbor.innervoice");
        r = AgentUrl.Parse("innervoice://projects/open?path=" + Uri.EscapeDataString(full));
        Assert.Equal(full, r.Args["path"]);

        Assert.Contains("a link can't run `lines assign`", Assert.Throws<AgentError>(() => AgentUrl.Parse("innervoice://lines/assign/3f2a9c1e?to=Mara")).Message);
        Assert.Contains("a link can't run `model download`", Assert.Throws<AgentError>(() => AgentUrl.Parse("innervoice://model/download/gemma-4-e4b?yes=true")).Message);
        Assert.Contains("a link can't run `export`", Assert.Throws<AgentError>(() => AgentUrl.Parse("innervoice://export?kind=cast")).Message);
        Assert.Contains("must be a full path", Assert.Throws<AgentError>(() => AgentUrl.Parse("innervoice://projects/open?path=Harbor.innervoice")).Message);
        Assert.Contains("has no option yes", Assert.Throws<AgentError>(() => AgentUrl.Parse("innervoice://open/cast?yes=true")).Message);
        Assert.Contains("missing id", Assert.Throws<AgentError>(() => AgentUrl.Parse("innervoice://lines/reveal")).Message);
        Assert.Contains("unknown command", Assert.Throws<AgentError>(() => AgentUrl.Parse("innervoice://nothing")).Message);
        // Every command a link may run exists, and only shows things.
        foreach (var name in AgentUrl.Allowed) Assert.False(AgentCommands.Named(name)!.Destructive);
    }
}
