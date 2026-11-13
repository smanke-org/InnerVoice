# InnerVoice for Windows

The Narration Tools from InnerVoice for Mac, on Windows: open a manuscript, see who speaks
every line, fix it, build character summaries with a local AI model (or Claude, in the cloud,
once the narrator sets it up and allows the book), keep an index and
narrator scripts, and read from the teleprompter in the booth (record in your own audio app).
Projects (`.innervoice`) open on both Windows and the Mac. There are no AI voices on Windows.

The plan and the decisions behind it are in [../docs/WINDOWS-PLAN.md](../docs/WINDOWS-PLAN.md).

## Layout

- `src/InnerVoice.Core` — the book model and logic, ported from the Mac's `Sources/InnerVoiceKit`
  (no UI). `Model/` (project.json rules, archive), `Import/` (Word, PDF, RTF, text, Markdown,
  OpenDocument, Pages, EPUB, HTML), rules, dialogue finder, prompts, llama.cpp binding.
- `src/InnerVoice.Agent` — the agent interface shared by the app and the CLI (the Mac's
  `Sources/InnerVoice/Agent/`): the command list, parser, help, output, MCP server, named pipe,
  `innervoice://` links. No packages.
- `src/InnerVoice.Cli` — `innervoice.exe`, the command line (a console program: Windows gives a
  windowed program no console). Publishing the app puts it in the app's `cli\` folder.
- `src/InnerVoice.App` — the Avalonia app: `Agent/` (what each command does, the pipe server,
  link registration), `Document/` (open, save, undo, edits), `Pages/`, `Windows/`, `Controls/`
  (the manuscript view), `AI/`, `Export/`, `Help/`.
- `tests/InnerVoice.Core.Tests` — xUnit tests, including golden checks against fixtures the Mac's
  Kit writes (`Fixtures/`).
- `tests/InnerVoice.App.Tests` — headless app tests and light/dark snapshots of every page.
- `packaging/` — the Microsoft Store manifest, tiles, and `pack.ps1`.
- `tools/fetch-llama.sh` — downloads llama.cpp b11205 for Windows (x64 Vulkan, ARM64 CPU) with
  SHA-256 checks into `native/` (not committed).
- `tools/check-agent-cli.ps1` — the agent-CLI check (the Mac's `check-agent-cli.sh`), on Windows.

## Build and test (on the Mac or Windows)

Needs the .NET 10 SDK (`~/.dotnet` on this Mac, from Microsoft's dotnet-install script).

```bash
dotnet test tests/InnerVoice.Core.Tests
```

```bash
dotnet run --project tests/InnerVoice.App.Tests
```

- `INNERVOICE_SNAP_DIR=<folder>` writes light and dark PNGs of every page.
- `INNERVOICE_TEST_MODEL=<file.gguf>` also tags the sample book with a real model.
- On a Mac, the llama.cpp tests use the Mac app's `Frameworks/llama.xcframework`
  (`Tools/fetch_llama.sh` in the repo root).

Run the app on the Mac for a quick look (the sample book is under Open the Sample Book):

```bash
dotnet run --project src/InnerVoice.App
```

Build for Windows from the Mac:

```bash
tools/fetch-llama.sh && dotnet publish src/InnerVoice.App -c Release -r win-x64 --self-contained -o artifacts/win-x64
```

The x64 build needs the Visual C++ runtime next to it (CI copies it in) or installed on the PC;
the Store package gets it from its VCLibs dependency. The publish also writes `cli\innervoice.exe`
(one self-contained file, about 14 MB).

## Agent CLI, MCP and links

The same commands, options, JSON and exit codes as the Mac's `innervoice` (`innervoice help` is
the Mac's word for word, but for the five Mac/PC words in `AgentCommands.WindowsWording`;
`AgentTests` compares it with `Fixtures/agent/`, taken from the Mac build's `innervoice help`,
`help <command>` and `help --json`). Agents use the repo's `.claude/skills/innervoice` the same
way on both.

- **Transport:** one JSON line each way over the named pipe `\\.\pipe\InnerVoice.Agent.<user>`
  (current user only), where the Mac uses a Unix socket. If nothing answers, the CLI starts
  `..\InnerVoice.exe --agent` (in the background) and waits up to 10 seconds.
- **One copy at a time:** a second start of the app (a double-clicked project, a link) hands its
  files and links to the running one through the pipe and quits.
- **Links:** `innervoice://open/cast?character=Mara`, `innervoice://open/review`,
  `innervoice://lines/reveal/3f2a9c1e`, `innervoice://projects/open?path=C:\Books\Harbor.innervoice`.
  Links only run `open`, `lines reveal` and `projects open`. The Store package declares the scheme
  in its manifest; a copy run from a folder writes `HKCU\Software\Classes\innervoice` at launch.
  `innervoice <link>` follows one from the command line.
- **MCP:** `claude mcp add --scope user innervoice -- innervoice mcp`.
- **Not yet:** `lines tone`, `open voices`/`pronunciations` and `export --kind pronunciations`
  answer error code `not_available` until AI voices come to Windows (`TODO(voices)`).
- **Test copies:** `APP_STORE=<folder>` makes a test copy: its own pipe (named from the folder),
  its settings and models in that folder, and it only opens or saves projects in the temporary
  folder. `INNERVOICE_DEMO=1` opens the sample book at launch; `projects demo` opens another.
  `APP_AGENT_PIPE` names the pipe outright, `INNERVOICE_APP` the app to start. Unlike the Mac,
  Release builds honor these too (CI tests a Release build). A Debug CLI only ever starts test
  copies, and a Debug app that isn't one stays off the pipe (it would otherwise hand itself to
  the person's running InnerVoice). Stop a test copy by its process id (`status --json` gives `pid` in test copies),
  never by name: that would also close the person's own copy.

```bash
dotnet run --project tests/InnerVoice.App.Tests -- -class InnerVoice.App.Tests.AgentAppTests
```

On Windows, after a publish:

```powershell
pwsh tools/check-agent-cli.ps1 artifacts/win-x64/cli/innervoice.exe
```

Needs a Windows PC to confirm: the pipe's per-user security, starting the app from the CLI,
protocol activation (Store and registry), the `innervoice.exe` alias from the Store package, and
the check script. The pipe itself also runs on the Mac (.NET uses a Unix socket there), which is
how the tests above exercise it.

## Fixtures

`Fixtures/` is written by the Mac's Kit (`Tests/InnerVoiceKitTests/WindowsFixtures.swift`), only
when asked:

```bash
INNERVOICE_WRITE_FIXTURES="$PWD/windows/Fixtures" swift test --filter WindowsFixtures
```

Placeholder text only (the sample story, every name made up). Never put a real manuscript here.

## Testing in a Windows 11 ARM virtual machine on this Mac

1. Install VMware Fusion Pro (free) from Broadcom's site, or UTM (free) as the fallback.
2. Download the Windows 11 ARM64 ISO from Microsoft's "Download Windows 11 Disk Image (ISO) for
   Arm-based PCs" page.
3. Create a VM with 4 cores, 8 GB of memory or more, and 80 GB of disk; install Windows (it runs
   unactivated for testing).
4. Share a folder from the Mac with the VM, copy in a build (`artifacts/win-arm64`, or `win-x64`
   to try it under emulation), and run `InnerVoice.exe`.
5. Use the sample book and the files in `Fixtures/import`; never a real manuscript.

The VM has no GPU for the AI, so Tag Speakers runs on the processor: use Gemma 4 E2B or
Qwen 3 4B there.

## Releasing

Through the Microsoft Store, which signs the package and updates the app. `packaging/store.json`
takes the identity from Partner Center once the name is reserved; a `win-v*` tag makes CI build
`InnerVoice_<version>.msixbundle` to upload. Windows tags are never GitHub releases, so the Mac
updater never sees them. Versions follow the Mac's rule (A.B.C, C runs 0–9).

## License

© 2026 Steve Manke. All rights reserved.
