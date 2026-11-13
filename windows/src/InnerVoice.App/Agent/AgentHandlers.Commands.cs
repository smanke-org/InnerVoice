using InnerVoice.Agent;
using InnerVoice.App.AI;
using InnerVoice.App.Document;
using InnerVoice.App.Export;
using InnerVoice.App.Pages;
using InnerVoice.App.Speech;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.AI;
using InnerVoice.Core.Import;

namespace InnerVoice.App.Agent;

public static partial class AgentHandlers
{
    // MARK: Status and projects

    static void AddApp(Action<string, Func<Args, object?>> add, Action<string, Func<Args, Task<object?>>> addAsync)
    {
        add("status", _ =>
        {
            var front = App.Current.FrontProject;
            var result = new JObject
            {
                ["app"] = "InnerVoice", ["version"] = App.Version, ["stage"] = "release", ["build"] = App.Version,
                ["projects"] = Open().Select(w => (object?)ProjectJSON(w, front)).ToList(), ["model"] = ModelSummary,
                ["busy"] = BusyReason(),
            };
            if (AgentPipe.IsTestRun)
            {
                result["testRun"] = true;
                // So a test can stop its copy by id (tools/check-agent-cli.ps1), never by name.
                result["pid"] = Environment.ProcessId;
            }
            return result;
        });

        add("open", args =>
        {
            var (w, d) = Project(args);
            if (args.String("character") is { } name)
            {
                var c = Character(name, d);
                w.ShowCharacter(c.Id);
                return new JObject { ["project"] = Name(w), ["page"] = "cast", ["character"] = c.Name };
            }
            var raw = (args.String("page") ?? "manuscript").ToLowerInvariant();
            PageKind? page = raw switch
            {
                "manuscript" => PageKind.Manuscript,
                "cast" => PageKind.Cast,
                "voices" => PageKind.Voices,
                "index" => PageKind.Index,
                // The Windows app keeps pronunciations in the project (the agent's `pronunciations`
                // commands, the guide in File › Export…) but has no page for them: the Mac's window
                // shows the Manuscript for a page the project doesn't show.
                "pronunciations" => null,
                "record" or "teleprompter" => PageKind.Teleprompter,
                "review" => PageKind.Review,
                "revisions" => PageKind.Revisions,
                // The Mac's pages (Workspace.Page); it has no Retakes page either.
                _ => throw AgentError.Usage($"no page \"{raw}\". Pages: manuscript, cast, voices, index, pronunciations, teleprompter, review, revisions"),
            };
            // As the Mac's window does when a page isn't shown for the project type (Narration
            // Tools hides Voices and Pronunciations, Produce with AI Voices hides Index and the
            // Teleprompter): the Manuscript instead.
            var shown = page is { } k && w.VisiblePages().Contains(k) ? k : PageKind.Manuscript;
            w.Workspace.Go(shown);
            return new JObject { ["project"] = Name(w), ["page"] = PageRaw(shown) };
        });

        add("projects list", _ =>
        {
            var front = App.Current.FrontProject;
            var open = Open();
            return new JObject { ["projects"] = open.Select(w => (object?)ProjectJSON(w, front)).ToList(), ["total"] = open.Count };
        });

        add("projects open", args =>
        {
            var path = args.Required("path");
            if (!path.EndsWith(".innervoice", StringComparison.OrdinalIgnoreCase))
                throw AgentError.Usage($"{Path.GetFileName(path)} isn't an InnerVoice project (.innervoice). To start one from a manuscript: innervoice projects new --manuscript \"{path}\"");
            if (!File.Exists(path)) throw AgentError.NotFound($"no project at {path}");
            RefuseRealFilesInTestRun(path);
            ProjectWindow window;
            bool opened;
            try { (window, opened) = App.Current.OpenProject(path); }
            catch (Exception e) { throw AgentError.Invalid($"couldn't open {Path.GetFileName(path)}: {e.Message}"); }
            return Merged(ProjectJSON(window, App.Current.FrontProject), new JObject { ["opened"] = opened }, keepFirst: true);
        });

        addAsync("projects new", async args =>
        {
            var path = args.Required("manuscript");
            if (!File.Exists(path) && !Directory.Exists(path)) throw AgentError.NotFound($"no file at {path}");
            var ext = Path.GetExtension(path.TrimEnd('/', '\\')).TrimStart('.').ToLowerInvariant();
            if (!ManuscriptImporter.FileExtensions.Contains(ext))
                throw AgentError.Usage($"InnerVoice can't read .{ext} files. It reads {string.Join(", ", ManuscriptImporter.FileExtensions.Order(StringComparer.Ordinal))}");
            ImportedBook book;
            try { book = await Task.Run(() => ManuscriptImporter.Read(path)); }
            catch (Exception e) { throw AgentError.Invalid($"couldn't read {Path.GetFileName(path)}: {e.Message}"); }
            var name = Path.GetFileName(path.TrimEnd('/', '\\'));
            var project = ProjectBuilder.Make(book, name);
            (string, byte[])? original = File.Exists(path) ? (name, await File.ReadAllBytesAsync(path)) : null;
            var document = ProjectDocument.New(project, original);
            var window = App.Current.Show(document);
            _ = document.RecolorAutomaticAsync();
            return ProjectJSON(window, App.Current.FrontProject);
        });

        add("projects save", args =>
        {
            var (w, d) = Project(args);
            string? error;
            if (args.String("as") is { } raw)
            {
                var path = raw.EndsWith(".innervoice", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".innervoice";
                RefuseRealFilesInTestRun(path);
                bool same = d.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
                if (File.Exists(path) && !same)
                    throw AgentError.Invalid($"{path} already exists. Choose another name, or move that file first");
                if (!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path))))
                    throw AgentError.NotFound($"no folder {Path.GetDirectoryName(Path.GetFullPath(path))}");
                if (!d.TrySave(out error, path, overwriteNewer: true)) throw AgentError.Invalid($"couldn't save {Name(w)}: {error}");
                if (!AppPaths.IsTestRun) AppSettings.AddRecent(path);
            }
            else
            {
                if (d.FilePath is not { } path)
                    throw AgentError.Usage($"{Name(w)} has never been saved. Give it a file: innervoice projects save --as ~/Documents/{Name(w)}.innervoice");
                RefuseRealFilesInTestRun(path);
                if (!d.TrySave(out error))
                {
                    throw AgentError.Invalid(error == "changed-on-disk"
                        ? $"{Name(w)} was changed on another computer or by another app since it opened. Save it in its window (File › Save) to choose which to keep"
                        : $"couldn't save {Name(w)}: {error}");
                }
            }
            w.AgentSaved();
            return Merged(ProjectJSON(w, App.Current.FrontProject), new JObject { ["path"] = d.FilePath, ["saved"] = true }, keepFirst: false);
        });

        if (AgentPipe.IsTestRun)
            add("projects demo", _ => ProjectJSON(App.Current.OpenSample(), App.Current.FrontProject));
    }

    // MARK: Chapters and lines

    static void AddLines(Action<string, Func<Args, object?>> add)
    {
        add("chapters list", args =>
        {
            var (w, d) = Project(args);
            var p = d.Project;
            return new JObject
            {
                ["project"] = Name(w), ["total"] = p.Chapters.Count,
                ["chapters"] = p.Chapters.Select((chapter, n) =>
                {
                    var ids = chapter.Paragraphs.Select(x => x.Id).ToHashSet();
                    var inChapter = p.Lines.Where(l => ids.Contains(l.ParagraphID)).ToList();
                    return (object?)new JObject
                    {
                        ["number"] = n + 1, ["title"] = chapter.Title, ["paragraphs"] = chapter.Paragraphs.Count,
                        ["words"] = chapter.WordCount, ["lines"] = inChapter.Count, ["linesNeedingYou"] = inChapter.Count(p.NeedsYou),
                    };
                }).ToList(),
            };
        });

        add("lines list", args =>
        {
            var (w, d) = Project(args);
            var p = d.Project;
            IEnumerable<Line> picked = p.Lines;
            if (args.Has("chapter"))
            {
                int n = args.Int("chapter", 1);
                if (n < 1 || n > p.Chapters.Count) throw AgentError.NotFound($"no chapter {n}: this book has {p.Chapters.Count}. Run `innervoice chapters list`");
                picked = picked.Where(l => d.Index.Positions.TryGetValue(l.ParagraphID, out var pos) && pos.Chapter == n - 1);
            }
            if (args.String("speaker") is { } speaker)
            {
                if (new[] { "none", "nobody", "who's speaking?", "whos speaking" }.Contains(speaker.ToLowerInvariant()))
                    picked = picked.Where(l => l.SpeakerID is null);
                else
                {
                    var c = Character(speaker, d);
                    picked = picked.Where(l => l.SpeakerID == c.Id);
                }
            }
            if (args.Bool("needs-you") == true) picked = picked.Where(p.NeedsYou);
            if (args.String("search") is { Length: > 0 } search)
                picked = picked.Where(l => p.TextOf(l, d.Index).Contains(search, StringComparison.CurrentCultureIgnoreCase));
            return Page(picked.ToList(), args, "lines", new JObject { ["project"] = Name(w) }, l => LineJSON(l, d));
        });

        add("lines assign", args =>
        {
            var (w, d) = Project(args);
            var picked = FindLines(args.List("ids"), d);
            bool clear = args.Bool("clear") == true;
            if (clear == (args.String("to") is not null))
                throw AgentError.Usage("give --to <character> or --clear. Example: innervoice lines assign 3f2a9c1e --to Mara");
            if (clear)
            {
                if (!args.Confirmed)
                {
                    return new JObject
                    {
                        ["project"] = Name(w), ["changed"] = false, ["wouldClear"] = picked.Count,
                        ["lines"] = picked.Select(l => (object?)LineJSON(l, d)).ToList(),
                        ["hint"] = "Nothing changed. Add --yes to make these lines Who's Speaking? again.",
                    };
                }
                d.Assign(picked.Select(l => l.Id).ToList(), null);
                return new JObject
                {
                    ["project"] = Name(w), ["changed"] = true, ["cleared"] = picked.Count,
                    ["lines"] = picked.Select(l => LineByID(l.Id, d)).OfType<Line>().Select(l => (object?)LineJSON(l, d)).ToList(),
                };
            }
            var name = args.Required("to");
            bool created = false;
            BookCharacter target;
            BookCharacter? existing = null;
            try { existing = Character(name, d); } catch (AgentError) { }
            if (args.Bool("new") == true && existing is null)
            {
                var trimmed = name.Trim();
                if (trimmed.Length == 0) throw AgentError.Usage("--to can't be empty");
                var id = d.AddCharacter(trimmed);
                target = d.Project.Character(id)!;
                created = true;
            }
            else
            {
                target = existing ?? Character(name, d, $"or add --new to make \"{name}\"");
            }
            d.Assign(picked.Select(l => l.Id).ToList(), target.Id);
            return new JObject
            {
                ["project"] = Name(w), ["changed"] = true, ["character"] = target.Name, ["createdCharacter"] = created,
                ["lines"] = picked.Select(l => LineByID(l.Id, d)).OfType<Line>().Select(l => (object?)LineJSON(l, d)).ToList(),
            };
        });

        add("lines kind", args =>
        {
            var (w, d) = Project(args);
            var ids = args.List("ids");
            var raw = args.String("kind");
            if (raw is null && ids.Count > 0 && ParseRaw<LineKind>(ids[^1]) is not null) { raw = ids[^1]; ids.RemoveAt(ids.Count - 1); }
            if (raw is null || ParseRaw<LineKind>(raw) is not { } kind)
            {
                var shown = raw ?? (ids.Count > 0 && !ids[^1].All(Uri.IsHexDigit) ? ids[^1] : null);
                throw AgentError.Usage((shown is null ? "" : $"\"{shown}\" isn't a kind. ") + "Give spoken, thought or written. Example: innervoice lines kind 3f2a9c1e thought");
            }
            var picked = FindLines(ids, d);
            var changing = picked.Where(l => l.Kind != kind).Select(l => l.Id).ToList();
            if (changing.Count > 0) d.SetKind(kind, changing);
            return new JObject
            {
                ["project"] = Name(w), ["changed"] = changing.Count > 0, ["kind"] = Raw(kind),
                ["lines"] = picked.Select(l => LineByID(l.Id, d)).OfType<Line>().Select(l => (object?)LineJSON(l, d)).ToList(),
            };
        });

        // The tone becomes the narrator's (`ToneSource.User`), so Tag Speakers never changes it.
        add("lines tone", args =>
        {
            var (w, d) = Project(args);
            var ids = args.List("ids");
            var raw = args.String("tone");
            if (raw is null && ids.Count > 0 && Tones.Parse(ids[^1]) is not null) { raw = ids[^1]; ids.RemoveAt(ids.Count - 1); }
            if (raw is null || Tones.Parse(raw) is not { } tone)
                throw AgentError.Usage("Give a tone: " + string.Join(", ", Tones.All.Select(t => t.Raw())) + ". Example: innervoice lines tone 3f2a9c1e angry");
            var picked = FindLines(ids, d);
            d.SetTone(tone, picked.Select(l => l.Id).ToList());
            return new JObject
            {
                ["project"] = Name(w), ["tone"] = tone.Raw(),
                ["lines"] = picked.Select(l => LineByID(l.Id, d)).OfType<Line>().Select(l => (object?)LineJSON(l, d)).ToList(),
            };
        });

        add("lines reveal", args =>
        {
            var (w, d) = Project(args);
            var found = FindLines([args.Required("id")], d)[0];
            w.Workspace.ShowLine(found.ParagraphID, found.Id);
            return new JObject { ["project"] = Name(w), ["line"] = LineJSON(found, d) };
        });
    }

    // MARK: Characters

    static void AddCharacters(Action<string, Func<Args, object?>> add)
    {
        add("characters list", args =>
        {
            var (w, d) = Project(args);
            var cast = d.Project.Characters.OrderByDescending(c => d.Index.StatsFor(c.Id)?.Lines ?? 0).ToList();
            return new JObject
            {
                ["project"] = Name(w), ["total"] = cast.Count, ["linesNeedingYou"] = d.Project.LinesNeedingYou,
                ["characters"] = cast.Select(c => (object?)CharacterJSON(c, d)).ToList(),
            };
        });

        add("characters show", args =>
        {
            var (w, d) = Project(args);
            var c = Character(args.Required("name"), d);
            var first = d.Index.StatsFor(c.Id)?.FirstLine;
            var order = FactKinds.NarratorOrder;
            int Rank(Fact f) => Array.IndexOf(order, f.Kind) is var i && i >= 0 ? i : 99;
            var json = CharacterJSON(c, d);
            json["project"] = Name(w);
            json["facts"] = c.Facts.OrderBy(Rank).Select(fact =>
            {
                BookIndex.Position? source = fact.SourceParagraphID is { } sid && d.Index.Positions.TryGetValue(sid, out var pos) ? pos : null;
                bool late = source is { } s && first is { } f && s.Chapter > f.Chapter;
                return (object?)new JObject
                {
                    ["id"] = Short(fact.Id), ["kind"] = Raw(fact.Kind), ["text"] = fact.Text,
                    ["source"] = fact.SourceParagraphID is { } id ? d.Index.Reference(id) : null,
                    ["quote"] = fact.Quote, ["fromAI"] = fact.FromAI && !fact.Edited, ["revealedLate"] = late,
                };
            }).ToList();
            json["notes"] = c.Notes;
            json["voiceNotes"] = c.VoiceNotes;
            json["voiceCue"] = ProjectActions.VoiceCue(c);
            return json;
        });

        add("characters merge", args =>
        {
            var (w, d) = Project(args);
            var from = Character(args.Required("from"), d);
            var into = Character(args.Required("into"), d);
            if (from.Id == into.Id) throw AgentError.Invalid($"{from.Name} and {into.Name} are the same character");
            int lines = d.Index.StatsFor(from.Id)?.Lines ?? 0;
            var aliases = new[] { from.Name }.Concat(from.Aliases).Where(a => a != into.Name && !into.Aliases.Contains(a)).ToList<object?>();
            if (!args.Confirmed)
            {
                return new JObject
                {
                    ["project"] = Name(w), ["changed"] = false, ["from"] = from.Name, ["into"] = into.Name,
                    ["linesToMove"] = lines, ["factsToMove"] = from.Facts.Count, ["aliasesToAdd"] = aliases,
                    ["hint"] = $"Nothing changed. Add --yes to merge {from.Name} into {into.Name}.",
                };
            }
            d.Merge(from.Id, into.Id);
            var merged = d.Project.Character(into.Id) ?? into;
            return new JObject { ["project"] = Name(w), ["changed"] = true, ["merged"] = from.Name, ["into"] = CharacterJSON(merged, d) };
        });

        add("characters rename", args =>
        {
            var (w, d) = Project(args);
            var c = Character(args.Required("name"), d);
            var name = args.Required("to").Trim();
            if (name.Length == 0) throw AgentError.Usage("--to can't be empty");
            if (d.Project.Characters.FirstOrDefault(o => o.Id != c.Id && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) is { } other)
                throw AgentError.Invalid($"another character is already called {other.Name}. To make them one: innervoice characters merge \"{c.Name}\" --into \"{other.Name}\"");
            if (name == c.Name) return new JObject { ["project"] = Name(w), ["changed"] = false, ["character"] = CharacterJSON(c, d) };
            var was = c.Name;
            d.UpdateCharacter(c.Id, "Rename Character", x => x.Name = name);
            return new JObject { ["project"] = Name(w), ["changed"] = true, ["was"] = was, ["character"] = CharacterJSON(d.Project.Character(c.Id) ?? c, d) };
        });

        add("characters color", args =>
        {
            var (w, d) = Project(args);
            var c = Character(args.Required("name"), d);
            var raw = args.Required("color").Trim();
            if (raw.Equals("auto", StringComparison.OrdinalIgnoreCase) || raw.Equals("automatic", StringComparison.OrdinalIgnoreCase))
            {
                var (wasHex, wasAuto) = (c.ColorHex, c.ColorIsAutomatic);
                d.SetColor(c.Id, null);
                var now = d.Project.Character(c.Id) ?? c;
                return new JObject
                {
                    ["project"] = Name(w), ["changed"] = wasHex != now.ColorHex || !wasAuto, ["character"] = c.Name,
                    ["color"] = now.ColorHex, ["automatic"] = true,
                };
            }
            string hex;
            if (CharacterPalette.Swatches.FirstOrDefault(s => s.Name.Equals(raw, StringComparison.OrdinalIgnoreCase)) is { } swatch)
                hex = swatch.Hex;
            else
            {
                var digits = raw.StartsWith('#') ? raw[1..] : raw;
                if (digits.Length != 6 || !digits.All(Uri.IsHexDigit))
                    throw AgentError.Usage($"\"{raw}\" isn't a color. Give #RRGGBB, like #0EA5E9, or a palette name: {string.Join(", ", CharacterPalette.Swatches.Select(s => s.Name))}");
                hex = "#" + digits.ToUpperInvariant();
            }
            if (CharacterPalette.IsTooRed(hex))
                throw AgentError.Invalid($"{hex} is too close to red, which InnerVoice keeps for Who's Speaking? and Unsure lines. Pick another color, like {CharacterPalette.Next(d.Project.Characters.Select(x => x.ColorHex))}");
            bool changed = !string.Equals(c.ColorHex, hex, StringComparison.OrdinalIgnoreCase) || c.ColorChosen != true;
            if (changed) d.SetColor(c.Id, hex);
            return new JObject { ["project"] = Name(w), ["changed"] = changed, ["character"] = c.Name, ["color"] = hex, ["automatic"] = false };
        });

        // Casting Style (docs/CASTING-STYLE.md)
        add("casting style", args =>
        {
            var (w, d) = Project(args);
            if (args.String("style") is { } raw)
            {
                var styles = new Dictionary<string, CastingStyle>
                {
                    ["one-voice"] = CastingStyle.OneVoice, ["one"] = CastingStyle.OneVoice, ["just-me"] = CastingStyle.OneVoice,
                    ["shared"] = CastingStyle.Shared, ["three-narrators"] = CastingStyle.Shared,
                    ["full-cast"] = CastingStyle.FullCast, ["full"] = CastingStyle.FullCast,
                };
                if (!styles.TryGetValue(raw.ToLowerInvariant(), out var style)) throw AgentError.Usage($"\"{raw}\" isn't a Casting Style. Give one-voice, shared or full-cast.");
                d.SetCastingStyle(style);
            }
            var readers = d.Readers();
            // A book with AI voices takes its shades from the voices; a narration book from the narrator.
            var shades = d.Project.ShowsVoices ? VoiceSetup.Resolver(d).Shades : d.Shades();
            return new JObject
            {
                ["project"] = Name(w), ["castingStyle"] = d.Project.Settings.CastingStyle is { } cs ? Raw(cs) : null,
                ["readers"] = d.Project.Characters.Where(c => (d.Index.StatsFor(c.Id)?.Lines ?? 0) > 0).Select(c => (object?)new JObject
                {
                    ["name"] = c.Name, ["readBy"] = readers.TryGetValue(c.Id, out var r) ? Raw(r) : "own",
                    ["shade"] = shades.TryGetValue(c.Id, out var s) ? s.Cue : null,
                }).ToList(),
            };
        });

        add("characters gender", args =>
        {
            var (w, d) = Project(args);
            var c = Character(args.Required("name"), d);
            var raw = args.Required("gender").ToLowerInvariant();
            CharacterGender? g = raw == "auto" ? null : ParseRaw<CharacterGender>(raw) ?? throw AgentError.Usage("Give male, female, other or auto.");
            var was = c.Gender;
            d.SetGender([c.Id], g);
            return new JObject { ["project"] = Name(w), ["changed"] = was != g, ["character"] = c.Name, ["gender"] = g is { } x ? Raw(x) : "auto" };
        });

        add("characters main", args =>
        {
            var (w, d) = Project(args);
            var c = Character(args.Required("name"), d);
            var raw = args.Required("main").ToLowerInvariant();
            bool? main = raw is "yes" or "true" ? true : raw is "no" or "false" ? false : raw == "auto" ? null
                : throw AgentError.Usage("Give yes, no or auto.");
            var was = c.IsMain;
            d.SetMain([c.Id], main);
            return new JObject { ["project"] = Name(w), ["changed"] = was != main, ["character"] = c.Name, ["main"] = main is { } m ? m : "auto" };
        });

        add("characters read-by", args =>
        {
            var (w, d) = Project(args);
            var c = Character(args.Required("name"), d);
            var raw = args.Required("role").ToLowerInvariant();
            VoiceRole? role = raw == "auto" ? null : ParseRaw<VoiceRole>(raw) ?? throw AgentError.Usage("Give narrator, men, women, other, own or auto.");
            var was = c.Role;
            d.SetRole(c.Id, role);
            var reader = d.Readers().TryGetValue(c.Id, out var r) ? Raw(r) : "own";
            return new JObject
            {
                ["project"] = Name(w), ["changed"] = was != role, ["character"] = c.Name, ["role"] = role is { } x ? Raw(x) : "auto", ["readBy"] = reader,
            };
        });

        add("characters shade", args =>
        {
            var (w, d) = Project(args);
            var c = Character(args.Required("name"), d);
            Shade? shade = args.Bool("auto") == true ? null
                : Shade.Make(args.Int("pitch", c.Shade?.Pitch ?? 0), args.Int("pace", c.Shade?.Pace ?? 0), args.String("words") ?? c.Shade?.Words);
            d.SetShade(c.Id, shade);
            return new JObject
            {
                ["project"] = Name(w), ["character"] = c.Name,
                ["shade"] = shade is { } s ? new JObject { ["pitch"] = s.Pitch, ["pace"] = s.Pace, ["words"] = s.Words, ["cue"] = s.Cue } : "auto",
            };
        });
    }

    // MARK: AI

    static void AddAI(Action<string, Func<Args, object?>> add)
    {
        add("tag start", args =>
        {
            var (w, d) = Project(args);
            RequireModel();
            if (w.ExistingRunner?.IsRunning == true) throw AgentError.Busy($"{Name(w)} is already {JobWords(w)}. Wait, or run `innervoice tag stop`");
            List<int>? chapters = null;
            if (args.Has("chapter"))
            {
                int n = args.Int("chapter", 1);
                if (n < 1 || n > d.Project.Chapters.Count) throw AgentError.NotFound($"no chapter {n}: this book has {d.Project.Chapters.Count}");
                chapters = [n - 1];
            }
            _ = w.TagSpeakers(chapters);
            return TaggingJSON(w);
        });

        add("tag stop", args =>
        {
            var (w, _) = Project(args);
            bool running = w.ExistingRunner?.IsRunning == true;
            w.ExistingRunner?.Stop();
            return new JObject { ["project"] = Name(w), ["stopped"] = running };
        });

        add("tag status", args => TaggingJSON(Project(args).Window));

        add("summaries start", args =>
        {
            var (w, d) = Project(args);
            RequireModel();
            if (w.ExistingRunner?.IsRunning == true) throw AgentError.Busy($"{Name(w)} is already {JobWords(w)}. Wait, or run `innervoice tag stop`");
            List<Guid>? only = args.String("character") is { } name ? [Character(name, d).Id] : null;
            _ = w.BuildSummaries(only);
            return TaggingJSON(w);
        });
    }

    // MARK: Pronunciations and export

    static void AddOutput(Action<string, Func<Args, object?>> add)
    {
        add("pronunciations list", args =>
        {
            var (w, d) = Project(args);
            IEnumerable<Pronunciation> terms = d.Project.Pronunciations;
            if (args.Bool("all") != true) terms = terms.Where(t => !t.Dismissed);
            if (args.String("search") is { Length: > 0 } search)
                terms = terms.Where(t => t.Term.Contains(search, StringComparison.CurrentCultureIgnoreCase) || t.SayItAs.Contains(search, StringComparison.CurrentCultureIgnoreCase));
            return Page(terms.ToList(), args, "pronunciations", new JObject { ["project"] = Name(w) }, t => PronunciationJSON(t, d));
        });

        add("pronunciations set", args =>
        {
            var (w, d) = Project(args);
            var term = args.Required("term").Trim();
            if (term.Length == 0) throw AgentError.Usage("the term can't be empty");
            var sayItAs = args.String("say-it-as");
            var notes = args.String("notes");
            var confirmed = args.Bool("confirmed");
            if (sayItAs is null && notes is null && confirmed is null)
                throw AgentError.Usage("give --say-it-as, --notes or --confirmed. Example: innervoice pronunciations set Kerensa --say-it-as \"keh-REN-sah\"");
            var existing = d.Project.Pronunciations.FirstOrDefault(p => string.Equals(p.Term, term, StringComparison.OrdinalIgnoreCase))
                // An id only when it looks like one (8 characters), so a new term like "Bead" never hits an id.
                ?? (term.Length == 8 ? UniquePrefix(term, d.Project.Pronunciations, p => p.Id) : null);
            void Apply(Pronunciation p)
            {
                if (sayItAs is not null) { p.SayItAs = sayItAs; p.Dismissed = false; }
                if (notes is not null) p.Notes = notes;
                if (confirmed is { } c) p.Confirmed = c;
            }
            if (existing is not null)
            {
                var before = existing.Copy();
                d.Change("Edit Pronunciation", p => { if (p.Pronunciations.FirstOrDefault(x => x.Id == existing.Id) is { } x) Apply(x); });
                var now = d.Project.Pronunciations.FirstOrDefault(p => p.Id == existing.Id) ?? existing;
                bool changed = before.SayItAs != now.SayItAs || before.Notes != now.Notes || before.Confirmed != now.Confirmed || before.Dismissed != now.Dismissed;
                return new JObject { ["project"] = Name(w), ["added"] = false, ["changed"] = changed, ["pronunciation"] = PronunciationJSON(now, d) };
            }
            var added = new Pronunciation { Term = term, AddedByUser = true };
            Apply(added);
            d.Change("Add Term", p => p.Pronunciations.Add(added.Copy()));
            return new JObject { ["project"] = Name(w), ["added"] = true, ["changed"] = true, ["pronunciation"] = PronunciationJSON(added, d) };
        });

        add("export", args =>
        {
            var (w, d) = Project(args);
            var rawKind = args.Required("kind").ToLowerInvariant();
            Exporter.Kind kind = rawKind switch
            {
                "narrator-script" => Exporter.Kind.NarratorScript,
                "manuscript" => Exporter.Kind.Manuscript,
                "cast" => Exporter.Kind.Cast,
                "pronunciations" => Exporter.Kind.Pronunciations,
                _ => throw AgentError.Usage($"--kind must be narrator-script, manuscript, cast or pronunciations, not \"{rawKind}\""),
            };
            var rawFormat = (args.String("format") ?? "pdf").ToLowerInvariant();
            Exporter.Format format = rawFormat switch
            {
                "pdf" => Exporter.Format.Pdf,
                "word" or "docx" => Exporter.Format.Word,
                "csv" => Exporter.Format.Csv,
                _ => throw AgentError.Usage($"--format must be pdf, word or csv, not \"{rawFormat}\""),
            };
            var formats = Exporter.Formats(kind);
            if (!formats.Contains(format))
                throw AgentError.Usage($"{kind.Title()} exports as {string.Join(" or ", formats.Select(f => f.ToString().ToLowerInvariant()))}, not {rawFormat}");
            var options = new Exporter.Options { Kind = kind, Format = format };
            if (args.String("narrator") is { } narratorName)
            {
                if (kind != Exporter.Kind.NarratorScript) throw AgentError.Usage("--narrator only goes with --kind narrator-script");
                var narrator = d.Project.Narrators.FirstOrDefault(n => string.Equals(n.Name, narratorName, StringComparison.OrdinalIgnoreCase));
                if (narrator is null)
                {
                    var names = d.Project.Narrators.Select(n => n.Name).ToList();
                    throw AgentError.NotFound($"no narrator named {narratorName}. " + (names.Count == 0
                        ? "This project has no narrators yet: the person sets them up in File › Export…"
                        : $"Narrators: {string.Join(", ", names)}"));
                }
                options.NarratorID = narrator.Id;
            }
            options.IncludeCues = args.Bool("no-cues") != true;
            options.IncludeSummaries = args.Bool("no-summaries") != true;
            var path = args.Required("out");
            var ext = format.Extension();
            if (!Path.GetExtension(path).Equals("." + ext, StringComparison.OrdinalIgnoreCase)) path += "." + ext;
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (!Directory.Exists(folder)) throw AgentError.NotFound($"no folder {folder}");
            if (File.Exists(path) && args.Bool("replace") != true)
                throw AgentError.Invalid($"{path} already exists. Add --replace to write over it, or choose another --out");
            try { Exporter.Write(options, d, path); }
            catch (Exception e) { throw AgentError.Invalid($"couldn't export: {e.Message}"); }
            return new JObject
            {
                ["project"] = Name(w), ["kind"] = rawKind, ["format"] = format.ToString().ToLowerInvariant(), ["path"] = path,
                ["bytes"] = new FileInfo(path).Length,
            };
        });
    }

    // MARK: Model

    static void AddModels(Action<string, Func<Args, object?>> add)
    {
        add("model list", _ =>
        {
            var manager = ModelManager.Shared;
            manager.Refresh();
            var hw = manager.Hardware;
            return new JObject
            {
                ["memoryGB"] = hw.MemoryGB, ["gpu"] = hw.GpuName, ["gpuGB"] = Math.Round(hw.GpuGB, 1),
                ["selection"] = manager.SelectionName, ["ready"] = manager.IsReady,
                ["models"] = ModelCatalog.All.Select(m =>
                {
                    var advice = ModelCatalog.Advice(m, hw);
                    return (object?)new JObject
                    {
                        ["id"] = m.Id, ["name"] = m.Name, ["size"] = m.SizeDescription, ["quality"] = m.Quality, ["speed"] = m.Speed,
                        ["fit"] = advice.Title, ["note"] = advice.Note, ["blurb"] = m.Blurb,
                        ["downloaded"] = manager.Downloaded.Contains(m.Id), ["selected"] = manager.Selection == "catalog:" + m.Id,
                        ["recommended"] = m.Id == manager.RecommendedID, ["download"] = DownloadJSON(m),
                    };
                }).ToList(),
                ["apple"] = new JObject { ["id"] = "apple", ["available"] = false, ["why"] = "Apple's on-device model is only on the Mac", ["selected"] = false },
            };
        });

        add("model select", args =>
        {
            var manager = ModelManager.Shared;
            manager.Refresh();
            var raw = args.Required("id");
            string selection;
            if (raw.Equals("apple", StringComparison.OrdinalIgnoreCase))
                throw AgentError.Invalid("Apple's on-device model is only on the Mac. Run `innervoice model list` for the ones on this PC");
            if (raw.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                var path = raw == "~" || raw.StartsWith("~/") || raw.StartsWith("~\\") ? AgentCli.Absolute(raw) : raw;
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw AgentError.NotFound($"no model file at {raw} (give the full path)");
                selection = "file:" + path;
            }
            else
            {
                var m = ModelCatalog.Model(raw.ToLowerInvariant()) ?? throw AgentError.NotFound($"no model {raw}. Run `innervoice model list`");
                if (!manager.Downloaded.Contains(m.Id))
                    throw AgentError.Invalid($"{m.Name} isn't downloaded yet ({m.SizeDescription}). Run `innervoice model download {m.Id}`, then select it");
                selection = "catalog:" + m.Id;
            }
            if (BusyReason() is { } busy) throw AgentError.Busy($"InnerVoice is {busy}. Stop it first with `innervoice tag stop`");
            bool changed = manager.Selection != selection;
            if (changed) manager.Selection = selection;
            return new JObject { ["changed"] = changed, ["selection"] = manager.SelectionName, ["ready"] = manager.IsReady };
        });

        add("model download", args =>
        {
            var manager = ModelManager.Shared;
            manager.Refresh();
            var raw = args.Required("id").ToLowerInvariant();
            var m = ModelCatalog.Model(raw) ?? throw AgentError.NotFound($"no model {raw}. Run `innervoice model list`");
            var advice = ModelCatalog.Advice(m, manager.Hardware);
            if (manager.Downloaded.Contains(m.Id)) return new JObject { ["id"] = m.Id, ["changed"] = false, ["downloaded"] = true };
            if (!args.Confirmed)
            {
                return new JObject
                {
                    ["id"] = m.Id, ["changed"] = false, ["size"] = m.SizeDescription, ["fit"] = advice.Title, ["note"] = advice.Note,
                    ["hint"] = $"Nothing downloaded. Add --yes to download {m.SizeDescription}.",
                };
            }
            manager.Download(m);
            return new JObject { ["id"] = m.Id, ["changed"] = true, ["size"] = m.SizeDescription, ["download"] = DownloadJSON(m) };
        });
    }
}
