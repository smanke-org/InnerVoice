---
name: innervoice
description: Drive Inner Voice, the audiobook narrator's app on this Mac, from the shell with the `innervoice` command: see open projects, chapters, lines and the cast, assign lines to characters, fix line kinds, rename, recolor and merge characters, run Tag Speakers and Build Summaries, set pronunciations, choose the model, and export narrator scripts, the tagged manuscript, the cast list or the pronunciation guide. Use when the user asks about a book they are narrating, who speaks a line, their cast or pronunciations, or names Inner Voice.
---

# Inner Voice

`innervoice` runs commands in the Inner Voice app. Changes show in the project's window at once,
and Edit › Undo in that window takes them back. If the app is closed, the command opens it in the
background. If `innervoice` isn't on PATH, use
`"/Applications/Inner Voice.app/Contents/MacOS/InnerVoice"` the same way.

## Rules

- Always add `--json` and read the JSON. Exit 0 is ok; otherwise read `error.code` and
  `error.message`, and do what the message says.
- Commands work on the frontmost project window. Name another with `--project <name or path>`
  (names and ids from `innervoice projects list`). `no_project`: nothing is open; open a
  `.innervoice` with `projects open <path>` or start one from a manuscript with
  `projects new --manuscript <file>` (it opens untitled). `projects save` saves like File › Save;
  `projects save --as <path>.innervoice` saves to a new file (needed once for an untitled project).
- Line ids are 8 characters, from `innervoice lines list`. Refs read "Ch 3 ¶12".
  Characters are named by name, alias or id.
- Use the glossary's words with the person: Line, Character, Cast, Assign, Who's Speaking?
  (red, no speaker), Unsure (red, low confidence), Tag Speakers, Build Summaries, Narrator.
- `characters merge` and `lines assign --clear` only report what they would change. Show the user
  that report and ask before you run them again with `--yes`. `model download` reports the size
  (2.5 to 19 GB) and needs `--yes` too: ask first.
- Tag Speakers and Build Summaries run in the background: start, then poll `innervoice tag status`.
  `no_model`: no model is ready; `innervoice model list`, then `model select <id>` (downloaded
  ones only). Lines the narrator assigned are never changed by the AI.
- Character colors are never reddish (red means Who's Speaking?); the app refuses them.
- To try something without real data, run a Debug build (`./build.sh --debug`, then
  `.build/app/Inner Voice.app/Contents/MacOS/InnerVoice`) with `APP_STORE=/tmp/iv-test` in front, and
  `INNERVOICE_DEMO=1` to open the sample book (or `innervoice projects demo`). A test copy only
  opens projects in /tmp. Quit it by its process id: `kill $(lsof -t "$APP_STORE"*.sock)` or the
  pid you started, never `pkill -x InnerVoice`, which also quits the person's own copy (and
  their open book). SIGTERM closes a test copy's projects without saving.

## Main commands

| Command | Does |
| --- | --- |
| `innervoice status --json` | Version, open projects (frontmost first), model and whether it's ready, AI jobs |
| `innervoice lines list [--chapter N] [--speaker NAME] [--needs-you] --json` | Lines with id, ref, speaker, kind, source, confidence, text |
| `innervoice lines assign <id>... --to NAME [--new] --json` | Give lines to a character (`--new` makes the character) |
| `innervoice characters list --json` / `characters show NAME --json` | The cast; one character's facts with sources and Revealed Late flags |
| `innervoice tag start [--chapter N] --json` / `tag status --json` | Run Tag Speakers and follow it |
| `innervoice export --kind narrator-script --narrator NAME --format pdf --out PATH --json` | Write a Narrator Script (also manuscript, cast, pronunciations; pdf, word, csv) |

Everything else (chapters, line kinds, rename, color, merge, summaries, pronunciations, models,
`open <page>`): `innervoice help`, and `innervoice help <command>` for its options.

MCP server for Claude Desktop and other clients without a shell (not registered by default):
`claude mcp add --scope user innervoice -- "/Applications/Inner Voice.app/Contents/MacOS/InnerVoice" mcp`
