using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using InnerVoice.Agent;
using InnerVoice.App.Agent;
using InnerVoice.App.Document;
using InnerVoice.App.Pages;
using InnerVoice.App.Speech;
using InnerVoice.Core;
using Xunit;
using JObject = System.Collections.Generic.Dictionary<string, object?>;

namespace InnerVoice.App.Tests;

/// The agent CLI end to end: `innervoice …` (AgentCli, the same code as the innervoice.exe)
/// over the named pipe to the app, on the sample book, in a test copy (APP_STORE, set by
/// TestApp). Also the agent-CLI check (the Mac's check-agent-cli.sh, tools/check-agent-cli.ps1
/// on Windows) and innervoice:// links.
public class AgentAppTests
{
    /// Runs the CLI on another thread while this (UI) thread answers it, as the app does.
    static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var run = Task.Run(() => AgentCli.Run(args, stdout, stderr, TextReader.Null));
        Pump(run);
        return (run.Result, stdout.ToString(), stderr.ToString());
    }

    static void Pump(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Assert.True(task.IsCompleted, "the CLI got no answer within 30 seconds");
    }

    /// `innervoice <args> --json`, which must exit `exit`; its JSON.
    static JObject Run(int exit, params string[] args)
    {
        var (code, output, err) = Cli([.. args, "--json"]);
        Assert.True(code == exit, $"innervoice {string.Join(" ", args)}: exit {code}, expected {exit}\n{output}{err}");
        Assert.Equal("", err);
        var json = Json.Parse(output);
        Assert.True(json is JObject, $"innervoice {string.Join(" ", args)} printed {output}");
        return (JObject)json!;
    }

    static JObject Ok(params string[] args) => Run(0, args);

    static string ErrorCode(int exit, params string[] args) => (string)((JObject)Run(exit, args)["error"]!)["code"]!;

    static List<JObject> List(JObject o, string key) => ((List<object?>)o[key]!).Cast<JObject>().ToList();

    static void Listen()
    {
        Assert.True(AgentPipe.IsTestRun);
        Assert.StartsWith("InnerVoice.test.", AgentPipe.Name);
        AgentServer.Start();
    }

    static void CloseProjects()
    {
        foreach (var w in App.Current.Projects.ToList()) w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void EveryCommandHasAHandler()
    {
        Assert.Equal(AgentCommands.All.Select(c => c.Name).Order(), AgentHandlers.All.Keys.Order());
    }

    [AvaloniaFact]
    public void DrivesTheSampleBook()
    {
        Listen();
        try
        {
            CloseProjects();
            var status = Ok("status");
            Assert.Equal(true, status["testRun"]);
            Assert.Equal((long)Environment.ProcessId, status["pid"]);
            Assert.Empty((List<object?>)status["projects"]!);
            Assert.Equal("no_project", ErrorCode(3, "lines", "list"));

            var demo = Ok("projects", "demo");
            Assert.Equal(true, demo["front"]);
            Assert.Equal(false, demo["saved"]);
            Assert.Equal("manuscript", demo["page"]);
            var project = (string)demo["name"]!;

            var chapters = Ok("chapters", "list");
            Assert.True((long)chapters["total"]! >= 2);
            Assert.Equal(1L, List(chapters, "chapters")[0]["number"]);

            // Lines, paged.
            var lines = Ok("lines", "list", "--limit", "3");
            Assert.Equal(3, List(lines, "lines").Count);
            Assert.Equal(3L, lines["nextOffset"]);
            var all = List(Ok("lines", "list", "--limit", "1000"), "lines");
            Assert.All(all, l => Assert.Matches("^Ch \\d+ ¶\\d+$", (string)l["ref"]!));
            Assert.All(all, l => Assert.Equal(8, ((string)l["id"]!).Length));
            var first = all[0];
            var id = (string)first["id"]!;

            // Assign, with a new character, as one undo step.
            var assigned = Ok("lines", "assign", id, "--to", "Quill Agent", "--new");
            Assert.Equal(true, assigned["createdCharacter"]);
            Assert.Equal("Quill Agent", List(assigned, "lines")[0]["speaker"]);
            Assert.Equal("user", List(assigned, "lines")[0]["source"]);
            Assert.False(string.IsNullOrEmpty(List(Ok("projects", "list"), "projects")[0]["undo"] as string), "Edit › Undo can take it back");
            Assert.Equal("not_found", ErrorCode(3, "lines", "assign", id, "--to", "Nobody At All"));
            Assert.Equal("usage", ErrorCode(2, "lines", "assign", id));

            // --clear only reports until --yes.
            var wouldClear = Ok("lines", "assign", id, "--clear");
            Assert.Equal(false, wouldClear["changed"]);
            Assert.Equal(1L, wouldClear["wouldClear"]);
            Assert.Equal("Quill Agent", List(Ok("lines", "list", "--speaker", "Quill"), "lines")[0]["speaker"]);
            var cleared = Ok("lines", "assign", id, "--clear", "--yes");
            Assert.Equal(true, cleared["changed"]);
            Assert.Null(List(cleared, "lines")[0]["speaker"]);
            Assert.Equal(true, List(cleared, "lines")[0]["needsYou"]);
            Assert.Contains(List(Ok("lines", "list", "--needs-you", "--limit", "1000"), "lines"), l => (string)l["id"]! == id);

            var kind = Ok("lines", "kind", id, "thought");
            Assert.Equal("thought", kind["kind"]);
            Assert.Equal(true, kind["changed"]);
            Assert.Equal(false, Ok("lines", "kind", id, "--kind", "thought")["changed"]);
            Assert.Contains("\"loud\" isn't a kind", ((JObject)Run(2, "lines", "kind", id, "loud")["error"]!)["message"] as string);

            // Tones: the narrator's (Tag Speakers never changes them), one undo step, shown in lines list.
            var toned = Ok("lines", "tone", id, "angry");
            Assert.Equal("angry", toned["tone"]);
            var tonedLine = List(toned, "lines")[0];
            Assert.Equal(id, tonedLine["id"]);
            Assert.Equal("angry", tonedLine["tone"]);
            Assert.Equal("user", tonedLine["toneSource"]);
            Assert.Equal(1.0, Convert.ToDouble(tonedLine["toneConfidence"]));
            Assert.Equal(false, tonedLine["toneNeedsYou"]);
            Assert.Equal("Set Tone", List(Ok("projects", "list"), "projects")[0]["undo"]);
            var model = App.Current.FrontProject!.Document.Project.Lines.First(l => l.Id.ToString("N").StartsWith(id, StringComparison.Ordinal));
            Assert.Equal(Core.Tone.Angry, model.Tone);
            Assert.Equal(Core.ToneSource.User, model.ToneSource);
            Assert.Equal("sad", List(Ok("lines", "tone", id, "--tone", "Sad"), "lines")[0]["tone"]);
            var listed = List(Ok("lines", "list", "--limit", "1000"), "lines").First(l => (string)l["id"]! == id);
            Assert.Equal("sad", listed["tone"]);
            Assert.Equal("user", listed["toneSource"]);
            Assert.Contains("Give a tone: neutral, excited, confused, sad, angry, whispered, scared, tender, sarcastic.",
                ((JObject)Run(2, "lines", "tone", id, "loud")["error"]!)["message"] as string);
            Assert.Equal("usage", ErrorCode(2, "lines", "tone", id));
            Assert.Equal("not_found", ErrorCode(3, "lines", "tone", "ffffffff", "angry"));

            var reveal = Ok("lines", "reveal", id);
            Assert.Equal(id, ((JObject)reveal["line"]!)["id"]);
            Assert.Equal(PageKind.Manuscript, App.Current.FrontProject!.Workspace.Page);

            // The cast.
            var cast = List(Ok("characters", "list"), "characters");
            var top = cast[0];
            var name = (string)top["name"]!;
            Assert.True((long)top["lines"]! >= (long)cast[^1]["lines"]!);
            var shown = Ok("characters", "show", name);
            Assert.Equal(name, shown["name"]);
            Assert.True(shown.ContainsKey("facts") && shown.ContainsKey("voiceCue"));
            Assert.Equal("not_found", ErrorCode(3, "characters", "show", "Zzyzx"));

            // Each character's voice on the engine the book speaks with now.
            var doc = App.Current.FrontProject!.Document;
            var engine = doc.Project.EngineInUse();
            var topCharacter = doc.Project.Characters.First(c => c.Name == name);
            doc.SetVoice(new Core.VoiceChoice(engine == Core.VoiceEngine.Mixed ? Core.VoiceEngine.Apple : engine, "agent-test-voice"), topCharacter.Id);
            var voice = (JObject)Ok("characters", "show", name)["voice"]!;
            Assert.Equal((engine == Core.VoiceEngine.Mixed ? Core.VoiceEngine.Apple : engine).Raw(), voice["engine"]);
            Assert.Equal(Core.Speech.VoiceNames.Name(topCharacter.Voice(engine)!.Choice), voice["name"]);
            Assert.Equal("user", voice["source"]);
            Assert.Equal(1.0, Convert.ToDouble(voice["confidence"]));
            Assert.Equal(false, voice["needsYou"]);
            Assert.Equal(voice["source"], ((JObject)List(Ok("characters", "list"), "characters").First(c => (string)c["name"]! == name)["voice"]!)["source"]);

            var renamed = Ok("characters", "rename", "Quill Agent", "--to", "Quill Tester");
            Assert.Equal("Quill Agent", renamed["was"]);
            Assert.Equal("invalid", ErrorCode(1, "characters", "rename", "Quill Tester", "--to", name));

            Assert.Equal("#3B82F6", Ok("characters", "color", "Quill Tester", "Blue")["color"]);
            Assert.Equal("#0EA5E9", Ok("characters", "color", "Quill Tester", "#0ea5e9")["color"]);
            Assert.Equal("invalid", ErrorCode(1, "characters", "color", "Quill Tester", "#FF0000"));
            Assert.Equal("usage", ErrorCode(2, "characters", "color", "Quill Tester", "reddish"));
            Assert.Equal(true, Ok("characters", "color", "Quill Tester", "auto")["automatic"]);

            Assert.Equal("female", Ok("characters", "gender", "Quill Tester", "female")["gender"]);
            Assert.Equal(true, Ok("characters", "main", "Quill Tester", "yes")["main"]);
            Assert.Equal("auto", Ok("characters", "main", "Quill Tester", "auto")["main"]);
            Assert.Equal("narrator", Ok("characters", "read-by", "Quill Tester", "narrator")["role"]);
            var shade = (JObject)Ok("characters", "shade", "Quill Tester", "--pitch", "-2", "--pace", "1", "--words", "gravelly")["shade"]!;
            Assert.Equal(-2L, shade["pitch"]);
            Assert.Equal("gravelly", shade["words"]);
            Assert.Equal("auto", Ok("characters", "shade", "Quill Tester", "--auto")["shade"]);

            var style = Ok("casting", "style", "shared");
            Assert.Equal("shared", style["castingStyle"]);
            Assert.NotEmpty(List(style, "readers"));
            Assert.Equal("usage", ErrorCode(2, "casting", "style", "choir"));
            // One Voice: the narrator shades everyone. A book with voices takes the shades from
            // the voices (the narrator's voice), a narration book from the narrator's gender.
            void ShadesMatch(JObject result, Dictionary<Guid, Core.Shade> shades)
            {
                var readers = List(result, "readers");
                Assert.Contains(readers, r => r["shade"] is string);
                Assert.All(readers, r => Assert.Equal(shades.GetValueOrDefault(doc.Project.Characters.First(c => c.Name == (string)r["name"]!).Id)?.Cue, r["shade"]));
            }
            ShadesMatch(Ok("casting", "style", "one-voice"), VoiceSetup.Resolver(doc).Shades);
            doc.Change("Project Type", p => { p.Settings.Purpose = Core.ProjectPurpose.Narration; p.Settings.NarratorGender = Core.CharacterGender.Male; });
            ShadesMatch(Ok("casting", "style"), doc.Shades());
            doc.Change("Project Type", p => { p.Settings.Purpose = null; p.Settings.NarratorGender = null; });
            Ok("casting", "style", "shared");

            // Merge reports until --yes.
            Ok("lines", "assign", id, "--to", "Quill Tester");
            var preview = Ok("characters", "merge", "Quill Tester", "--into", name);
            Assert.Equal(false, preview["changed"]);
            Assert.Equal(1L, preview["linesToMove"]);
            Assert.NotNull(App.Current.FrontProject!.Document.Project.Characters.FirstOrDefault(x => x.Name == "Quill Tester"));
            var merged = Ok("characters", "merge", "Quill Tester", "--into", name, "--yes");
            Assert.Equal(true, merged["changed"]);
            Assert.Contains("Quill Tester", (List<object?>)((JObject)merged["into"]!)["aliases"]!);
            Assert.Null(App.Current.FrontProject!.Document.Project.Characters.FirstOrDefault(x => x.Name == "Quill Tester"));

            // Pronunciations.
            var term = Ok("pronunciations", "set", "Kerensa", "--say-it-as", "keh-REN-sah");
            Assert.Equal(true, term["added"]);
            var again = Ok("pronunciations", "set", "kerensa", "--confirmed");
            Assert.Equal(false, again["added"]);
            Assert.Equal(true, ((JObject)again["pronunciation"]!)["confirmed"]);
            Assert.Equal(false, ((JObject)again["pronunciation"]!)["matchCase"]);
            var cased = Ok("pronunciations", "set", "Kerensa", "--match-case");
            Assert.Equal(true, cased["changed"]);
            Assert.Equal(true, ((JObject)cased["pronunciation"]!)["matchCase"]);
            Assert.Contains(List(Ok("pronunciations", "list", "--search", "keren"), "pronunciations"), p => (string)p["term"]! == "Kerensa");
            Assert.Equal("usage", ErrorCode(2, "pronunciations", "set", "Kerensa"));

            // Pages.
            Assert.Equal("cast", Ok("open", "cast")["page"]);
            Assert.Equal(PageKind.Cast, App.Current.FrontProject!.Workspace.Page);
            Assert.Equal("record", Ok("open", "teleprompter")["page"]);
            Assert.Equal(name, Ok("open", "--character", name)["character"]);
            Assert.Equal("voices", Ok("open", "voices")["page"]);
            Assert.Equal(PageKind.Voices, App.Current.FrontProject!.Workspace.Page);
            Assert.Equal("pronunciations", Ok("open", "pronunciations")["page"]);
            Assert.Equal(PageKind.Pronunciations, App.Current.FrontProject!.Workspace.Page);
            doc.Change("Project Type", p => p.Settings.Purpose = Core.ProjectPurpose.Narration);
            Assert.Equal("manuscript", Ok("open", "voices")["page"]);
            Assert.Equal("manuscript", Ok("open", "pronunciations")["page"]);
            Assert.Equal(PageKind.Manuscript, App.Current.FrontProject!.Workspace.Page);
            Assert.Equal("record", Ok("open", "teleprompter")["page"]);
            doc.Change("Project Type", p => p.Settings.Purpose = Core.ProjectPurpose.AiVoices);
            Assert.Equal("manuscript", Ok("open", "teleprompter")["page"]);
            Assert.Equal("manuscript", Ok("open", "index")["page"]);
            Assert.Equal("voices", Ok("open", "voices")["page"]);
            doc.Change("Project Type", p => p.Settings.Purpose = null);
            Assert.Equal("usage", ErrorCode(2, "open", "attic"));
            Assert.Equal("usage", ErrorCode(2, "open", "retakes"));

            // Export, into the temporary folder.
            var dir = Path.Combine(Path.GetTempPath(), $"iv-agent-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var csv = Ok("export", "--kind", "cast", "--format", "csv", "--out", Path.Combine(dir, "Cast"));
                Assert.Equal(Path.Combine(dir, "Cast.csv"), csv["path"]);
                Assert.True((long)csv["bytes"]! > 0);
                Assert.Equal("invalid", ErrorCode(1, "export", "--kind", "cast", "--format", "csv", "--out", Path.Combine(dir, "Cast.csv")));
                Ok("export", "--kind", "cast", "--format", "csv", "--out", Path.Combine(dir, "Cast.csv"), "--replace");
                Assert.Equal("usage", ErrorCode(2, "export", "--kind", "manuscript", "--format", "csv", "--out", Path.Combine(dir, "M")));
                // The pronunciation guide: terms the narrator dismissed are left out.
                var guide = Ok("export", "--kind", "pronunciations", "--format", "csv", "--out", Path.Combine(dir, "Guide"));
                Assert.Equal(Path.Combine(dir, "Guide.csv"), guide["path"]);
                Assert.Equal("pronunciations", guide["kind"]);
                var guideText = File.ReadAllText(Path.Combine(dir, "Guide.csv"));
                Assert.StartsWith("Term,Say It As,Notes,Confirmed,Count\n", guideText);
                Assert.Contains("\"Kerensa\",\"keh-REN-sah\",\"\",yes,", guideText);
                Assert.True((long)Ok("export", "--kind", "pronunciations", "--out", Path.Combine(dir, "Guide"))["bytes"]! > 0);
                Assert.True(new FileInfo(Path.Combine(dir, "Guide.pdf")).Length > 0);
                Assert.Equal("usage", ErrorCode(2, "export", "--kind", "glossary", "--out", Path.Combine(dir, "G")));
                Assert.True(File.Exists(Path.Combine(dir, "Narrator Script.pdf")) is false);
                Ok("export", "--kind", "narrator-script", "--out", Path.Combine(dir, "Narrator Script"));
                Assert.True(new FileInfo(Path.Combine(dir, "Narrator Script.pdf")).Length > 0);

                // Save to the temporary folder; a test copy refuses anywhere else.
                var file = Path.Combine(dir, "Agent Sample.innervoice");
                var saved = Ok("projects", "save", "--as", Path.Combine(dir, "Agent Sample"));
                Assert.Equal(file, saved["path"]);
                Assert.Equal(true, saved["saved"]);
                Assert.True(File.Exists(file));
                Assert.Equal("Agent Sample", saved["name"]);
                Assert.Equal(false, Ok("projects", "open", file)["opened"]);
                Assert.Equal("Agent Sample", Ok("projects", "list")["projects"] is List<object?> ps ? ((JObject)ps[0]!)["name"] : null);
                Ok("projects", "save", "--project", "Agent Sample");
                var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"iv-agent-test-{Guid.NewGuid():N}.innervoice");
                Assert.Equal("invalid", ErrorCode(1, "projects", "save", "--as", home));
                Assert.False(File.Exists(home));
                var real = FixturePath("sample-mac.innervoice");
                Assert.Equal("invalid", ErrorCode(1, "projects", "open", real));
                Assert.Equal("usage", ErrorCode(2, "projects", "open", Path.Combine(dir, "Cast.csv")));
                Assert.Equal("not_found", ErrorCode(3, "projects", "open", Path.Combine(dir, "Missing.innervoice")));

                // A second project, from a manuscript; --project picks a window.
                var manuscript = Path.Combine(dir, "Short Story.md");
                File.WriteAllText(manuscript, "# Short Story\n\n## One\n\n\"Hello,\" said Ann.\n\n\"Hi,\" said Ben.\n");
                var made = Ok("projects", "new", "--manuscript", manuscript);
                Assert.Equal(false, made["saved"]);
                Assert.Equal(2, List(Ok("projects", "list"), "projects").Count);
                Assert.Equal(made["name"], List(Ok("projects", "list"), "projects")[0]["name"]);
                Assert.Equal("Agent Sample", Ok("chapters", "list", "--project", "Agent Sample")["project"]);
                Assert.Equal("not_found", ErrorCode(3, "chapters", "list", "--project", "Nothing Open"));
                Assert.Equal("usage", ErrorCode(2, "projects", "new", "--manuscript", Path.Combine(dir, "Cast.csv")));
            }
            finally { Directory.Delete(dir, true); }

            // The model: none is ready in a test copy.
            var models = Ok("model", "list");
            Assert.NotEmpty(List(models, "models"));
            Assert.Equal(false, ((JObject)models["apple"]!)["available"]);
            Assert.Equal("not_found", ErrorCode(3, "model", "select", "no-such-model"));
            Assert.Equal("invalid", ErrorCode(1, "model", "select", "apple"));
            Assert.Equal("no_model", ErrorCode(1, "tag", "start", "--project", project));
            Assert.Equal("no_model", ErrorCode(1, "summaries", "start", "--project", project));
            Assert.Equal(false, Ok("tag", "stop", "--project", project)["stopped"]);
            Assert.Equal(false, Ok("tag", "status", "--project", project)["running"]);
            var download = Ok("model", "download", List(models, "models")[0]["id"] as string ?? "");
            Assert.Equal(false, download["changed"]);
            Assert.Contains("Add --yes", download["hint"] as string);

            // People read plain lines.
            var (exit, human, _) = Cli("characters", "list", "--project", project);
            Assert.Equal(0, exit);
            Assert.Contains("characters:\n  id: ", human);
        }
        finally
        {
            AgentServer.Stop();
            CloseProjects();
        }
    }

    /// The Mac's check-agent-cli.sh, item by item (tools/check-agent-cli.ps1 runs it against a
    /// real innervoice.exe on Windows).
    [AvaloniaFact]
    public void PassesTheAgentCliCheck()
    {
        Listen();
        try
        {
            CloseProjects();
            App.Current.OpenSample();
            var (exit, output, _) = Cli("help");
            Assert.True(exit == 0 && output.Length > 0, "help exits 0 and prints the commands");

            var schema = (JObject)Json.Parse(Cli("help", "--json").Out)!;
            var commands = List(schema, "commands");
            Assert.NotEmpty(commands);
            foreach (var c in commands)
            {
                Assert.Contains("Example:", (string)c["description"]!);
                Assert.Equal("object", ((JObject)c["inputSchema"]!)["type"]);
            }

            Assert.Equal(2, Cli("no-such-command").Exit);
            var err = (JObject)((JObject)Json.Parse(Cli("no-such-command", "--json").Out)!)["error"]!;
            Assert.False(string.IsNullOrEmpty(err["code"] as string) || string.IsNullOrEmpty(err["message"] as string));

            // Every read-only command without required params runs and prints JSON.
            foreach (var c in commands.Where(c => c["readOnly"] is true && ((List<object?>)((JObject)c["inputSchema"]!)["required"]!).Count == 0))
                Ok(((string)c["name"]!).Split(' '));

            // MCP: initialize and tools/list, one tool per command; a call reaches the app.
            var input = new StringReader(string.Join("\n",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"check\",\"version\":\"1\"}}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"chapters_list\",\"arguments\":{}}}"));
            var mcpOut = new StringWriter { NewLine = "\n" };
            var mcp = Task.Run(() => AgentCli.Run(["mcp"], mcpOut, TextWriter.Null, input));
            Pump(mcp);
            var replies = mcpOut.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JObject)Json.Parse(l)!).ToList();
            Assert.Equal(3, replies.Count);
            Assert.Equal(commands.Count, ((List<object?>)((JObject)replies[1]["result"]!)["tools"]!).Count);
            var call = (JObject)replies[2]["result"]!;
            Assert.Equal(false, call["isError"]);
            Assert.Contains("\"chapters\":", (string)((JObject)((List<object?>)call["content"]!)[0]!)["text"]!);
        }
        finally
        {
            AgentServer.Stop();
            CloseProjects();
        }
    }

    /// innervoice:// links and a second copy of the app reach the running one through the pipe.
    [AvaloniaFact]
    public void FollowsLinks()
    {
        Listen();
        try
        {
            CloseProjects();
            var w = App.Current.OpenSample();
            var mara = w.Document.Project.Characters.OrderByDescending(c => w.Document.Index.StatsFor(c.Id)?.Lines ?? 0).First();

            var forwarded = Task.Run(() => AgentPipe.Forward(["innervoice://open/review"]));
            Pump(forwarded);
            Assert.True(forwarded.Result);
            Assert.Equal(PageKind.Review, w.Workspace.Page);

            var link = $"innervoice://open/cast?character={Uri.EscapeDataString(mara.Name)}";
            var reply = Task.Run(() => AgentPipe.Exchange(Json.Write(new JObject { ["activate"] = new List<object?> { link } }), 1000));
            Pump(reply);
            var result = (JObject)((JObject)Json.Parse(reply.Result!)!)["result"]!;
            Assert.Equal(mara.Name, result["character"]);
            Assert.Equal(PageKind.Cast, w.Workspace.Page);
            Assert.Equal(mara.Id, w.Workspace.SelectedCharacter);

            var line = w.Document.Project.Lines[3];
            var reveal = AgentRouter.OpenLink($"innervoice://lines/reveal/{AgentHandlers.Short(line.Id)}");
            Pump(reveal);
            Assert.Equal(PageKind.Manuscript, w.Workspace.Page);

            // A link can't change the book.
            var undo = w.Document.UndoName;
            var refused = Task.Run(() => AgentPipe.Exchange(Json.Write(new JObject { ["activate"] = new List<object?> { $"innervoice://lines/assign/{AgentHandlers.Short(line.Id)}?to={Uri.EscapeDataString(mara.Name)}" } }), 1000));
            Pump(refused);
            Assert.Contains("a link can't run `lines assign`", refused.Result);
            Assert.Equal(undo, w.Document.UndoName);
        }
        finally
        {
            AgentServer.Stop();
            CloseProjects();
        }
    }

    static string FixturePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Fixtures", name);
    }
}
