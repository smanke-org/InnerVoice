# InnerVoice for Windows

The Narration Tools from InnerVoice for Mac, on Windows: open a manuscript, see who speaks
every line, fix it, build character summaries with a local AI model, keep an index and
narrator scripts, and read from the teleprompter in the booth (record in your own audio app).
Projects (`.innervoice`) open on both Windows and the Mac. There are no AI voices on Windows.

The plan and the decisions behind it are in [../docs/WINDOWS-PLAN.md](../docs/WINDOWS-PLAN.md).

## Layout

- `src/InnerVoice.Core` — the book model and logic, ported from the Mac's `Sources/InnerVoiceKit`
  (no UI). `Model/` (project.json rules, archive), `Import/` (Word, PDF, RTF, text, Markdown,
  OpenDocument, EPUB, HTML), rules, dialogue finder, prompts, llama.cpp binding.
- `src/InnerVoice.App` — the Avalonia app: `Document/` (open, save, undo, edits), `Pages/`,
  `Windows/`, `Controls/` (the manuscript view), `AI/`, `Export/`, `Help/`.
- `tests/InnerVoice.Core.Tests` — xUnit tests, including golden checks against fixtures the Mac's
  Kit writes (`Fixtures/`).
- `tests/InnerVoice.App.Tests` — headless app tests and light/dark snapshots of every page.
- `packaging/` — the Microsoft Store manifest, tiles, and `pack.ps1`.
- `tools/fetch-llama.sh` — downloads llama.cpp b11205 for Windows (x64 Vulkan, ARM64 CPU) with
  SHA-256 checks into `native/` (not committed).

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
the Store package gets it from its VCLibs dependency.

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
