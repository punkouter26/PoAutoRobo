# Plan: turn PoAutoRobo into the "Teaching the R1" series studio

## Context
The approved `SPEC.md` (2026-10-22) makes the app about a single series: the owner teaching a Unitree R1 one thing per
episode. Each episode is the owner's footage mixed with about the same amount of AI-made clips, wrapped in a standard
branded intro and outro, about 3–5 minutes long, plus 2–3 ten-second teasers. Today the app is general-purpose (three
subjects and Topic Radar), has no intro, outro, numbering or beat structure, always silences the owner's footage, and
makes one short per clip. Approved 2026-10-22. The ordered list of tasks with their checks is in `tasks/todo.md`.

## Key design decisions (from exploring the code)
1. **"My sound" becomes just another narration file.** Every part of the pipeline assumes each clip has a WAV:
   `FfmpegArgs.Timeline`, the `Join` audios, `ClipMark.AudioPath`, the waveform in `TimelineView`, and the SRT. So for an
   own-sound clip, `EpisodeBuilder.NarrateClipAsync` (`Render/EpisodeBuilder.cs:23`) takes the footage's audio out
   to `audio\<clipId>-<hash>.wav` with FFmpeg instead of calling the narrator. Its length is the footage length, and its
   words are the typed dialogue spread evenly (a helper taken out of `MockNarrator`'s spreading). Footage with no sound
   track gives silence. Loudness matching comes free from the existing master `loudnorm`. Nothing downstream changes.
2. **The intro and outro are slots at the ends of the timeline.** They are encoded with exactly the same settings
   (`FfmpegArgs.Encode`: libx264 veryfast crf 18, `-video_track_timescale 90000`), so the copy-join still works. Each
   gets its own WAV (the music sting padded or cut to the slot, or silence), which goes into `Join`'s audios. The
   intro is not a chapter of its own: YouTube hides chapters when any is under ten seconds, so the first clip's chapter
   starts at 0:00 and takes it in. Clip encodes are cached through `MediaCache` as
   today.
3. **Section straps are a second ASS style** (`Strap`, accent box, top-left, Layer 1), added only to the per-clip
   captions file of the first clip in each beat (`AssCaptions.Build`).
4. **Teasers are cut from caption-free 1080p clip renders plus the clip WAVs, not from the master**, so the 16:9 master's
   burned-in captions don't get shrunk into the teaser. Each teaser is one FFmpeg command: trim and concatenate the
   moments → split → blurred cover background + centred 16:9 foreground → upright ASS captions → hook `drawtext` at the
   top → channel name bar → `loudnorm`. This replaces `ExportShortsAsync`.
5. **The brand kit is a lenient JSON file** at `%LOCALAPPDATA%\PoAutoRobo\series\series.json`, written with
   `ProjectStore.JsonOptions` so enums are saved as names. `JsonFile` would save them as numbers. The owner's files
   are copied into `series\`. The series music bed is **copied into the episode's `imports\`** when an episode is
   created, so the rule that media stays inside the episode folder holds and `KeepOwnMedia`, `Duplicate` and
   `UnusedMedia` need no change.
6. **Subject is removed outright.** Strict reading doesn't reject unknown JSON properties (no `UnmappedMemberHandling`),
   so old General or Essay files load as R1 without any special handling. **Change to the spec:** the "opened as R1"
   notice (§7 and §12) is dropped. Detecting it would mean keeping a dead enum. `SPEC.md` is updated in T5.
7. **New fields** are all `init` with defaults: `Episode.Number` (0 = unnumbered), `Episode.Structure` (a
   `BeatStructure` enum, default `Story`), `Clip.Beat` (`string?`), `Clip.OwnSound` (`bool`).
8. **Footage is placed by the script writer as an index.** Each clip in the schema gets a `beat` (an enum of the
   structure's beat names) and a `footage` (-1 or the file's index). A pure `BeatPlan.Repair` makes sure every file is
   used exactly once, at the end of its best beat if the model got it wrong. After the script is written, the app
   attaches each file through the existing `AttachVideoAsync` path (`MainViewModel.Edits.cs:344`), pulled out into a
   method that takes a clip id. Own-sound files skip the narration-fitting step (`Conformance`).

## Unit-test budget (cap 100)
There are 98 now. Removing Topic Radar (4) and the any-topic test (1) leaves **93**. New tests: fields and numbering 1,
series kit 1, beat placement and repair 1, footage sound 1, intro and outro arguments 1, teaser fit and rules 1, teaser
prompt (added into the existing prompt snapshot test) 0, strap (into the existing caption snapshot) 0, teaser arguments
(replaces the shorts layout test) 0. **Total 99.** Integration tests are not counted.

## Tasks (each: Red → Green → full unit suite → build → `dotnet format` → one commit)
Each task touches at most 5 source files, not counting tests. Where a task goes over, the reason is given.

**A. Clearing out (makes room)**
- **T1 Move `IsWebLink`** from `TrendFeed` to `Files`. Its test moves with it. *Files:* Files.cs, TrendFeed.cs, ProjectStore.cs. *Done when:* `ProjectStore.LoadSnippets` no longer depends on TrendFeed.
- **T2 Take Topic Radar out of the app.** *Files:* App.xaml.cs, MainViewModel.cs, TopicPage.xaml(.cs). *Done when:* there is no radar UI or commands; the app builds and starts.
- **T3 Delete TrendFeed** and its tests, and the Syndication package. *Files:* TrendFeed.cs, Core.csproj, Directory.Packages.props. *Done when:* no `Syndication` reference is left; 94 unit tests.
- **T4 Stop offering subjects in the UI.** *Files:* MainViewModel.cs, Prefs.cs, TopicPage.xaml. *Done when:* every new episode is R1 and grounded.
- **T5 Remove `Subject` from Core** (8 files: one parameter threaded through the writer API; splitting it would leave the build broken). *Files:* Subject.cs (deleted), Episode.cs, IScriptWriter.cs, AzureScriptWriter.cs, MockScriptWriter.cs, ScriptSchemas.cs, MainViewModel(.Edits).cs, plus SPEC §7/§12. *Tests:* the General/Essay inputs become R1, the any-topic test is deleted, and an old `"Subject": "General"` file loads. *Done when:* 93 unit tests, all green.

**B. Series data**
- **T6 New episode fields and numbering.** Adds `Number`, `Structure`, `Clip.Beat` and `Clip.OwnSound`, plus a `BeatPlan` with the three structures (beat names, clip ranges, which beats suit footage). `ProjectStore.NextNumber(root)`, `EpisodeSummary.Number`, folder `07-slug`. *Files:* Episode.cs, BeatPlan.cs (new), ProjectStore.cs. *Test (1):* the fields survive a round trip, an old file loads with defaults, and the next number is the highest + 1.
- **T7 SeriesKit.** A record (name, tagline, logo, sting, music, mascot, `Look`, `CaptionStyle`, outro seconds 5–20) with `Load`/`Save`/`Import(file)`, and `EpisodeEditor.UseSeriesLook(ep, kit, musicCopy)`. *Files:* SeriesKit.cs (new), EpisodeEditor.cs. *Test (1):* save and load keep everything; a damaged file falls back to defaults; applying the look is a pure change that leaves other fields alone.

**C. Script around the lesson**
- **T8 Lesson-driven script.** `Lesson(Text, WentWrong, Structure, Footage[])` with `FootageNote(Note, OwnSound, Seconds)` goes to `WriteEpisodeAsync`. The schema gains `beat` and `footage`, and the prompt lists the beats and the footage notes; lesson text isn't fenced, reference passages still are. `EpisodeLength.Standard(6,10)`; Short and Two-clip are removed. `HostVisible` is off unless the model turns it on in a beat that isn't for footage. The mock places footage by rule. `BeatPlan.Repair` handles the rest. *Files:* IScriptWriter.cs, AzureScriptWriter.cs, MockScriptWriter.cs, ScriptSchemas.cs, EpisodeLength.cs. *Tests:* the prompt snapshot is updated (diff read, then accepted); +1 test: the mock and `Repair` place every file exactly once in a beat that suits footage.
- **T9 Lesson page.** The topic box becomes lesson text, "what went wrong", structure (Segmented), footage list (add files, note, My sound or Narrate toggle, length probed), and episode number. Create writes the script, then attaches footage, copies the series look and music, and saves into `NN-slug`. The length choices become Standard and Quick test. *Files:* TopicPage.xaml(.cs), MainViewModel.cs, MainViewModel.Edits.cs, Prefs.cs. *Check:* run the app on stand-ins; a 2-footage Story episode is created.

**D. My sound**
- **T10 Footage sound in the render.** `FfmpegArgs.FootageSound(video, wav)` (48 kHz stereo; `anullsrc` when there is no sound track); `NarrateClipAsync` branches on `OwnSound`; `WordTiming.Spread` comes out of MockNarrator. *Files:* FfmpegArgs.cs, EpisodeBuilder.cs, INarrator.cs, MockNarrator.cs. *Test (1):* argument snapshot plus the spreading. *Integration:* an own-sound clip's master audio comes from the footage, and no narrator call is made.
- **T11 Sound switch per clip.** `EpisodeEditor.SetOwnSound`; switching to Narrate runs the existing narration fit. The inspector toggle and a card badge. *Files:* EpisodeEditor.cs, MainViewModel.Edits.cs, ClipViewModel.cs, InspectorView.xaml, ClipCard.xaml. *Check:* in the app; undo restores the switch.

**E. Brand**
- **T12 Series dialog.** Channel name and tagline; logo, sting and music pickers (copied in); mascot from the host design (`AppPaths.HostSheet`) or a PNG; look, caption preset and accent; outro length. "Use series look" in the menu. *Files:* SeriesDialog.xaml(.cs) (new), MainViewModel.Series.cs (new), AppPaths.cs, MainWindow.xaml(.cs). *Check:* in the app; changes survive a restart.
- **T13 Intro and outro arguments.** `FfmpegArgs.Intro(...)` (accent gradient from `color`/`gradients`, logo and mascot overlays fading in, `drawtext` from text files with `title-font.ttf`) and `Outro(...)`, plus `FfmpegArgs.SlotSound(sting?, length)`. *Files:* FfmpegArgs.cs. *Test (1):* snapshot of both, with and without the optional parts.
- **T14 Intro and outro in the master and preview.** `RenderAsync` takes optional bookends; the first chapter starts at 0:00 and takes in the intro; `ClipMark` for each end; the master is named `NN-slug-1080p30.mp4`. *Files:* EpisodeBuilder.cs, PublishPack.cs, MainViewModel.Export.cs. *Integration:* `EndToEndTests` checks the length = intro 5 s + clips + outro (±0.2) and the first chapter at 0:00. Exporting with no brand kit still works.
- **T15 Section straps.** `AssCaptions.Build(..., strap)` plus the builder adds the beat name to the first clip of each beat. *Files:* AssCaptions.cs, EpisodeBuilder.cs. *Test:* the existing caption snapshot gains a strap event (diff read).

**F. Teasers**
- **T16 Teaser model.** `Teaser(Hook, Moments[])` and `TeaserMoment(ClipId, Start, Length)`. `Teasers.Fit` (trim or pad to 10 s ±0.5 inside the clip's real length, drop what can't be fixed, hook ≤ 8 words) and `Teasers.ByRule`. *Files:* Teasers.cs (new, Render). *Test (1).*
- **T17 Suggesting teasers.** `IScriptWriter.SuggestTeasersAsync(Episode, lengths, ct)`: the fast model with a schema, the script fenced, token use logged. The mock returns `ByRule`. *Files:* IScriptWriter.cs, AzureScriptWriter.cs, MockScriptWriter.cs, ScriptSchemas.cs. *Test:* the teaser prompt is added to the prompt snapshot test.
- **T18 Rendering teasers.** `FfmpegArgs.Teaser(...)` and `EpisodeBuilder.ExportTeasersAsync` (caption-free Hd30 clip renders through the cache, plus the WAVs) into `export\teasers\`. Deletes `ExportShortsAsync` and `ExportPreset.Shorts`. *Files:* FfmpegArgs.cs, EpisodeBuilder.cs. *Test:* the shorts layout test becomes the teaser argument snapshot. *Integration:* 3 teasers, each 1080×1920, 30 fps, h264/aac, 10 s ±0.5; no picture or narration requests.
- **T19 Teaser panel.** It replaces the "short per clip" button: Suggest → cards with an editable hook, moments (clip choice and start), drop, then Render kept. *Files:* MainViewModel.Teasers.cs (new), MainViewModel.Export.cs, MainViewModel.Activity.cs, MainViewModel.cs, MainWindow.xaml. *Check:* in the app on stand-ins.

**G. Finish**
- **T20 Length and titles.** A warning outside 3–5 min (`Durations.MaxEpisode`); suggested titles start with "<channel> #N". *Files:* Durations.cs, MainViewModel.cs, ScriptSchemas.cs (publish prompt). *Test:* the prompt snapshot diff.
- **T21 Docs.** `CLAUDE.md` (removed subjects and Topic Radar, the series folder, bookends), `SPEC.md` status set to Built, `tasks/todo.md` closed.

## Phase 3 (UI design) after this is approved
There's no `/design` skill in this session. I'll present ten layout concepts (ASCII) for the three new screens (Lesson
page, Series dialog, Teaser panel) with AskUserQuestion, before T9, T12 and T19 are built.

## Tools (for selection: ★ already in use; anything new is "ask first")
★ FFmpeg filters `ass`, `drawtext`, `overlay`, `boxblur`, `gradients`, `concat`, `loudnorm`, `anullsrc` · ★ ffprobe ·
★ Microsoft Edge headless (SceneRenderer) · ★ Win2D · ★ CommunityToolkit.Mvvm · ★ CommunityToolkit.WinUI Segmented ·
★ WinUIEx · ★ Verify.Xunit · ★ NSubstitute · ★ Azure.AI.OpenAI structured output · ★ Azure Speech SDK ·
★ `dotnet format` · candidates: FFmpeg `scdet` (scene changes, to suggest teaser moments), FFmpeg `ebur128` (a loudness
report), Azure Speech speech-to-text with word timings (open question 1), Whisper.net (local transcription, open
question 1), CommunityToolkit.WinUI SettingsControls (Series dialog layout), coverlet (coverage), Stryker.NET (mutation
testing), `dotnet-counters` (render memory). **Recommended: nothing new for this plan.**

## Ten practical examples to pick from (how a piece gets built)
1. Taking footage sound out → `-vn -ac 2 -ar 48000 -c:a pcm_s16le`, or `anullsrc` + `-t` when there is no sound track.
2. Upright teaser over a blurred copy → `split[a][b];[a]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,boxblur=20[bg];[b]scale=1080:-2[fg];[bg][fg]overlay=(W-w)/2:(H-h)/2`.
3. Intro text with `drawtext=textfile=...:expansion=none` (as title cards do today), with `alpha` fading in by time.
4. Logo fading in → `[logo]format=rgba,fade=in:st=0.3:d=0.6:alpha=1[l];[bg][l]overlay=...`.
5. Strap as an ASS `Style: Strap` + `Dialogue: 1,...` inside the clip's existing captions file.
6. YouTube chapters: the first at 0:00, none under ten seconds, so the intro joins the first clip's chapter.
7. A sting padded to the slot → `apad,atrim=0:5`.
8. A pure `BeatPlan.Repair` keeping the model's footage placement honest, like `VisualMix.Settle` does for kinds.
9. The brand kit applied by one `Edit(e => EpisodeEditor.UseSeriesLook(e, kit, music))` for a one-step undo.
10. Teaser moments checked by `Teasers.Fit` before rendering, like `CheckShape` guards loading.

## Verification (end of each phase, and Phase 5)
- `dotnet build PoAutoRobo.slnx`; `dotnet test ... --filter "Category!=Integration&Category!=Live"` (≤100, all green);
  `dotnet test ... --filter "Category=Integration"`; `dotnet format ... --verify-no-changes`.
- Run the app on stand-ins (no `az login` needed): set up the brand kit, create a Story episode with one own-sound and
  one narrated footage file, export, check with ffprobe (intro and outro lengths, the h264/aac master), suggest and
  render 3 teasers (1080×1920, 10 s). Then `/code-review`, `/security-review`, `/simplify`, and every SPEC §13
  criterion proven with output.

## Phase 3 design picks (2026-10-22)
- **Lesson page: concept 1, form + library.** `TopicPage` is renamed `LessonPage`.
  - Left (`FormColumn`), in order: Episode number (NumberBox), "What I taught it" (TextBox), "What went wrong" (TextBox),
    Structure (Segmented: Story | Tutorial | Hook first, with a tooltip listing the beats), "My footage" (Add button +
    ListView of `FootageRowViewModel`: name, length, note TextBox, ToggleSwitch My sound / Narrate, remove), Length
    (Standard | Quick test), Write script.
  - Right: the existing library GridView, items show "#N · Title".
- **Brand kit: concept 5, dialog + intro preview.** `SeriesDialog` (ContentDialog), opened from the header overflow menu as "Series…".
  - Left: Channel, Tagline, Logo / Sting / Music bed pickers, Mascot (Use host design | Pick PNG), Look, Caption
    preset, Accent, Outro length.
  - Right: a 16:9 MediaPlayerElement playing a draft intro (T13 arguments at 640×360 into scratch), redrawn by "▶ Intro".
- **Teasers: concept 8, phone cards on Export.** A row under the Export preview.
  - "Teasers" heading + Suggest, then an ItemsRepeater of `TeaserViewModel` cards (9:16 poster, editable hook, moment
    list with clip ComboBox and start NumberBox, ▶ play draft, Keep toggle), then "Render N kept".
  - Replaces the "short per clip" button.

Because the brand kit's preview needs the intro arguments, **T13 is built before T12**.
