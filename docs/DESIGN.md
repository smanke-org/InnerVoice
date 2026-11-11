# InnerVoice design plan

## Archetype
Document-based app: one window per book (`.innervoice` package), each a
`NavigationSplitView` (sidebar + detail) with an inspector. A Welcome window
when no project is open. Settings window. Optional menu bar item (house standard).

## Information architecture
Sidebar, two levels at most:
- **Book**: Manuscript ⌘1, Cast ⌘2, Index ⌘3, Pronunciations ⌘4 (AI voices only), Teleprompter ⌘5 (narration), Review ⌘6, Voices ⌘7 (AI voices), Revisions (only after a revised manuscript)
- **Chapters**: every chapter, with a count of lines that need you
- **Cast**: the characters with the most lines, color dot + line count

Cast → character detail is a push inside the Cast page (back button).
Assign To is a popover on the line. InnerVoice doesn't record: narrators record in their own audio app.

## Command map
| Command | Menu | Shortcut |
| --- | --- | --- |
| New from Manuscript… | File | ⌘N |
| Open… / Open Recent | File | ⌘O |
| Import Revised Manuscript… | File | ⇧⌘I |
| Export… | File | ⇧⌘E |
| Tag Speakers | Book | ⇧⌘T |
| Stop | Book | ⌘. |
| Build Summaries | Book | ⇧⌘B |
| Find Pronunciations | Book | — |
| Next / Previous Line That Needs You | Book | ⌘↓ / ⌘↑ |
| Assign Line To… | Book | Return (on a selected line) |
| Highlight / Outline | View | ⌥⌘H / ⌥⌘O |
| Show Initials | View | — |
| Pages | View | ⌘1 … ⌘6 |
| Find | Edit | ⌘F (system find bar in the manuscript) |
| Show Sidebar / Inspector | View | ⌃⌘S / ⌃⌘I |

## Empty states
- Welcome window: drop area "Drop a manuscript here" + Open Manuscript… + recent projects.
- Manuscript before tagging: banner "InnerVoice found 2,314 lines. Tag Speakers to find who says each one."
- Cast with no characters: "No characters yet" + Tag Speakers.
- Index with every character hidden: "Turn on characters to list their lines."
- Pronunciations: "No unusual words found" + Add Term.

## Feeling
Calm and confident: a quiet reading surface where color does the talking.
Chrome stays system; the manuscript uses a serif reading face, generous line
height and a centered measure. Motion is short fades. Red appears only when
the narrator is needed.
