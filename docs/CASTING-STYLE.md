# Casting Style: feature plan

Status: phases 0–6 done (2026-11-12). 2026-11-13: Pocket TTS (Kyutai) replaced Qwen3-TTS (no Chinese models; Chatterbox was tried first and dropped, as parts of it come from Alibaba research); Qwen notes below are kept as the record of the shading spike. Left: phase 7 (suggestions), ElevenLabs remixing (needs the narrator's OK), the Characters step count. (Texture words for Qwen3-TTS are dropped: Pocket TTS can't take words.) Mockups: the "InnerVoice Casting Style Mockups"
design canvas (private Claude artifact; ask for the link).

## 1. What and why

Most audiobooks aren't full cast. InnerVoice should support the three ways audiobooks are
actually made, in both project types:

| Casting Style | Produce with AI Voices | Narration Tools |
|---|---|---|
| **One Voice** | One AI voice reads everything and *shades* each character: a little lower or higher, slower or quicker | **Just Me**: one narrator reads everything; InnerVoice shows a shading cue for every line |
| **Narrator + Two Voices** | A narration voice, one voice for every man, one for every woman | **Three Narrators**: one for the narration, one for the men, one for the women; a Narrator Script each |
| **Full Cast** | A voice per character, plus the narrator (today's behavior) | A narrator per character (today's behavior) |

The plan is a second question after the project type, and it can be changed any time on
Voices (AI) or Cast (narration). **Changing the plan never loses casting**: the plan only
decides which voice reads each character. Every character's own voices, Voice Notes and
narrator assignments stay in the file.

## 2. Words (add to GLOSSARY.md)

| Term | Means | Not |
|---|---|---|
| Casting Style | How voices are shared in a book: One Voice, Narrator + Two Voices, Full Cast | voice plan, mode |
| One Voice / Just Me | The AI and narration names of the one-voice style | single narrator, solo |
| Narrator + Two Voices / Three Narrators | The AI and narration names of the shared style | duet, dual narration (they mean different things to different people) |
| Full Cast | A voice (or narrator) per character | multicast |
| Main character | A character the person marked as main: in Full Cast they always get their own voice | lead, star |
| Shade | How one voice changes for a character: pitch and pace, plus a few words ("lower, rough") | variation, tint |
| Role | Who reads a character under the style: Narrator, Men, Women, Other or Own Voice | group, bucket |
| Other | The role for characters who are neither male nor female, or whose gender isn't known | neutral, unknown |
| Own Voice | A character who keeps a voice of their own inside a shared style | override, exception |
| Gender to review | A character whose gender the book doesn't settle (none found, or a guess from one pronoun); a filter lists them so the person can choose Male, Female or Other | unknown gender |

Narration Tools says **narrator**, never "reader" (decided 2026-11-12): "Three Narrators",
"Men's narrator", "Who reads the narration?".

## 3. What exists today (what this builds on)

- `ProjectSettings.purpose` + `Views/ProjectType.swift`: the project-type sheet, with a
  second step for AI voices (Choose the Voices: engine), then Tag Speakers + casting.
- `BookCharacter.voices` (per engine), `ProjectSettings.narratorVoices` (per engine),
  `VoiceChoice.isNarrator` ("Same as Narrator"), `CastVoice.source` (`.user` is never
  changed by casting).
- `VoiceChoice.rate` / `.pitch`. Apple voices take them directly (`pitchMultiplier`);
  Pocket TTS turns pitch into whole semitones of its clip (`PocketVoice.semitones`) and
  rate into a time-stretch of the finished audio. Casting fakes ages with them for both
  (`VoiceCasting.ageAdjusted`) and nudges them from the voice brief.
- `CastingProfile`: gender (+ `genderGuessed`), age, accent, timbre, `pitchLean` /
  `paceLean` from the voice brief. That's everything needed for an automatic shade.
- `VoiceResolver.choice(for:)` (`Speech/SpeechPlayer.swift`): character voice →
  narrator. Every voice decision already goes through it: it's where the plan plugs in.
- Pocket TTS: every line is cloned from a clip (`ModelSpeech.render`, `PocketClips`): a
  stock voice's clip from Resources/StockVoices, or a cloned voice's Clip. The tone sets
  its sampling temperature and a small pitch, pace and loudness change (`PocketDelivery`;
  it has no emotion setting). (Before 2026-11-13, Qwen3-TTS: VoiceDesign
  recorded one sample per description and tone, and the Base model cloned every line from
  it.)
- Narration Tools: `Narrator` people, `BookCharacter.narratorID`, Narrator Scripts
  (`Export/Exporter.swift`), MarginOverlay initials, the Teleprompter.
- Windows (narration only) keeps unknown fields (`[JsonExtensionData]`) but must learn
  any field it shows. WINDOWS-PLAN rule 9: schema changes start in the Kit, with a fixture.

## 4. Data model (Kit, `Project.swift` + `CastingStyle.swift`)

All new fields are optional, so older files open unchanged and Windows round-trips them.

```swift
public enum CastingStyle: String { case oneVoice, shared, fullCast }
public enum VoiceRole: String { case narrator, men, women, other, own }
public enum CharacterGender: String { case male, female, other }
public enum MinorVoices: String { case narrator, byGender, own }
public enum ShadeStrength: String { case subtle, clear, strong }
public struct Shade { var pitch: Int /* -4…4 */; var pace: Int /* -2…2 */; var words: String? }

extension ProjectSettings {
    var castingStyle: CastingStyle?      // nil = not chosen yet: plays as today (Full Cast) until the person picks
    var narrationRole: VoiceRole?        // shared style: who reads the narration; nil = .narrator (.men / .women = a two-person book)
    var minorVoices: MinorVoices?        // Full Cast: who reads characters not marked main; nil = .narrator
    var shadeStrength: ShadeStrength?    // One Voice; nil = .clear
    var roleVoices: [String: [String: CastVoice]]?  // role → engine → voice (men, women, other)
}

extension BookCharacter {
    var gender: CharacterGender?   // the person's choice; nil = what the book says
    var isMain: Bool?              // the person marked them main (true) or not main (false); nil = not marked
    var role: VoiceRole?           // the person's override (drag, menu); nil = from the style
    var shade: Shade?              // the person's shade; nil = automatic from the profile
}

extension Narrator {
    var role: VoiceRole?           // Three Narrators: which part this person reads
}
```

Nothing automatic is written into these fields. It's computed, so changing the style or a
character's gender updates everything, and the person's own choices always win (same rule
as `.user` lines, tones and voices).

## 5. How a character's voice is decided (Kit, `CastingStyleRules`)

Pure functions with Swift Testing tests:

1. **Gender:** `BookCharacter.gender` (the person's) wins. `VoiceCasting.profile` reads it
   first ("Gender: you chose"), so Automatic colors and casting follow it too; Other
   counts as neutral. Otherwise it comes from the book as today. **Gender to review** =
   no choice by the person and either nothing found or a guess from one pronoun.
2. **Role** (`role(for:in:gender:)`):
   - The person's `role`, if set. (In One Voice only Own Voice and Narrator apply; the
     rest read as Narrator.)
   - **One Voice:** Narrator (shaded).
   - **Narrator + Two Voices:** male → Men, female → Women, other or unknown → Other.
     A guessed gender is used but listed under Gender to review.
   - **Full Cast:** characters marked main → Own Voice. Others → `minorVoices` (Narrator,
     their gender's role, or Own Voice). **If nobody is marked main yet, everyone gets
     their own voice** (today's behavior). `suggestedMain` (over 15% of the lines, the
     Index's rule) is only a suggestion for the person to accept.
   - Not chosen yet (`castingStyle == nil`): Own Voice, exactly as today.
3. **Who reads a role** (`reader(of:in:)`): in Narrator + Two Voices, Narrator means
   whoever reads the narration (`narrationRole`), so a two-person book maps Narrator →
   Men or Women.
4. **Shade** (`shade(for:profile:strength:narratorGender:)`): the person's `shade` if set.
   Otherwise:
   - **Gender:** a character of a different gender from the narrator moves 1/2/3 steps
     (Subtle/Clear/Strong): down for men, up for women. The same gender, or Other, doesn't
     move. With an unknown narrator gender, men go down and women go up.
   - **Age:** child +2 and a little quicker; older −1 and a little slower.
   - **Voice brief:** its pitch and pace leans add ±1.
   - **Limits:** pitch −4…4, pace −2…2. Timbre words ("rough") become the shade's words.
     The cue reads "lower, rough".
5. **Narration Tools** (`narrator(for:in:gender:)`): the character's `narratorID` (the
   person's) wins. Otherwise, in Three Narrators, the narrator whose `role` matches; in
   Just Me, the narration narrator.

The app's `VoiceResolver` (phase 2) then maps role → `VoiceChoice`:
- Narrator → the narrator voice, plus the shade in One Voice.
- Men, Women, Other → `roleVoices`.
- Own Voice → the character's own voice (today's path).

Written text and "Same as Narrator" keep working as they do now.

**Export Audio** (phase 2): the person chooses **Combined** (one file per chapter) or
**Separate** (a file per role per chapter: Narration, Men, Women, Other, and each Own Voice
character).

## 6. Shading AI voices (the hard part: spike first)

Each engine shades with its own way of changing a voice: Voice Remixing for ElevenLabs
(words), a pitch shift of the clip for Pocket TTS, a shift of the finished audio for
Orpheus.

What the code does today (checked 2026-11-13):
- **Pocket TTS:** Voice Notes don't change the sound (it can't take a description); casting
  reads them for gender, age, pitch and pace. (Qwen3-TTS, until 2026-11-12, redesigned a
  "design" voice from them at speaking time.)
- **ElevenLabs:** notes are **not** sent when speaking. Only the text, the tone and sound
  audio tags (`[whispers]`, `[sighs]`) and speed go. Notes reach ElevenLabs only through
  Design a Voice from Voice Notes, which makes a new voice.

| Engine | How a character is shaded | Confidence |
|---|---|---|
| Pocket TTS, any narrator (stock or cloned) | **Pitch:** shift the narrator's clip, then clone (the method tested exact with Qwen3-TTS, below); pace time-stretches the finished line. No texture: it takes no words | Pitch: same method as the Qwen test; Pocket TTS shaded and unshaded readings made 2026-11-13 for listening (self-test `onevoice-pocket-*`) |
| ElevenLabs | **Voice Remixing** of the narrator's voice with the shade words, at low prompt strength so it stays close to the original. The original isn't changed. Each remix uses credits and a voice slot, so only main characters, only on the person's click, only for books that allow ElevenLabs (like Design a Voice). Minor characters: narrator voice + speed | Medium–high |
| Orpheus | No descriptions: shift the output's pitch (±2 steps max) and time-stretch it | Medium |
| Apple | `pitchMultiplier` + `rate` (already works) | Works, but lower quality: **never a default or a fallback** |

One `Shade` feeds both kinds of engine. For ElevenLabs, pitch and pace steps become words
("a little deeper", "quicker"), joined with the texture words. For Pocket TTS, Orpheus and
Apple they're numbers. Texture is back for AI voices, but only on engines that read words.
The shade editor hides it where it does nothing.

**Qwen3-TTS test results (2026-11-12, `tools/shade-test`, 6 lines per voice; the tool was
removed with Qwen3-TTS on 2026-11-13, the numbers are why Pocket TTS shifts its clip):**

| Way to shade | Pitch moved as asked? | Similarity to narrator | Verdict |
|---|---|---|---|
| Shift the narrator's sample, then clone | Yes: −2 → −2.0 st, −4 → −4.1, +2 → +1.5 / +2.6 | 0.968–0.982 at ±2; 0.940 at −4 | **Predictable.** Use for pitch, keep within ±2–3 st |
| Shade words in the description (Voice Notes path) | Direction mostly right (once wrong); amount random: "subtle" gave −2.5 to −12.1 st across seeds | 0.948–0.979 | **Unpredictable** as a shade; the seed matters more than the words |
| Shift finished lines (Orpheus approach) | Yes | 0.966 at −2 | Same as shifting the sample |

- The speaker encoder is a blunt judge here. Different characters scored 0.932–0.978
  against the narrators, overlapping every shade, and a redesign from the same words with a
  new seed scored 0.977. Similarity can't prove "same person": **listening decides**
  (files in `Code Test/voice-shading-test/`, guide in LISTEN.md there).
- Re-designing always moves the voice a little (0.977 for the same words with a new seed),
  so description shading carries that drift on top of the shade.
- **Listening check passed (2026-11-12):** the pitch-shifted voices sound like the same
  narrator doing a character. Direction for building:
  - **Pitch comes from the sample shift.** It's exact and keeps the voice. It also works
    for ready-made and cloned voices, so Qwen needs no separate route for them.
  - **Voice Notes add texture** ("rough", "breathy") only through an audition:
    InnerVoice designs 3–4 candidates, keeps those within ±1 st of the target pitch and
    closest to the narrator, and the person can click Try Another. Never a single blind
    design.
- Not tested yet: CustomVoice 1.7B. mlx-audio-swift supports it (`voice` = "speaker,
  instruction"), but it isn't downloaded. ElevenLabs remix needs the person's OK
  (credits).

**Spike (Phase 0, before anything else):**
- Qwen VoiceDesign: the narrator's description vs. the description + shade words at
  Subtle, Clear and Strong, 10 lines each. Measure speaker-encoder similarity to the narrator
  (the drift harness from the Qwen notes). Pass if a shaded voice stays closer to the
  narrator than two different characters are, and the shade is audible.
- Qwen CustomVoice: does mlx-audio-swift run it, and does an instruction shade the voice
  without changing who it is? Same measure.
- Pitch-shifting the sample for ready-made and cloned narrators (try a formant-preserving
  shift if `AVAudioUnitTimePitch` sounds like a chipmunk).
- ElevenLabs remix of two characters at low and medium strength. **This uses your account,
  credits and voice slots: ask before running it.**
- Write the results into CLAUDE.md's Qwen and ElevenLabs notes either way.

Rules:
- Add the shade to the `SpeechRenderer` cache key.
- Shifted Pocket TTS clips are made once per (voice, semitones) and kept in memory
  (`PocketClips`); they take milliseconds.
- ElevenLabs remixes are listed as the narrator's variants and never cast to other
  characters (same rule as clones: `ElevenLabs.castable`).

## 7. Narration Tools

- **Just Me:** shading cues from `shade(for:)`: ↓/↑ plus words ("Kade · lower, rough").
  - Shown in the margin (MarginOverlay, beside the initials), on the Teleprompter (current
    line + "Next:"), in the Index and in the Tagged Manuscript export.
  - The person edits a character's cue on Cast (a Shading row: pitch, pace, words).
- **Three Narrators:** Cast shows three columns (Narration, Men, Women), each with a
  narrator name field and Narrator Script.
  - Characters are sorted by gender; dragging one sets `role`.
  - "Who reads the narration?" set to Men or Women covers a two-person book: two
    columns, two scripts.
  - The plan creates the `Narrator`s with roles if missing and never deletes narrators
    the person made.
- **Full Cast:** today's Cast and narrators.
- Narrator Scripts per role use the same exporter: a role's lines = lines of characters
  whose effective narrator is that person (computed, section 4).

## 8. Screens

- **Setup:** `ProjectTypeSheet` gains a step.
  - AI: Project Type → Casting Style → Engine.
  - Narration: Project Type → Narrators.
  - Cards side by side, each with a who-reads-what map and Hear It, and one follow-up
    question for the chosen plan (mockup "Setup").
  - **No default.** No card is selected and Continue stays dimmed until the person picks one.
  - Existing projects are asked once when opened (like the project type). Until they
    answer, they play as they do today. In the sheet, Full Cast says "How this book is set up
    now".
  - The engine step never preselects Apple's voices. If no voice pack is downloaded, it
    leads with the free download, and Apple stays an option further down.
- **Hear It:** bundle six short pre-rendered clips (3 plans × AI wording; narration
  reuses them labeled "Example"), made from SeedData's sample book with Pocket TTS's Tara
  (`APP_MAKE_CASTING_SAMPLES`; remade with Pocket TTS 2026-11-13, first made with Qwen3-TTS).
  This works before any voice pack is downloaded. Keep each under 20 s (app size).
- **Voices (AI):** the plan segmented control under the title; characters grouped by role
  (Narrator, Men, Women, Own Voice); drag between groups or right-click › Read By.
  - In One Voice, a character list with shades plus a shade editor (pitch, pace, Hear Kade
    / Hear Both).
  - Cast Voices Again picks men's and women's voices that sound clearly different from
    the narrator's (and from each other). Never cast a clone (`ElevenLabs.castable`).
- **Cast (narration):** plan control + the Three Narrators columns.
- **Review:** a "needs a gender" card, only when the plan sorts by gender. It offers
  Men's / Women's / Narrator / Own Voice and sets `role`; keys 1–4.
- **Steps:** the Characters step counts missing genders when the plan needs them.
- **Project Settings, Help (new topic), GLOSSARY, README, site page 613.**
- **Agent CLI:** `innervoice plan [one|shared|full]`, `innervoice role <character> <role|auto>`,
  `innervoice shade <character> <pitch> <pace> [words]`.
- Gate everything with `showsVoices` / `showsNarrationTools` (CLAUDE.md rule).

## 9. Phases

| # | Phase | Ends with |
|---|---|---|
| 0 | Spike: Qwen sample shift, Orpheus output shift | **Qwen: GO (2026-11-12)**, pitch by sample shift, confirmed by listening. Still open: CustomVoice, ElevenLabs remix |
| 1 | Kit: model fields, `CastingStyleRules`, shade rules; Windows fixture | **Done 2026-11-12**: `CastingStyle.swift`, 21 tests, all 89 Kit tests green; fixture regenerated. C# round-trip test updated but **not run** (no dotnet on this Mac) |
| 2 | AI: Narrator + Two Voices and Full Cast minor rule; Voices page groups; role casting | **Done 2026-11-12**: Casting Style control, role groups, right-click Read By / Main Character / Gender, Gender to Review filter, shared-voice casting, Export Audio Combined or Separate; 6 self-test checks + 3 Kit tests; snapshots voices-shared, voices-fullcast. Not yet heard: a table read with real voices |
| 3 | AI: One Voice shading per engine | **Done 2026-11-12** for pitch and pace on every engine (`ShadeAudio`: Qwen3-TTS shifts its sample, Apple pitch/rate, Orpheus and ElevenLabs shift the audio); Shading strength, shade editor with Hear buttons; self-test incl. a real Qwen3-TTS table read (`Code Test/voice-shading-test/one-voice-table-read`), **approved by listening 2026-11-12**. **Still to do:** texture words for Qwen3-TTS (audition 3–4 designs) and ElevenLabs remixing (needs your OK, credits) |
| 4 | Narration: Just Me cues (margin, Teleprompter, exports), Three Narrators on Cast | **Done 2026-11-12**: Casting Style bar on Cast (Your Voice, Shading; Set Up Narrators; Narration part; Minor), Cast grouped by part, narrator Parts in Narrators, effective narrator in Narrator Scripts / Index / Cast List / CLI (a hand-cast narrator still wins), margin arrows (↓ ↓↓ ↑ ↑↑), Teleprompter "Shade:" and Next cues, Shade cue editor on character pages; 2 Kit tests + 7 self-test checks; snapshots cast-threenarrators, cast-justme, character-justme, manuscript-justme, teleprompter-justme |
| 5 | Setup step + Hear It clips, Review gender cards, Steps, Help, glossary, CLI, self-test, snapshots | **Done 2026-11-12**: Casting Style step in the setup sheet (no default, Continue dimmed until chosen; existing books asked once on open), follow-up question per style, Hear It clips (`Resources/Samples/casting-*.m4a`, Qwen3-TTS, made with `APP_MAKE_CASTING_SAMPLES`), Review › Genders to Review (keys 1–3), Help › Casting Style, GLOSSARY terms, CLI `casting style`, `characters gender/main/read-by/shade`; snapshots project-type-style(-narration), review-genders. **Not done:** the Characters step doesn't count genders to review yet (kept off the per-redraw Steps path) |
| 6 | Windows: Casting Style for Narration Tools (setup, Cast columns, cues) | **Done 2026-11-12**: C# model fields + `CastingStyle.cs` (rules, shades, ShadeProfile with the Mac's age/timbre/brief reading), golden-checked against the Mac (`Fixtures/casting-style.json`, 7 scenarios); Cast page Casting Style bar, parts, card cues, right-click Read By/Main/Gender, Gender to Review filter, Shade editor, narrator Parts, effective narrator in scripts/Index/CSV, margin arrows, Teleprompter cues. Core 64, App 10 tests green. On Windows the style is chosen on the Cast page (no setup step there) |
| 7 | Later: suggestions ("Kade has 412 lines: his own voice?", "Hale and Kade sound alike": scene partners from the voice brief) | Opt-in cards on Voices |

Phases 2 and 4 don't depend on each other once Phase 1 lands.

## 10. Tests

- Kit: role resolution for every plan × gender × override; shade from profiles (child,
  older, brief leans, strength); a plan change keeps every voice and narrator assignment;
  old files decode with nil fields and encode unchanged.
- Self-test (sample book):
  - Each plan resolves the expected voice for a known male, female and no-gender character.
  - Same as Narrator and written text still go to the narrator.
  - The redraw voice-question timing stays < 1 ms.
- Self-test with `APP_SELFTEST_VOICES=1`: a shaded line renders on Apple and Pocket TTS (Qwen3-TTS until 2026-11-13), and
  its pitch differs from the narrator's in the expected direction.
- Snapshots: setup step (both types, light/dark), Voices in each plan, Cast columns,
  Review gender card, margin cues.

## 11. Decisions (2026-11-12)

1. **No default style.** The person always chooses (section 8).
2. **Name:** "Casting Style".
3. **Main characters:** the person marks them. InnerVoice only suggests (over 15% of the lines).
4. **Export Audio:** the person chooses Combined or Separate (a file per role).
5. **Other** is a group of its own in Narrator + Two Voices, for non-binary characters and
   unknown genders. Characters whose gender is a guess or missing get a **Gender to
   review** filter (Cast, and the Review page) to choose Male, Female or Other.
6. **Apple's voices are never a default or a fallback.** Qwen3-TTS shading passed (section 6), by the clip shift Pocket TTS now uses.
7. **ElevenLabs remixes:** only for characters marked main. Up to 5 per book without
   asking. Beyond that, InnerVoice says how many voice slots it would use and asks.
   Remixes are tested only after the person says so (credits).
8. **Narration Tools says "narrator"**, never "reader".
