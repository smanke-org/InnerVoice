# InnerVoice glossary

## Voice
Calm, clear, encouraging. Talk to the narrator as "you". Say "click".

| Term | Means | Not |
| --- | --- | --- |
| Project | One book's `.innervoice` file: manuscript, cast, notes | document, book file |
| Manuscript | The book's text inside a project | document, file, script (except Narrator Script) |
| Chapter | A section of the manuscript that starts at a heading | section, part (Part is a heading kind) |
| Paragraph | One block of text in a chapter; referenced as "Ch 3 ¶12", or on Review cards by the chapter's own title, "Chapter 6 · ¶26" | block |
| Line | One piece of text spoken, thought or written by a character | quote, utterance, dialogue item |
| Spoken / Thought / Written | The three kinds of line | speech, monologue, text |
| Narration | Text that is not a line; read in the narrator's own voice | prose, body |
| Character | Someone in the book who has lines | role, persona |
| Speaker | The character a given line belongs to (speaker lists, speakers in the margin) | voice, owner |
| Alias | Another name the same character goes by | nickname field |
| Cast | All the characters in a project | roster, people |
| Color | A character's color in the manuscript | tag color, label |
| Automatic (color) | The default: InnerVoice picks the color from the character's gender (masculine blues and greens, feminine pinks and purples, neutral warm colors) and keeps it up to date | auto, default color |
| Highlight / Outline / Underline | The three ways a line can show its color | fill, border, rule |
| Who's Speaking? | A line the AI could not decide on, shown in red | unassigned, unknown, untagged |
| Unsure | A line the AI or the rules tagged with a best guess but low confidence; red, with "Name?" in the margin | low confidence |
| Best guess | The speaker an Unsure line probably has, for the narrator to confirm | suggestion, prediction |
| Steps | The bar across the top of the window: Manuscript, Speakers, Review, Characters, then Voices and Listen (AI voices) or Pronunciations and Record (narration), each with how far along it is; the next step has a button | wizard, tour, onboarding, checklist |
| Skip | Mark a step done without doing it; right-click the step to bring it back | dismiss, ignore |
| To review | Lines in a chapter that still need the narrator ("2 to review" in the sidebar) | needs attention, flagged |
| Review | The page listing every line, tone and voice that needs the narrator, with context | queue, inbox |
| Context | On Review, how many paragraphs show before and after each line's paragraph (This paragraph only up to 5 paragraphs around) | surrounding text, excerpt |
| Tone | How a line is said: neutral, excited, confused, sad, angry, whispered, scared, tender or sarcastic | emotion, mood, style |
| Voice | The speech voice a character (or the narrator) is read in, on one engine | TTS voice, speaker voice |
| Engine | Where voices come from: Apple Voices, Qwen3-TTS or Orpheus, all on the Mac | provider, backend |
| Voice pack | The downloaded models for Qwen3-TTS or Orpheus, in Settings › Voices | model files, weights |
| Voice description | The words Qwen3-TTS designs a character's voice from | prompt, voice prompt |
| Voice sample | A few seconds Qwen3-TTS records in a designed voice (one per tone) and speaks every line from | reference clip |
| Ready-made voice | One of Qwen3-TTS's 16 named voices (Warm Storyteller, Gravelly Elder…), each a fixed description | preset |
| Mix Engines | A project casting voices from every engine that's on, not one engine | mixed mode, hybrid |
| Same as Narrator | A character whose lines are read in the narrator's voice | narrator voice, default voice |
| Project type | What a project is for: Narration Tools or Produce with AI Voices; decides which pages and commands show | mode, workflow |
| Project Settings | Book › Project Settings: settings that belong to one book (its project type) | document settings |
| Activity | Settings › Activity: what InnerVoice did for you across books (numbers only) | analytics, usage, telemetry |
| Book Stats | Book › Book Stats: the open book's numbers | statistics, report |
| Lines tagged for you | Lines the rules or the AI gave a speaker | auto-tagged |
| Pause | A gap of silence after a sentence, line, paragraph, scene break, chapter title or chapter, set in Settings › Voices › Pauses | gap, silence, break |
| Playback speed | How fast Play plays (0.75× to 2×), pauses included; Export Audio is always 1× | rate, tempo |
| From the book | The part of a character's Voice Notes Build Summary writes (age, gender, accent, voice, manner); the narrator's once edited | AI notes, auto notes |
| Design a Voice from Voice Notes | ElevenLabs designs a new voice from a character's description and Voice Notes (Cloud; uses credits and a voice slot) | clone, generate voice |
| Use Voices from Another Project | Copy voices (and optionally Voice Notes, narrator voice, engine) from an earlier book in a series, after matching its characters to this book's | import cast, series voices, voice transfer |
| Voice brief | The AI's casting note in Voice Notes: pitch, pace, texture, attitude, and the contrast with scene partners | voice profile, direction |
| Cloud | Marks ElevenLabs, the one engine that sends text off the Mac | online, remote |
| Send This Book to ElevenLabs | The per-book permission before any of its text goes to ElevenLabs; Stop Sending This Book ends it | consent, opt in |
| Sound | A sigh, laugh, gasp, groan, sniffle, yawn or cough a speech tag asks for; Orpheus performs it | effect, tag |
| Casting | Choosing a voice for each character from the summary; Cast Voices Again redoes it | auto-assign, matching |
| Narrator voice | The voice that reads narration and lines nobody is cast for | default voice |
| Play Line / Play from Here | Hear the selected line, or a table read from it to the end | preview, speak |
| Table read | Play from Here with narration read too (Settings › Voices) | read-through |
| Export Audio | Saving the voices as M4A or WAV files | render, bounce |
| Assign | Give a line to a character | tag, retag, set speaker |
| Pin | Keep a character at the top of every speaker list | favorite, star |
| Delete Text | Take text the narrator doesn't read out of the project (the imported file never changes) | remove, hide, strike |
| Tag Speakers | Run the AI over the manuscript to assign lines | analyze, process, parse |
| Summary | What the book says about a character: age, gender, accent, voice, personality, history, relationships | profile, bio |
| Fact | One item in a summary, with its source line | attribute, trait |
| Source | The paragraph a fact came from | citation, reference |
| Revealed Late | A fact first mentioned after the character's first line | spoiler |
| Build Summaries | Run the AI to fill in summaries | generate profiles |
| Merge | Make two characters one; the other name becomes an alias | combine, join |
| Index | Every character's lines by chapter, with links | line list, lookup |
| Show in Index | The per-character switch for the index | visible, enabled |
| Rehearse | Step through one character's lines with cue lines | practice |
| Cue | The line just before one of yours | prompt, previous line |
| Narrator | A person reading the audiobook; characters are cast to narrators | reader, voice actor |
| Narrator Script | An export with one narrator's lines | sides, part |
| Pronunciation | A term and how to say it | glossary entry, word |
| Record | The teleprompter mode for the booth | studio, session, prompter |
| Recorded | A paragraph the narrator has read in Record | done, complete |
| Retake | A paragraph marked to read again, with a note | pickup (said once in Help), redo |
| Revised Manuscript | A new draft imported into an existing project | update, new version |
| Model | The on-device AI that reads the book | engine, LLM |
