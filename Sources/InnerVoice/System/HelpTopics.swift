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
    case voices
    case castingStyle
    case index
    case narratorsAndScripts
    case pronunciations
    case teleprompter
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
        case .voices: "Voices & Listening"
        case .castingStyle: "Casting Style"
        case .index: "Index"
        case .narratorsAndScripts: "Narrators & Scripts"
        case .pronunciations: "Pronunciations"
        case .teleprompter: "Teleprompter"
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
        case .voices: "person.wave.2"
        case .castingStyle: "person.3"
        case .index: "list.bullet.indent"
        case .narratorsAndScripts: "mic"
        case .pronunciations: "character.bubble"
        case .teleprompter: "text.alignleft"
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
            .text("InnerVoice reads your audiobook manuscript with an AI that runs on your Mac, colors every spoken line by the character who says it, and gives you the cast with a summary of each character, and a voice for each one: notes on how they sound when you narrate, or an AI voice when you produce with AI voices."),
            .heading("Your first project"),
            .text("Choose File › New from Manuscript… (⌘N), or drop a manuscript on the Start window. InnerVoice makes a project, a .innervoice file with its own copy of the manuscript, and finds every quoted line right away. Lines it hasn't tagged yet show as Who's Speaking?."),
            .text("Then click Tag Speakers (⇧⌘T). The on-device AI reads the book chapter by chapter and assigns each line to a character. You can keep reading while it works, and Stop keeps what's done."),
            .heading("Follow the steps"),
            .text("The steps across the top of the window show where the book stands and what comes next: Manuscript, Speakers, Review, Characters, then Voices and Listen when you produce with AI voices, or the Teleprompter when you narrate it yourself. Each step says how far along it is (\"3 to review\", \"2 need a voice\"), and the next step has a button to start it. Nothing starts until you click."),
            .text("1. Manuscript: delete anything you won't read aloud, like a copyright page or an ISBN, then click It's Ready. 2. Speakers: Tag Speakers finds who says each line. 3. Review: confirm the lines the AI wasn't sure of; typing a name that isn't in the cast adds a new character. 4. Characters: Build Summaries reads the book again for how each character sounds and writes a voice brief into their Voice Notes. 5. Voices: cast and audition a voice for every character, including the ones you added in Review. 6. Listen: play a table read and export the audio."),
            .text("Click any step to go to its page. Skip moves on without it; right-click a step to bring it back. Click × or choose View › Hide Steps to hide them, and View › Show Steps to bring them back."),
            .heading("Two kinds of project"),
            .text("A new project asks what it's for, and shows the tools for that job. Projects made with an earlier InnerVoice ask once, the next time they're opened."),
            .keys([
                ("Narration Tools", "You'll narrate the book yourself: who's speaking on every line, character summaries and voice notes, the Index, a teleprompter for the booth, and Narrator Scripts. You record in your own audio app."),
                ("Produce with AI Voices", "You'll make the audiobook with AI voices: a voice and a tone for every character, pronunciations for names, table reads and Export Audio. The setup chooses an engine, runs Tag Speakers, casts the voices and opens the Voices page."),
            ]),
            .text("Both have the Manuscript, Cast, Review and Revisions. Change a project's type in Book › Project Settings (⌥⌘,): the other type's pages and commands are hidden, never deleted, and come back when you switch again."),
            .heading("The project window"),
            .text("The sidebar has {pages}, as the project type shows them. ⌘1 to ⌘7 go straight to each one, in that order."),
            .keys([
                ("Manuscript", "Read the colored book and fix lines"),
                ("Cast", "Every character, with a summary, line counts and narrator"),
                ("Index", "Each character's lines by chapter, with links"),
                ("Pronunciations", "Names and made-up words, and how the AI voices should say them"),
                ("Teleprompter", "The book in large type for the booth, one paragraph at a time"),
                ("Review", "Lines, tones and voices that need you (⌘6)"),
                ("Voices", "A voice for each character and the narrator (⌘7)"),
            ]),
            .tip("Your manuscript stays on this Mac. The only downloads are the AI models themselves. The two exceptions are ElevenLabs voices and Claude, which you have to set up and allow for each book: see Voices & Listening and Choosing a Model."),
            .tip("Quitting asks first. Turn off \"Ask before quitting\" in Settings › General, or tick \"Don't ask again\" when it asks."),
        ]

        case .openingManuscript: [
            .text("InnerVoice opens Word (.docx and .doc), PDF, RTF, plain text, Markdown, OpenDocument (.odt), Pages, EPUB and HTML. It splits the manuscript into chapters at headings such as \"Chapter 1\", \"CHAPTER ONE\", \"Prologue\" and \"Part II\", and at Word heading styles."),
            .heading("Google Docs"),
            .text("Download the document as Word first: in Google Docs, choose File › Download › Microsoft Word (.docx), then open that file in InnerVoice."),
            .heading("Pages"),
            .text("InnerVoice reads the preview Pages saves inside the document. If a Pages file says it can't be read, open it in Pages, choose File › Export To › Word, and open the Word file instead."),
            .heading("EPUB"),
            .text("An EPUB's chapters come from its own table of contents, with the chapter titles it gives, even when the whole book is one long file inside. The contents page and the copyright page are left out. If the book has no table of contents, or one with a single entry, InnerVoice finds the chapters at their headings instead."),
            .text("Footnotes and endnotes aren't part of the narration: the notes are left out, and so are the little numbers and symbols in the text that link to them."),
            .heading("PDF"),
            .text("Running headers, footers and page numbers are left out, and paragraphs split across pages are joined again. Lines that a book spreads out to fill the width (with wide gaps between the letters) are read as ordinary words in their paragraph. A scanned PDF has pictures of pages and no text: export it with text from the app that made it, or run it through TextLens first."),
            .tip("InnerVoice keeps its own copy of the manuscript in the project, so moving or changing the original file doesn't change your project. To bring in a new draft, see Revised Manuscripts."),
        ]

        case .taggingSpeakers: [
            .text("Tag Speakers (⇧⌘T, or Book › Tag Speakers) uses the on-device AI to decide who speaks each line. Chapters fill with color as it goes, and the toolbar shows which chapter it's on."),
            .heading("How it decides"),
            .text("Plain rules come first: \"said Tom\", \"Tom asked\" and a pronoun after a named speaker settle most lines with no AI at all. In a back-and-forth with no tags, the lines alternate between the two characters. The model reads the rest, and any name it finds that isn't in the cast becomes a new character."),
            .text("Lines you assigned yourself are never changed by Tag Speakers. Stop (⌘.) keeps everything done so far, and Tag Speakers carries on from there next time. Undo Tag Speakers takes the whole run back."),
            .heading("Privacy"),
            .text("The AI runs on your Mac. Your manuscript, your cast and your notes never leave it, and InnerVoice works offline. The only thing downloaded is the model you choose, once."),
            .tip("When it's done, a banner says how many lines need you. Click Show Lines That Need You, or press ⌘↓ to go to the next one."),
        ]

        case .fixingLines: [
            .text("Click a line to assign it. The Assign To popover lists the characters with their colors: pinned characters first, then the ones used most in this chapter. Type to filter the list, or press a number key to pick one of the first nine. Narration (not spoken) sits at the top of the list for text no one says out loud."),
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
            .heading("Find a speaker fast"),
            .text("In the right-click Assign To list, the search field is ready the moment the list opens: type any part of a name (\"uin\" finds Mara Quinn) and press Return to pick the first match. Esc clears the search, then closes the menu."),
            .heading("Pin the speakers you use most"),
            .text("Pinned characters sit at the top of every speaker list, A to Z by first name, with everyone else A to Z below them. Hold Option in the right-click list and choose Pin, click the pin beside a name in Assign To, or right-click a character in Cast and choose Pin to Top of Speaker Lists."),
            .heading("Fix lines from the margin"),
            .text("Click a name (or a red ?) in the left margin to assign every line in that paragraph at once: Return confirms the best guess for all of them, or pick someone else and the whole paragraph changes. Right-click the name for the same choice as a menu."),
            .text("To give several paragraphs to one character, ⌘-click their names in the margin (⌘-click a line to add just that line). A bar shows how many lines you picked; click Assign…, press Return, or click any picked name, and one choice goes to all of them."),
            .heading("Fix a whole exchange"),
            .text("When you fix one line in a back-and-forth that had no tags, the lines after it are probably off by one too. A banner offers to swap the rest of the exchange, up to the next tagged line or break in the narration. Click Swap or Keep."),
            .heading("Several lines at once"),
            .text("Shift-click or ⌘-click to select several lines, or select all of one character's lines in a chapter, then assign them all at once."),
            .heading("Merge characters"),
            .text("When the same person turns up under two names, say Tom and Thomas, select both in Cast and choose Merge. Every line and fact moves to one character, and the other name becomes an alias."),
            .heading("Delete text the narrator doesn't read"),
            .text("Select an ISBN, a copyright page, or an ad for another book and press Delete (or right-click and choose Delete Text). It goes from the project and from every script and export; the manuscript file you imported never changes. Select a chapter's heading and all of its text to delete the whole chapter, or right-click a chapter in the sidebar and choose Delete Chapter. Front matter often comes in as a chapter heading: delete a heading on its own and its text joins the chapter before (the book's first text stays, under Opening). ⌘Z brings it back."),
            .heading("Lines to review in each chapter"),
            .text("In the sidebar, \"2 to review\" beside a chapter means two of its lines still need you to confirm who says them. A checkmark means every line in the chapter has a speaker. The numbers beside the cast are how many lines each character speaks."),
            .tip("Once you've saved a project with a name, it saves itself every 30 seconds while it has changes, so you can keep working without stopping to save. A new project waits for your first File › Save."),
            .heading("Review the guesses"),
            .text("When the AI or the rules aren't sure, they still make a best guess: the margin shows the name with a question mark (\"Cyrus?\") and the line stays red until you check it. Review in the sidebar (⌘6) lists every line that needs you, Unsure Guesses first. Return confirms the guess, typing or 1–9 picks someone else, 0 makes it narration, and ↓ ↑ move between lines. A settled line leaves the list and the next one comes up."),
            .text("Each card shows the line's whole paragraph, headed with its chapter's title and paragraph number (\"Chapter 6 · ¶26\"). Lines in the same paragraph that need you share one card, with a row for each: its best guess, its words, and Confirm, Assign… and Show. Click a row to make it the line the keys act on. The lines under review are red; the paragraphs around them are smaller and dimmer, keep their speakers' colors, and start with who speaks in them."),
            .text("Context in the toolbar sets how many paragraphs show before and after the line, from This paragraph only up to 5 paragraphs around; click − or + to change it. Cards never reach past the chapter's edges, and the setting stays the same for every book."),
            .tip("⌘↓ and ⌘↑ jump to the next and previous line that needs you and open Assign To with the guess picked: Return confirms it. The inspector's Confirm button does the same."),
        ]

        case .colors: [
            .text("Every character has a color, and each line shows the color of the character who says it. Red is never a character's color: it's kept for lines that need you."),
            .heading("Automatic colors"),
            .text("Colors start on Automatic: blues and greens for men, pinks and purples for women, and warm colors (amber, orange, gold) for anyone the book hasn't said yet. Main characters get the clearest colors. When Tag Speakers or Build Summaries learns more about someone (\"she said\", a title like Mrs., their summary), their color follows, and two characters share a color only when a family runs out."),
            .heading("Choose a color"),
            .text("To choose a character's color yourself, open their page in Cast and click their picture. Pick any color, or Other… for one of your own. A color you choose stays until you choose Automatic again."),
            .heading("Highlight, Outline or Underline"),
            .text("Highlight fills each line with its color; Outline draws a line around it; Underline draws a colored line under the words and keeps the page calmest. Choose in the toolbar or the View menu (⌥⌘H, ⌥⌘O, ⌥⌘U). Each project keeps its own choice."),
            .keys([
                ("Spoken", "Highlighted, outlined or underlined in the character's color"),
                ("Thought", "Dashed"),
                ("Written", "Dotted, for letters, signs and texts"),
            ]),
            .heading("Red means it needs you"),
            .text("Red is kept for lines that need you, and no character is ever red. Who's Speaking? is a line the AI couldn't decide on. Unsure is a line it tagged with low confidence: the margin shows its best guess with a question mark (\"Cyrus?\"), and Who's Speaking? lines show just \"?\"."),
            .heading("Speakers in the margin"),
            .text("Each line also shows who says it in the margin, so you never have to rely on color alone. Choose initials, first names or full names in Settings › Manuscript or View › Speakers in Margin; long names are shortened with \"…\". Turn them off for a project with View › Show Speakers in Margin."),
            .heading("Text size and spacing"),
            .text("Make the text bigger or smaller with the buttons in the toolbar, or View › Bigger Text (⌘+), Smaller Text (⌘−) and Actual Size (⌘0). Switch between single and double line spacing with the spacing button beside them (⌥⌘L), or in Settings › Manuscript. The page keeps your place when you change either."),
        ]

        case .castingStyle: [
            .text("Casting Style is how a book shares its voices, the way audiobooks are really made. Choose it when you start a project, then change it any time: on the Voices page when you produce with AI voices, on the Cast page when you narrate. Changing it never deletes a voice or a narrator; it only decides who reads each character."),
            .heading("The three styles"),
            .keys([
                ("One Voice / Just Me", "One voice reads the narration and plays everyone, shading a little lower for men and higher for women. Most audiobooks are made this way."),
                ("Narrator + Two Voices / Three Narrators", "A narrator, one voice (or narrator) for every man, one for every woman, and an Other group for anyone else."),
                ("Full Cast", "Every main character has a voice (or narrator) of their own, plus the narrator."),
            ]),
            .heading("Shading"),
            .text("When the narrator plays a character, InnerVoice works out a shade from their gender, age and voice brief: Subtle, Clear or Strong is about one, two or three semitones for a character of another gender. Open a character on the Voices page to hear the shade, or set the pitch and pace yourself. Qwen3-TTS shifts the narrator’s voice sample, so a shaded character still sounds like the narrator."),
            .text("When you narrate yourself (Just Me), choose Your Voice on the Cast page. Characters of your own gender are read in your voice; others get an arrow in the manuscript margin (↓ lower, ↑ higher, doubled for a bigger change) and a Shade cue on the Teleprompter and in Narrator Scripts."),
            .heading("Who reads whom"),
            .text("Right-click a character to choose who reads them, to mark them as a main character, or to set their gender. Main characters always have a voice of their own in Full Cast; InnerVoice suggests the characters with the most lines, but only you mark them."),
            .text("Narrator + Two Voices sorts characters by gender. When the book doesn’t settle someone’s gender, they’re listed under Gender to Review: click the filter on the Voices or Cast page, or the Genders to Review list on the Review page, and choose Male, Female or Other. To set several at once, ⌘-click them in the Voices list or on the Cast page (⇧-click picks a run), then click Male, Female or Other once for all of them."),
            .text("For a book read by two people, choose the men’s or women’s voice (or narrator) under Narration: they read the narration too."),
            .tip("Export Audio can save the voices Separate, a file per voice, all the same length so they line up in an audio editor."),
        ]
        case .voices: [
            .text("InnerVoice can read the book aloud with a different voice for each character, so you can hear a scene before you record it, check who says what, or rehearse against the other parts. Apple Voices, Qwen3-TTS and Orpheus run on your Mac and send nothing anywhere. ElevenLabs, if you set it up, runs in the cloud (see below)."),
            .heading("Voices for the cast"),
            .text("After Tag Speakers and Build Summaries, InnerVoice casts a voice for each character from what the book says about them: gender, age and accent from the summary, or a title like Mrs. or a \"she said\" when the summary is quiet. Main characters choose first, and two characters who share a scene get different voices. The narrator gets the best voice of all."),
            .text("Open the Voices page (⌘7) to hear and change them. Click a character, then play any voice: it says one of that character's own lines. Click Use to give them that voice, or Confirm to keep the one casting picked. Speed and pitch make an Apple voice older, younger or more distinct."),
            .text("The Narrator is always first: on the Voices page, the Cast page and in the sidebar's Cast list. The narrator's voice reads the narration, every line nobody is cast for, and letters, signs and other written text (Settings › Voices can give written text to the character who wrote it). Give a minor character Same as Narrator and their lines are read in the narrator's voice. In a first-person book, mark the character who tells the story as the narrator on their page and the narration uses their voice, unless you choose a separate narrator voice. Export Audio can save the narrator's part on its own."),
            .text("A red ? means casting wasn't sure, usually because the book hasn't said whether the character is a woman or a man. Those wait for you in Review › Unsure Voices."),
            .heading("How each line is said"),
            .text("Every line also gets a tone: neutral, excited, confused, sad, angry, whispered, scared, tender or sarcastic. \"She snapped\" is angry and \"he whispered\" is a whisper; Tag Speakers asks the AI about the rest, with the paragraphs around each line. Tones the AI isn't sure of wait in Review › Unsure Tones, where 1 to 9 pick a tone and Space plays the line."),
            .text("Change a tone yourself from the inspector or by right-clicking a line. Tag Speakers never changes a tone you set."),
            .heading("Listening"),
            .keys([
                ("⌥Space", "Play the selected line in its character's voice"),
                ("⌥⇧Space", "Play from Here: a table read to the end of the book"),
                ("⌥Esc", "Stop playing"),
            ]),
            .text("Play from Here follows along in the manuscript. Settings › Voices decides whether narration is read too (a full table read) or only the dialogue."),
            .heading("Pauses and speed"),
            .text("Settings › Voices › Pauses sets each gap in seconds: between sentences, between a line and the narration around it, between paragraphs, after a chapter title, at a scene break and between chapters. Tight, Natural and Relaxed are starting points. With \"Set the pause between sentences\" on, each sentence is spoken on its own so that gap is exact; turn it off to let each voice pace its own sentences."),
            .text("Click Try It to hear a passage from the front book over and over; move a slider and the next pause changes, with nothing to remake. Playback speed (0.75× to 2×) speeds up Play, pauses included, without changing the voices' pitch. Export Audio always saves at normal speed, with your pauses."),
            .heading("Exporting audio"),
            .text("File › Export Audio… saves all the voices or one character's lines as M4A or WAV files, one file per chapter or all in one. Under Chapters, check the chapters to include: Select All checks or clears every one, and ⇧-click checks a run of chapters. The window remembers your choice until you close the book. When chapters have recaps, Say chapter recaps reads each one (\"Previously…\") after the chapter's title. Each file is finished on this Mac and then moved into the folder you chose, so exporting into OneDrive, iCloud Drive or Dropbox works too."),
            .heading("EPUB with Audio"),
            .text("File › Export EPUB with Audio… saves the book as an EPUB with the voices built in. Open it in a reading app that plays read-along books (EPUB 3 Media Overlays), such as Thorium Reader, and click Play: each sentence is highlighted as it's read. Apps that don't play them show the book as ordinary text. Check the chapters to include, as in Export Audio. Every chapter is read with narration, in the voices and tones you chose, and the file is finished on this Mac before it's moved into the folder you chose."),
            .tip("Reading the whole book takes as long as Export Audio does. Lines already played or exported come from the audio cache, so they're quick."),
            .heading("Engines"),
            .text("Choose the engine for each project from the menu at the top of the Voices page. Each engine keeps its own casting, so switching back and forth loses nothing."),
            .keys([
                ("Apple Voices", "Come with your Mac and work right away. They can't act, so a tone only changes speed and pitch a little. More natural Premium voices are a free download in System Settings › Accessibility › Spoken Content."),
                ("Qwen3-TTS", "Sixteen ready-made voices, from a little girl to a weathered sailor, or any voice you describe. When the summary says how a character sounds (\"An older man with a deep, gravelly voice, speaking English with a Scottish accent\"), casting designs that voice for them. It records a short sample of each voice in each tone, then speaks every line from that sample, so the voice stays the same and the tone comes through. 6.2 GB."),
                ("Orpheus", "Eight natural American voices that act from the words. A sigh, laugh, gasp or groan in the speech tag (\"she sighed\") is performed too. 3.6 GB."),
                ("ElevenLabs (Cloud)", "Natural voices from your ElevenLabs account, made on ElevenLabs' servers with its eleven_v4 model, which acts each tone and any delivery hint (\"barely audible\"; see Choosing a Model › Claude's own features). Unlike the others it sends text off your Mac: every line an ElevenLabs voice speaks goes to ElevenLabs, and uses your plan's credits."),
            ]),
            .heading("ElevenLabs and your manuscript"),
            .text("ElevenLabs is the only part of InnerVoice that sends your book anywhere. Set it up in Settings › Voices: Open API Keys Page, create a key, paste it, click Check Key, and tick that you understand your text goes to ElevenLabs. The key is kept in your Mac's keychain."),
            .text("Then each book asks before its text is sent for the first time, when you choose ElevenLabs (or include it in Mix Engines) on its Voices page. A table read or an exported book sends the whole manuscript, so allow it only for books you have the right to share. Stop Sending This Book on the Voices page ends it for that book, and Disconnect in Settings › Voices forgets the key. Audio ElevenLabs already made stays in the saved audio on this Mac."),
            .heading("Books in a series"),
            .text("Give the characters the voices they had in an earlier book: on the Voices page, click Use Voices from Another Project (or choose Book › Use Voices from Another Project…), and pick the earlier book's project from Recent Projects or with Choose Project…. Nothing changes yet."),
            .text("Each character with a voice in the earlier book is listed, main characters first, with their voice, next to who InnerVoice thinks they are in this book. Same name means the names (or a name they go by) match; First name only means just the first name does, so check it; Not matched means nobody here was found. Choose anyone from the menu, or Not in this book. Each character here gets one voice: choosing someone already matched moves the match."),
            .text("Use These Voices gives the matched characters every voice they had, on every engine, as your own choices, so casting never changes them; one Undo takes it all back. Bring their Voice Notes too keeps Qwen3-TTS voices the same, since it designs voices from the notes. The narrator's voice and the earlier book's engine can come along too. ElevenLabs voices still need this book's own permission."),
            .heading("Voice Notes and the AI voices"),
            .text("Qwen3-TTS and ElevenLabs can design a voice from a description, so they get each character's Voice Notes: your own notes first, then the voice brief and \"From the book\". With Qwen3-TTS this happens on its own: a character whose summary or Voice Notes say how they sound gets a voice designed for them, and the notes go with it every time it speaks, so editing them changes the voice (the first line after an edit takes a few seconds while the new voice is made). Ready-made voices you pick stay as they are."),
            .text("With ElevenLabs, click Design Voice under Design a Voice from Voice Notes on a character's Voices page. You see exactly what will be sent; ElevenLabs designs a voice from it, adds it to your ElevenLabs voices, and gives it to the character. It uses credits and one of your plan's voice slots, so it only happens when you click. If a character's voice list says ElevenLabs voices haven't loaded, click Load Voices; when loading fails it says why (a key without permission to read voices, no credits left, or no internet). Orpheus and Apple voices can't take a description: casting reads the notes for gender, age, pitch and pace instead."),
            .heading("Clone a voice"),
            .text("You can make a voice from a recording of a real person, such as yourself or an actor who has agreed to it, and give it to any character. Choose Book › Clone a Voice… (or click Clone a Voice… on the Voices page), then choose where the voice is made: On This Mac or ElevenLabs (Cloud)."),
            .text("Name the voice, choose who in this book it's for (or No One Yet), then click Record… to record with your Mac's microphone or Add Files… to add recordings (you can drop files on the list too). Use one person speaking clearly, with no music, echo or other voices. Something to read aloud gives you a passage if you need one. Tick that you have the right and the consent of the person to clone their voice, then click Clone Voice."),
            .heading("On this Mac"),
            .text("Qwen3-TTS (a free download in Settings › Voices) learns the voice from the first 15 seconds of speech in your recordings; 15 to 30 seconds is plenty. InnerVoice finds the words in that clip on this Mac and shows them under The Clip: fix any word that's wrong, because the clone copies the voice best when every word matches. If your Mac can't find the words, type them. Play Clip lets you hear the part that's used."),
            .text("Nothing leaves your Mac and it's free. The voice is listed first among the Qwen3-TTS voices in every project, marked Cloned, and it's heard in books that speak with Qwen3-TTS or Mix Engines. Rename or delete it in Settings › Voices › Voices Cloned on This Mac."),
            .heading("With ElevenLabs"),
            .text("For a book that has ElevenLabs set up and allowed, ElevenLabs (Cloud) makes the voice in your ElevenLabs account. One to two minutes of recording works best; at least 10 seconds is needed."),
            .text("The recordings and the name go to ElevenLabs; no text from your book is sent. The voice is saved in your ElevenLabs account, so every InnerVoice project (and elevenlabs.io) can use it: it's listed first among the ElevenLabs voices, marked Cloned. Casting never chooses a cloned voice on its own, so it goes only where you put it. Play it, then click Use for… to give it to the character. Recordings made in InnerVoice are deleted when the sheet closes unless you click Save…; files you added stay where they are."),
            .text("Cloning needs a paid ElevenLabs plan (Starter or above), and each cloned voice takes one of your plan's voice slots; the sheet shows how many are in use. Rename or delete cloned voices in Settings › Voices › Cloned Voices; deleting one frees its slot, and characters that used it will need another voice. Your API key needs write access to Voices to clone, rename or delete."),
            .text("Download Qwen3-TTS and Orpheus once from Settings › Voices, or from the Voices page when you choose one. Downloading is the only time they use the internet; your book never leaves the Mac. They need a Mac with Apple silicon, and the first line takes a few seconds while the engine loads."),
            .text("Want a wider range? Choose Mix Engines from the engine menu on the Voices page (when two or more engines are on). Every voice from every engine that's on is in one list, and casting picks from all of them; each character can sound like a different engine. On a Mac with 32 GB of memory or more, Qwen3-TTS and Orpheus stay loaded together, so lines play without waiting for a model to load."),
            .text("The first line from Qwen3-TTS or Orpheus takes a few seconds while the engine loads; a bar at the bottom of the window says so. If a line can't play, the bar says why."),
            .text("Don't use an engine? Turn it off with its switch in Settings › Voices and it's no longer offered on the Voices page. A project set to it uses an engine that's on until you turn it back on; its casting is kept. One engine always stays on."),
            .tip("Made audio is kept in a cache, so a line plays at once the second time. Settings › Voices shows its size and can clear it."),
        ]

        case .castAndSummaries: [
            .text("Cast shows every character as a card, with their color, line and word counts, first appearance and narrator. Click a card or its name to open that character’s page and read their summary."),
            .heading("Summaries"),
            .text("Build Summaries has the AI read the text around each character and fill in what the book says: age, gender, accent, voice, personality, history and relationships. Each fact has a source, such as \"Ch 3 ¶12\". Click it to see that paragraph in the manuscript."),
            .text("Build Summaries in the Cast toolbar (⇧⌘B) does every character in one go. On a character's page, Build Summary does just that character; click its arrow and choose Build Everyone's Summaries to do them all from there."),
            .text("While it reads, a bar shows how far it is with the character it's reading, named below the bar; for the whole cast there's a second bar for the run (\"Character 3 of 12\"). Stop keeps what it has found. Running it for the whole cast again asks first, because new notes can change the voices casting picked."),
            .heading("Voice briefs"),
            .text("After it reads about everyone, Build Summaries writes a voice brief for each character into their Voice Notes under \"Voice brief:\": pitch, pace, texture, attitude, and how they should differ from the characters they share scenes with (\"Lower and slower than Mara; rougher than Edda\"). Main characters come first, so each later brief can set itself apart from voices already described. Your own notes come first when the AI writes it."),
            .text("Casting reads the brief: a low or high pitch and a slow or quick pace go into the Qwen3-TTS voice description and nudge the pitch and speed of Apple voices. Like \"From the book\", the brief is yours once you edit it."),
            .heading("When the AI is working"),
            .text("Whenever the AI is working in the background (Tag Speakers, Build Summaries, voice briefs, or an AI voice engine loading its models), a bar at the top of the page shows how far it has got, with Stop for the runs you started. It's on every page, the Cast page and each character's page included; in the Manuscript, Tag Speakers shows its own banner."),
            .heading("Voice notes from the book"),
            .text("What Build Summary learns about how a character sounds (age, gender, accent, voice and manner) goes into their Voice Notes under \"From the book:\", below anything you wrote. Casting and the AI voices read the notes, so editing them fine-tunes how the character sounds; voices you chose yourself don't change. Once you edit the \"From the book\" part it's yours, and Build Summary leaves it alone. Delete it to get a fresh one next time."),
            .text("A fact the AI can't back up with text from the book is dropped, so every fact you see can be checked."),
            .heading("Revealed Late"),
            .text("A fact marked Revealed Late is first mentioned after the character's first line, for example \"First speaks Ch 2, revealed Ch 20\". It's worth knowing before you choose a voice in Chapter 2."),
            .heading("Editing"),
            .text("Change a character's name, aliases, color or any fact, and add your own notes and voice notes. A fact you edited is never replaced when Build Summaries runs again."),
            .text("What you type in these fields is saved when you pause, press Return or click elsewhere, so typing stays quick on a long book. A burst of typing is one step to undo (⌘Z)."),
        ]

        case .index: [
            .text("Index lists each character's lines by chapter. Click a line to go to it in the manuscript."),
            .heading("Show in Index"),
            .text("Each character has a Show in Index switch. Main characters with a large share of the lines start hidden, so the index stays useful for finding the smaller parts; turn them on whenever you like."),
            .tip("⌘F finds text anywhere in the manuscript, and ⌘G goes to the next match."),
        ]

        case .narratorsAndScripts: [
            .text("For a production with more than one narrator, add the narrators in Cast and cast each character to one of them."),
            .heading("Narrator Scripts"),
            .text("Export a Narrator Script for each narrator as PDF or Word. It has only their lines, each with its cue line and its chapter and paragraph, and the summaries of the characters they voice. Send it to the narrator; they don't need InnerVoice."),
            .text("A chapter with a recap (\"Previously…\", in the inspector next to the chapter, or from Book › Claude › Write Chapter Recaps) has it under the chapter's title."),
            .heading("The whole manuscript"),
            .text("You can also export the tagged manuscript as a PDF with colors, initials and a key to the characters, for printing or reading on an iPad, and the cast list as PDF or CSV."),
        ]

        case .pronunciations: [
            .text("Pronunciations lists the names, places and unusual words the AI found, with how often and where each one appears. What you type in Say It As is how the AI voices say the word. It's part of Produce with AI Voices projects; a narrator says names in their own voice."),
            .keys([
                ("Say It As", "How to say it, in your own spelling: \"SHEV-on\""),
                ("Note", "Anything else: where it comes from, what the author said"),
                ("Confirmed", "Mark it once the author has confirmed it"),
                ("Suggest Spellings", "Cloud: Claude suggests a Say It As for each term without one. Only the terms and the sentence each first appears in are sent; you check each spelling you want to use"),
            ]),
        ]

        case .teleprompter: [
            .text("The Teleprompter shows the book in large type for the booth, one paragraph at a time, with the speaker's name, color and voice cue, and who speaks next below it. Each book opens where you left off."),
            .keys([
                ("Space or →", "Next paragraph"),
                ("←", "Back one paragraph"),
            ]),
            .text("Foot pedals work too: most send Space, Page Down or the arrow keys. Pick the key in Settings › Teleprompter, along with the text size."),
            .tip("InnerVoice doesn't record sound. Record in your own audio app while you read; the Teleprompter only keeps your place."),
        ]

        case .revisedManuscripts: [
            .text("When the author sends a new draft, choose File › Import Revised Manuscript… and pick it. InnerVoice compares it with the manuscript in your project, paragraph by paragraph."),
            .heading("What is kept"),
            .text("Every paragraph that didn't change keeps its tags, your corrections and notes, even when paragraphs were added, removed or moved around it. In a lightly edited paragraph, lines whose text still matches keep their character."),
            .heading("Review what changed"),
            .text("A list shows the added, changed and removed paragraphs; click one to go to it. Changed paragraphs go through Tag Speakers again, and your own assignments still win."),
        ]

        case .choosingModel: [
            .text("The model is the AI that reads your book. Most run on your Mac and are downloaded once; Claude runs in the cloud. Settings › AI lists the models, with the one that fits your Mac best first and a note on each about why it fits or not."),
            .heading("How much memory"),
            .text("Recommendations come from your Mac's memory. With 8 GB, a small model; with 16 GB, a medium one; with 24 to 36 GB, a large one; with 48 GB or more, the most accurate. A model too big for your memory makes everything slow."),
            .keys([
                ("Download", "Get the model; you can cancel, and it picks up where it stopped"),
                ("Use This Model", "Tag with it from now on"),
                ("Remove Download", "Free the disk space it takes"),
            ]),
            .heading("No download"),
            .text("On a Mac with Apple Intelligence, Apple's on-device model is offered too. It needs no download and is quicker, but it is less accurate at deciding who speaks."),
            .heading("Claude (cloud)"),
            .text("Claude is Anthropic's AI, offered next to the downloaded models and marked Cloud. It is the most accurate choice and needs no download, but it runs on Anthropic's computers: each passage of the book is sent to Anthropic while Tag Speakers or Build Summaries runs, and Anthropic charges for what you use."),
            .keys([
                ("Set Up…", "Read the cloud warning, paste an API key from the Claude Console and agree; the key is kept in your keychain"),
                ("Model menu", "Pick Claude Fable, Opus, Sonnet or Haiku; new models appear on their own"),
                ("Use Claude", "Tag and summarize with Claude from now on"),
                ("Disconnect", "Forget the key; nothing more is sent"),
            ]),
            .text("Each book asks once before anything is sent: \"Send this book to Claude?\" Until you say yes, nothing from that book leaves your Mac. To take the yes back, choose Book › Stop Sending This Book to Claude. Anthropic doesn't train on API data unless your organization has opted in; the setup window links to how to check."),
            .text("After Tag Speakers runs with Claude, Review Claude’s Changes lists every line it gave a new speaker or tone. Everything starts checked; uncheck what you don’t want and click Undo Unchecked, and those lines go back to how they were, in one Undo step. Keep All keeps everything. Lines you assigned yourself are never changed. To keep Claude’s changes straight away instead, turn off Review Claude’s changes in Book › Project Settings…"),
            .heading("What it costs"),
            .text("Before Tag Speakers, Build Summaries or any of Claude's own features sends a book, InnerVoice shows the estimated cost for the model you picked, worked out on your Mac from the book's length (nothing is sent to count it). It's an estimate: your bill may differ. A model InnerVoice doesn't know the price of shows tokens only. Tick \"Don’t show the estimate again\" to skip it next time."),
            .heading("Claude's own features"),
            .text("Book › Claude (Cloud) has four features that only Claude does. Each one sends text to Anthropic, so each asks first, and each is one Undo step."),
            .keys([
                ("Clean Up Text…", "Finds what importing left behind: scanning mistakes, words broken by a hyphen, page numbers and headers, footnote markers, spaced-out letters. Only the project changes; your manuscript file never does, and lines you assigned keep their speakers"),
                ("Write Chapter Recaps…", "A short \"Previously…\" for each chapter, in the inspector next to the chapter. Edit it, and it's yours. Narrator Scripts include it, and Export Audio can say it after the chapter title"),
                ("Suggest Delivery Hints…", "How each line is performed beyond its tone (\"barely audible\", \"through gritted teeth\"). Hints go to ElevenLabs only, as audio tags; edit them in the inspector. Produce with AI Voices only"),
                ("Suggest Spellings…", "A Say It As for each pronunciation without one; only the terms and the sentence each first appears in are sent. You confirm each one"),
            ]),
            .text("With Review Claude's changes on (Book › Project Settings…), fixes, recaps and hints wait in a list to keep or leave out; nothing changes until you apply them. Suggested spellings always wait for you."),
            .tip("With a downloaded model or Apple's, your manuscript never leaves this Mac. Only the model is downloaded."),
        ]

        case .keyboardShortcuts: [
            .heading("Projects"),
            .keys([
                ("⌘N", "New from Manuscript…"),
                ("⌘O", "Open…"),
                ("⇧⌘T", "Tag Speakers"),
                ("⌘.", "Stop Tag Speakers"),
                ("⌘1 – ⌘7", "{pages}"),
                ("⌥⇧⌘E", "Export Audio…"),
                ("⌥⌘,", "Project Settings…"),
            ]),
            .heading("Listening"),
            .keys([
                ("⌥Space", "Play Line"),
                ("⌥⇧Space", "Play from Here"),
                ("⌥Esc", "Stop Playing"),
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
            .heading("Teleprompter"),
            .keys([
                ("Space or →", "Next paragraph"),
                ("←", "Previous paragraph"),
            ]),
            .heading("Everywhere"),
            .keys([
                ("⌘,", "Settings"),
                ("⌘?", "InnerVoice Help"),
            ]),
            .tip("ShortcutsLens lists InnerVoice's shortcuts too."),
        ]

        case .settings: [
            .keys([
                ("General", "Appearance, asking before quitting, the Dock and menu bar icons, iCloud and updates"),
                ("Manuscript", "The typeface, text size, line spacing, and initials, first names or full names in the margin"),
                ("AI", "The model"),
                ("Voices", "Table reads, written text, the voice engines and saved audio"),
                ("Teleprompter", "The Teleprompter's text size and keys"),
                ("Accent", "The color for selections, main buttons and switches"),
                ("Activity", "What InnerVoice has done for you: lines tagged, how often the AI's guesses held up (by model), time saved, audio made"),
            ]),
            .heading("Activity and Book Stats"),
            .text("Settings › Activity counts what InnerVoice did across all your books: lines tagged by the rules and the AI, guesses you kept or changed, tones, facts and audio. Time saved is an estimate, about six seconds for each line you didn't have to tag by hand. With settings sync on, All Macs adds up every Mac. Only numbers are kept; nothing from your books is counted or sent."),
            .text("Book › Book Stats shows the same kind of numbers for the open book. Choose a Model shows how each model did on your books once you've checked enough of its guesses."),
            .heading("Where InnerVoice appears"),
            .text("Show InnerVoice in the Dock, the menu bar, both, or neither. The menu bar menu has New from Manuscript…, Open…, Open Recent and Settings…. With neither, open InnerVoice again from Applications or Spotlight to get back to Settings. With the Dock icon on, its right-click menu has Settings…."),
            .text("While ToolHUD is running, InnerVoice's menu moves into ToolHUD's menu bar menu as a submenu, with Tag Speakers and Teleprompter for the project in front. To keep the icon in the menu bar, turn off \"Put in ToolHUD's menu while ToolHUD is running\" in Settings › General."),
            .heading("Sync"),
            .text("With \"Sync settings with your other Macs\" on, appearance, the accent color, asking before quitting, where InnerVoice appears and the update choices follow you to every Mac signed in to the same iCloud account. Projects never sync; they are files you keep where you like."),
        ]

        case .updates: [
            .text("Settings › General › Updates chooses what InnerVoice does when a new version is out: install it automatically while your Mac is idle and the AI isn't working, ask you first, or not check at all. Check Now looks right away."),
            .heading("What's new"),
            .text("The update window shows the notes for the latest releases, with the ones you don't have yet marked New. \"Don't remind me about this version\" is ticked to begin with: click Not Now with it ticked, and InnerVoice stops reminding you about that version (Check Now still offers it)."),
            .text("The version you're running is on the Welcome window, under the book's name at the top of every project window, and in About InnerVoice."),
            .text("After an update that installed itself, Settings shows what changed the next time it opens. What's New… beside Check Now shows the notes any time, and About InnerVoice shows the last 10 releases."),
        ]
        }
    }
}
