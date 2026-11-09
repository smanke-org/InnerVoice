import Foundation

/// The pages of Help, in sidebar order. Words follow docs/GLOSSARY.md: project, manuscript,
/// line, character, Assign, Tag Speakers, Who's Speaking?, Unsure. When a feature or shortcut
/// changes, change its page here too (and ShortcutCatalog for shortcuts).
enum HelpTopic: String, CaseIterable, Identifiable, Hashable {
    case gettingStarted
    case openingManuscript
    case taggingSpeakers
    case fixingLines
    case colors
    case castAndSummaries
    case indexAndRehearse
    case narratorsAndScripts
    case pronunciations
    case record
    case revisedManuscripts
    case choosingModel
    case keyboardShortcuts
    case settings
    case updates

    var id: String { rawValue }

    var title: String {
        switch self {
        case .gettingStarted: "Getting Started"
        case .openingManuscript: "Opening a Manuscript"
        case .taggingSpeakers: "Tagging Speakers"
        case .fixingLines: "Fixing Lines"
        case .colors: "Colors"
        case .castAndSummaries: "Cast & Summaries"
        case .indexAndRehearse: "Index & Rehearse"
        case .narratorsAndScripts: "Narrators & Scripts"
        case .pronunciations: "Pronunciations"
        case .record: "Record"
        case .revisedManuscripts: "Revised Manuscripts"
        case .choosingModel: "Choosing a Model"
        case .keyboardShortcuts: "Keyboard Shortcuts"
        case .settings: "Settings & Sync"
        case .updates: "Updates"
        }
    }

    var symbol: String {
        switch self {
        case .gettingStarted: "sparkles"
        case .openingManuscript: "doc.text"
        case .taggingSpeakers: "quote.bubble"
        case .fixingLines: "pencil.line"
        case .colors: "paintpalette"
        case .castAndSummaries: "person.3"
        case .indexAndRehearse: "list.bullet.indent"
        case .narratorsAndScripts: "mic"
        case .pronunciations: "character.bubble"
        case .record: "record.circle"
        case .revisedManuscripts: "arrow.triangle.2.circlepath.doc.on.clipboard"
        case .choosingModel: "cpu"
        case .keyboardShortcuts: "keyboard"
        case .settings: "gearshape"
        case .updates: "arrow.down.circle"
        }
    }

    var blocks: [HelpBlock] {
        switch self {
        case .gettingStarted: [
            .text("Inner Voice reads your audiobook manuscript with an AI that runs on your Mac, colors every spoken line by the character who says it, and gives you the cast, the pronunciations and a teleprompter for the booth."),
            .heading("Your first project"),
            .text("Choose File › New from Manuscript… (⌘N), or drop a manuscript on the Start window. Inner Voice makes a project, a .innervoice file with its own copy of the manuscript, and finds every quoted line right away. Lines it hasn't tagged yet show as Who's Speaking?."),
            .text("Then click Tag Speakers (⇧⌘T). The on-device AI reads the book chapter by chapter and assigns each line to a character. You can keep reading while it works, and Stop keeps what's done."),
            .heading("The project window"),
            .text("The sidebar has {pages}. ⌘1 to ⌘6 go straight to each one, in that order."),
            .keys([
                ("Manuscript", "Read the colored book and fix lines"),
                ("Cast", "Every character, with a summary, line counts and narrator"),
                ("Index", "Each character's lines by chapter, with links"),
                ("Pronunciations", "Names and made-up words, how to say them, and samples"),
                ("Record", "The teleprompter for the booth"),
                ("Retakes", "Every paragraph marked to read again, with your notes"),
            ]),
            .tip("Your manuscript never leaves this Mac. The only download is the AI model itself."),
            .tip("Quitting asks first. Turn off \"Ask before quitting\" in Settings › General, or tick \"Don't ask again\" when it asks."),
        ]

        case .openingManuscript: [
            .text("Inner Voice opens Word (.docx and .doc), PDF, RTF, plain text, Markdown, OpenDocument (.odt), Pages, EPUB and HTML. It splits the manuscript into chapters at headings such as \"Chapter 1\", \"CHAPTER ONE\", \"Prologue\" and \"Part II\", and at Word heading styles."),
            .heading("Google Docs"),
            .text("Download the document as Word first: in Google Docs, choose File › Download › Microsoft Word (.docx), then open that file in Inner Voice."),
            .heading("Pages"),
            .text("Inner Voice reads the preview Pages saves inside the document. If a Pages file says it can't be read, open it in Pages, choose File › Export To › Word, and open the Word file instead."),
            .heading("PDF"),
            .text("Running headers, footers and page numbers are left out, and paragraphs split across pages are joined again. A scanned PDF has pictures of pages and no text: export it with text from the app that made it, or run it through TextLens first."),
            .tip("Inner Voice keeps its own copy of the manuscript in the project, so moving or changing the original file doesn't change your project. To bring in a new draft, see Revised Manuscripts."),
        ]

        case .taggingSpeakers: [
            .text("Tag Speakers (⇧⌘T, or Book › Tag Speakers) uses the on-device AI to decide who speaks each line. Chapters fill with color as it goes, and the toolbar shows which chapter it's on."),
            .heading("How it decides"),
            .text("Plain rules come first: \"said Tom\", \"Tom asked\" and a pronoun after a named speaker settle most lines with no AI at all. In a back-and-forth with no tags, the lines alternate between the two characters. The model reads the rest, and any name it finds that isn't in the cast becomes a new character."),
            .text("Lines you assigned yourself are never changed by Tag Speakers. Stop (⌘.) keeps everything done so far, and Tag Speakers carries on from there next time. Undo Tag Speakers takes the whole run back."),
            .heading("Privacy"),
            .text("The AI runs on your Mac. Your manuscript, your cast and your notes never leave it, and Inner Voice works offline. The only thing downloaded is the model you choose, once."),
            .tip("When it's done, a banner says how many lines need you. Click Show Lines That Need You, or press ⌘↓ to go to the next one."),
        ]

        case .fixingLines: [
            .text("Click a line to assign it. The Assign To popover lists the characters with their colors, the ones used most in this chapter first. Type to filter the list, or press a number key to pick one of the first nine. Narration (not spoken) sits at the top of the list for text no one says out loud."),
            .keys([
                ("Click a line", "Open Assign To"),
                ("Return", "Open Assign To for the focused line"),
                ("1 – 9", "Assign to that character in the list"),
                ("Type", "Filter the characters"),
                ("New Character…", "Make a character and assign the line to them"),
                ("0 or Narration (not spoken)", "No one says it: the color goes and you read it as narration"),
                ("⌘Z", "Undo the change"),
            ]),
            .text("A line you assign is marked as yours, and a later Tag Speakers run never changes it. You can also right-click a line and choose Assign To."),
            .heading("Fix a whole exchange"),
            .text("When you fix one line in a back-and-forth that had no tags, the lines after it are probably off by one too. A banner offers to swap the rest of the exchange, up to the next tagged line or break in the narration. Click Swap or Keep."),
            .heading("Several lines at once"),
            .text("Shift-click or ⌘-click to select several lines, or select all of one character's lines in a chapter, then assign them all at once."),
            .heading("Merge characters"),
            .text("When the same person turns up under two names, say Tom and Thomas, select both in Cast and choose Merge. Every line and fact moves to one character, and the other name becomes an alias."),
            .tip("⌘↓ and ⌘↑ jump to the next and previous line that needs you: Who's Speaking? and Unsure lines."),
        ]

        case .colors: [
            .text("Every character has a color, and each line shows the color of the character who says it. New characters get the next color that's easy to tell apart. Change a character's color in Cast."),
            .heading("Highlight, Outline or Underline"),
            .text("Highlight fills each line with its color; Outline draws a line around it; Underline draws a colored line under the words and keeps the page calmest. Choose in the toolbar or the View menu (⌥⌘H, ⌥⌘O, ⌥⌘U). Each project keeps its own choice."),
            .keys([
                ("Spoken", "Highlighted, outlined or underlined in the character's color"),
                ("Thought", "Dashed"),
                ("Written", "Dotted, for letters, signs and texts"),
            ]),
            .heading("Red means it needs you"),
            .text("Red is kept for lines that need you, and no character is ever red. Who's Speaking? is a line the AI couldn't decide on. Unsure is a line it tagged with low confidence. Both show a \"?\" in the margin."),
            .heading("Speakers in the margin"),
            .text("Each line also shows who says it in the margin, so you never have to rely on color alone. Choose initials, first names or full names in Settings › Manuscript or View › Speakers in Margin; long names are shortened with \"…\". Turn them off for a project with View › Show Speakers in Margin."),
            .heading("Text size and spacing"),
            .text("Make the text bigger or smaller with the buttons in the toolbar, or View › Bigger Text (⌘+), Smaller Text (⌘−) and Actual Size (⌘0). Choose Single or Double line spacing in Settings › Manuscript or View › Line Spacing. The page keeps your place when you change either."),
        ]

        case .castAndSummaries: [
            .text("Cast shows every character as a card, with their color, line and word counts, first appearance and narrator. Open a character to read their summary."),
            .heading("Summaries"),
            .text("Build Summaries has the AI read the text around each character and fill in what the book says: age, gender, accent, voice, personality, history and relationships. Each fact has a source, such as \"Ch 3 ¶12\". Click it to see that paragraph in the manuscript."),
            .text("A fact the AI can't back up with text from the book is dropped, so every fact you see can be checked."),
            .heading("Revealed Late"),
            .text("A fact marked Revealed Late is first mentioned after the character's first line, for example \"First speaks Ch 2, revealed Ch 20\". It's worth knowing before you choose a voice in Chapter 2."),
            .heading("Editing"),
            .text("Change a character's name, aliases, color or any fact, and add your own notes and voice notes. A fact you edited is never replaced when Build Summaries runs again."),
        ]

        case .indexAndRehearse: [
            .text("Index lists each character's lines by chapter. Click a line to go to it in the manuscript."),
            .heading("Show in Index"),
            .text("Each character has a Show in Index switch. Main characters with a large share of the lines start hidden, so the index stays useful for finding the smaller parts; turn them on whenever you like."),
            .heading("Rehearse"),
            .text("Choose Rehearse for a character to step through only their lines. Each line comes with its cue, the line just before it, so you hear what you're answering."),
            .tip("⌘F finds text anywhere in the manuscript, and ⌘G goes to the next match."),
        ]

        case .narratorsAndScripts: [
            .text("For a production with more than one narrator, add the narrators in Cast and cast each character to one of them."),
            .heading("Narrator Scripts"),
            .text("Export a Narrator Script for each narrator as PDF or Word. It has only their lines, each with its cue line and its chapter and paragraph, and the summaries of the characters they voice. Send it to the narrator; they don't need Inner Voice."),
            .heading("The whole manuscript"),
            .text("You can also export the tagged manuscript as a PDF with colors, initials and a key to the characters, for printing or reading on an iPad, and the cast and pronunciation lists as PDF or CSV."),
        ]

        case .pronunciations: [
            .text("Pronunciations lists the names, places and unusual words the AI found, with how often and where each one appears."),
            .keys([
                ("Say It As", "How to say it, in your own spelling: \"SHEV-on\""),
                ("Note", "Anything else: where it comes from, what the author said"),
                ("Confirmed", "Mark it once the author has confirmed it"),
                ("Record", "Record a short sample in your own voice; click Play to hear it"),
            ]),
            .text("Inner Voice asks for the microphone only the first time you click Record here. Samples are kept in the project."),
        ]

        case .record: [
            .text("Record is a teleprompter for the booth. It shows one paragraph at a time in large type, with the speaker's name and color, and who speaks next below it."),
            .keys([
                ("Space or →", "Next paragraph; the one you read is marked Recorded"),
                ("←", "Back one paragraph"),
                ("R", "Mark a retake and add a note"),
            ]),
            .text("Foot pedals work too: most send Space or the arrow keys, which is all Record needs."),
            .text("Retakes lists every paragraph marked for a retake (a pickup, in studio terms), with your notes, so you can go back to each one. Record also shows how much of each chapter is recorded."),
            .tip("Inner Voice doesn't record the narration itself. Keep using your recording app for that; Record only keeps your place."),
        ]

        case .revisedManuscripts: [
            .text("When the author sends a new draft, choose File › Import Revised Manuscript… and pick it. Inner Voice compares it with the manuscript in your project, paragraph by paragraph."),
            .heading("What is kept"),
            .text("Every paragraph that didn't change keeps its tags, your corrections, notes, Recorded state and retakes, even when paragraphs were added, removed or moved around it. In a lightly edited paragraph, lines whose text still matches keep their character."),
            .heading("Review what changed"),
            .text("A list shows the added, changed and removed paragraphs; click one to go to it. Changed paragraphs go through Tag Speakers again, and your own assignments still win."),
        ]

        case .choosingModel: [
            .text("The model is the AI that reads your book. It runs on your Mac and is downloaded once. Settings › AI lists the models, with the one that fits your Mac best first and a note on each about why it fits or not."),
            .heading("How much memory"),
            .text("Recommendations come from your Mac's memory. With 8 GB, a small model; with 16 GB, a medium one; with 24 to 36 GB, a large one; with 48 GB or more, the most accurate. A model too big for your memory makes everything slow."),
            .keys([
                ("Download", "Get the model; you can cancel, and it picks up where it stopped"),
                ("Use This Model", "Tag with it from now on"),
                ("Remove Download", "Free the disk space it takes"),
            ]),
            .heading("No download"),
            .text("On a Mac with Apple Intelligence, Apple's on-device model is offered too. It needs no download and is quicker, but it is less accurate at deciding who speaks."),
            .tip("Your manuscript never leaves this Mac. Only the model is downloaded."),
        ]

        case .keyboardShortcuts: [
            .heading("Projects"),
            .keys([
                ("⌘N", "New from Manuscript…"),
                ("⌘O", "Open…"),
                ("⇧⌘T", "Tag Speakers"),
                ("⌘.", "Stop Tag Speakers"),
                ("⇧⌘R", "Record"),
                ("⌘1 – ⌘6", "{pages}"),
            ]),
            .heading("Manuscript"),
            .keys([
                ("⌘↓", "Next Line That Needs You"),
                ("⌘↑", "Previous Line That Needs You"),
                ("Return", "Assign To for the focused line"),
                ("1 – 9", "In Assign To: pick a character"),
                ("⌘F", "Find"),
                ("⌘G", "Find Next"),
                ("⌘Z", "Undo"),
            ]),
            .heading("Record"),
            .keys([
                ("Space or →", "Next paragraph"),
                ("←", "Previous paragraph"),
                ("R", "Retake"),
            ]),
            .heading("Everywhere"),
            .keys([
                ("⌘,", "Settings"),
                ("⌘?", "Inner Voice Help"),
            ]),
            .tip("ShortcutsLens lists Inner Voice's shortcuts too."),
        ]

        case .settings: [
            .keys([
                ("General", "Appearance, asking before quitting, the Dock and menu bar icons, iCloud and updates"),
                ("Manuscript", "The typeface, text size, line spacing, and initials, first names or full names in the margin"),
                ("AI", "The model"),
                ("Recording", "Record's font size and keys"),
            ]),
            .heading("Where Inner Voice appears"),
            .text("Show Inner Voice in the Dock, the menu bar, both, or neither. The menu bar menu has New from Manuscript…, Open…, Open Recent and Settings…. With neither, open Inner Voice again from Applications or Spotlight to get back to Settings. With the Dock icon on, its right-click menu has Settings…."),
            .text("While ToolHUD is running, Inner Voice's menu moves into ToolHUD's menu bar menu as a submenu, with Tag Speakers and Record for the project in front. To keep the icon in the menu bar, turn off \"Put in ToolHUD's menu while ToolHUD is running\" in Settings › General."),
            .heading("Sync"),
            .text("With \"Sync settings with your other Macs\" on, appearance, the accent color, asking before quitting, where Inner Voice appears and the update choices follow you to every Mac signed in to the same iCloud account. Projects never sync; they are files you keep where you like."),
        ]

        case .updates: [
            .text("Settings › General › Updates chooses what Inner Voice does when a new version is out: install it automatically while your Mac is idle and the AI isn't working, ask you first, or not check at all. Check Now looks right away."),
            .heading("What's new"),
            .text("The update window shows the notes for the latest releases, with the ones you don't have yet marked New. \"Don't remind me about this version\" is ticked to begin with: click Not Now with it ticked, and Inner Voice stops reminding you about that version (Check Now still offers it)."),
            .text("After an update that installed itself, Settings shows what changed the next time it opens. What's New… beside Check Now shows the notes any time, and About Inner Voice shows the last 10 releases."),
        ]
        }
    }
}
