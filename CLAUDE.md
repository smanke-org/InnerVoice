# InnerVoice — notes for agents

Document-based Mac app for audiobook narrators: opens a manuscript, finds every line, tags who speaks it with an on-device model (llama.cpp or Apple's), and builds character summaries. Read `docs/BRIEF.md`, `docs/STORIES.md`, `docs/GLOSSARY.md` (one name per idea; use it in all UI text) and `docs/DESIGN.md` before changing anything.

## Layout
- `Sources/InnerVoiceKit/` pure logic with Swift Testing tests: `Project.swift` (the book model), `Import/` (readers for docx, odt, epub, pdf, pages, rtf, html, txt, md; chapter detection), `DialogueFinder`, `RuleAttribution` (+ palette, exchange), `AI/` (prompts, grammars, model catalog + memory advice), `RevisionMerger`, `ProjectBuilder` (+ pronunciation finder), `BookIndex`.
- `Sources/InnerVoice/` the app: `Document/` (ReferenceFileDocument `.innervoice`: one zip file, `ProjectArchive` in the Kit; 0.1.0 packages still open and are saved as one file; every edit goes through `change(_:undoManager:)` for undo), `AI/` (LlamaEngine, ModelManager, TaggingController), `Views/` (ManuscriptTextView is TextKit 1 with a custom layout manager for highlights and a MarginOverlay for initials), `Export/`, `System/` (house features: updater, sync, appearance, Dock/menu bar, ToolHUD hub, ShortcutsLens, About, Help; wired through `SystemHooks`).
- `Frameworks/llama.xcframework` is fetched by `Tools/fetch_llama.sh` (pinned b11205, not committed).

## Rules
- US spelling, "Settings", "click". Red is reserved for Who's Speaking?/Unsure; character colors come from `CharacterPalette` and never reddish.
- Never touch `NSDocumentController.shared` before launch finishes (menus included): SwiftUI must create it, or the app crashes in `createDocumentClassIfNeeded`. Use `RecentProjects`.
- Lines the narrator assigned (`source == .user`) are never changed by the AI or the rules.
- A project is always saved as one ordinary file: cloud folders (OneDrive) rename and replace packages under an open window, which then can't save.
- The model download is the only network use besides updates; manuscripts never leave the Mac.
- `InnerVoiceDocument` is `nonisolated` (ReferenceFileDocument); its mutation methods are `@MainActor`. @Published can't be used there: send `objectWillChange` by hand.

## Build and check
- `./build.sh --debug` → `.build/app/InnerVoice.app` (Developer ID, hardened runtime). `--install` replaces /Applications (quits the running copy).
- `swift test` — Kit tests, including real files made with textutil, ditto and CoreGraphics.
- `tools/selftest.sh` — in-app checks on the sample book (SeedData). `APP_SELFTEST_MODEL=<gguf>` also tags the sample with a real model and prints a score (Gemma 4 E2B: 27/28 in 18 s).
- `tools/snap.sh` — light and dark PNGs of every page, popover, sheet and Settings tab in /tmp/InnerVoice-shots. Look at them after UI changes.
- Test runs (APP_SNAPSHOT, --selftest, APP_STORE) start no menu bar item, sync, updater or quit question, and never save a project.
- Never capture the live screen; snapshot mode captures only its own windows.
- Agent CLI: drive the app with `innervoice` (the app binary; `Sources/InnerVoice/Agent/`, `Main.swift`). See `.claude/skills/innervoice/SKILL.md`. Commands find windows through `OpenProjects` (registered by ProjectWindow) and edit with the window's undo manager. Test with a Debug build and `APP_STORE=/tmp/iv-cli INNERVOICE_DEMO=1` (sample book; /tmp projects only; stop it with `kill <its pid>` (SIGTERM closes it without saving), never `pkill -x InnerVoice`: the person's own copy has the same name); `~/.claude/skills/agent-cli/scripts/check-agent-cli.sh <binary>` with `INNERVOICE_DEMO=1` prints PASS agent CLI.
- Release: `./release.sh` (see the script; version rules in the user's memory). Don't release or push without the user's OK.
