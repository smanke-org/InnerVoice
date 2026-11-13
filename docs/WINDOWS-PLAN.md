# InnerVoice for Windows — plan

## Parity (decided 2026-11-12, replaces the Narration-Tools-only scope below)

The Mac app is the primary version. Windows gets **feature-by-feature parity wherever
possible**, and every new Mac feature gets a Windows port by default. Anything below
that is marked "Dropped" or "Later" is now in scope:

- **AI voices, both project types:** the Voices page, Play and table reads, Export Audio,
  pauses and speed, the tone pass, Casting Style audio, Series Voices, Clone a Voice,
  spoken chapter recaps, delivery hints, Export EPUB with Audio.
- **Voice engines:**
  - Windows built-in voices (the stand-in for Apple's voices: instant, no download, the
    fallback on slow PCs).
  - ElevenLabs (cloud, same consent gates as the Mac).
  - Qwen3-TTS and Orpheus, the same models as the Mac, through ONNX Runtime with DirectML
    (any DirectX 12 GPU), with a CPU fallback. Same voice packs and design/clone behavior;
    where a model part won't export, say so and match the Mac as closely as possible.
- **Also ported:** the agent CLI and MCP (`innervoice`, named pipes), `innervoice://` URLs,
  and Pages import.
- **Distribution: the Microsoft Store is the primary channel** (free for individual
  developers; the Store signs the MSIX, so there is no SmartScreen warning, and it handles
  installs and updates). The Mac's GitHub-releases updater is **not** ported: Store copies
  update through the Store. No paid code signing. An unsigned build may still be posted on
  GitHub releases, with a note that SmartScreen will warn ("More info → Run anyway") and
  that it doesn't update itself.
- **Widest range of PCs:** x64 and ARM64, GPU or CPU-only. Before a local voice model is
  downloaded or used, the app checks the hardware and, on weak machines, warns plainly why
  voices may be unusably slow there and suggests the built-in voices or ElevenLabs.
  Settings shows what makes a better experience: a DirectX 12 GPU with 8 GB or more of
  video memory (for example NVIDIA RTX 3060 or AMD RX 6700 XT and up) and 16 GB of RAM
  (32 GB for the larger models) is ideal; integrated graphics or a CPU alone will work,
  slowly. Measure real speeds on test hardware before shipping these numbers.

### AI voices, part A (done 2026-11-13, branch `claude/win-voices-a-7d409e`)

Everything but the local neural models. Ported file for file into `InnerVoice.Core`, with
the Mac's tests: `Voices.cs` (tones, engines, `VoiceChoice`/`CastVoice`, `VoiceInfo` with
Orpheus's voices and the Qwen3-TTS presets, `VoiceCasting`), `ToneRules.cs` (+ `TonePrompt`,
run inside Tag Speakers), `SpeechScript.cs` (segments, pauses, pronunciations, recaps; a
rule-based `SentenceSplitter` stands in for NaturalLanguage and is tested against the Mac's
cases), `SeriesVoices.cs`, `VoiceDescription` (in `VoiceNotes.cs`), the voice titles and
`SharedRolesNeeded` in `CastingStyle.cs`, and Voices/Listen in `GuidedSteps.cs`. Tones and
voices are modeled now (`Line.Tone…`, `BookCharacter.Voices`, `ProjectSettings.VoiceEngine`,
`NarratorVoices`, `RoleVoices`, `CloudVoicesAllowed`, `Project.TonesTagged/VoicesCast`), with
the Mac's JSON names; `RoundTripTests` still compares the Mac fixture field by field.

The app has the Voices page, Play Line / Play from Here (follow-along), tones on the line's
right-click menu, Settings › Voices, Export Audio (M4A via WinRT `MediaTranscoder`, or WAV),
Export EPUB with Audio, Series Voices, Clone a Voice (ElevenLabs), Voice Design, Cloud
Usage for ElevenLabs, Project Type (asked once; Book › Project Type…), and Help. New Windows
projects no longer default to Narration Tools: they ask, like the Mac.

Shortcuts differ from the Mac where Windows reserves the Mac's: Play Line is Ctrl+Space
(⌥Space opens the window menu on Windows), Play from Here Ctrl+Shift+Space, Stop Playing
Ctrl+Shift+. (Ctrl+. stops an AI run), Voices page Ctrl+5.

A Mac book's Apple voices (`com.apple.voice…`) speak on Windows as the closest Windows voice
of the same gender ("Ava (Mac voice)"); the book keeps the Mac's choice. The Mac doesn't yet
do the reverse for Windows voice IDs (a Mac follow-up: it shows them as missing).

### Voice engines (the interface part B plugs into)

`InnerVoice.Core/Speech/IVoiceEngine.cs`. One `IVoiceEngine` per `VoiceEngine` value (`apple`,
`qwen`, `orpheus`, `elevenlabs`; never `mixed`), registered once at startup with
`VoiceEngineRegistry.Register` (App: `Speech/VoiceActions.cs` › `VoiceSetup.Start`).

| Member | Contract |
| --- | --- |
| `Engine` | The `VoiceEngine` it implements. |
| `IsReady` | Can speak now (downloaded, or set up). Cheap: views read it every redraw. |
| `NotReadyReason` | Words for the narrator when not ready (Settings › Voices, errors). |
| `Voices` | Its voices, best first, cached by the engine. Qwen3-TTS: `VoiceInfo.QwenPresets` plus clones (`Cloned = true`, voiceID `clone:<id>`). Orpheus: `VoiceInfo.Orpheus`. |
| `Info(choice)` | The `VoiceInfo` a choice names, or null. |
| `ShadesPitch` / `ShadesPace` | The engine shades by itself (Qwen3-TTS: pitch-shift the voice sample before cloning, as the Mac does; pace is left to the renderer). When false, `SpeechRenderer` shifts the finished audio (`ShadeAudio`, resample + WSOLA). |
| `Render(request, ct)` | One `SpeechRequest` → 24 kHz mono float samples. May run on any thread, several at once (the player renders two ahead). Throw `SpeechException` with words for the narrator. |
| `Prepare(ct)` | Optional: load the model ahead of the first line (the Voices page calls it). |
| `Unload()` | Optional: free memory when the narrator turns the engine off. |

What part B gets for free: the cache (`SpeechRenderer`, keyed by the Mac's `SpeechRequest`
cache key, in LocalAppData\InnerVoice\Speech, pruned at 1 GB), trimming each voice's
silence, pauses, playback speed, shading, Export Audio and EPUB with Audio, casting
(`VoiceCasting.Cast` designs Qwen3-TTS voices from summaries and Voice Notes;
`VoiceResolver.WithNotes` adds the notes at speaking time), the Voices page (it lists any
registered engine; Qwen shows Describe a Voice), engine switches and Mix Engines. Part B
still needs: the ONNX Runtime + DirectML engines (`SpeechAudio.Leveled` is the Mac's -20 dB
leveling), the voice-pack downloads and the hardware check in Settings › Voices (replace the
"Qwen3-TTS and Orpheus — Coming later" card), On This PC cloning in Clone a Voice, and
`Tone.Direction()` / `VocalSound` for the models that act.

Platform split: a `-r win-*` build targets `net10.0-windows10.0.19041.0` (WinRT speech and
Media Foundation, under `#if WINDOWS`); other builds are `net10.0` (tests on the Mac; on a
PC, SAPI through System.Speech). Audio out is NAudio's WinMM output; test runs play
silently unless `INNERVOICE_SPEECH_AUDIBLE` is set.

Original scope, kept for its porting notes:

A Windows version of InnerVoice with only the **Narration Tools** workflow: who's
speaking, Review, character summaries, Index, Rehearse, Pronunciations, the Record
teleprompter, Retakes, narrators and Narrator Scripts. No AI voices, no audio export.

It's a new app, not a port: SwiftUI, AppKit, TextKit and PDFKit don't exist on Windows.
What carries over is the Kit's logic (translated), the prompts and grammars (verbatim),
the GGUF models (the same files), the design and the words in `docs/GLOSSARY.md`, and
the `.innervoice` file format, so one book opens on both.

Everything below can be done at no cost. The only optional spending is code signing for
a download outside the Microsoft Store (see Distribution).

---

## 1. Stack

### Recommendation: .NET 10 + Avalonia (C#)

| Part | Choice | License |
|---|---|---|
| Runtime | **.NET 10** (LTS, supported to November 2028). .NET 8 reaches end of support on 2026-11-10, so don't start on it. | MIT |
| UI | **Avalonia** (latest stable), Fluent theme, MVVM with CommunityToolkit.Mvvm | MIT |
| Manuscript view | **AvaloniaEdit**, read-only document with a background renderer, a colorizer and a custom margin (details below) | MIT |
| Local AI | **llama.cpp** pinned to the Mac's build (b11205), through a thin P/Invoke binding; LLamaSharp as the fallback (details below) | MIT |
| PDF import | **PdfPig** (letters and words with positions, font sizes) | Apache-2.0 |
| Word import and export | **DocumentFormat.OpenXml** | MIT |
| HTML and EPUB chapters | **AngleSharp** (forgiving HTML), `System.Xml` for well-formed XHTML, OPF and ODT | MIT |
| RTF import | **RtfPipe** (RTF → HTML, then the HTML path) | MIT |
| PDF export | **QuestPDF** (per-run background color and solid, dashed and dotted underlines). Community license is free for individuals and businesses under $1M a year; re-check its terms before shipping. PDFsharp/MigraDoc (MIT) is the fallback, with the highlights drawn by hand. | Community |
| Pronunciation samples | **NAudio** (WASAPI capture, Media Foundation AAC encode to `.m4a`, playback) | MIT |
| Tests | xUnit, plus **Avalonia.Headless** for light and dark snapshots of every page (the `tools/snap.sh` equivalent) | Apache-2.0 / MIT |

Why this one:
- **Builds and runs on this Mac.** The .NET SDK for macOS publishes `win-x64` and
  `win-arm64` builds, and an Avalonia app also runs natively on macOS, so most UI work is
  iterated on the Mac without a VM. The VM is for Windows specifics (file dialogs, fonts,
  DPI, audio, the installer).
- **C# strings are UTF-16**, like `NSString`. Every `TextSpan` in a project is a UTF-16
  offset, so spans mean the same thing on both sides with no conversion. (Swift's
  `String.count` counts characters, `.Length` counts UTF-16 units; the few places the Kit
  uses `.count` for a threshold, like "a short line", are close enough.)
- **One language** for logic and UI, a mature text editor control, and first-class MSIX
  packaging for the Store.
- The Kit's value types map onto C# `record`s with immutable collections, which gives the
  cheap whole-project snapshots that undo depends on (see Undo below).

### Manuscript view in AvaloniaEdit

The Mac view is TextKit 1 with a custom layout manager plus a `MarginOverlay`. The
AvaloniaEdit equivalents:

| Mac | AvaloniaEdit |
|---|---|
| Paragraphs joined into one text storage | One `TextDocument`, paragraphs joined by `\n`, with a table of paragraph start offsets. A line's span maps straight to a document offset (both UTF-16). |
| Highlight style: colored background behind a line | `IBackgroundRenderer` on `KnownLayer.Background`, using `BackgroundGeometryBuilder` for spans that wrap |
| Outline style; red for Who's Speaking?/Unsure | Same renderer, stroked geometry |
| Colored rule under the words: solid for spoken, dashed for thoughts, dotted for written | `IBackgroundRenderer` on `KnownLayer.Selection`, drawing per-segment lines with a `Pen` whose `DashStyle` is solid, dash or dot |
| Italic runs, chapter heading sizes | `DocumentColorizingTransformer` (`ChangeLinePart` sets typeface and `FontRenderingEmSize`) |
| Initials in the margin | A custom `AbstractMargin` that asks the `TextView` for each visual line's top and draws the speaker's initials |
| Click a line → Assign popover | `TextView.GetPositionFloor` → offset → line → a `Flyout` with the speaker menu |
| ⌘F / ⌘G search | AvaloniaEdit's search panel (Ctrl+F, F3), restyled |
| Delete Text (paragraph cuts, chapters, headings) | The same `ProjectEditing` logic; the view is rebuilt from the project after each change |

Week one includes a spike: a placeholder novel of 150,000 words with a few thousand lines,
scrolled and redrawn after a change, must stay smooth. If it can't, the fallback is a
virtualized list of paragraphs, each a custom control drawing an Avalonia `TextLayout`
(more work for selection and search).

### Local AI: llama.cpp

`Sources/InnerVoice/AI/LlamaEngine.swift` talks to the llama.cpp C API directly (about 20
functions: model and context setup, the chat template, `llama_decode`, a sampler chain with
a grammar sampler, KV-cache reuse through `llama_memory_seq_rm`, LoRA adapters).
`ChatTemplate.swift` appends a prefill right after the assistant header, which generic
template engines don't allow.

- **Recommended:** a thin P/Invoke binding (about 300 lines of C#) over the **official
  llama.cpp b11205 Windows release DLLs** (`win-cpu-x64`, `win-cpu-arm64`,
  `win-vulkan-x64`). Both apps then run the same llama.cpp build, Gemma 4 and Qwen 3
  support is guaranteed, and `LlamaEngine.swift` ports line for line. Vulkan covers
  NVIDIA, AMD and Intel GPUs without the size of the CUDA runtime; CPU is the fallback and
  the only choice on ARM PCs.
- **LLamaSharp** is the working assumption and it would work, but each LLamaSharp release
  is tied to one llama.cpp build that trails upstream, so the newest models in
  `ModelCatalog` may not load, and its higher-level executors don't expose the prefill.
  Use it only if the spike shows its bundled build loads every catalog model; its
  low-level `NativeApi` would then replace the hand-written binding.

The prompts and GBNF grammars in `AI/Prompts.swift` are copied verbatim (they're strings).
The same GGUF files download from the same Hugging Face URLs and are checked against the
same SHA-256.

### Alternatives considered

**Tauri** (Rust shell + WebView2 + a TypeScript UI). Strong points: tiny installer, and the
manuscript is easy to style with CSS (`text-decoration-style: dashed | dotted` is one line).
Against it: two languages plus Rust bindings for llama.cpp, a DOM of 100,000+ spans for a
novel that needs hand-made virtualization, PDF line extraction through pdf.js in the
webview, Rust strings are UTF-8 (every span needs converting), and building the Windows
binary from a Mac needs `cargo-xwin` or CI. More moving parts for no feature gain.

**Swift on Windows** (swift.org toolchain). The pure Kit files would compile almost
unchanged: swift-corelibs-foundation has `JSONEncoder`, `XMLParser` and
`NSRegularExpression`. But there is no UI framework worth shipping: no SwiftUI, and the
WinRT/WinUI bindings for Swift are thin and lightly maintained. It can't be built from the
Mac, debugging and packaging tools are immature, and the importers that use AppKit and
PDFKit still need rewriting. The shared logic doesn't pay for an unproven UI layer.

**Verdict:** .NET 10 + Avalonia. Translating about 4,000 lines of pure logic is a known,
testable job; the golden fixtures in section 4 keep the two versions agreeing.

---

## 2. Features

### Ports (Narration Tools)

| Mac feature | Windows notes |
|---|---|
| Open a manuscript: Word (.docx), PDF, RTF, plain text, Markdown, OpenDocument, EPUB, HTML | Readers rewritten on the libraries above; chapter detection ported |
| Dialogue finding, rule attribution, Fix a whole exchange | Pure logic, ported |
| Tag Speakers with a local model (llama.cpp), model picker with memory advice, download and SHA-256 check | Advice reworded for PCs (RAM plus GPU memory, see below). The tone pass runs too (AI voices part A). |
| Manuscript: colors, highlight or outline, initials, line kinds, Unsure in red, Assign popover, merge and split characters, Delete Text, search, jump to chapter | AvaloniaEdit, see above |
| Review page | Ported |
| Cast and character summaries: facts linked to their line, late-reveal flags, line and word counts, Voice Notes ("From the book:" block, voice brief), colors from `CharacterPalette` (Automatic or picked), pin | Ported. Red stays reserved for Who's Speaking?/Unsure. |
| Build Summaries with the local model | Ported |
| Index per character with on/off, Rehearse a character's lines | Ported |
| Narrators, casting characters to narrators | Ported |
| Exports: Narrator Script, Tagged Manuscript, Cast List, Pronunciation Guide as PDF, Word or CSV | QuestPDF, OpenXml, CSV |
| Pronunciations: finder, how to say it, notes, recorded samples (15 s, `.m4a`) | NAudio; samples stay `.m4a` in `Audio/` so they play on both |
| Record teleprompter: large type, step with keys or a foot pedal, recorded marks, retakes | Ported. Pedals that send keys work the same. |
| Revised manuscript import and the Revisions review | `RevisionMerger` ported |
| Guided Steps bar | Ported, Narration Tools steps only (no Voices, no Listen) |
| Undo for every change | See Undo below |
| Settings, Help, About (last ten releases), Activity (counts only, never book text), System/Light/Dark switch | Windows versions of each |

### Dropped

| Mac feature | Why |
|---|---|
| AI voices: Apple voices, Qwen3-TTS and Orpheus on MLX, ElevenLabs, Voices page, Play, Export Audio, pauses and speed, Series Voices, the tone pass | Superseded by § Parity: ported in part A (Windows Voices, ElevenLabs); Qwen3-TTS and Orpheus on ONNX Runtime are part B |
| Apple's on-device model (FoundationModels fallback) | Mac only. Without a model, the Windows app still finds dialogue, applies the rules and lets the narrator tag by hand. (Windows' own on-device model only exists on Copilot+ PCs; not worth a second engine.) |
| Pages import | Pages is a Mac app; the error already tells people to export to Word |
| `.rtfd` import, `.doc` (Word 97) import | Mac-only formats / no free reader. A `.doc` gets a "Save it as .docx" message. |
| iCloud settings sync | No equivalent worth adding. Books themselves move as single files (below). |
| ToolHUD menu hub, ShortcutsLens report | Mac house features |
| Dock and menu bar choices, menu bar item | No Windows equivalent needed |
| Agent CLI `innervoice` + MCP, `innervoice://` URLs | Later, if asked for (named pipes and an MSIX protocol activation) |
| Project type question | Superseded: ported in part A (asked once, Book › Project Type…) |

### Needs a Windows equivalent

**Updates.** The Microsoft Store installs updates by itself, so the Store build needs no
updater. The Mac's GitHub-releases updater is not ported. If a direct download is added
later, Velopack (MIT) updates from GitHub releases; Windows releases would then need tags
that never collide with the Mac's (see Distribution).

**Settings storage.** A JSON file in `%LOCALAPPDATA%\InnerVoice\settings.json` (works
packaged and unpacked; MSIX redirects it to the package's private folder). It holds the
model choice, the Unsure threshold, appearance, recent projects, Activity counts, window
sizes. Recent projects also go in the taskbar Jump List.

**Model storage.** `%LOCALAPPDATA%\InnerVoice\Models`, with a Settings button to pick
another folder (models are gigabytes; MSIX deletes package data on uninstall). Free space
is checked with `DriveInfo` before a download.

**Memory advice.** The Mac reads unified memory. On a PC: total RAM
(`GlobalMemoryStatusEx`) and the GPU's dedicated memory (DXGI). A model that fits in GPU
memory gets every layer offloaded; one that doesn't runs partly or fully on the CPU, and the
note says it will be slower. Same `Fit` levels (recommended, good, tight, too large).

**Saving.** Autosave in place like the Mac, written to a temporary file and swapped in with
`File.Replace` so a crash or a OneDrive sync never leaves half a file. Before saving, check
the file's modification time; if another computer changed it, ask before overwriting.

**Undo.** The Mac snapshots the whole `Project` (Swift value types with copy-on-write) on
every `change`. In C#, `Project` and its parts are immutable records over
`System.Collections.Immutable` collections, so a snapshot is a reference and a change
copies only the path it touches. Undo and redo are stacks of snapshots, one per `change`,
named like the Mac's menu items.

**Text fields.** The Mac's `DraftTextField` rule carries over: a field bound to the project
types into a draft and commits on a pause, Enter or focus loss, as one undo step, because
every change rebuilds `BookIndex`.

**Keyboard.** ⌘ becomes Ctrl; ⌘G becomes F3. Menus live in the window's menu bar (File,
Edit, View, Help), not a global menu.

**File association.** `.innervoice` registered in the MSIX manifest, with the app icon.
Double-click opens the book in its own window.

### File format: one book, both apps

A project is a zip (`ProjectArchive.swift`):

```
Placeholder Book.innervoice  (zip, UTF-8 names)
  project.json
  Original/<manuscript file>
  Audio/<sample>.m4a
```

`System.IO.Compression.ZipArchive` reads and writes this as is. The rules for
`project.json`, which the Mac writes with Swift's default `JSONEncoder` (sorted keys):

1. **Dates** are numbers: seconds since 2001-01-01 00:00:00 UTC (Swift's reference date),
   not ISO strings. A `System.Text.Json` converter reads and writes them.
2. **UUIDs** are uppercase strings. .NET writes lowercase by default; the converter
   uppercases. Reading is case-insensitive.
3. **Every non-optional property is written, always.** The Mac uses synthesized `Codable`,
   which fails on a missing key even when the Swift property has a default value. Optional
   properties are left out when empty (that's how Swift writes them) and are fine missing.
4. **Enums** are their raw strings (`spoken`, `thought`, `written`; `narration`,
   `aiVoices`; …). Sets (`recorded`) are arrays.
5. **Keep what you don't know.** Every model class has a `[JsonExtensionData]` bag, so
   the voice fields Windows doesn't use (`tone`, `toneSource`, `voices`, `voiceEngine`,
   `narratorVoices`, `cloudVoicesAllowed`, `voicesCast`, `tonesTagged`, …) and any field
   a newer Mac version adds come back out unchanged on save. A Mac book with voices
   survives a trip through the Windows app.
6. **Keep `Original/` and `Audio/`** entries on save, including ones Windows can't use.
7. **`purpose`:** as on the Mac since AI voices part A: nil (not asked) shows everything,
   the window asks once, and Book › Project Type… changes it. Narration Tools hides the
   voices; Produce with AI Voices hides the Index, Teleprompter, narrators and Narrator Scripts.
8. **`format`:** if it's higher than the Windows app knows, open read-only and say a newer
   InnerVoice made it.
9. **Schema changes start in the Kit.** Any new field is added to the Swift model first (or
   at the same time), with a fixture. Windows never invents a field the Mac doesn't know:
   the Mac ignores unknown keys and would drop them on its next save.
10. Old Mac projects saved as packages (folders) don't open on Windows; saving once on a
    current Mac version turns them into single files.

A book moves between Mac and PC through any synced folder (OneDrive, Dropbox, iCloud for
Windows) or a USB stick. There's no merging: the last save wins, guarded by rule
"Saving" above.

---

## 3. Porting the Kit

`Sources/InnerVoiceKit/` is 22 files, about 5,300 lines. Pure logic is translated to C#
in a class library `InnerVoice.Core` (no UI references, like the Kit), file for file, with
the same type names so diffs stay readable. `NSRegularExpression` patterns move to .NET
`Regex` (`\p{L}`, `\p{Ll}`, `\b` and the curly-quote classes work the same; use
`RegexOptions.CultureInvariant`).

| File | Lines | Status | Windows plan |
|---|---:|---|---|
| `Project.swift` | 550 | Pure | Records + JSON converters (rules above). Drop the `NSRange` helper. |
| `ProjectArchive.swift` | 254 | Apple's `Compression` framework (own zip writer) | Replace with `ZipArchive`; same layout |
| `ProjectEditing.swift` | 155 | Pure | Port |
| `ProjectBuilder.swift` | 83 | Pure (includes the pronunciation finder's tokenizer) | Port |
| `BookIndex.swift` | 114 | Pure | Port |
| `DialogueFinder.swift` | 210 | Pure | Port |
| `RuleAttribution.swift` | 389 | Pure (`CharacterPalette`, `RuleAttribution`, `Exchange`) | Port |
| `RevisionMerger.swift` | 154 | Pure | Port |
| `GuidedSteps.swift` | 114 | Pure | Ported, with the `voices` and `listen` steps (AI voices part A) |
| `Activity.swift` | 153 | Pure | Port (days in the PC's time zone) |
| `VoiceNotes.swift` | 116 | Pure | Port ("From the book:" blocks help a human narrator too) |
| `AI/Prompts.swift` | 408 | Pure (attribution and summary prompts, GBNF grammars, JSON reply parsing) | Port; grammars copied verbatim |
| `AI/ChatTemplate.swift` | 91 | Pure | Port |
| `AI/VoiceBrief.swift` | 125 | Pure | Port (the casting note is written for the narrator as well) |
| `AI/ModelCatalog.swift` | 122 | Pure (`ByteCountFormatter` only) | Port the catalog; rewrite `advice(for:memoryGB:)` for RAM + GPU memory |
| `Import/ChapterDetector.swift` | 124 | Pure | Port |
| `Import/ManuscriptImporter.swift` | 499 | **Mixed, AppKit** | Plain text and Markdown: pure, port. `DocxReader`, `OdtReader`: `XMLParser` delegates, port the logic onto `XmlReader`. `AttributedReader` (RTF, DOC, HTML through `NSAttributedString` and `NSFont`): rewrite on RtfPipe and AngleSharp, keeping its body-size and heading rules. `Zip.unpack` shells out to `ditto`: use `ZipArchive`. `PagesReader`: dropped. |
| `Import/EpubPdfReaders.swift` | 384 | **Mixed, AppKit + PDFKit** | EPUB: `XMLParser` parts port; the `NSAttributedString` HTML fallback becomes AngleSharp. PDF: line extraction (`PDFDocument`, line selections, `NSFont` sizes) is rewritten on PdfPig (group words into lines by baseline; size from the letters). `PDFLine`, `dropRunningLines`, `median`, `pageLefts` and `paragraphs(from:)` are pure and port line for line. PdfPig uses the same bottom-left PDF coordinates as PDFKit, so the per-page margins in `pageLefts` (mirrored margins in printed books) work unchanged. |
| `ToneRules.swift` | 286 | Pure | Ported with `TonePrompt` (AI voices part A); the tone pass runs inside Tag Speakers |
| `SpeechScript.swift` | 222 | `NaturalLanguage` | Ported (AI voices part A); a rule-based `SentenceSplitter` replaces NLTokenizer |
| `Voices.swift` | 650 | `Synchronization` | Ported (AI voices part A); tones and voices are modeled, no longer extension data |
| `SeriesVoices.swift` | 119 | Pure | Ported (AI voices part A) |

Ported: 18 files, about 4,100 lines of Swift, of which only the two importer files (about
880 lines) touch AppKit or PDFKit.

From the app target (`Sources/InnerVoice/`, about 19,800 lines), the Narration Tools part
is roughly 10,000 lines to rewrite as Avalonia views and view models: `Views/` without
`VoicesPage` and `SeriesVoicesSheet`, `Document/`, `Export/`, `AI/` (engine, model manager
without FoundationModels, tagging without the tone pass), Settings, and the Help, About,
Activity and appearance parts of `System/`. `Speech/`, `Agent/` and most of `System/` stay
behind.

### Keeping the two Kits in step

- **Golden fixtures**, in the repo at `Fixtures/` (placeholder text only, never a real
  manuscript): small manuscripts in every format, made on the Mac (the Swift tests already
  build them with textutil and CoreGraphics; Windows can't, so they're committed), each
  with the Swift importer's blocks and chapters as JSON. The C# tests must produce the same
  JSON. Same for `DialogueFinder`, `RuleAttribution`, `BookIndex` stats and
  `RevisionMerger` on the sample book from `SeedData.swift`.
- **Round-trip fixtures:** `.innervoice` files saved by the Mac (with voices, tones, a
  revision, a sample) are opened and saved by C#; the test compares the JSON
  field by field. A Swift test opens the C#-saved copies.
- A Swift test target writes the fixtures (`swift test --filter Fixtures`), so a Kit change
  that alters output shows up as a fixture diff in the same commit.
- Port the Kit tests in `Tests/InnerVoiceKitTests.swift` to xUnit alongside the code.

---

## 4. Milestones

Each ends in something a narrator can use. Repository: a `windows/` folder in this repo
(`windows/InnerVoice.sln`, `windows/src/InnerVoice.Core`, `windows/src/InnerVoice.App`,
`windows/tests`), so fixture and schema changes land in one commit with the Kit.

**W0 — Setup and spikes (about a week).** Solution, CI on GitHub Actions producing
`win-x64` and `win-arm64` zips, Windows 11 ARM VM. Spikes: the AvaloniaEdit renderer on a
placeholder novel (speed), and llama.cpp b11205 loading Gemma 4 E2B with a grammar on
Windows. Decide P/Invoke vs LLamaSharp here.
*Usable:* nothing yet for narrators; the risks are settled.

**W1 — Read a Mac book on Windows.** `Project` model and JSON rules, archive,
`BookIndex`, `CharacterPalette`; the window with sidebar; Manuscript (colors, outline,
line kinds, initials, chapters, search); Cast list and character summaries, read-only;
Record teleprompter with key/pedal stepping and recorded marks; saving recorded marks and
retakes. Round-trip fixtures pass.
*Usable:* tag the book on the Mac, read and record from it on the Windows PC in the booth.

**W2 — Open and tag a manuscript by hand.** All importers and chapter detection;
dialogue finding and rules; Assign popover, Fix a whole exchange, merge, split, colors;
Delete Text; undo/redo; autosave; Review page; Guided Steps. Importer fixtures pass.
*Usable:* the full workflow on Windows without AI.

**W3 — Local AI.** Model catalog, download with resume and SHA-256 check, RAM/GPU advice,
engine, Tag Speakers (speakers only), Build Summaries (facts, From the book, voice brief),
per-model Activity counts.
*Usable:* the Mac's core loop on a PC.

**W4 — The rest of Narration Tools.** Index with per-character on/off, Rehearse,
narrators and casting, Pronunciations with recorded samples, all four exports in PDF/Word/
CSV, Revisions (import a revised manuscript, review what changed).
*Usable:* feature parity with Narration Tools on the Mac.

**W5 — Ship.** Settings (General, AI, appearance), Help pages (from `HelpTopics.swift`,
minus voices), About with the last ten releases, file association, MSIX packaging in CI,
Store listing, privacy policy page on stevemanke.com, private-audience flight, then public
release.
*Usable:* installable from the Microsoft Store, updating itself.

Versions follow the house rule (A.B.C, C runs 0–9). MSIX needs four parts with a trailing
zero: `0.1.0` ships as `0.1.0.0`.

---

## W0 decisions (2026-11-10)

- **llama.cpp binding:** our own P/Invoke binding (`windows/src/InnerVoice.Core/AI/LlamaNative.cs`)
  over the official b11205 DLLs, not LLamaSharp. Struct sizes and offsets are tested against
  clang's numbers for b11205's `llama.h`. On this Mac it ran Gemma 4 26B A4B through the Mac
  app's llama.framework with a JSON grammar. `windows/tools/fetch-llama.sh` fetches the
  Windows DLLs (Vulkan x64, CPU ARM64) with SHA-256 checks. They need the Visual C++ runtime:
  CI copies it next to the app for zip builds; the Store package declares the VCLibs
  dependency.
- **Manuscript view:** a custom control drawing each paragraph with Avalonia's `TextLayout`
  (virtualized: only visible paragraphs are drawn), not AvaloniaEdit. AvaloniaEdit can't do
  paragraph spacing, centered scene breaks, indented block quotes or a line-height multiple
  per paragraph, which the Mac's reading surface has. `TextLayout.HitTestTextRange` gives
  the rectangles for highlights, outlines, underlines and margin labels.
- **Undo:** the model is mutable classes with a deep `Copy()` (strings are shared), and undo
  keeps a copy per change, like the Mac's value snapshots. Simpler to port than immutable
  collections, and cheap enough for a novel.
- **Fixtures:** in `windows/Fixtures/`, written by `Tests/InnerVoiceKitTests/WindowsFixtures.swift`
  only when `INNERVOICE_WRITE_FIXTURES` is set (normal Mac test runs leave the tree clean).
  The C# tests check the Mac project round-trips with identical JSON, and the rules give the
  same lines, speakers, sources and confidences; the Swift test reads the Windows-saved copy.

## 5. Distribution at no cost

### Microsoft Store (recommended, free)

- **Account:** individual developer registration in Partner Center is free (Microsoft
  dropped the fee for individuals in 2025; company accounts still pay). Needs a Microsoft
  account and identity check.
- **Signing:** submit an **MSIX**; the Store signs it with Microsoft's certificate. No
  certificate to buy, no SmartScreen warning.
- **Packaging:** an `AppxManifest.xml` (identity from Partner Center, `.innervoice` file
  association, `runFullTrust`, `internetClient` for model downloads, `microphone` for
  samples) and `makeappx pack` on a GitHub Actions Windows runner (the Windows SDK is
  preinstalled there; it isn't available on macOS). One bundle with x64 and ARM64.
- **Updates:** automatic through the Store.
- **Testing before release:** a private-audience listing or package flight in Partner
  Center installs the real signed package on chosen accounts, including the VM.
- **Store requirements:** privacy policy URL (models download from Hugging Face; book text
  never leaves the PC), age rating questionnaire, screenshots, description. Review takes
  a few days.

### Testing in a free Windows 11 ARM VM on this Mac

- **VMware Fusion Pro** is free (personal and commercial use since late 2024) and has 3D
  acceleration for Windows on ARM, so Avalonia's Skia rendering is smooth. **UTM** (free,
  open source) works as the fallback, with software rendering.
- **Windows:** Microsoft publishes a Windows 11 ARM64 ISO. Unactivated Windows runs fine
  for testing (a watermark and locked personalization settings only).
- **What it covers:** the `win-arm64` build natively, the `win-x64` build through Windows'
  Prism emulation, file dialogs, DPI scaling, fonts, audio recording, file association and
  the Store-installed package.
- **Limits:** no GPU for llama.cpp in the VM, so AI tests run on the CPU with the smallest
  model (Gemma 4 E2B; give the VM 8 GB+). Vulkan on a real x64 PC with an NVIDIA or AMD
  card is checked once before W3 ships (a friend's PC or a narrator beta tester).
- Move builds in through a shared folder; never put a real manuscript in the VM (use the
  sample book and placeholder fixtures).

### GitHub Actions

- The repo is public, so standard runners are free with no minute limit, including
  `windows-latest` (x64) and `windows-11-arm` (ARM64). (A private repo on the Free plan
  gets 2,000 minutes a month, and Windows minutes count double.)
- Jobs: build and test `InnerVoice.Core` on Windows and macOS on every push; publish both
  architectures; on a `win-v*` tag, build the MSIX bundle as an artifact for upload to
  Partner Center. Upload stays manual (or later the Microsoft Store submission API).
- Windows tags use a `win-v` prefix and are never GitHub *releases*, so the Mac updater,
  which reads this repo's latest release, never sees them.

### Signing a download outside the Store (optional, costs money)

Without a signature, a downloaded `.exe` or `.msix` gets SmartScreen's "Windows protected
your PC" warning (people must click More info → Run anyway), and an unsigned MSIX won't
install at all. Options:

| Option | Rough cost | Notes |
|---|---|---|
| **Azure Artifact Signing** (formerly Trusted Signing) | about $10 a month | Cheapest; open to individual developers in the US and Canada after identity validation. Reputation builds with downloads. |
| OV code signing certificate | about $200–400 a year, plus a hardware token or cloud HSM | Keys must live on hardware since 2023. |
| EV code signing certificate | about $300–600 a year | No longer skips SmartScreen's reputation check (since 2024), so not worth the premium. |
| SignPath Foundation | free | Only for open-source (OSI-licensed) projects; InnerVoice is "All rights reserved", so it doesn't qualify. |

Prices change; check them before buying. **Recommendation:** ship through the Store only.
Add a signed direct download (Artifact Signing + Velopack) only if narrators ask for one.

---

## 6. Risks

- **Manuscript view speed** with a whole novel in AvaloniaEdit — spiked in W0, fallback
  named above.
- **PDF paragraphs** depend on line grouping; PdfPig gives words, not PDFKit's line
  selections. The fixtures with mirrored margins and running headers catch differences.
- **llama.cpp on CPU** is much slower than on Apple silicon. The model advice must steer
  CPU-only PCs to the small models, and Tag Speakers must stay cancelable and resumable.
- **Two Kits drifting.** Fixtures, rule 9 of the file format, and one repo keep them in
  step; every Kit change gets a "Windows?" line in its commit message until W4 ships.
- **Windows N editions** lack Media Foundation, so sample recording needs the Media
  Feature Pack there; the app says so instead of failing silently.
