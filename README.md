# Inner Voice

Inner Voice helps audiobook narrators prepare a book before they press record. Open the manuscript and an AI that runs entirely on your Mac reads it, colors every spoken line by who says it, and builds a summary of each character from what the book says about them: age, gender, accent, voice, personality, history. When it can't tell who's speaking, the line turns red so you can settle it with a click.

No more highlighter pens, character notes in a separate document, or hunting for a minor character's three lines in chapter 31. Your manuscript never leaves your Mac.

<p align="center"><img src="docs/images/manuscript.jpg" width="820" alt="The manuscript in Inner Voice: spoken lines highlighted in each character's color, initials in the margin, unsure lines in red, and the inspector showing the selected line"></p>

## Download

### [⬇ Download Inner Voice for Mac](https://github.com/smanke-org/InnerVoice/releases/latest/download/InnerVoice.dmg)

1. Open `InnerVoice.dmg`.
2. Drag Inner Voice to Applications.
3. Open Inner Voice and drop a manuscript on the Welcome window.

The first time you click Tag Speakers, Inner Voice suggests an AI model for your Mac's memory and downloads it once (2.5 to 19 GB). Requires macOS 26 or later on Apple silicon. Free.

## Features

- **Opens the manuscript you were sent.** Word (.docx and .doc), PDF, RTF, plain text, Markdown, OpenDocument, Pages (with a preview saved), EPUB and HTML. PDF page numbers, running headers and hyphenation are cleaned up. For Google Docs, choose File › Download › Microsoft Word first.
- **Tag Speakers.** Plain rules find every line first ("said Tom", a quote that runs across paragraphs, a two-person back-and-forth, italic thoughts, letters), then the on-device AI decides who speaks the rest. Chapters fill with color while you keep reading; Stop keeps what's done.
- **Your choice of model.** Inner Voice reads your Mac's memory and recommends a model, with a note on every model saying why it fits or doesn't: from a 2.5 GB model for 8 GB Macs to a 19 GB one for 64 GB Macs, or Apple's built-in on-device model with no download. You can also use any GGUF model file.
- **Highlight or outline.** Each character has a color (red is kept for lines that need you). Thoughts are dashed and written text is dotted, and the speaker's initials sit in the margin, so color is never the only clue.
- **Fix lines in a click.** Click a line, type a few letters or press 1–9, and it's assigned. Fix one line of a back-and-forth and Inner Voice offers to swap the rest of the exchange. Select a passage to assign several lines at once, merge "Mara" into "Mara Quinn", or make a line from text it missed. Everything can be undone.
- **Character summaries you can check.** Each fact the AI finds links to the paragraph it came from, and facts revealed only after a character first speaks are flagged **Revealed Late**, so you know about that Highland accent before chapter 2, not after chapter 20. Edit any fact, add your own, and keep voice notes.
- **Index.** Every character's lines by chapter, each a link into the book. Main characters start hidden so the index stays short; turn any character on or off.
- **Rehearse.** Step through one character's lines, each with the line before it as a cue.
- **Multi-narrator productions.** Add narrators, cast characters to them, and export a Narrator Script for each one (PDF or Word) with their lines, cue lines and character summaries.
- **Pronunciations.** Inner Voice lists the names, places and invented words it finds. Add how to say each one, mark it confirmed with the author, and record a short sample.
- **Record.** A large-type teleprompter for the booth: one paragraph at a time, the speaker's color and voice cue, the next speaker below. Space or → moves on and marks the paragraph recorded, ← goes back, R marks a retake with a note. Foot pedals that send keys work too.
- **Revised manuscripts.** When the author sends a new draft, import it: tags, notes, recorded marks and retakes stay on every paragraph that didn't change, edited lines keep their speakers, and a Revisions list walks you through what's new.
- **Exports.** The tagged manuscript as a color PDF for print or iPad, Narrator Scripts, the cast list and the pronunciation guide (PDF, Word or CSV).
- **The usual comforts.** Light or dark appearance, menu bar and Dock icons you can hide, Ask before quitting, settings that follow you to your other Macs, updates from GitHub, ToolHUD and ShortcutsLens support, and Help built in (⌘?).

## Screenshots

<table>
<tr>
<td width="50%"><img src="docs/images/cast.jpg" alt="The Cast page: a card per character with initials, voice cue and line counts"><br><sub><b>Cast.</b> Every character with a voice cue from their summary.</sub></td>
<td width="50%"><img src="docs/images/character.jpg" alt="A character page with summary facts, each linked to its source paragraph, and a Revealed Late badge"><br><sub><b>Summaries.</b> Each fact links to the line it came from.</sub></td>
</tr>
<tr>
<td width="50%"><img src="docs/images/record.jpg" alt="Record: the current paragraph in large type with the speaker's name and voice cue"><br><sub><b>Record.</b> A teleprompter for the booth.</sub></td>
<td width="50%"><img src="docs/images/models.jpg" alt="Choosing a model: each model with a fit badge and a note about this Mac's memory"><br><sub><b>Models.</b> Recommended for your Mac's memory.</sub></td>
</tr>
</table>

## Privacy

Inner Voice reads your manuscript with a model running on your Mac. Nothing in the book is sent anywhere. The only downloads are the model you choose (once, from Hugging Face) and Inner Voice's own updates (from GitHub).

## Updates

Inner Voice checks GitHub for new versions (you choose how often, or turn it off, in Settings › General) and can install them for you.

## Building

```bash
Tools/fetch_llama.sh        # the pinned llama.cpp framework (once)
./build.sh                  # .build/app/Inner Voice.app
swift test                  # unit tests
tools/selftest.sh           # in-app self-test on the sample book
```

## License

© 2026 Steve Manke. All rights reserved.
