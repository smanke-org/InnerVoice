namespace InnerVoice.App.Help;

// The pages of Help, ported from the Mac's System/HelpTopics.swift with only what the
// Windows app has (Narration Tools: no voices, Play, Export Audio or pronunciations). Words follow
// docs/GLOSSARY.md. When a feature or shortcut changes, change its page here too.

/// One block of a Help page.
public abstract record HelpBlock
{
    public sealed record Text(string Value) : HelpBlock;
    public sealed record Heading(string Value) : HelpBlock;
    /// A numbered list.
    public sealed record Steps(string[] Items) : HelpBlock;
    /// Keys (or a term) on the left, what they do on the right.
    public sealed record Keys((string Keys, string What)[] Rows) : HelpBlock;
    public sealed record Tip(string Value) : HelpBlock;

    /// Everything a search looks through.
    public string SearchText => this switch
    {
        Text t => t.Value,
        Heading h => h.Value,
        Tip t => t.Value,
        Steps s => string.Join(" ", s.Items),
        Keys k => string.Join(" ", k.Rows.Select(r => r.Keys + " " + r.What)),
        _ => "",
    };
}

/// The pages of Help, in list order.
public enum HelpTopic
{
    GettingStarted,
    OpeningManuscript,
    TaggingSpeakers,
    FixingLines,
    Colors,
    CastAndSummaries,
    Index,
    NarratorsAndScripts,
    Teleprompter,
    RevisedManuscripts,
    ChoosingModel,
    KeyboardShortcuts,
    Settings,
    Updates,
}

public static class HelpContent
{
    public static IReadOnlyList<HelpTopic> All { get; } = Enum.GetValues<HelpTopic>();

    public static string Title(HelpTopic topic) => topic switch
    {
        HelpTopic.GettingStarted => "Getting Started",
        HelpTopic.OpeningManuscript => "Opening a Manuscript",
        HelpTopic.TaggingSpeakers => "Tagging Speakers",
        HelpTopic.FixingLines => "Fixing Lines",
        HelpTopic.Colors => "Colors",
        HelpTopic.CastAndSummaries => "Cast & Summaries",
        HelpTopic.Index => "Index",
        HelpTopic.NarratorsAndScripts => "Narrators & Scripts",
        HelpTopic.Teleprompter => "Teleprompter",
        HelpTopic.RevisedManuscripts => "Revised Manuscripts",
        HelpTopic.ChoosingModel => "Choosing a Model",
        HelpTopic.KeyboardShortcuts => "Keyboard Shortcuts",
        HelpTopic.Settings => "Settings",
        _ => "Updates",
    };

    /// True when `query` (already trimmed) is in the topic's title or any of its text.
    public static bool Matches(HelpTopic topic, string query) =>
        query.Length == 0
        || Title(topic).Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Blocks(topic).Any(b => b.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    static HelpBlock T(string s) => new HelpBlock.Text(s);
    static HelpBlock H(string s) => new HelpBlock.Heading(s);
    static HelpBlock Tip(string s) => new HelpBlock.Tip(s);
    static HelpBlock Steps(params string[] items) => new HelpBlock.Steps(items);
    static HelpBlock Keys(params (string, string)[] rows) => new HelpBlock.Keys(rows);

    public static IReadOnlyList<HelpBlock> Blocks(HelpTopic topic) => topic switch
    {
        HelpTopic.GettingStarted =>
        [
            T("InnerVoice reads your audiobook manuscript with an AI that runs on your PC, colors every spoken line by the character who says it, and gives you the cast, character summaries and a teleprompter for the booth."),
            H("Your first project"),
            T("Choose File › New from Manuscript… (Ctrl+N), or drop a manuscript on the Start window. InnerVoice makes a project, a .innervoice file with its own copy of the manuscript, and finds every quoted line right away. Lines it hasn't tagged yet show as Who's Speaking?."),
            T("Then choose Book › Tag Speakers (Ctrl+Shift+T). The AI reads the book chapter by chapter on your PC and assigns each line to a character. You can keep reading while it works, and Stop keeps what's done. The first time, choose a model in Settings › AI (see Choosing a Model)."),
            H("Follow the steps"),
            T("The Steps bar across the top of the window shows where the book stands and what comes next: Manuscript, Speakers, Review, Characters and Teleprompter. Each step says how far along it is (\"3 to review\", \"Not read yet\"), and the next step is highlighted. Nothing starts until you click."),
            Steps(
                "Manuscript: delete anything you won't read aloud, like a copyright page or an ISBN.",
                "Speakers: Tag Speakers finds who says each line.",
                "Review: confirm the lines the AI wasn't sure of; typing a name that isn't in the cast adds a new character.",
                "Characters: Build Summaries reads the book again for what it says about each character and writes a voice brief into their Voice Notes.",
                "Teleprompter: read the book in the booth with the teleprompter. This step is done when you say so: right-click it and choose Mark Done."),
            T("Click any step to go to its page. To skip a step, right-click it and choose Mark Done; right-click it again and choose Bring Back. View › Show/Hide Steps hides the bar or brings it back, and so does Steps bar in Settings › General."),
            H("The project window"),
            T("The sidebar has the book's pages, then its chapters and the main characters. Ctrl+1 to Ctrl+4 go straight to Manuscript, Cast, Index and Teleprompter; Review is in the sidebar and the View menu."),
            Keys(
                ("Manuscript", "Read the colored book and fix lines"),
                ("Review", "Every line that needs you"),
                ("Cast", "Every character, with a summary, line counts and narrator"),
                ("Index", "Each character's lines by chapter, with links"),
                ("Teleprompter", "The teleprompter for the booth"),
                ("Revisions", "What changed, after File › Import Revised Manuscript…")),
            H("Saving"),
            T("A project is one .innervoice file. Save it once with File › Save (Ctrl+S); from then on InnerVoice saves by itself every 30 seconds while it has changes. File › Save As… (Ctrl+Shift+S) saves a copy under a new name or in another folder."),
            H("A book on your PC and your Mac"),
            T("The same .innervoice file opens in InnerVoice on a Mac, so you can tag a book on one computer and read it in the booth on the other. Keep it in OneDrive, Dropbox or any synced folder, or carry it on a USB stick."),
            T("There's no merging: the last save wins. If the file changed on another computer since you opened it, saving asks before replacing it."),
            T("A book set up for AI voices on the Mac opens here with the Narration Tools pages. Its voices are kept, and they're all there again when you open the book on the Mac."),
            Tip("Your manuscript stays on this PC. The only thing InnerVoice downloads is the AI model you choose, once. The one exception is Claude, which you have to set up and allow for each book: see Choosing a Model."),
        ],

        HelpTopic.OpeningManuscript =>
        [
            T("InnerVoice opens Word (.docx), PDF, RTF, plain text, Markdown, OpenDocument (.odt), EPUB and HTML. It splits the manuscript into chapters at headings such as \"Chapter 1\", \"CHAPTER ONE\", \"Prologue\" and \"Part II\", and at Word heading styles."),
            H("Older Word files and Pages"),
            T("An older Word .doc file: open it in Word, choose File › Save As, and save it as a Word Document (.docx). A Pages file from a Mac: in Pages, choose File › Export To › Word, and open the Word file instead."),
            H("Google Docs"),
            T("Download the document as Word first: in Google Docs, choose File › Download › Microsoft Word (.docx), then open that file in InnerVoice."),
            H("PDF"),
            T("Running headers, footers and page numbers are left out, and paragraphs split across pages are joined again. Lines that a book spreads out to fill the width (with wide gaps between the letters) are read as ordinary words in their paragraph. A scanned PDF has pictures of pages and no text: export it with text from the app that made it, or run it through a text recognition (OCR) app first."),
            H("EPUB"),
            T("An EPUB's chapters come from its own table of contents, with the chapter titles it gives, even when the whole book is one long file inside. The contents page and the copyright page are left out. If the book has no table of contents, or one with a single entry, InnerVoice finds the chapters at their headings instead."),
            T("Footnotes and endnotes aren't part of the narration: the notes are left out, and so are the little numbers and symbols in the text that link to them."),
            Tip("InnerVoice keeps its own copy of the manuscript in the project, so moving or changing the original file doesn't change your project. To bring in a new draft, see Revised Manuscripts."),
        ],

        HelpTopic.TaggingSpeakers =>
        [
            T("Tag Speakers (Book › Tag Speakers, Ctrl+Shift+T) uses the AI on your PC to decide who speaks each line. Chapters fill with color as it goes, and a bar at the top of the window shows how far it has got."),
            H("How it decides"),
            T("Plain rules come first: \"said Tom\", \"Tom asked\" and a pronoun after a named speaker settle most lines with no AI at all. In a back-and-forth with no tags, the lines alternate between the two characters. The model reads the rest, and any name it finds that isn't in the cast becomes a new character."),
            T("Lines you assigned yourself are never changed by Tag Speakers. Stop (Ctrl+.) keeps everything done so far, and Tag Speakers carries on from there next time. Edit › Undo Tag Speakers (Ctrl+Z) takes the whole run back."),
            T("Tag Speakers needs a model on this PC. If there isn't one yet, it offers to open Settings › AI. Without one, you can still assign every line yourself."),
            H("Privacy"),
            T("A downloaded model runs on your PC: your manuscript, your cast and your notes never leave it, and InnerVoice works offline. The only thing downloaded is the model you choose, once. Claude is the exception: it runs in the cloud, and only books you allow are sent to it (see Choosing a Model)."),
            Tip("When it's done, a banner says how many lines need you. Click Review to see them, or press Ctrl+Down to go to the next one in the manuscript."),
        ],

        HelpTopic.FixingLines =>
        [
            T("Click a line to assign it. Assign To lists the characters with their colors: pinned characters first, then the ones used most in this chapter. Type to filter the list, or press a number key to pick one of the first nine. Narration sits at the top of the list for text no one says out loud."),
            Keys(
                ("Click a line", "Open Assign To"),
                ("Enter", "Open Assign To for the selected line"),
                ("1 – 9", "Assign to that character in the list"),
                ("Type", "Filter the characters"),
                ("Enter (in Assign To)", "Confirm the best guess, or the name you picked with ↓ ↑"),
                ("A new name, then Enter", "Make a character and assign the line to them"),
                ("0 or Narration", "No one says it: the color goes and you read it as narration"),
                ("Esc", "Close Assign To"),
                ("Ctrl+Z", "Undo the change")),
            T("A line you assign is marked as yours, and a later Tag Speakers run never changes it."),
            H("Right-click a line"),
            Keys(
                ("Assign To…", "Open Assign To"),
                ("Speaker", "Pick from every character"),
                ("Kind", "Spoken, Thought or Written"),
                ("Mark as Narration", "No one says it"),
                ("Who's Speaking?", "Clear the speaker, so the line waits for you again"),
                ("Open (name)", "Go to the speaker's page in Cast")),
            H("Pin the speakers you use most"),
            T("Pinned characters sit at the top of every speaker list. Right-click a character in Cast and choose Pin to the Top of Speaker Lists, or tick \"Pin to the top of speaker lists\" on their page."),
            H("Fix a whole exchange"),
            T("When you fix one line in a back-and-forth that had no tags, the lines after it are probably off by one too. A bar offers to swap the rest of the exchange, up to the next tagged line or break in the narration. Click Swap or Keep As Is."),
            H("Several lines at once"),
            T("Shift-click or Ctrl-click to select several lines, then press Enter, or right-click them, to assign them all at once."),
            H("Make a line"),
            T("When the book has a line InnerVoice didn't find, drag across its words, right-click and choose Make Line, then pick who says it. Copy (Ctrl+C) copies the words you dragged across."),
            H("Merge characters"),
            T("When the same person turns up under two names, say Tom and Thomas, right-click one of them in Cast and choose Merge Tom Into › Thomas. Every line and fact moves to one character, and the other name becomes an alias. When two names look like the same person, Cast offers the merge at the top of the page."),
            H("Delete text the narrator doesn't read"),
            T("Drag across an ISBN, a copyright page, or an ad for another book and press Delete (or right-click and choose Delete Text…). It goes from the project and from every script and export; the manuscript file you imported never changes. Select a chapter's heading and all of its text to delete the whole chapter. Front matter often comes in as a chapter heading: delete a heading on its own and its text joins the chapter before (the book's first text stays, under Opening). Ctrl+Z brings it back."),
            H("Lines to review in each chapter"),
            T("In the sidebar, \"2 to review\" beside a chapter means two of its lines still need you to confirm who says them. The numbers beside the cast are how many lines each character speaks."),
            Tip("Once you've saved a project with a name, it saves itself every 30 seconds while it has changes, so you can keep working without stopping to save. A new project waits for your first File › Save."),
            H("Review the guesses"),
            T("When the AI or the rules aren't sure, they still make a best guess: the margin shows the name with a question mark (\"Cyrus?\") and the line stays red until you check it. Review in the sidebar lists every line that needs you, in book order, each with the paragraph around it. Click Confirm to keep the guess, Assign… to pick someone else, or Show to see it in the manuscript. A settled line leaves the list."),
            Tip("Ctrl+Down and Ctrl+Up (Book › Next Line That Needs You and Previous Line That Needs You) jump to the next and previous line that needs you and open Assign To: Enter confirms the best guess."),
        ],

        HelpTopic.Colors =>
        [
            T("Every character has a color, and each line shows the color of the character who says it. Red is never a character's color: it's kept for lines that need you."),
            H("Automatic colors"),
            T("Colors start on Automatic: blues and greens for men, pinks and purples for women, and warm colors (amber, orange, gold) for anyone the book hasn't said yet. Main characters get the clearest colors. When Tag Speakers or Build Summaries learns more about someone (\"she said\", a title like Mrs., their summary), their color follows, and two characters share a color only when a family runs out."),
            H("Choose a color"),
            T("To choose a character's color yourself, right-click them in Cast, choose Color, and pick one from the masculine, feminine or neutral colors. A color you choose stays until you choose Automatic again."),
            H("Highlight, Outline or Underline"),
            T("Highlight fills each line with its color; Outline draws a line around it; Underline draws a colored line under the words and keeps the page calmest. Choose in the View menu. Each project keeps its own choice."),
            Keys(
                ("Spoken", "Highlighted, outlined or underlined in the character's color"),
                ("Thought", "Dashed"),
                ("Written", "Dotted, for letters, signs and texts")),
            H("Red means it needs you"),
            T("Red is kept for lines that need you, and no character is ever red. Who's Speaking? is a line the AI couldn't decide on. Unsure is a line it tagged with low confidence: the margin shows its best guess with a question mark (\"Cyrus?\"), and Who's Speaking? lines show just \"?\"."),
            H("Speakers in the margin"),
            T("Each line also shows who says it in the margin, so you never have to rely on color alone. Choose initials, first names or full names in View › Margin Shows or Settings › General; long names are shortened with \"…\". Turn them off for a project with View › Show/Hide Initials."),
            H("Text size and spacing"),
            T("Make the text bigger or smaller with A+ and A− on the Manuscript page, or View › Bigger Text (Ctrl+=) and Smaller Text (Ctrl+-). View › Line Spacing switches between single and double spacing, and View › Serif / Sans Serif Text changes the typeface. Settings › General has the same choices."),
        ],

        HelpTopic.CastAndSummaries =>
        [
            T("Cast shows every character as a card, with their color, line and word counts, and narrator. Click a card to open that character's page and read their summary; ‹ Cast goes back. Right-click a card to pin, color, merge or delete the character."),
            H("Summaries"),
            T("Build Summaries (Book › Build Summaries, Ctrl+Shift+B) has the AI read the text around each character and fill in what the book says: age, gender, accent, voice, personality, history and relationships. Each fact has a source, such as \"Ch 3 ¶12\". Click it to see that paragraph in the manuscript."),
            T("While it reads, a bar at the top of the window shows how far it has got. Stop (Ctrl+.) keeps what it has found."),
            H("Voice briefs"),
            T("After it reads about everyone, Build Summaries writes a voice brief for each character into their Voice Notes under \"Voice brief:\": pitch, pace, texture, attitude, and how they should differ from the characters they share scenes with (\"Lower and slower than Mara; rougher than Edda\"). Main characters come first, so each later brief can set itself apart from voices already described. Your own notes come first when the AI writes it, and the brief is yours once you edit it."),
            H("Voice notes from the book"),
            T("What Build Summaries learns about how a character sounds (age, gender, accent, voice and manner) goes into their Voice Notes under \"From the book:\", below anything you wrote. Once you edit the \"From the book\" part it's yours, and Build Summaries leaves it alone. Delete it to get a fresh one next time."),
            T("A fact the AI can't back up with text from the book is dropped, so every fact you see can be checked."),
            H("Revealed Late"),
            T("A fact marked Revealed Late is first mentioned after the character's first line, for example \"First speaks Ch 2, revealed Ch 20\". It's worth knowing before you decide how they sound in Chapter 2."),
            H("Editing"),
            T("Change a character's name, aliases, narrator or any fact, add facts of your own, and write your own notes and voice notes. A fact you edited is never replaced when Build Summaries runs again."),
            T("What you type in these fields is saved when you pause, press Enter or click elsewhere, so typing stays quick on a long book. A burst of typing is one step to undo (Ctrl+Z)."),
        ],

        HelpTopic.Index =>
        [
            T("Index lists each character's lines by chapter. Click a line to go to it in the manuscript."),
            H("Show in Index"),
            T("Each character has a Show in Index switch. Main characters with a large share of the lines start hidden, so the index stays useful for finding the smaller parts; turn them on whenever you like. Show All and Hide All switch every character at once."),
            Tip("Ctrl+F finds text anywhere in the manuscript; F3 goes to the next match and Shift+F3 to the one before."),
        ],

        HelpTopic.NarratorsAndScripts =>
        [
            T("For a production with more than one narrator, choose Book › Narrators… (or click Narrators… in Cast) and add them. Then cast each character to one of them on the character's page."),
            H("Narrator Scripts"),
            T("Choose File › Export…, pick Narrator Script and the narrator, and save it as PDF or Word. It has only their lines, each with its cue line and its chapter and paragraph, and the summaries of the characters they voice. Send it to the narrator; they don't need InnerVoice."),
            T("A chapter with a recap (\"Previously…\", from right-clicking the chapter's text and choosing Chapter Recap…, or from Book › Claude (Cloud) › Write Chapter Recaps) has it under the chapter's title."),
            H("The whole manuscript"),
            T("File › Export… also saves the Tagged Manuscript as PDF or Word, with colors, initials and a key to the characters, for printing or reading on a tablet, and the Cast List as PDF, Word or CSV."),
        ],

        HelpTopic.Teleprompter =>
        [
            T("The Teleprompter is for the booth. It shows one paragraph at a time in large type, with the speaker's name, color and voice cue, and who speaks next below it. The top shows the chapter and where you are in the book (\"Paragraph 12 of 840\")."),
            Keys(
                ("Space or →", "Next paragraph"),
                ("←", "Back one paragraph")),
            T("It starts where you left off in that book: InnerVoice remembers the last paragraph you had on screen, on this PC. A book you haven't read from yet starts at its first paragraph. To start somewhere else, right-click a paragraph in the manuscript and choose Open Teleprompter Here."),
            T("Settings › Teleprompter chooses the key that moves on (Space, Down Arrow, Page Down or Enter; → always works too) and the text size. A− and A+ on the Teleprompter page change the size too."),
            T("Foot pedals work too: set the pedal to send the key the Teleprompter moves on with."),
            T("When you've read the whole book, right-click Teleprompter in the Steps bar and choose Mark Done."),
            Tip("InnerVoice doesn't record sound: record in your own audio app while you read."),
        ],

        HelpTopic.RevisedManuscripts =>
        [
            T("When the author sends a new draft, choose File › Import Revised Manuscript… and pick it. InnerVoice compares it with the manuscript in your project, paragraph by paragraph."),
            H("What is kept"),
            T("Every paragraph that didn't change keeps its tags, your corrections and notes, even when paragraphs were added, removed or moved around it. In a lightly edited paragraph, lines whose text still matches keep their character."),
            H("Review what changed"),
            T("The Revisions page lists the added, changed and removed paragraphs; click one to go to it. Changed paragraphs go through Tag Speakers again, and your own assignments still win."),
        ],

        HelpTopic.ChoosingModel =>
        [
            T("The model is the AI that reads your book. Most run on your PC and are downloaded once; Claude runs in the cloud. Settings › AI (or Book › Choose a Model…) lists the models, each with a note on how well it fits this PC."),
            H("Memory and graphics card"),
            T("The advice comes from your PC's memory and its graphics card. A model that fits in the graphics card's memory runs fastest; one that doesn't runs partly or fully on the processor, which is much slower. On a PC without a graphics card the AI can use, the smaller models finish much sooner. A model too big for your memory makes everything slow."),
            Keys(
                ("Best for this PC", "The one to choose if you're not sure"),
                ("Good fit", "Runs well here"),
                ("Tight fit", "Runs, but slowly or with little memory to spare"),
                ("Too large", "Needs more memory than this PC can give it")),
            Keys(
                ("Download", "Get the model; Cancel stops the download"),
                ("Use This Model", "Tag with it from now on"),
                ("Delete…", "Free the disk space it takes; you can download it again later"),
                ("Use a GGUF File…", "Use a model file you already have"),
                ("Change Models Folder…", "Keep models somewhere else, like a drive with more room; they take several gigabytes")),
            H("Claude (cloud)"),
            T("Claude is Anthropic's AI, offered after the downloaded models and marked Cloud. It is the most accurate choice and needs no download, but it runs on Anthropic's computers: each passage of the book is sent to Anthropic while Tag Speakers or Build Summaries runs, and Anthropic charges for what you use."),
            Keys(
                ("Set Up…", "Read the cloud warning, paste an API key from the Claude Console and agree; the key is kept in Windows Credential Manager"),
                ("Model menu", "Pick Claude Fable, Opus, Sonnet or Haiku; new models appear on their own"),
                ("Use Claude", "Tag and summarize with Claude from now on"),
                ("Disconnect", "Forget the key; nothing more is sent")),
            T("Each book asks once before anything is sent: \"Send this book to Claude?\" Until you say yes, nothing from that book leaves your PC. To take the yes back, choose Book › Stop Sending This Book to Claude. Anthropic doesn't train on API data unless your organization has opted in; the setup window links to how to check."),
            T("After Tag Speakers runs with Claude, Review Claude's Changes lists every line it gave a new speaker. Everything starts checked; uncheck what you don't want and click Undo Unchecked, and those lines go back to how they were, in one Undo step. Keep All keeps everything. Lines you assigned yourself are never changed. To keep Claude's changes straight away instead, turn off Book › Review Claude's Changes for that book."),
            H("What it costs"),
            T("Before Tag Speakers, Build Summaries or any of Claude's own features sends a book, InnerVoice shows the estimated cost for the model you picked, worked out on your PC from the book's length (nothing is sent to count it). It's an estimate: your bill may differ. A model InnerVoice doesn't know the price of shows tokens only. Tick \"Don't show the estimate again\" to skip it next time."),
            H("Claude's own features"),
            T("Book › Claude (Cloud) has features that only Claude does. Each one sends text to Anthropic, so each asks first, and each is one Undo step. They use the Claude model you picked, or Claude Sonnet when a downloaded model is chosen."),
            Keys(
                ("Clean Up Text…", "Finds what importing left behind: scanning mistakes, words broken by a hyphen, page numbers and headers, footnote markers, spaced-out letters. Only the project changes; your manuscript file never does, and lines you assigned keep their speakers"),
                ("Write Chapter Recaps…", "A short \"Previously…\" for each chapter; right-click the chapter's text and choose Chapter Recap… to read or edit it. Edit it, and it's yours. Narrator Scripts include it")),
            T("With Book › Review Claude's Changes on, fixes and recaps wait in a list to keep or leave out; nothing changes until you apply them."),
            Tip("On the Mac, Book › Claude also suggests delivery hints for ElevenLabs voices and spellings for the pronunciation guide. Those need AI voices, which InnerVoice for Windows doesn't have; a project's hints and spellings are kept when you open it here."),
            Tip("With a downloaded model, your manuscript never leaves this PC. The model download is the only time the AI goes online."),
        ],

        HelpTopic.KeyboardShortcuts =>
        [
            H("Projects"),
            Keys(
                ("Ctrl+N", "New from Manuscript…"),
                ("Ctrl+O", "Open…"),
                ("Ctrl+S", "Save"),
                ("Ctrl+Shift+S", "Save As…"),
                ("Ctrl+W", "Close"),
                ("Ctrl+,", "Settings…")),
            H("Book"),
            Keys(
                ("Ctrl+Shift+T", "Tag Speakers"),
                ("Ctrl+Shift+B", "Build Summaries"),
                ("Ctrl+.", "Stop"),
                ("Ctrl+Down", "Next Line That Needs You"),
                ("Ctrl+Up", "Previous Line That Needs You")),
            H("Pages"),
            Keys(
                ("Ctrl+1", "Manuscript"),
                ("Ctrl+2", "Cast"),
                ("Ctrl+3", "Index"),
                ("Ctrl+4", "Teleprompter")),
            H("Manuscript"),
            Keys(
                ("Enter", "Assign To for the selected line"),
                ("1 – 9", "In Assign To: pick a character"),
                ("0", "In Assign To: Narration"),
                ("Esc", "Close Assign To"),
                ("Ctrl+C", "Copy"),
                ("Delete", "Delete Text…"),
                ("Ctrl+F", "Find"),
                ("F3", "Find Next"),
                ("Shift+F3", "Find Previous"),
                ("Ctrl+Z", "Undo"),
                ("Ctrl+Y", "Redo"),
                ("Ctrl+=", "Bigger Text"),
                ("Ctrl+-", "Smaller Text")),
            H("Teleprompter"),
            Keys(
                ("Space or →", "Next paragraph"),
                ("←", "Previous paragraph")),
        ],

        HelpTopic.Settings =>
        [
            T("Choose File › Settings… (Ctrl+,)."),
            Keys(
                ("General", "Appearance (System, Light or Dark), the reading text size, typeface and line spacing, what the margin shows, and the Steps bar"),
                ("AI", "The model"),
                ("Teleprompter", "The key that moves on and the Teleprompter's text size"),
                ("Activity", "What InnerVoice has done for you, in numbers")),
            H("Activity"),
            T("Settings › Activity counts what InnerVoice did across all your books: lines tagged by the rules and the AI, guesses you kept or changed, lines you assigned, summary facts and lines carried to a revision. Only numbers are kept; nothing from your books is counted or sent."),
            T("Settings are kept on this PC. Projects are files you keep where you like."),
        ],

        _ =>
        [
            T("InnerVoice comes from the Microsoft Store, which installs new versions by itself. To look for one right away, open the Microsoft Store, go to Library and click Get updates."),
            T("The version you're running is in Help › About InnerVoice."),
        ],
    };
}
