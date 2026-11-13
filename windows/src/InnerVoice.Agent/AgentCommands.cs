// The command list, ported from Sources/InnerVoice/Agent/AgentCommands.swift: the same names,
// params, summaries and examples (`innervoice help` matches the Mac's; WindowsWording lists
// the few words that differ). The app runs them (InnerVoice.App/Agent/AgentHandlers*.cs);
// AgentTests checks every command has a handler and the help matches the Mac's.

namespace InnerVoice.Agent;

public static class AgentCommands
{
    /// Every command, in the Mac's order (`projects demo` after `projects save` in test copies).
    public static IReadOnlyList<AgentCommand> All
    {
        get
        {
            if (!AgentPipe.IsTestRun) return Commands;
            var list = Commands.ToList();
            list.Insert(list.FindIndex(c => c.Name == "projects save") + 1, Demo);
            return list;
        }
    }

    public static AgentCommand? Named(string name) => All.FirstOrDefault(c => c.Name == name);

    /// The Mac's words → the Windows app's, applied to nothing at run time: the help text
    /// above already says them. Tests use this to compare with the Mac's help.
    public static readonly (string Mac, string Windows)[] WindowsWording =
    [
        ("with advice for this Mac's memory.", "with advice for this PC's memory."),
        ("and `apple` (Apple's on-device model).", "and `apple` (Apple's on-device model, only on the Mac)."),
        ("a downloaded model's id, \"apple\" for Apple's on-device model, or the path to your own .gguf file.", "a downloaded model's id, or the path to your own .gguf file."),
        ("a model id from `innervoice model list`, apple, or a .gguf path", "a model id from `innervoice model list`, or a .gguf path"),
        ("the size and the fit for this Mac", "the size and the fit for this PC"),
    ];

    /// Test copies only (APP_STORE): the sample book, so there is a project to drive.
    static readonly AgentCommand Demo = new("projects demo", "Open the sample book in a new window (Debug test copies only).",
        "APP_STORE=/tmp/iv-cli innervoice projects demo --json");

    public static readonly Param ProjectParam = new("project", Help: "the project: its name, path or id from `innervoice projects list` (default: the frontmost)");
    public static readonly Param CharacterParam = new("name", Required: true, Positional: true, Help: "the character: a name, alias or id");
    public static readonly Param IdsParam = new("ids", Required: true, Positional: true, Multiple: true, Help: "line ids from `innervoice lines list`");

    static readonly AgentCommand[] Commands =
    [
        new("status", "Show the version, the open projects (the frontmost first), the model and whether it is ready, and what the AI is doing in each project.", "innervoice status --json") {
            ReadOnly = true,
        },
        new("open", "Show a page in a project's window: manuscript, cast, voices, index, pronunciations, record, retakes, review or revisions. With --character, opens that character on the Cast page.", "innervoice open cast --character Mara --json") {
            Params = [
                new Param("page", Positional: true, Help: "manuscript, cast, voices, index, pronunciations, record, retakes, review or revisions (default manuscript)"),
                new Param("character", Help: "open this character's page in Cast (a name, alias or id)"),
                ProjectParam,
            ],
        },
        new("projects list", "List the open projects, the frontmost first. Returns `projects` (id, name, title, author, path, front, edited, chapters, lines, linesNeedingYou, characters, tagging).", "innervoice projects list --json") {
            ReadOnly = true,
        },
        new("projects open", "Open a saved project (.innervoice) in its own window, as File › Open… does. If it is open already, returns it.", "innervoice projects open ~/Documents/Harbor.innervoice --json") {
            Params = [
                new Param("path", ParamKind.Path, Required: true, Positional: true, Help: "the .innervoice project"),
            ],
        },
        new("projects new", "Start a project from a manuscript (Word, PDF, RTF, text, Markdown, OpenDocument, Pages, EPUB or HTML), as File › New from Manuscript… does: finds every line and runs the plain rules. The window opens untitled; the person saves it with File › Save.", "innervoice projects new --manuscript ~/Documents/Harbor.docx --json") {
            Params = [
                new Param("manuscript", ParamKind.Path, Required: true, Help: "the manuscript file"),
            ],
        },
        new("projects save", "Save a project, as File › Save does. With --as, save it to a new .innervoice file, as File › Save As… does (needed the first time an untitled project is saved).", "innervoice projects save --as ~/Documents/Harbor.innervoice --json") {
            Params = [
                new Param("as", ParamKind.Path, Help: "save to this .innervoice file instead"),
                ProjectParam,
            ],
        },
        new("chapters list", "List the chapters in book order. Returns `chapters` (number, title, paragraphs, words, lines, linesNeedingYou).", "innervoice chapters list --json") {
            ReadOnly = true,
            Params = [
                ProjectParam,
            ],
        },
        new("lines list", "List lines in book order. Returns `lines` (id, ref like \"Ch 3 ¶12\", speaker, kind, source, confidence, needsYou, text, tone, toneSource, toneConfidence, toneNeedsYou), `total` and `nextOffset` when there are more.", "innervoice lines list --chapter 2 --needs-you --json") {
            ReadOnly = true,
            Params = [
                new Param("chapter", ParamKind.Integer, Help: "only this chapter (from 1, see `innervoice chapters list`)"),
                new Param("speaker", Help: "only this character's lines (a name, alias or id), or \"none\" for Who's Speaking?"),
                new Param("needs-you", ParamKind.Boolean, Help: "only red lines: Who's Speaking? and Unsure"),
                new Param("search", Help: "only lines whose text contains this"),
                new Param("limit", ParamKind.Integer, Help: "at most this many (default 50)"),
                new Param("offset", ParamKind.Integer, Help: "skip this many, for the next page"),
                ProjectParam,
            ],
        },
        new("lines assign", "Give lines to a character, as Assign To does in the manuscript (one Edit › Undo step). Or --clear them back to Who's Speaking?; --clear only reports what it would change until --yes.", "innervoice lines assign 3f2a9c1e 7b0d44e2 --to Mara --json") {
            Destructive = true,
            YesHelp = "with --clear: do it. Assigning to a character needs no --yes",
            Params = [
                IdsParam,
                new Param("to", Help: "the character (a name, alias or id)"),
                new Param("new", ParamKind.Boolean, Help: "make the character named by --to if there is none yet"),
                new Param("clear", ParamKind.Boolean, Help: "make the lines Who's Speaking? again"),
                ProjectParam,
            ],
        },
        new("lines kind", "Change what kind of line these are: spoken, thought or written (one Edit › Undo step).", "innervoice lines kind 3f2a9c1e thought --json") {
            Params = [
                IdsParam,
                new Param("kind", Help: "spoken, thought or written. May also come last: `lines kind 3f2a9c1e thought`"),
                ProjectParam,
            ],
        },
        new("lines tone", "Set how lines are said, as Tone does in the line's menu (one Edit › Undo step): neutral, excited, confused, sad, angry, whispered, scared, tender or sarcastic. Tag Speakers never changes a tone set this way.", "innervoice lines tone 3f2a9c1e angry --json") {
            Params = [
                IdsParam,
                new Param("tone", Help: "the tone. May also come last: `lines tone 3f2a9c1e angry`"),
                ProjectParam,
            ],
        },
        new("lines reveal", "Show a line in the manuscript, selected, as clicking it in the Index does.", "innervoice lines reveal 3f2a9c1e --json") {
            Params = [
                new Param("id", Required: true, Positional: true, Help: "the line's id (see `innervoice lines list`)"),
                ProjectParam,
            ],
        },
        new("characters list", "List the cast, most lines first. Returns `characters` (id, name, aliases, color, lines, words, firstLine, chapters, facts, narrator, showInIndex, voice).", "innervoice characters list --json") {
            ReadOnly = true,
            Params = [
                ProjectParam,
            ],
        },
        new("characters show", "Show one character: summary facts with their sources (\"Ch 2 ¶4\") and Revealed Late flags, notes, voice notes, narrator, and line and word counts.", "innervoice characters show Mara --json") {
            ReadOnly = true,
            Params = [
                CharacterParam,
                ProjectParam,
            ],
        },
        new("characters merge", "Make two characters one, as Merge does in Cast: every line and fact of FROM moves to TO, and FROM's names become TO's aliases. Only reports what it would change until --yes (Edit › Undo can take it back).", "innervoice characters merge Mara --into \"Mara Quinn\" --yes --json") {
            Destructive = true,
            Params = [
                new Param("from", Required: true, Positional: true, Help: "the character to merge away (a name, alias or id)"),
                new Param("into", Required: true, Help: "the character to keep"),
                ProjectParam,
            ],
        },
        new("characters rename", "Rename a character, as editing the name on its Cast page does. To make two characters one, use `characters merge`.", "innervoice characters rename Tom --to \"Tom Hale\" --json") {
            Params = [
                CharacterParam,
                new Param("to", Required: true, Help: "the new name"),
                ProjectParam,
            ],
        },
        new("characters color", "Change a character's color in the manuscript. Takes auto (the default: blues and greens for men, pinks and purples for women, warm colors when the book doesn't say), #RRGGBB or a palette name (Blue, Pink, Amber…). Reddish colors are refused: red means Who's Speaking?", "innervoice characters color Mara \"#0EA5E9\" --json") {
            Params = [
                CharacterParam,
                new Param("color", Required: true, Positional: true, Help: "auto, #RRGGBB, or a palette name: Blue, Emerald, Indigo, Teal, Sky, Green, Slate, Cyan, Fuchsia, Violet, Lavender, Orchid, Pink, Purple, Lilac, Blush, Amber, Lime, Orange, Brown, Gold, Olive"),
                ProjectParam,
            ],
        },
        new("casting style", "Show or change the book's Casting Style: one-voice (One Voice, or Just Me when narrating), shared (Narrator + Two Voices, or Three Narrators) or full-cast. Changing it keeps every voice and narrator. Returns `castingStyle` and who reads each character (`readers`).", "innervoice casting style shared --json") {
            Params = [
                new Param("style", Positional: true, Help: "one-voice, shared or full-cast (leave out to show the current one)"),
                ProjectParam,
            ],
        },
        new("characters gender", "Set a character's gender: male, female, other, or auto (what the book says). Casting Style sorts by it; colors and casting follow it.", "innervoice characters gender Quinn other --json") {
            Params = [
                CharacterParam,
                new Param("gender", Required: true, Positional: true, Help: "male, female, other or auto"),
                ProjectParam,
            ],
        },
        new("characters main", "Mark a character as a main character (yes), not main (no), or neither (auto). In Full Cast, main characters always have a voice of their own.", "innervoice characters main Mara yes --json") {
            Params = [
                CharacterParam,
                new Param("main", Required: true, Positional: true, Help: "yes, no or auto"),
                ProjectParam,
            ],
        },
        new("characters read-by", "Choose who reads a character under the Casting Style: narrator, men, women, other, own (a voice or narrator of their own), or auto (the style decides).", "innervoice characters read-by Kade own --json") {
            Params = [
                CharacterParam,
                new Param("role", Required: true, Positional: true, Help: "narrator, men, women, other, own or auto"),
                ProjectParam,
            ],
        },
        new("characters shade", "Set how the narrator shades a character: pitch (-4 to 4 semitones, lower to higher), pace (-2 to 2) and a few words, or --auto to go back to the shade worked out from gender, age and the voice brief.", "innervoice characters shade Tom --pitch -2 --pace -1 --words gravelly --json") {
            Params = [
                CharacterParam,
                new Param("pitch", ParamKind.Integer, Help: "-4 (much lower) to 4 (much higher)"),
                new Param("pace", ParamKind.Integer, Help: "-2 (slower) to 2 (quicker)"),
                new Param("words", Help: "texture for the cue: gravelly, clipped…"),
                new Param("auto", ParamKind.Boolean, Help: "use the automatic shade again"),
                ProjectParam,
            ],
        },
        new("tag start", "Start Tag Speakers: the on-device model decides who speaks every line the narrator and the rules haven't settled. Runs in the background; follow it with `innervoice tag status`. Lines the narrator assigned are never changed.", "innervoice tag start --json") {
            Params = [
                new Param("chapter", ParamKind.Integer, Help: "only this chapter (from 1)"),
                ProjectParam,
            ],
        },
        new("tag stop", "Stop Tag Speakers or Build Summaries. What the AI has done so far is kept.", "innervoice tag stop --json") {
            Params = [
                ProjectParam,
            ],
        },
        new("tag status", "Show what the AI is doing in a project: job, progress (0 to 1), message, the last run's result or failure, and how many lines still need you.", "innervoice tag status --json") {
            ReadOnly = true,
            Params = [
                ProjectParam,
            ],
        },
        new("summaries start", "Start Build Summaries: the model reads about each character (most lines first) and adds facts with their sources. Runs in the background; follow it with `innervoice tag status`.", "innervoice summaries start --character Mara --json") {
            Params = [
                new Param("character", Help: "only this character (a name, alias or id)"),
                ProjectParam,
            ],
        },
        new("pronunciations list", "List the pronunciation terms. Returns `pronunciations` (id, term, sayItAs, notes, confirmed, occurrences, first, hasSample).", "innervoice pronunciations list --json") {
            ReadOnly = true,
            Params = [
                new Param("search", Help: "only terms or sayings containing this"),
                new Param("all", ParamKind.Boolean, Help: "include terms dismissed as needing no entry"),
                new Param("limit", ParamKind.Integer, Help: "at most this many (default 50)"),
                new Param("offset", ParamKind.Integer, Help: "skip this many, for the next page"),
                ProjectParam,
            ],
        },
        new("pronunciations set", "Say how to pronounce a term, as typing in the Pronunciations list does. Adds the term when it isn't there yet.", "innervoice pronunciations set Kerensa --say-it-as \"keh-REN-sah\" --json") {
            Params = [
                new Param("term", Required: true, Positional: true, Help: "the term (or its id)"),
                new Param("say-it-as", Help: "how to say it, like SHEV-on"),
                new Param("notes", Help: "your notes"),
                new Param("confirmed", ParamKind.Boolean, Help: "checked with the author or rights holder (true or false)"),
                ProjectParam,
            ],
        },
        new("export", "Export a Narrator Script, the tagged manuscript, the cast list or the pronunciation guide, as File › Export… does, to a file.", "innervoice export --kind narrator-script --narrator Ana --format pdf --out ~/Desktop/Ana.pdf --json") {
            Params = [
                new Param("kind", Required: true, Help: "narrator-script, manuscript, cast or pronunciations"),
                new Param("format", Help: "pdf (default) or word; cast and pronunciations also csv"),
                new Param("out", ParamKind.Path, Required: true, Help: "the file to write (.pdf, .docx or .csv is added when missing)"),
                new Param("narrator", Help: "narrator-script only: this narrator's characters (default every character)"),
                new Param("no-cues", ParamKind.Boolean, Help: "narrator-script only: leave out the cue line before each line"),
                new Param("no-summaries", ParamKind.Boolean, Help: "narrator-script only: leave out the character summaries"),
                new Param("replace", ParamKind.Boolean, Help: "replace the file if it exists"),
                ProjectParam,
            ],
        },
        new("model list", "List the models InnerVoice can use, with advice for this PC's memory. Returns `models` (id, name, size, quality and speed 1 to 5, fit, note, downloaded, selected, recommended, download) and `apple` (Apple's on-device model, only on the Mac).", "innervoice model list --json") {
            ReadOnly = true,
        },
        new("model select", "Choose the model Tag Speakers and Build Summaries use, as Use This Model does: a downloaded model's id, or the path to your own .gguf file.", "innervoice model select gemma-4-e4b --json") {
            Params = [
                new Param("id", Required: true, Positional: true, Help: "a model id from `innervoice model list`, or a .gguf path"),
            ],
        },
        new("model download", "Download a model from the list (2.5 to 19 GB from Hugging Face; the only network use besides updates). Only reports the size until --yes. Follow it with `innervoice model list`; when it finishes it becomes the model if none was ready.", "innervoice model download gemma-4-e4b --yes --json") {
            Destructive = true,
            YesHelp = "start the download. Without --yes it only shows the size and the fit for this PC",
            Params = [
                new Param("id", Required: true, Positional: true, Help: "a model id from `innervoice model list`"),
            ],
        }
,
    ];
}
