namespace InnerVoice.App.Help;

// The pages of Help, ported from the Mac's System/HelpTopics.swift with what the Windows app
// has (Narration Tools, and AI voices with Windows Voices, Pocket TTS, Orpheus and ElevenLabs). Words follow docs/GLOSSARY.md. When a feature or shortcut changes, change its
// page here too.

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
    Voices,
    Index,
    Pronunciations,
    NarratorsAndScripts,
    Teleprompter,
    RevisedManuscripts,
    ChoosingModel,
    CommandLine,
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
        HelpTopic.Voices => "Voices & Listening",
        HelpTopic.Index => "Index",
        HelpTopic.Pronunciations => "Pronunciations",
        HelpTopic.NarratorsAndScripts => "Narrators & Scripts",
        HelpTopic.Teleprompter => "Teleprompter",
        HelpTopic.RevisedManuscripts => "Revised Manuscripts",
        HelpTopic.ChoosingModel => "Choosing a Model",
        HelpTopic.CommandLine => "Command Line & Links",
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
            T("The sidebar has the book's pages, then its chapters and the main characters. Ctrl+1 to Ctrl+6 go straight to Manuscript, Cast, Index, Teleprompter, Voices and Pronunciations; Review is in the sidebar and the View menu."),
            Keys(
                ("Manuscript", "Read the colored book and fix lines"),
                ("Review", "Every line that needs you"),
                ("Cast", "Every character, with a summary, line counts and narrator"),
                ("Index", "Each character's lines by chapter, with links"),
                ("Teleprompter", "The teleprompter for the booth"),
                ("Voices", "A voice for the narrator and every character, to hear the book read aloud"),
                ("Revisions", "What changed, after File › Import Revised Manuscript…")),
            H("Saving"),
            T("A project is one .innervoice file. Save it once with File › Save (Ctrl+S); from then on InnerVoice saves by itself every 30 seconds while it has changes. File › Save As… (Ctrl+Shift+S) saves a copy under a new name or in another folder."),
            H("A book on your PC and your Mac"),
            T("The same .innervoice file opens in InnerVoice on a Mac, so you can tag a book on one computer and read it in the booth on the other. Keep it in OneDrive, Dropbox or any synced folder, or carry it on a USB stick."),
            T("There's no merging: the last save wins. If the file changed on another computer since you opened it, saving asks before replacing it."),
            T("Voices cast on one computer are kept on the other. A Mac book's Apple voices speak here as the closest Windows voice, named \"(Mac voice)\" on the Voices page; choose another to replace one. Pocket TTS and Orpheus voices speak here as they do on the Mac once those engines are downloaded in Settings › Voices."),
            H("What is this project for?"),
            T("A new project asks once, and Book › Project Type… changes it later; nothing is lost when you do."),
            Keys(
                ("Narration Tools", "You'll narrate the book yourself: who's speaking on every line, character summaries, the Index, the Teleprompter and Narrator Scripts."),
                ("Produce with AI Voices", "You'll make the audiobook with AI voices: give every character a voice and a tone, hear a table read, and export the audio. The Steps bar ends with Voices and Listen.")),
            Tip("Your manuscript stays on this PC. The only things InnerVoice downloads are the AI model you choose, once. The exceptions are Claude and ElevenLabs, which you have to set up and allow for each book: see Choosing a Model and Voices & Listening."),
        ],

        HelpTopic.OpeningManuscript =>
        [
            T("InnerVoice opens Word (.docx), PDF, RTF, plain text, Markdown, OpenDocument (.odt), Pages, EPUB and HTML. It splits the manuscript into chapters at headings such as \"Chapter 1\", \"CHAPTER ONE\", \"Prologue\" and \"Part II\", and at Word heading styles."),
            H("Pages"),
            T("A Pages file from a Mac opens when it was saved with a preview (Pages does this unless \"Include preview in document\" is turned off): InnerVoice reads the text from that preview, so italics and heading styles may not come through. If InnerVoice can't read it, open it in Pages on a Mac, choose File › Export To › Word, and open the Word file instead."),
            H("Older Word files"),
            T("An older Word .doc file: open it in Word, choose File › Save As, and save it as a Word Document (.docx)."),
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

        HelpTopic.Voices =>
        [
            T("InnerVoice can read the book aloud with a different voice for each character, so you can hear a scene, check who says what, or make the audiobook itself. Windows Voices run on your PC and send nothing anywhere. ElevenLabs, if you set it up, runs in the cloud (see below). Voices are part of every project type but Narration Tools (Book › Project Type…)."),
            H("Voices for the cast"),
            T("After Tag Speakers and Build Summaries, InnerVoice casts a voice for each character from what the book says about them: gender, age and accent from the summary, or a title like Mrs. or a \"she said\" when the summary is quiet. Main characters choose first, and two characters who share a scene get different voices. The narrator gets the best voice of all."),
            T("Open the Voices page (Ctrl+5) to hear and change them. Click a character, then Play any voice: it says one of that character's own lines. Click Use to give them that voice, or Confirm to keep the one casting picked. Speed and pitch make a Windows voice older, younger or more distinct. Cast Voices Again chooses every voice again; voices you chose stay."),
            T("The Narrator is always first. The narrator's voice reads the narration, every line nobody is cast for, and letters, signs and other written text (Settings › Voices can give written text to the character who wrote it). Give a minor character Same as Narrator and their lines are read in the narrator's voice. In a first-person book the narration uses the voice of the character who tells the story, unless you choose a separate narrator voice."),
            T("A red dot means casting wasn't sure, usually because the book hasn't said whether the character is a woman or a man. Choose the gender on the Cast page, or listen and Confirm."),
            T("Find a character with the field at the top of the Cast page (above the cards) or the Voices page (above Casting Style), or press Ctrl+F there: type a name, nickname or initials, or words from their notes, Voice Notes, or the voice they're cast with. It filters every casting style; the Narrator always stays in the Voices list. Esc or the ✕ clears it. Under All Voices, Find a voice looks through each voice's name, accent and description."),
            H("Casting Style"),
            T("Choose how the book shares its voices at the top of the Voices page: One Voice (the narrator's voice plays everyone, a little lower for men and higher for women; adjust each character's Shade, or give them a voice of their own), Narrator + Two Voices (a narrator, one voice for every man, one for every woman, and one for anyone else), or Full Cast (a voice per character). Changing it keeps every voice."),
            T("A shade changes the narrator's pitch and pace for a character. Pocket TTS shifts the short clip it copies the narrator's voice from, so a shaded character still sounds like the narrator; the other engines shift the finished line."),
            H("How each line is said"),
            T("Every line also gets a tone: neutral, excited, confused, sad, angry, whispered, scared, tender or sarcastic. \"She snapped\" is angry and \"he whispered\" is a whisper; Tag Speakers asks the AI about the rest, with the paragraphs around each line. Change a tone by right-clicking a line and choosing Tone; Tag Speakers never changes a tone you set."),
            T("To clear tones in bulk, choose Book › Remove Tones…. Pick the tones Tag Speakers set, the ones you set yourself, or both. Lines without a tone are read evenly, and getting tones back means running Tag Speakers again (tones you set yourself can't be tagged back). Edit › Undo brings them back while the book is open."),
            T("ElevenLabs acts each tone, and a delivery hint (\"barely audible\", from Delivery Hint… on the same menu) goes with it. Windows Voices can't act: a tone only changes their speed, pitch and loudness a little."),
            H("Listening"),
            Keys(
                ("Ctrl+Space", "Play the selected lines in their characters' voices"),
                ("Ctrl+Shift+Space", "Play from Here: a table read to the end of the book"),
                ("Ctrl+Shift+.", "Stop playing")),
            T("Right-click anywhere in the manuscript and choose Play from Here to start the table read at the line you clicked (or the top of the paragraph, if you didn't click a line). While it plays, Stop Playback sits just above it."),
            T("Play from Here follows along in the manuscript. Settings › Voices decides whether narration is read too (a full table read) or only the dialogue, and whether the manuscript follows along."),
            H("Pauses and speed"),
            T("Settings › Voices › Pauses sets each gap in seconds: between sentences, between a line and the narration around it, between paragraphs, after a chapter title, at a scene break and between chapters. Tight, Natural and Relaxed are starting points. With sentence pauses on, each sentence is spoken on its own so that gap is exact; turn it off to let each voice pace its own sentences."),
            T("Click Try It to hear a passage from the front book; move a slider and the next pause changes, with nothing to remake. Playback speed (0.5× to 2×) speeds up Play, pauses included, without changing the voices' pitch. Export Audio always saves at normal speed, with your pauses."),
            H("Exporting audio"),
            T("File › Export Audio… saves all the voices or one character's lines as M4A or WAV files, one file per chapter or all in one. Check the chapters to include: Select All checks or clears every one, and Shift-click checks a run of chapters. When chapters have recaps, Say chapter recaps reads each one (\"Previously…\") after the chapter's title. Separate per voice saves a track for each voice, lined up so they play together. Each file is finished on this PC and then moved into the folder you chose, so exporting into OneDrive or Dropbox works too."),
            T("M4A needs Windows' Media Foundation, which every edition has except Windows N without the Media Feature Pack; there, export WAV."),
            H("EPUB with Audio"),
            T("File › Export EPUB with Audio… saves the book as an EPUB with the voices built in. Open it in a reading app that plays read-along books (EPUB 3 Media Overlays), such as Thorium Reader, and click Play: each sentence is highlighted as it's read. Apps that don't play them show the book as ordinary text. Check the chapters to include, as in Export Audio."),
            Tip("Reading the whole book takes as long as Export Audio does. Lines already played or exported come from the audio cache, so they're quick."),
            H("Engines"),
            T("Choose the engine for each project from the menu at the top of the Voices page. Each engine keeps its own casting, so switching back and forth loses nothing."),
            Keys(
                ("Windows Voices", "Come with Windows and work right away. They can't act, so a tone only changes speed and pitch a little. Add more voices in Windows Settings › Time & language › Speech (Get More Voices… in Settings › Voices), then click Reload."),
                ("ElevenLabs (Cloud)", "Natural voices from your ElevenLabs account, made on ElevenLabs' servers with its eleven_v4 model, which acts each tone and any delivery hint. Unlike Windows Voices it sends text off your PC: every line an ElevenLabs voice speaks goes to ElevenLabs, and uses your plan's credits."),
                ("Pocket TTS", "Kyutai's small, fast voice model. Nine stock voices (Orpheus's eight young American voices, five women and three men, plus Alba, Pocket TTS's own), and any voice you clone on this PC. Every line is spoken from a short clip of the voice, so it stays the same voice from line to line. It has no setting for feeling: a tone changes how freely it reads (livelier for angry or excited, steadier for sad or tender) and a little of its pace and pitch, so tones are milder than with Orpheus or ElevenLabs. It can't whisper: a whispered line is said softly and slowly. Casting picks a stock voice by gender and age, and makes it higher for a child or lower and slower for an older character. Pocket TTS can't make a voice from a description. Runs on this PC; a free download of 470 MB, and about 2 GB of memory while it speaks."),
                ("Orpheus", "Eight natural American voices that act from the words. A sigh, laugh, gasp or groan in the speech tag (\"she sighed\") is performed too. Runs on this PC; a free download of 2.5 GB.")),
            T("Choose Mix Engines (when two or more engines are on) for every voice from every engine in one list; each character can sound like a different engine. Don't use an engine? Turn it off in Settings › Voices; a project set to it uses an engine that's on, and keeps its casting."),
            H("Pocket TTS and Orpheus on this PC"),
            T("Download them once from Settings › Voices, or from Clone a Voice. Downloading is the only time they use the internet; your book never leaves the PC. A download that stops picks up where it left off, and every file is checked before it's used. The first line takes a few seconds while the engine loads."),
            T("How fast they speak depends on your PC. Pocket TTS is small: on the processor of most PCs with 8 GB of memory it makes a line faster than it plays. Orpheus is a large model: with a DirectX 12 graphics card that has 8 GB or more of its own video memory (an NVIDIA GeForce RTX 3060 or AMD Radeon RX 6700 XT, or better) and 16 GB of memory, its lines come about as fast as they play, or faster; on the processor it takes about eight times longer or more. With 16 GB of memory or more, Pocket TTS and Orpheus stay loaded together, so lines play without waiting for a model to load."),
            T("Books made with Qwen3-TTS, the engine Pocket TTS replaced, switch to Pocket TTS when you open them: ready-made voices become the nearest stock voice, designed voices the stock voice casting picks, and voices cloned on this PC keep their recording. Your own choices stay yours. If Settings › Voices shows Old Voice Files, Remove Old Voice Files… moves Qwen3-TTS's models to the Recycle Bin."),
            T("Settings › Voices shows what InnerVoice found on this PC (graphics card, video memory, memory and processor) and says plainly when an engine may be too slow here. It asks before downloading one on such a PC, and the first time you choose it for a book. Windows Voices are quick on any PC, and ElevenLabs makes its voices in the cloud."),
            Tip("Lines already made play at once from the audio cache, so a slow PC can make a chapter's lines ahead of time with Export Audio, then play them freely."),
            H("ElevenLabs and your manuscript"),
            T("ElevenLabs is, with Claude, the only part of InnerVoice that sends your book anywhere. Set it up in Settings › Voices: create an API key at elevenlabs.io, paste it, check it, and tick that you understand your text goes to ElevenLabs. The key is kept in Windows Credential Manager."),
            T("Then each book asks before its text is sent for the first time, when you choose ElevenLabs (or include it in Mix Engines) on its Voices page. A table read or an exported book sends the whole manuscript, so allow it only for books you have the right to share. Stop Sending This Book on the Voices page (or the Book menu) ends it for that book, and Disconnect in Settings › Voices forgets the key. The Cloud Usage button shows the characters sent and your plan's credits."),
            H("Voice Notes and the AI voices"),
            T("Only ElevenLabs can make a voice from a description. It gets each character's Voice Notes when you ask it to: your own notes first, then the voice brief and \"From the book\". The voices on your PC (Pocket TTS, Orpheus and Windows Voices) can't take a description, so editing Voice Notes doesn't change how they sound; casting reads the notes for gender, age, pitch and pace when it chooses a voice."),
            T("With ElevenLabs, click Design Voice under Design a Voice from Voice Notes on a character's Voices page. You see exactly what will be sent; ElevenLabs designs a voice from it, adds it to your ElevenLabs voices, and gives it to the character. It uses credits and one of your plan's voice slots, so it only happens when you click. For a voice your PC can speak that matches a description, clone a voice from a recording of someone who sounds right (below)."),
            H("Books in a series"),
            T("Give the characters the voices they had in an earlier book: on the Voices page, click Use Voices from Another Project… (or choose Book › Use Voices from Another Project…), and pick the earlier book's project. Nothing changes yet."),
            T("Each character with a voice in the earlier book is listed, main characters first, next to who InnerVoice thinks they are in this book; a ? means only the first name matches, so check it. Choose anyone from the menu, or Not in this book. Use These Voices gives the matched characters every voice they had, on every engine, as your own choices, so casting never changes them; one Undo takes it all back. The narrator's voice, their Voice Notes and the earlier book's engine can come along too. ElevenLabs voices still need this book's own permission."),
            H("Clone a voice"),
            T("You can make a voice from recordings of a real person, such as yourself or an actor who has agreed to it, and give it to any character. Choose Book › Clone a Voice… (or click Clone a Voice… on the Voices page), then choose where the voice is made: On This PC or ElevenLabs (Cloud)."),
            T("Name the voice, choose who in this book it's for (or No One Yet), then click Record… to record with your PC's microphone or Add Files… to add recordings (WAV, MP3 or M4A). Use one person speaking clearly, with no music, echo or other voices. Tick that you have the right and the consent of the person to clone their voice, then click Clone Voice."),
            H("On this PC"),
            T("Pocket TTS (a free download in Settings › Voices) learns the voice from the first 15 seconds of speech in your recordings; 15 to 30 seconds is plenty. InnerVoice finds the words in that clip with Windows' own speech recognizer, on this PC, and shows them under The Clip, so you can check it's the part you want; fix any word that's wrong, or type them if Windows has no English speech recognizer. Play Clip lets you hear the part that's used."),
            T("Nothing leaves your PC and it's free. The voice is listed first among the Pocket TTS voices in every project, marked Cloned, and it's heard in books that speak with Pocket TTS or Mix Engines. Rename or delete it in Settings › Voices › Voices Cloned on This PC. Casting never chooses a cloned voice on its own."),
            H("With ElevenLabs"),
            T("One to two minutes of one person speaking clearly works best; at least 10 seconds is needed. The recordings and the name go to ElevenLabs; no text from your book is sent. The voice is saved in your ElevenLabs account and listed first among the ElevenLabs voices, marked Cloned. Cloning needs a paid ElevenLabs plan, and each cloned voice takes one of your plan's voice slots. Play, rename or delete cloned voices in Settings › Voices › Cloned Voices (Play reads a random passage from the front book, or the sample book when that book hasn’t allowed ElevenLabs). Voices cloned at elevenlabs.io show up there too; click Refresh to fetch them."),
            Tip("Made audio is kept in a cache, so a line plays at once the second time. Settings › Voices shows its size and can clear it."),
            H("Credits"),
            T(InnerVoice.App.Windows.Credits.HelpText),
        ],

        HelpTopic.Index =>
        [
            T("Index lists each character's lines by chapter. Click a line to go to it in the manuscript."),
            H("Show in Index"),
            T("Each character has a Show in Index switch. Main characters with a large share of the lines start hidden, so the index stays useful for finding the smaller parts; turn them on whenever you like. Show All and Hide All switch every character at once."),
            Tip("Ctrl+F (or Ctrl+Shift+F, or Edit › Find in Manuscript…) opens the find bar (on the Cast and Voices pages, Ctrl+F finds a character instead): it ignores capitals and accents, and straight and curly quotes match each other. Enter or F3 goes to the next match, Shift+Enter or Shift+F3 to the one before, and Esc closes the bar."),
        ],

        HelpTopic.Pronunciations =>
        [
            T("AI voices guess how to say a word from its spelling, and names and made-up words often come out wrong. Pronunciations is where you correct them: what you type in Say It As is swapped in for the word before any voice speaks it, so every voice says it your way from then on. Audio you already exported doesn't change until you export again."),
            T("InnerVoice also lists the names, places and unusual words it found, with how often each one appears. To fix a word while you read, right-click it in the Manuscript and choose Fix Pronunciation. It's part of Produce with AI Voices projects; a narrator says names in their own voice."),
            T("One entry covers the word's plural and possessive too: an entry for Barnaby also fixes Barnaby's, Barnaby’s and the Barnabys. Capitals don't matter unless you check Match Case."),
            Keys(
                ("Say It As", "How to say it, in your own spelling, with capitals for the stressed part: \"BAR-nuh-bee\""),
                ("Play button", "Hear it in the narrator's voice, in the sentence where it first appears"),
                ("Match Case", "Only fix it when the capitals match, so Will the name doesn't change will"),
                ("Note", "Anything else: where it comes from, what the author said"),
                ("Confirmed", "Mark it once the author has confirmed it"),
                ("Suggest Spellings", "Cloud: Claude suggests a Say It As for each term without one. Only the terms and the sentence each first appears in are sent; you check each spelling you want to use")),
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
            T("While Claude is the chosen model, or this book uses ElevenLabs voices, a Cloud Usage button (a bar chart) appears in the toolbar. It shows, for each cloud model, the tokens sent and received (characters for ElevenLabs), the number of requests and the cost at the list price, with charts by day. Switch between This Book and All Books. ElevenLabs also shows your plan's credits used and left; for Claude, your account's remaining credit is in the Claude Console. Only these numbers are kept, on this PC, never any text from your books."),
            H("Claude's own features"),
            T("Book › Claude (Cloud) has four features that only Claude does. Each one sends text to Anthropic, so each asks first, and each is one Undo step. They use the Claude model you picked, or Claude Sonnet when a downloaded model is chosen."),
            Keys(
                ("Clean Up Text…", "Finds what importing left behind: scanning mistakes, words broken by a hyphen, page numbers and headers, footnote markers, spaced-out letters. Only the project changes; your manuscript file never does, and lines you assigned keep their speakers"),
                ("Write Chapter Recaps…", "A short \"Previously…\" for each chapter; right-click the chapter's text and choose Chapter Recap… to read or edit it. Edit it, and it's yours. Narrator Scripts include it"),
                ("Suggest Delivery Hints…", "How each line is performed beyond its tone (\"barely audible\", \"through gritted teeth\"). Hints go to ElevenLabs only, as audio tags; right-click a line and choose Delivery Hint… to edit one. Produce with AI Voices books only"),
                ("Suggest Spellings…", "A Say It As for each pronunciation without one; only the terms and the sentence each first appears in are sent. You confirm each one. Produce with AI Voices books only")),
            T("With Book › Review Claude's Changes on, fixes, recaps and hints wait in a list to keep or leave out; nothing changes until you apply them. Suggested spellings always wait for you."),
            Tip("With a downloaded model, your manuscript never leaves this PC. The model download is the only time the AI goes online."),
        ],

        HelpTopic.CommandLine =>
        [
            T("AI assistants such as Claude Code, and your own scripts, can work in InnerVoice with the innervoice command: see the open projects, chapters, lines and cast, assign lines, fix line kinds, rename, recolor and merge characters, run Tag Speakers and Build Summaries, set pronunciations, choose the model, and export Narrator Scripts, the tagged manuscript or the cast list. Changes show in the project's window at once, and Edit › Undo takes them back."),
            Keys(("innervoice help", "Every command, with an example"),
                 ("innervoice status", "The open projects, the model, and what the AI is doing"),
                 ("innervoice lines list --needs-you", "The lines that need you, with their ids"),
                 ("innervoice lines assign <id> --to Mara", "Give a line to a character"),
                 ("innervoice mcp", "A server for Claude Desktop and other apps that use MCP")),
            T("The Microsoft Store version puts innervoice on your PATH. A copy you run from a folder keeps it in its cli folder: add that folder to PATH, or type the full path. If InnerVoice isn't open, the command opens it in the background. Add --json to get JSON for scripts."),
            T("Merging characters, clearing lines back to Who's Speaking? and downloading a model only say what they would do until you add --yes. Tones and voices aren't in InnerVoice for Windows yet; those commands say so."),
            H("Links"),
            T("An innervoice:// link opens InnerVoice and shows something, in the open project or the one you name: innervoice://open/cast?character=Mara opens Mara on the Cast page, innervoice://open/review the Review page, innervoice://lines/reveal/3f2a9c1e a line in the manuscript, and innervoice://projects/open?path=C:\\Books\\Harbor.innervoice a project. Links only show things: they never change your book, save, export, run the AI or download anything."),
            Tip("Everything happens on this PC: the command and links talk only to InnerVoice, never to the internet."),
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
                ("Ctrl+4", "Teleprompter"),
                ("Ctrl+5", "Voices"),
                ("Ctrl+6", "Pronunciations")),
            H("Listening"),
            Keys(
                ("Ctrl+Space", "Play Line: the selected lines in their characters' voices"),
                ("Ctrl+Shift+Space", "Play from Here: a table read to the end of the book"),
                ("Ctrl+Shift+.", "Stop Playing")),
            H("Manuscript"),
            Keys(
                ("Enter", "Assign To for the selected line"),
                ("1 – 9", "In Assign To: pick a character"),
                ("0", "In Assign To: Narration"),
                ("Esc", "Close Assign To"),
                ("Ctrl+C", "Copy"),
                ("Delete", "Delete Text…"),
                ("Ctrl+F or Ctrl+Shift+F", "Find in Manuscript"),
                ("Ctrl+F on Cast or Voices", "Find a character: the field at the top of the page filters by name, notes or voice; Esc clears it"),
                ("Enter or F3", "Next match"),
                ("Shift+Enter or Shift+F3", "Previous match"),
                ("Esc", "Close the find bar"),
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
                ("Voices", "The voice engines, ElevenLabs (Cloud), cloned voices, pauses and playback speed, table reads, written text, and the saved audio"),
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
