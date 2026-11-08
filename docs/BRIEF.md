# Inner Voice

A Mac app that reads an audiobook manuscript with an on-device AI, colors every
spoken line by who says it, and gives the narrator the cast, the voices, the
pronunciations and a teleprompter for the booth. For audiobook narrators and
multi-narrator productions.

## Replaces
Printing the manuscript and marking it up with highlighter pens, a separate
character notes document, and a hand-kept pronunciation list.

## Main jobs
1. **Open a manuscript** (Word, PDF, RTF, plain text, Markdown, OpenDocument,
   Pages, EPUB, HTML) and let the local AI tag who speaks every line.
2. **Read and fix** the colored manuscript: unsure lines are red; click any
   line to give it to another character, fix a whole exchange, merge or split
   characters.
3. **Know the cast**: each character's summary (age, gender, accent, voice,
   personality, history, relationships) with every fact linked to the line it
   came from, flags for facts revealed late, and line and word counts.
4. **Find lines**: an index per character with links to every line, which can
   be turned on or off per character; cast characters to narrators and export
   each narrator's script.
5. **Pronunciations**: names and made-up words found by the AI, with the
   narrator's own notes and short recorded samples.
6. **Record**: a large-type teleprompter that steps line by line with keys or
   a foot pedal, marks lines recorded and keeps notes for retakes.
7. **Revised manuscript**: import the author's new draft and keep every tag,
   correction and note on the paragraphs that did not change.

## Data
- Project (document package `.innervoice`): manuscript copy, settings, model choice
- Paragraph / Line: text, chapter, kind (narration, dialogue, thought, written), speaker, confidence, recorded state, note
- Character: name, aliases, color, summary facts (with source line), narrator, show in index
- Pronunciation: term, how to say it, note, audio sample
- Narrator: name, characters cast

## Shape
Document-based app (one window per book) with a sidebar: Manuscript, Cast,
Index, Pronunciations, Record, plus an inspector. macOS 26 or later, Apple
silicon. Local data, works offline. No accounts.

## AI
llama.cpp on the Mac, with a model the narrator picks from a list. Recommendations
come from the Mac's memory, with a note for each model on why it fits or not.
The model download is the only network use; manuscripts never leave the Mac.
Plain code finds the dialogue; the model only decides who speaks and reads the
character facts. Apple's on-device model is a no-download fallback.

## System hooks
Updater (public GitHub releases), Help, About, Settings, Light/Dark switch,
Dock and menu bar choices, ToolHUD menu hub, ShortcutsLens report, agent CLI
`innervoice` + MCP, `innervoice://` URLs.

## Reference
None (decided by the agent).

## Look
Native Mac with Liquid Glass, a calm reading surface, and a deep indigo accent
(#4F46E5) that never competes with the character colors. Red is reserved for
"who is speaking?" (decided by the agent).

## Not in version one
- iPad or iPhone app (maybe later, iCloud sync then)
- Audio recording of the narration itself (use a DAW)
- Sharing projects over a network (narrators get exported scripts instead)
