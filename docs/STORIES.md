# InnerVoice stories

Snapshot pages are named in `Screens:`; there is no reference app.

## M1 Core loop: open a book, see who speaks

### S-001 Create a project from a manuscript
As the narrator, I choose File › New from Manuscript… (⌘N) or drop a file on the Start window, and InnerVoice makes a `.innervoice` project with a copy of the manuscript.
Screens: start, manuscript
Acceptance:
- [unit] .txt, .md, .rtf, .rtfd, .docx, .doc, .odt, .html, .pages (preview PDF/text), .epub and .pdf all import into chapters and paragraphs.
- [unit] Chapter headings ("Chapter 1", "CHAPTER ONE", "Prologue", "Part II", Markdown #, Word heading styles) start new chapters.
- [unit] PDF running headers, footers and page numbers are dropped; paragraphs split across pages are joined.
- [self] The project saves and reopens with the same paragraphs.
UX:
- Flow: ⌘N, File › New from Manuscript…, Start window "Open Manuscript…" or drop a file -> open panel (manuscript types) -> "Reading Chapter 3 of 41…" in the window -> manuscript shows, Tag Speakers offered in a banner
- Surface: new document window (a place people come back to)
- States: empty Start window "Open a manuscript to begin" + Open Manuscript…; error inline "Unable to read “Book.pages”. Export it from Pages as Word or PDF, then try again."; scanned PDF "This PDF has no text. Export it with text, or run it through TextLens first."
- Words: "New from Manuscript…", "Open Manuscript…", "Drop a manuscript here"
- Access: Return opens the selected recent project; VoiceOver reads the drop area as a button
Status: done

### S-002 Find the dialogue
As the narrator, I see every quoted line found before the AI runs.
Screens: manuscript
Depends: S-001
Acceptance:
- [unit] Curly, straight, single (UK) and « » quotes; quotes that run across paragraphs (no closing mark); nested quotes stay inside their parent.
- [unit] Dash-style dialogue (paragraph starting with an em dash) is found.
- [unit] Italic-only sentences in a paragraph are marked as possible thoughts.
- [unit] Letters, signs and texts in block quotes are marked as written.
UX:
- Flow: automatic after import, no user step
- Surface: inline (lines show as Who's Speaking? until tagged)
- Words: none new
Status: done

### S-003 Tag speakers with the local AI
As the narrator, I press Tag Speakers and watch chapters fill with color as the model works; I can keep reading while it runs and stop it.
Screens: manuscript-tagging
Depends: S-002, S-020
Acceptance:
- [unit] Attribution rules first: "said Tom", "Tom said", "Tom asked" and pronoun + last named speaker give a speaker with high confidence, with no model needed.
- [unit] Lines in a back-and-forth with no tags alternate between the two speakers and get medium confidence.
- [self] The model's JSON reply is parsed; unknown names become new characters; bad JSON retries once, then the lines stay unassigned.
- [snap] A progress bar in the toolbar shows chapter n of N; Stop keeps what is done.
UX:
- Flow: toolbar Tag Speakers (⇧⌘T), banner button, Book › Tag Speakers -> progress in toolbar "Tagging Chapter 4 of 41…" + chapters fill -> done banner "Tagged 2,314 lines. 41 need you." with Show Lines That Need You
- Surface: inline toolbar progress (people keep reading)
- States: no model "Choose a model to tag speakers" + Choose Model…; stopped "Stopped after Chapter 12. Tag Speakers continues from there."
- AI: sparkles on Tag Speakers; lines you assigned are never changed; Undo Tag Speakers
- Words: "Tag Speakers", "Stop", tooltip "Use the on-device AI to find who speaks each line (⇧⌘T)"
- Access: ⌘. stops
Status: done

### S-004 Read the colored manuscript
As the narrator, I read the book with each spoken line highlighted or outlined in its character's color, and the character's initials in the margin.
Screens: manuscript, manuscript-outline, manuscript-dark
Depends: S-003
Acceptance:
- [snap] Highlight and Outline styles both draw; thoughts use a dashed outline; written text a dotted underline.
- [snap] Unsure lines (below the confidence setting) and unassigned lines are red with a "?" in the margin.
- [snap] Readable in light and dark mode; text color stays legible on every highlight.
UX:
- Flow: scroll; ⌘↓/⌘↑ next/previous line that needs you (Who's Speaking? or Unsure)
- Surface: main reading pane
- States: empty chapter "No text in this chapter"; long names in margin show initials, full name on hover
- Access: never color alone: margin initials + "?" on red lines; VoiceOver reads "Line, Tom: …"
Status: done

### S-005 Assign a line to a character
As the narrator, I click a line and pick its speaker from a popover (type to filter, number keys 1–9 for the most recent), or create a new character.
Screens: assign-popover
Depends: S-004
Acceptance:
- [snap] The popover lists characters with color dots, most used in this chapter first, and "New Character…".
- [self] Picking marks the line confirmed (confidence 1) and it is never changed by a later AI run.
- [self] ⌘Z undoes the change.
UX:
- Flow: click a line, Return on the focused line, or right-click › Assign To -> popover with search field focused -> type or 1–9 -> Return assigns, popover closes, line recolors -> ⌘Z undoes
- Surface: popover (a few choices tied to one control)
- Words: "Assign To", "New Character…", "Narration (not a line)", "Undo Assign Line"
- Access: arrow keys move, Escape closes
Status: done

### S-006 Fix a whole exchange
As the narrator, when I fix one line in an untagged back-and-forth, InnerVoice offers to swap the following lines of that exchange.
Screens: exchange-banner
Depends: S-005
Acceptance:
- [unit] Fixing line k of an A/B exchange to B proposes B/A for k+1… until a tagged line or narration break.
- [snap] A banner says "Swap the next 6 lines too?" with Swap and Keep.
UX:
- Flow: after an assign that breaks an exchange -> banner under the line "Swap the next 6 lines of this exchange too?" -> Swap / Keep -> ⌘Z undoes
- Surface: inline banner (information tied to the line, no modal)
- Words: "Swap", "Keep"
Status: done

### S-007 Pick the AI model
As the narrator, I see models recommended for my Mac's memory, with a note on each, and download the one I choose.
Screens: settings-ai, onboarding-model
Acceptance:
- [unit] Recommendation tiers: 8 GB, 16 GB, 24–36 GB, 48 GB+; each model's note says why it fits or not.
- [self] Download shows progress, can be canceled and resumes; the file's size is checked.
- [manual] Apple's on-device model is offered when available, with a note that it is quicker but less accurate.
UX:
- Flow: first Tag Speakers without a model, or Settings › AI -> list of models, recommended one first with "Best for your Mac (32 GB)" -> Download -> progress row with Cancel -> Use This Model
- Surface: Settings pane, and a sheet the first time (one short task before tagging)
- States: downloading "2.1 of 5.0 GB"; no space "Not enough free space: 5 GB needed"; offline "Can't reach Hugging Face. Check your connection."
- Words: "Download", "Use This Model", "Remove Download", note under each model ("Fits easily in 16 GB…")
- Privacy: "Your manuscript never leaves this Mac. Only the model is downloaded."
Status: done

## M2 Cast

### S-010 See the cast
As the narrator, I see every character as a card with color, name, line count, word count, first appearance and narrator.
Screens: cast
Depends: S-003
Status: done

### S-011 Read a character's summary
As the narrator, I open a character and read age, gender, accent, voice, personality, history and relationships, each fact with a link to the line it came from.
Screens: character-detail
Depends: S-010, S-012
Acceptance:
- [snap] Each fact shows a source link "Ch 3 ¶12"; clicking it shows the line in the manuscript.
- [snap] Facts revealed after the character's first line show a "Revealed late" badge with "First speaks Ch 2, revealed Ch 20".
Status: done

### S-012 Build character summaries with the AI
As the narrator, I press Build Summaries; the model reads the text around each character and fills in facts.
Depends: S-003
Acceptance:
- [unit] Facts are merged by kind; a fact the narrator edited is never replaced.
- [self] Facts whose quote cannot be found in the text are dropped.
Status: done

### S-013 Edit a character
As the narrator, I change name, aliases, color, any fact, add my own notes and voice notes.
Screens: character-detail
Depends: S-011
Status: done

### S-014 Set character colors
As the narrator, I pick each character's color from a palette made for highlighting, never red; new characters get the next distinct color.
Screens: character-detail, settings-appearance
Status: done

### S-015 Merge characters
As the narrator, I select "Tom" and "Thomas" and choose Merge; all lines and facts move to one character and the other name becomes an alias.
Screens: cast
Status: done

### S-016 Move lines to another character
As the narrator, I select several lines (or all of a character's lines in a chapter) and give them to another character at once.
Screens: manuscript
Status: done

### S-017 Highlight or outline
As the narrator, I choose Highlight or Outline in the toolbar and in Settings › Manuscript; the choice is saved per project.
Screens: manuscript, manuscript-outline
Status: done

## M3 Find lines

### S-020 Use the index
As the narrator, I open the Index and see each character's lines grouped by chapter, each a link to the line.
Screens: index
Depends: S-003
Status: done

### S-021 Turn a character on or off in the index
As the narrator, I switch characters on or off in the index; main characters with many lines start off.
Screens: index
Acceptance:
- [unit] Characters with over 15% of all lines start hidden.
Status: done

### S-022 Rehearse a character's lines
As the narrator, I choose Rehearse for a character and step through only their lines, each with the line before it as a cue.
Screens: rehearse
Status: done

### S-023 Search the book
As the narrator, I press ⌘F and find text; ⌘G goes to the next match.
Screens: manuscript-search
Status: done

### S-024 Jump to a chapter
As the narrator, I pick a chapter from the sidebar outline to scroll there.
Screens: manuscript
Status: done

## M4 Production

### S-030 Add narrators and cast them
As the producer, I add narrators and assign each character to one.
Screens: cast-narrators
Status: done

### S-031 Export a narrator's script
As the producer, I export a PDF or Word file per narrator with only their lines, the cue line before each, chapter and paragraph references, and their characters' summaries.
Screens: export-sheet
Status: done

### S-032 Export the colored manuscript
As the narrator, I export the whole tagged manuscript as PDF (print or iPad) with colors, initials and a character key.
Screens: export-sheet
Status: done

### S-033 Export the cast and pronunciation lists
As the narrator, I export the cast list and pronunciation guide as PDF and CSV.
Status: done

## M5 Pronunciations

### S-040 Collect pronunciations
As the narrator, I see names, places and unusual words the AI found, with how often and where they appear.
Screens: pronunciations
Status: done

### S-041 Add how to say it
As the narrator, I type a pronunciation ("SHEV-on") and notes, and mark it confirmed with the author.
Screens: pronunciations
Status: done

### S-042 Record a sample
As the narrator, I record and play a short audio sample of a pronunciation.
Screens: pronunciations
Acceptance:
- [manual] Microphone permission is asked only when I press Record.
Status: done

## M6 Record

### S-050 Read in Record mode
As the narrator, I open Record and read one paragraph at a time in large type with the speaker's color and name, and the next speaker shown below.
Screens: record, record-dark
Status: done

### S-051 Step with keys or a foot pedal
As the narrator, I move with Space / → and ← (foot pedals send these), and mark the current line recorded.
Screens: record
Status: done

### S-052 Note a retake
As the narrator, I mark a line for a retake (R) with a note; the Retakes list shows them all.
Screens: record, retakes
Status: done

### S-053 Track progress
As the narrator, I see how much of the book is recorded per chapter.
Screens: record
Status: done

## M7 Revisions

### S-060 Import a revised manuscript
As the narrator, I choose File › Import Revised Manuscript…, and InnerVoice keeps tags, notes, recorded state and retakes on unchanged paragraphs.
Screens: revision-review
Acceptance:
- [unit] Paragraph matching survives inserted, deleted, moved and lightly edited paragraphs (diff + similarity).
- [unit] Lines in edited paragraphs keep their speaker when their quote text matches.
Status: done

### S-061 Review what changed
As the narrator, I see a list of added, changed and removed paragraphs, jump to each, and changed paragraphs re-run through the AI.
Screens: revision-review
Status: done

## M8 System and polish

### S-070 Start window
As the narrator, I see recent projects and a big drop area when no project is open.
Screens: start
Status: done

### S-071 Settings
General (appearance, Dock/menu bar, updates, sync), Manuscript (style, confidence threshold, font), AI (model), Recording (font size, keys).
Screens: settings-general, settings-manuscript, settings-ai, settings-recording
Status: done

### S-072 Help, About, updater, menu hub, CLI, URLs
House standards shared with the other apps.
Screens: help, about
Status: done

### S-073 App icon
Status: done

## Dropped
- Accounts and sync servers: one narrator per Mac; projects are files.
- Live collaboration: narrators get exported scripts.
- Audio recording of the narration: the DAW does that.
- iPad app: later.
