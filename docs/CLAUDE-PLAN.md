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
3. DONE 2026-11-12 (Mac): Book › Claude (Cloud), each asking first (the book's permission,
   then the estimated cost) and each one Undo step:
   - Clean Up Text (`CleanupPrompt`, `TextCleanup` in the Kit): find/replace fixes for import
     problems, refused unless the text is really there and the change is a repair (edit
     distance), never a rewrite. Edited paragraphs get their lines found again; each keeps
     the old line with the same words (id, speaker, tone, delivery). A paragraph whose fixes
     would lose a line the narrator assigned keeps its text. Emptied paragraphs (page
     numbers) are removed unless they're all the chapter has. The imported file never changes.
   - Write Chapter Recaps (`RecapPrompt`): `Chapter.recap` / `recapEdited` (optional, older
     files open). Each recap is written from the story chapter before it; edited ones are
     never rewritten. Shown and edited in the inspector next to the chapter, in Narrator
     Scripts under the chapter title, and spoken by the narrator after the title in Export
     Audio when "Say chapter recaps" is on (`SpeechScript.segments(…, recaps:)`).
   - Suggest Delivery Hints (`DeliveryPrompt`): `Line.delivery` / `deliverySource` (the
     narrator's are never replaced). eleven_v4 audio tags are free-form text in square
     brackets (ElevenLabs' audio tag guide, checked 2026-11-12), layered in one bracket with
     the tone: "[sad, barely audible]". Only `VoiceResolver` for an ElevenLabs voice puts the
     hint on a `SpeechRequest`; no other engine reads it. Edited in the inspector. AI voice
     projects only.
   - Suggest Spellings (`SpellingPrompt`): only each term and the sentence it first appears
     in are sent; always waits for the narrator to check each one; Confirmed stays theirs.
   - Review: with Review Claude's changes on, fixes, recaps and hints wait in
     `ClaudeProposalsSheet` (keep/leave out, editable); off, they're applied at once.
   - Cost estimate (phase 1 leftover): `ClaudePricing` / `CostEstimate` / `CostEstimator` in
     the Kit, from the text that would be sent (about 4 characters a token plus a margin;
     output doubled for thinking). Shown in "Send this book to Claude?" and in a confirmation
     before every whole-book run (Tag Speakers, Build Summaries, each feature above), with
     "Don't show the estimate again". Never calls count_tokens.
   - Self-test: `FakeClaude` URLProtocol (SelfTestClaude.swift) checks nothing is sent without
     a key, consent and the book's yes, and runs each feature end to end. Live calls are
     untested here (no key).
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
