// What each agent command does in the app (the `run` closures of the Mac's
// Sources/InnerVoice/Agent/AgentCommands.swift). The command list itself, with its params and
// help, is InnerVoice.Agent/AgentCommands.cs. Each handler runs on the UI thread and calls
// the same code as the window's buttons and menus, through the document's `Change`, so the
// window updates at once and Edit › Undo works.

using System.Text.Json;
using InnerVoice.Agent;
using InnerVoice.App.AI;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.AI;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Agent;

public static partial class AgentHandlers
{
    public delegate Task<object?> Handler(Args args);

    /// Every command's handler, by name. AgentTests checks this matches AgentCommands.All.
    public static readonly IReadOnlyDictionary<string, Handler> All = Build();

    static Dictionary<string, Handler> Build()
    {
        var map = new Dictionary<string, Handler>();
        void Add(string name, Func<Args, object?> run) => map[name] = a => Task.FromResult(run(a));
        void AddAsync(string name, Func<Args, Task<object?>> run) => map[name] = a => run(a);
        AddApp(Add, AddAsync);
        AddLines(Add);
        AddCharacters(Add);
        AddAI(Add);
        AddOutput(Add);
        AddModels(Add);
        return map;
    }

    // MARK: Finding things

    public static string Short(Guid id) => id.ToString("D")[..8].ToLowerInvariant();

    /// The enum's raw value as the Mac and the file format write it: `fullCast`, `spoken`.
    static string Raw<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    static T? ParseRaw<T>(string raw) where T : struct, Enum =>
        Enum.GetValues<T>().Cast<T?>().FirstOrDefault(v => Raw(v!.Value).Equals(raw, StringComparison.OrdinalIgnoreCase));

    /// The open projects, the frontmost first.
    static List<ProjectWindow> Open()
    {
        var front = App.Current.FrontProject;
        var all = App.Current.Projects.ToList();
        if (front is not null && all.Remove(front)) all.Insert(0, front);
        return all;
    }

    static string Name(ProjectWindow w) => w.Document.DisplayName;

    /// The project named by --project, or the frontmost one.
    static (ProjectWindow Window, ProjectDocument Document) Project(Args args)
    {
        var open = Open();
        if (open.Count == 0) throw AgentError.NoProject;
        ProjectWindow entry;
        if (args.String("project") is { Length: > 0 } raw)
        {
            string? path = null;
            try { path = Path.GetFullPath(AgentCli.Absolute(raw)); } catch (Exception) { }
            // By path, then file name, then the book's title, then id.
            Func<ProjectWindow, bool>[] tests =
            [
                w => path is not null && w.Document.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), path, StringComparison.OrdinalIgnoreCase),
                w => string.Equals(Name(w), raw, StringComparison.OrdinalIgnoreCase),
                w => string.Equals(w.Document.Project.Title, raw, StringComparison.OrdinalIgnoreCase),
                w => raw.Length >= 4 && Short(w.AgentID).StartsWith(raw.ToLowerInvariant(), StringComparison.Ordinal),
            ];
            var matches = tests.Select(t => open.Where(t).ToList()).FirstOrDefault(m => m.Count > 0) ?? [];
            if (matches.Count == 0)
                throw AgentError.NotFound($"no open project \"{raw}\". Open: {string.Join(", ", open.Select(Name))}. Run `innervoice projects list`");
            if (matches.Count > 1)
                throw AgentError.Usage($"\"{raw}\" matches {matches.Count} open projects. Use the id from `innervoice projects list`");
            entry = matches[0];
        }
        else
        {
            entry = App.Current.FrontProject ?? open[0];
        }
        if (entry.Document.Project.IsEmpty)
            throw AgentError.Invalid($"{Name(entry)} has no manuscript yet. Start a project with `innervoice projects new --manuscript <file>`");
        return (entry, entry.Document);
    }

    /// A character by name, alias, id, or the unique start of a name.
    static BookCharacter Character(string raw, ProjectDocument document, string? hint = null)
    {
        var cast = document.Project.Characters;
        var key = raw.Trim();
        bool Same(string a) => string.Equals(a, key, StringComparison.OrdinalIgnoreCase);
        if (cast.FirstOrDefault(c => Same(c.Name)) is { } byName) return byName;
        var byAlias = cast.Where(c => c.Aliases.Any(Same)).ToList();
        if (byAlias.Count == 1) return byAlias[0];
        if (key.Length >= 4 && UniquePrefix(key, cast, c => c.Id) is { } byID) return byID;
        var started = cast.Where(c => c.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (started.Count == 1) return started[0];
        if (started.Count > 1 || byAlias.Count > 1)
        {
            var names = (started.Count == 0 ? byAlias : started).Select(c => c.Name);
            throw AgentError.Usage($"\"{raw}\" could be {string.Join(" or ", names)}. Give the full name");
        }
        throw AgentError.NotFound($"no character \"{raw}\"{(hint is null ? "" : $", {hint}")}. Run `innervoice characters list`");
    }

    /// The one item whose id starts with `raw` (8 characters is what lists show; 4 or more works).
    static T? UniquePrefix<T>(string raw, IEnumerable<T> items, Func<T, Guid> id) where T : class
    {
        var key = raw.ToLowerInvariant().Replace("-", "");
        if (key.Length < 4 || !key.All(Uri.IsHexDigit)) return null;
        var found = items.Where(i => id(i).ToString("N").StartsWith(key, StringComparison.Ordinal)).Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    static Line? LineByID(Guid id, ProjectDocument d) =>
        d.Index.LineIndexByID.TryGetValue(id, out var i) ? d.Project.Lines[i] : null;

    static List<Line> FindLines(IEnumerable<string> ids, ProjectDocument d)
    {
        var list = ids.ToList();
        if (list.Count == 0) throw AgentError.Usage("give at least one line id. Run `innervoice lines list` to find them");
        return list.Select(raw => UniquePrefix(raw, d.Project.Lines, l => l.Id)
            ?? throw AgentError.NotFound($"no line with id {raw}. Run `innervoice lines list`")).ToList();
    }

    /// A test copy (APP_STORE) only touches files in the temporary folder: never a real project.
    static void RefuseRealFilesInTestRun(string path)
    {
        if (!AgentPipe.IsTestRun) return;
        var full = Path.GetFullPath(path);
        try
        {
            // A link in the temporary folder to a real project is a real project.
            if (new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target) full = target.FullName;
        }
        catch (IOException) { }
        var temp = Path.GetTempPath();
        var temporary = new[] { "/tmp/", "/private/tmp/", "/var/folders/", "/private/var/folders/", temp };
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!temporary.Any(t => full.StartsWith(t, comparison)))
            throw AgentError.Invalid($"a test copy (APP_STORE) only opens projects in the temporary folder ({temp}), never real ones. Copy it there first");
    }

    // MARK: Results

    static JObject Page<T>(List<T> items, Args args, string key, JObject extra, Func<T, JObject> json)
    {
        int limit = Math.Max(1, args.Int("limit", 50));
        int offset = Math.Max(0, args.Int("offset", 0));
        var slice = items.Skip(offset).Take(limit).ToList();
        var result = new JObject(extra) { [key] = slice.Select(json).ToList<object?>(), ["total"] = items.Count };
        if (offset + slice.Count < items.Count) result["nextOffset"] = offset + slice.Count;
        return result;
    }

    static JObject Merged(JObject a, JObject b, bool keepFirst)
    {
        var result = new JObject(a);
        foreach (var (k, v) in b) if (!keepFirst || !result.ContainsKey(k)) result[k] = v;
        return result;
    }

    static string PageRaw(Pages.PageKind page) => page == Pages.PageKind.Teleprompter ? "record" : Raw(page);

    static JObject ProjectJSON(ProjectWindow w, ProjectWindow? front)
    {
        var d = w.Document;
        var p = d.Project;
        return new JObject
        {
            ["id"] = Short(w.AgentID), ["name"] = Name(w), ["title"] = p.Title, ["author"] = p.Author,
            ["path"] = d.FilePath, ["saved"] = d.FilePath is not null,
            ["edited"] = d.IsDirty, ["front"] = w == front,
            ["manuscript"] = p.SourceFileName, ["chapters"] = p.Chapters.Count, ["words"] = p.WordCount, ["lines"] = p.Lines.Count,
            ["linesNeedingYou"] = p.LinesNeedingYou, ["untagged"] = p.UntaggedCount, ["characters"] = p.Characters.Count,
            ["page"] = PageRaw(w.Workspace.Page), ["tagging"] = JobJSON(w),
            // What Edit › Undo in that window would take back, so an agent can tell the person.
            ["undo"] = d.UndoName,
        };
    }

    static JObject LineJSON(Line line, ProjectDocument d)
    {
        var p = d.Project;
        return new JObject
        {
            ["id"] = Short(line.Id), ["ref"] = d.Index.Reference(line.ParagraphID),
            ["speaker"] = p.Character(line.SpeakerID)?.Name, ["kind"] = Raw(line.Kind),
            ["source"] = Raw(line.Source), ["confidence"] = Json.Hundredths(line.Confidence),
            ["needsYou"] = p.NeedsYou(line), ["text"] = p.TextOf(line, d.Index),
            ["tone"] = line.Tone?.Raw(), ["toneSource"] = line.ToneSource is { } ts ? Raw(ts) : null,
            ["toneConfidence"] = line.ToneConfidence is { } c ? Json.Hundredths(c) : null,
            ["toneNeedsYou"] = p.ToneNeedsYou(line),
        };
    }

    static JObject CharacterJSON(BookCharacter c, ProjectDocument d)
    {
        var stats = d.Index.StatsFor(c.Id);
        return new JObject
        {
            ["id"] = Short(c.Id), ["name"] = c.Name, ["aliases"] = c.Aliases.ToList<object?>(), ["color"] = c.ColorHex,
            ["colorAutomatic"] = c.ColorIsAutomatic,
            ["lines"] = stats?.Lines ?? 0, ["words"] = stats?.Words ?? 0,
            ["firstLine"] = stats?.FirstLine is { } f ? $"Ch {f.Chapter + 1} ¶{f.Paragraph + 1}" : null,
            ["chapters"] = stats?.Chapters.Count ?? 0, ["facts"] = c.Facts.Count,
            ["narrator"] = d.EffectiveNarrator(c)?.Name,
            ["gender"] = c.Gender is { } g ? Raw(g) : "auto", ["main"] = c.IsMain is { } m ? m : "auto",
            ["showInIndex"] = d.Index.ShowsInIndex(c),
            ["voice"] = VoiceJSON(c, d.Project),
        };
    }

    /// The character's voice on the engine the book speaks with now, as on the Mac.
    static JObject? VoiceJSON(BookCharacter c, Project p)
    {
        var engine = p.EngineInUse();
        if (c.Voice(engine) is not { } v) return null;
        return new JObject
        {
            ["engine"] = v.Choice.Engine.Raw(), ["name"] = VoiceNames.Name(v.Choice), ["source"] = Raw(v.Source),
            ["confidence"] = Json.Hundredths(v.Confidence), ["needsYou"] = p.VoiceNeedsYou(c, engine),
        };
    }

    static JObject PronunciationJSON(Pronunciation p, ProjectDocument d) => new()
    {
        ["id"] = Short(p.Id), ["term"] = p.Term, ["sayItAs"] = p.SayItAs, ["notes"] = p.Notes, ["confirmed"] = p.Confirmed,
        ["occurrences"] = p.Occurrences, ["first"] = p.FirstParagraphID is { } id ? d.Index.Reference(id) : null,
        ["hasSample"] = p.AudioFile is not null, ["dismissed"] = p.Dismissed,
    };

    static object? JobJSON(ProjectWindow w)
    {
        if (w.ExistingRunner?.Current is not { } s) return null;
        return new JObject { ["job"] = JobName(s.Job), ["progress"] = Json.Hundredths(s.Progress), ["message"] = s.Message };
    }

    static string JobName(AIRunner.Job job) => job switch
    {
        AIRunner.Job.Tagging => "tag speakers",
        AIRunner.Job.Summaries => "build summaries",
        _ => "claude",
    };

    static string JobWords(ProjectWindow w) => w.ExistingRunner?.Current?.Job switch
    {
        AIRunner.Job.Summaries => "building summaries",
        AIRunner.Job.Claude => "running Claude",
        _ => "tagging speakers",
    };

    /// What the AI is busy with, in any project ("tagging speakers in Harbor"), or null.
    static string? BusyReason() =>
        App.Current.Projects.FirstOrDefault(w => w.ExistingRunner?.IsRunning == true) is { } busy ? $"{JobWords(busy)} in {Name(busy)}" : null;

    static JObject TaggingJSON(ProjectWindow w)
    {
        var p = w.Document.Project;
        var runner = w.ExistingRunner;
        return new JObject
        {
            ["project"] = Name(w), ["running"] = runner?.IsRunning == true, ["job"] = JobJSON(w),
            ["finished"] = runner?.Finished, ["failure"] = runner?.Failure,
            ["lines"] = p.Lines.Count, ["linesNeedingYou"] = p.LinesNeedingYou, ["untagged"] = p.UntaggedCount,
            ["lastTagged"] = p.LastTagged is { } t ? Json.Date(t.ToUtc()) : null,
            ["summariesBuilt"] = p.SummariesBuilt is { } b ? Json.Date(b.ToUtc()) : null,
            ["model"] = ModelManager.Shared.SelectionName,
        };
    }

    static JObject ModelSummary
    {
        get
        {
            var m = ModelManager.Shared;
            var load = m.Load switch
            {
                ModelManager.LoadState.None => "not loaded",
                ModelManager.LoadState.Loading => "loading",
                ModelManager.LoadState.Ready => "loaded",
                _ => $"failed: {m.LoadError}",
            };
            return new JObject
            {
                ["selection"] = m.SelectionName, ["id"] = m.CatalogModel?.Id, ["ready"] = m.IsReady, ["state"] = load,
                ["recommended"] = m.RecommendedID, ["memoryGB"] = m.Hardware.MemoryGB,
            };
        }
    }

    static object? DownloadJSON(CatalogModel m) => ModelManager.Shared.Downloads.TryGetValue(m.Id, out var state) ? state switch
    {
        ModelManager.DownloadState.Downloading d => new JObject { ["state"] = "downloading", ["progress"] = Json.Hundredths(d.Fraction) },
        ModelManager.DownloadState.Verifying => new JObject { ["state"] = "verifying" },
        ModelManager.DownloadState.Failed f => new JObject { ["state"] = "failed", ["message"] = f.Message },
        _ => null,
    } : null;

    static void RequireModel()
    {
        if (!ModelManager.Shared.IsReady)
            throw AgentError.NoModel($"no model is ready ({ModelManager.Shared.SelectionName}). Run `innervoice model list`, then `innervoice model select <id>` with a downloaded one, or `innervoice model download <id>`");
    }
}
