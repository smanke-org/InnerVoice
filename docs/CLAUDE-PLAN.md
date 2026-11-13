# Claude (cloud) as an AI model — plan

Decided 2026-11-12. Mac and Windows ship together (same version).

## What it is
Claude becomes one more choice in Settings › AI next to the downloaded models and Apple's
on-device model. Every AI feature that runs through `TextModel` (Tag Speakers + tones,
Build Summaries + voice briefs) can then use it.

Models: Claude Fable 5.2, Opus 5.5, Sonnet 5.5, Haiku 5.5 (built-in list), refreshed from
`GET /v1/models` when a key is set so new models appear without an update. Default Sonnet 5.5.
Selection string: `claude:<model id>`.

## Privacy (same pattern as ElevenLabs)
- Labeled Cloud wherever it appears (`CloudBadge`).
- Set Up… sheet: API key (Keychain on Mac, Credential Manager on Windows; tests: memory only)
  plus the one-time warning, which must be agreed before the key is saved:
  > Claude runs in the cloud: the book text you send goes to Anthropic. Anthropic doesn't
  > train on API data unless your organization has opted in. Check your organization's
  > privacy settings in the Claude Console. [How to check]
- Per book: `cloudTextAllowed` (asked once per book, like `cloudVoicesAllowed`). Nothing is
  sent without key + consent + the book's yes; `ClaudeTextModel` itself enforces it.
- Self-test: a fake URLProtocol proves nothing is sent without permission.

## Phases
1. Claude model choice, key, warning, per-book consent, cost estimate before a whole-book run. (Mac)
2. DONE 2026-11-12: Review Claude's Changes (Project Settings toggle, default on): after a
   Claude Tag Speakers run, a list of changed speakers/tones to keep or undo (`AIReview` in the
   Kit, `AIReviewSheet`). Build Summaries isn't reviewed: its notes are already replaced only
   while unedited and stay editable.
3. New features: text cleanup (OCR/EPUB artifacts), chapter recaps, delivery hints for
   ElevenLabs (audio tags), pronunciation spellings from Claude.
4. EPUB: table of contents → chapters, footnotes left out of narration; EPUB 3 export with
   Media Overlays (text highlights as the audio plays).
5. Windows port of 1–4 (C# Anthropic SDK).
   - 1–2 ported 2026-11-12 (with autosave and the PDF letter-spacing fix from the same Mac
     commit). `windows/src/InnerVoice.Core/AI/Claude.cs` (`ClaudeTextModel`, official `Anthropic`
     NuGet 13.8.0; the model can't be made without key + consent + the book's yes),
     `AIReview.cs` (speakers only: Windows has no tones), `ProjectSettings.CloudTextAllowed` /
     `ReviewCloudAI` (same JSON names as the Mac). App: `AI/ClaudeAccount.cs` (key in Windows
     Credential Manager, target `InnerVoice/anthropic`; in memory for test runs and on a Mac),
     `Windows/SettingsWindow.Claude.cs` (row + Set Up dialog), `Windows/AIReviewWindow.cs`.
   - Windows has no Project Settings window: the per-book toggle is Book › Review Claude's
     Changes (shown once Claude is chosen or the book said yes), next to Book › Stop Sending
     This Book to Claude.
   - The SDK sends the key as `Authorization: Bearer` (its documented behavior), always to
     api.anthropic.com (`BaseUrl` is set, so `ANTHROPIC_BASE_URL` can't redirect book text).
   - Windows has no Export Audio, so the cloud-safe audio export doesn't apply there.
   - The Windows PDF reader (PdfPig) gets one word per letter on a letter-spaced line; its row
     builder now spaces such a run by the gaps between letters, then `CloseLetterSpacing` and
     `JoinSplitLines` run as on the Mac.
   - Checks: `ClaudeTests` (fake HttpMessageHandler: nothing sent without all three yeses),
     `PdfLetterSpacingTests`, and `ClaudeAppTests` (the real runner against a fake Anthropic).
     Credential Manager and the real API need a Windows PC and a key.
   - 3–4 not ported yet.

## API notes
Raw HTTPS (no Swift SDK): POST /v1/messages, `anthropic-version: 2026-10-21`, streaming for
long jobs, `output_config.effort` low for tagging, prompt caching on the system prompt + cast
list, Batch API offered for whole-book jobs. GBNF grammars are ignored; replies go through the
same parsers. Check `stop_reason` (refusal → fail that chunk, keep going).
