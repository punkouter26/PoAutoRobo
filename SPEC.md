# PoAutoRobo: Teaching the R1, the series studio

Status: **Built** · Last updated: 2026-10-22

## 1. Objective

A single-user Windows desktop app for one YouTube series: the owner teaching a Unitree R1 EDU humanoid one new thing
per episode. For each lesson the owner types what they taught the robot, adds their own footage, and picks a story
structure. The app writes a script around that footage, adds AI-made clips to explain the ideas (about half the
episode), wraps the result in the series' standard branded intro and outro, and exports:

1. a **3–5 minute 16:9 episode**, plus the files needed to upload it by hand (titles, description, chapters, subtitles, thumbnail);
2. **two or three upright 10-second teasers** for other social sites, cut from the finished episode. The app suggests
   the moments and the owner approves them.

Every episode looks the same: same intro, outro, captions, colours, music bed and section straps, numbered in order.

User: the channel owner, working alone on their own Windows 11 machine.

### What changes from today (2026-10-22)

| Today | After this spec |
|---|---|
| Three subjects: Unitree R1, Any topic, Video essay | **R1 only.** General and Essay are removed. |
| Topic Radar (news feeds) suggests topics | **Removed.** An episode starts from the owner's lesson and footage. |
| No intro or outro | **Branded intro and standard outro**, built by the app from a brand kit set up once. |
| No episode numbers | **Numbered series** ("#7 · Walking on stairs"). |
| Look, captions and music chosen per episode | **Series template** set once, copied into each new episode. |
| The script model chooses its own shape | **Three beat structures** to pick from when an episode is created. |
| The owner's footage is silenced and narrated over | **Per clip:** keep the footage's own sound, or narrate over it. |
| Cartoon host presents every clip | **Mascot**: in the intro, outro and some explainer clips. |
| One upright short per clip | **2–3 ten-second teasers**: suggested, approved, then rendered. |
| 15–20 clips (about 5–15 minutes) | **3–5 minutes**, not counting intro and outro. |

## 2. User journeys

Five steps, chosen in the header: *1 · Intro*, *2 · Lesson*, *3 · Clips*, *4 · Preview*, *5 · Publish*. Under them a
bar lists what the step asks for, each ticked as it is done and the next one lit, with one button: the next thing the
step needs that has no button on the page (the voices, then the pictures, on *3 · Clips*), or *Continue to* the next
step once this one is done. Optional items say so and never hold a step back. Each page lights one button at a time:
the thing to do next. With no episode to go back to, the app opens on *1 · Intro* until the channel has a name, and on
*2 · Lesson* after that.

1. **First run: the intro** (*1 · Intro*). Signed in with `az login`, so live services work. The intro is the template
   every episode opens with, so it has a step of its own: the channel name and tagline, a logo PNG, a mascot (a PNG, or
   the host design), a 3–5 second sting, the accent colour, and the outro length. The intro plays beside them, as large
   as the window allows, drawn on request for free. Everything is saved as it changes and used for every episode.
   *Series and look* (in the header's ⋯ menu) holds the rest: on *New episodes*, the music bed, picture style and
   caption preset new episodes start with; on *Host*, the cartoon host chosen from generated designs (paid, confirmed
   first) or supplied as a PNG; on *This episode*, the open episode's look, its host switch, its visual mix and *Use
   the series look*.
2. **New episode.** On *2 · Lesson* the owner:
   - types what they taught the R1 and, optionally, what went wrong;
   - picks one of three beat structures (§7);
   - drops in their footage files, giving each a one-line note ("first try, falls over") and choosing **My sound** or
     **Narrate over it**;
   - checks the episode number (next in the series, editable) and creates the episode.
   The app reads the official repositories, then writes the script: each beat is filled with the owner's footage clips
   where they fit and AI-made clips around them. The aim is about half real footage by running time.
3. **Direct the episode** (*3 · Clips*). Everything that exists today stays: reorder, depth tiers, editing dialogue,
   auditioning, pictures with a confirmed cost, takes, visual mix, undo and redo. In addition, every clip shows its
   beat, and footage clips show their sound choice, which can be switched. Any clip can be watched on its own (▶ on
   its card, or *Watch clip* in the inspector). Under the deck, a strip lays the clips end to end, each as wide as it
   runs, against the series' three to five minutes; a click picks a clip and a drag along the strip moves it (arrows
   pick, Ctrl and the arrows move).
4. **Preview** (*4 · Preview*). Watch the whole episode (intro, clips, outro) with captions. The caption style is set
   on the preview itself (*Aa*, in its corner), where it shows at once; the column beside holds the music.
   **Publish** (*5 · Publish*): export the master MP4 and its upload files, with the video size (an advanced control)
   and *Render master video* always in view at the foot of its column.
   The header has one **Make** button: a click does the next thing still to make (record the voices, then draw the
   pictures still wanted, then build a draft to watch), each asked first as before. Its menu has each of those on its
   own and *All of it*: the voices, the pictures and then either the preview (*a draft to watch*) or the master (*the
   finished video*), as one job confirmed once.
5. **Teasers.** On *5 · Publish*, beside the master video, **Suggest teasers** asks the fast script model for three
   10-second cuts from the finished episode. Each cut is one to three moments plus a hook line. Each card is upright, as on a phone, with the
   hook across its picture as the teaser shows it. The owner can drop a suggestion, swap a moment for another clip,
   drag it along its clip to a nearby time, or edit the hook, then renders the ones they keep as upright MP4s.
6. **Reopen later.** The library on *2 · Lesson* lists episodes by number, with thumbnail, clip count and running
   time. Opening one works offline with everything already made.

## 3. Tech stack (pinned)

Stays WinUI 3. Blazor WASM and Radzen are **not used**, by explicit decision (2026-10-22): FFmpeg, Edge and large
local video files need a desktop app, and Radzen cannot run inside WinUI XAML.

| Area | Choice | Version |
|---|---|---|
| Runtime | .NET (SDK on the build machine 10.0.400) | `net10.0`, app `net10.0-windows10.0.22621.0`, oldest Windows 10.0.19041 |
| UI | WinUI 3, unpackaged, self-contained Windows App SDK | Microsoft.WindowsAppSDK 2.5.1, WinUIEx 2.9.3, CommunityToolkit.WinUI.Controls.Segmented 8.2.251219 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| Graphics | Win2D, ComputeSharp.D2D1.WinUI (window backdrop) | 1.4.0, 3.2.0 |
| Script | Azure OpenAI, structured JSON output; the fast model also looks at frames | Azure.AI.OpenAI 2.1.0; deployments `gpt-5.4`, fast `gpt-5.4-mini` |
| Small jobs, optional | A model on this computer in the OpenAI style (Ollama, LM Studio), over plain HTTP | off until switched on in *Settings* or `POAUTOROBO_LOCAL_MODEL` is set; address `POAUTOROBO_LOCAL_URL` |
| Narration and listening | Azure AI Speech: the voice with word timings, and speech to text for "My sound" films | Microsoft.CognitiveServices.Speech 1.52.0; voice `en-US-DavisNeural` |
| Pictures | Azure OpenAI images | deployment `gpt-image-1-mini` |
| AI video | Azure OpenAI video | off until switched on in *Settings* or `POAUTOROBO_VIDEO_MODEL` is set |
| Stock photos | Pexels | key from the vault or `POAUTOROBO_PEXELS_KEY`; on unless switched off in *Settings* |
| Feeds | System.ServiceModel.Syndication | 10.0.12. **Removed** with Topic Radar unless still used elsewhere. |
| Secrets | Azure Key Vault as the `az login` user | Azure.Security.KeyVault.Secrets 4.11.2, Azure.Identity 1.21.0 |
| Rendering | FFmpeg CLI (built and tested on 9.0.1); Microsoft Edge headless for code-drawn scenes | on PATH or downloaded on first run |
| Tests | xUnit, Verify, NSubstitute | xunit 2.9.3, xunit.runner.visualstudio 3.1.4, Verify.Xunit 31.12.5, NSubstitute 6.2.0, Microsoft.NET.Test.Sdk 17.14.1 |

No new NuGet package is planned. Any addition is an *ask first* (§10).

## 4. Commands

```powershell
dotnet build PoAutoRobo.slnx
dotnet test PoAutoRobo.slnx --filter "Category!=Integration&Category!=Live"   # unit tests, about a second
dotnet test PoAutoRobo.slnx --filter "Category=Integration"                   # runs FFmpeg and Edge for real
dotnet format PoAutoRobo.slnx --verify-no-changes                             # CI runs this first
dotnet run --project src/PoAutoRobo.App
```

## 5. Structure

```
src/PoAutoRobo.Core/
  Library/   episode, edits, saving, settings, spend log, files; NEW: SeriesKit (brand kit + series look), BeatPlan
  Script/    script writer and prompts, grounding; NEW: teaser suggestions. REMOVED: TrendFeed, General/Essay briefs
  Voice/     narration and word timings
  Pictures/  generated pictures, AI video, stock photos, code-drawn scenes
  Render/    FFmpeg arguments, captions, builder; NEW: intro/outro cards, section straps, teaser cuts
src/PoAutoRobo.App/
  ViewModels/MainViewModel.*.cs   one view model in partial files; NEW partial: .Series, .Teasers
  Views/                          NEW: SeriesDialog, TeaserPanel; TopicPage becomes the Lesson page
tests/PoAutoRobo.Core.Tests/
```

On disk:

```
%LOCALAPPDATA%\PoAutoRobo\
  series\series.json        channel name, tagline, look, caption style, outro length (episode numbers come from the library)
  series\logo.png, sting.*, music.*, mascot.png    copies of the owner's files (never the original paths)
  host\                     the mascot's design sheet (as today)
Documents\PoAutoRobo\<NN-episode-slug>\
  episode.json, grounding.json, spend.jsonl, audio\, images\, video\, imports\
  export\NN-<slug>-1080p30.mp4 + .chapters.txt + .srt + .thumbnail.png + .description.txt
  export\teasers\NN-<slug>-teaser-<n>.mp4
```

### Capability map

There is one app, so this table takes the place of a separate `CAPABILITY-MAP.md`.

| Capability | Lives in | Depends on | Status |
|---|---|---|---|
| Brand kit and series look | `Library/SeriesKit` | file system | **new** |
| Lesson intake and beat structures | `Library/BeatPlan`, `Script/ScriptSchemas` | none | **new** |
| Grounding in the official repositories | `Script/Grounding` | GitHub REST | kept |
| Script, tiers, drift, fitting narration to footage | `Script/AzureScriptWriter`, `Script/Conformance` | Azure OpenAI | changed: beats, footage placement |
| Narration and word timings | `Voice/*` | Azure Speech | changed: skipped for "My sound" clips |
| Pictures, mascot, AI video, stock, scenes | `Pictures/*` | Azure OpenAI, Pexels, Edge | kept |
| Intro, outro, section straps | `Render/FfmpegArgs`, `Render/EpisodeBuilder` | FFmpeg | **new** |
| Master export and upload files | `Render/EpisodeBuilder`, `Render/PublishPack` | FFmpeg | changed: wrapped in intro and outro |
| Teaser suggestions and rendering | `Script/` (suggestion), `Render/` (cut) | Azure OpenAI fast model, FFmpeg | **new**, replaces per-clip shorts |
| Topic Radar | `Script/TrendFeed` | feeds | **removed** |
| Any-topic and essay subjects | `Script/ScriptSchemas`, `Library/Subject` | none | **removed** |
| Spend log and budget | `Library/SpendLog` | file system | kept |
| Settings and secrets | `Library/AppSettings` | Key Vault | kept |

## 6. Conventions

These are the rules in `CLAUDE.md`, which win if the two ever differ. In short:

- Every change to an episode goes through `MainViewModel.Edit`, and the edit itself is a pure function in `EpisodeEditor`.
- `Episode`, `Clip` and `VisualSpec` are read back strictly. New fields are optional or `init` with a default, and new
  enum values go on the end.
- Media paths stay inside the episode folder. Anything new that holds a path is handled in `ProjectStore.KeepOwnMedia`,
  `ProjectStore.Duplicate` and `EpisodeBuilder.UnusedMedia`. Brand kit files live in the series folder and are never
  stored in an episode.
- Nothing that costs money happens without being asked for. Batches show their cost first and check `OverBudget`.
- Text that did not come from the owner is fenced (`AzureScriptWriter.Fenced`). The owner's lesson text and footage
  notes are the owner's own and are not fenced; the finished script fed back for teaser suggestions is.
- Working files go under `Files.ScratchRoot`, and every program the app starts dies with its job.
- Seams for tests are delegates; an interface only where there are two real implementations.
- Comments and messages use plain words and say *why*. A deliberate shortcut is marked `// ponytail:`.

## 7. Behaviour rules

**Subject.** Every episode is an R1 episode: grounded in the official repositories and held to the R1 accuracy rules,
as R1 episodes are today. An `episode.json` saved as General or Essay still opens, now as an R1 episode: the old
subject field is simply no longer read, and the script is not rewritten. (No notice is shown, by decision on
2026-10-22: noticing would mean keeping the removed subjects in the code.)

**Beat structures.** Picked when the episode is created and saved with it. Each beat has a name (shown as the section
strap), a clip range, and a hint whether real footage belongs there:

| Structure | Beats (footage-first beats marked ●) |
|---|---|
| Story | Goal · Try ● · Fail ● · Fix · Result ● · Recap |
| Tutorial | Concept · Setup ● · Demo ● · Recap |
| Hook first | Hook ● · Explain · Demo ● · Next time |

The script model fills every beat with 1–3 clips, in order. Every footage file is placed exactly once, in the beat its
note fits best (footage-first beats preferred). The owner can move it afterwards. AI-made clips explain the ideas
between footage. Without footage, the episode is all AI-made clips and a notice says so.

**Length.** *Standard*: 6–10 clips, aiming at 3–5 minutes of running time without intro and outro. A warning (export
still allowed) appears outside that range. It is the only length: the Short, Two-clip and Quick test lengths are
removed.

**Footage sound.** A footage clip has a sound choice:
- *Narrate over it* (today's behaviour): the dialogue is fitted to the footage length, footage sound muted.
- *My sound*: no narration is recorded; the clip lasts as long as the footage, and its own sound plays,
  loudness-matched to the narration. Dialogue is optional: if the owner types what is said, it is captioned with
  words spread evenly across the clip; if not, the clip has no captions. When the episode is written, each "My
  sound" film is listened to (Azure speech to text, paid by the second, as part of writing the script, which the
  owner asked for). What is heard is kept beside the film's copy (`*.heard.json`), becomes the clip's captions for
  the owner to correct, and is timed as it was said; while the typed words are as many as those heard, a corrected
  word keeps its time, and otherwise they are spread evenly again.

**How the models are used.** Each is asked only as part of something the owner did, and each answer is kept.
- *The script* is planned first (the main model: titles, beats, footage, picture types and what each clip covers),
  then each clip is written on its own from the plan, four at a time; the first goes alone so the others find the
  long opening they share already cached. A clip the service turns away is left for the owner, as before. A "My
  sound" film's clip is not asked for at all.
- *The owner's films* are watched before the script is written: eight small frames to the fast model, which says
  what happens second by second and when the telling moment is. That goes to the script and, kept on the clip
  (`Clip.FootageSeen`), to the teaser suggestions. The eye button on a film row asks the same to fill in its note.
  Narrated films are given a word count for their length, at the voice's pace.
- *The series so far* (up to ten earlier episodes, by number, title and clip titles) goes to the script, so a clip
  may call back to one in a sentence instead of explaining it again.
- *The repositories* are searched by terms a model gives for the lesson, as they are written in the code, as well as
  by its own words; and besides each README, up to four documentation pages whose path names those terms (the list
  of pages is read once a day, each page once).
- *Checks*: a code-drawn scene that runs is looked at once it has built (frame at 90%) and sent back once if a label
  is cut off, overlapping or under the captions; a new picture of the host is compared with its design and a
  notice says when it has drifted. The editor's note on a clip can be answered with *Rewrite to answer the note*.
- *Small jobs* (the drift check, search terms, teaser ideas, upload notes) go to the local model when one is named,
  for free, and to the fast model when it is not running.
- *Batches* draw three pictures and record four voices at once. No picture is started that would take the spending,
  counting the ones being drawn, past the confirmed cost or the budget.
- *The voice's pace* is learned from its recordings (a fifth of the way towards each line of 12 words or more at the
  normal pace) and kept in `%LOCALAPPDATA%\PoAutoRobo\pace.json`, so estimates are close from the first clip.

Prompt and model changes are compared with `ScriptQualityTests` (Live, opt-in): the same three lessons, counted
scores and a judge model's, added to `%LOCALAPPDATA%\PoAutoRobo\quality.jsonl` each run.

**Brand kit and series look.** Saved in `series\series.json`. When an episode is created, it copies the series look
(picture style, caption style, music bed) into its own `episode.json`, so a later change to the series does not alter
old episodes. A **Use series look** command re-applies it to an open episode, as one undoable edit.

**Intro** (about 5 s, built locally, never paid for once the mascot exists): the logo and the mascot animate in over the
series background, the channel name and tagline appear, then "#N · Episode title", with the music sting under it. It
opens on a **swarm**: up to 24,000 points of light spiral in on a swirling current over the dark, are pulled harder and
harder to their places in the finished card, and land together at 2.2 s, when the background has risen out of the dark;
the blow throws them out, each springs back, and they fade into the real card. The swarm is drawn by the app
(`Render/Swarm`, at most 1280×720) and fed to FFmpeg as raw frames; a card with nothing bright on it is used as it is.
With no sting in the kit, the app makes one (`Render/Synth`): a climbing band of noise, then on the hit a low thump, a
bright chord and high notes scattered left and right. The intro is rendered at export in the export's resolution and
frame rate, and cached in the scratch folder by everything that went into it.

**Into each beat.** Where the beat changes between clips, a made-up whoosh is mixed into the join, loudest on the cut;
the music dips under it as under the voice.

**Robot's eyes.** A clip with the owner's film can be shown as the robot's own camera might see it (*Look* switch on the
picture tab; `Clip.RobotView`, off by default): brightness read as depth in false colour, edges traced in light, scan
lines, corner brackets and a readout with the beat and the clip's title, a clock and a blinking dot. A film in the
*Fail* beat also glitches and flashes FAULT. It is drawn by FFmpeg inside the clip's own encode, so it costs nothing and
teasers cut from that clip show it too.

**Outro** (series setting, 5–20 s, default 15 s): mascot, channel name, "Subscribe" and "Next time: …" (from the
script's last beat when there is one), with two empty areas left where YouTube end-screen cards go. The music bed
continues under it.

**Section straps.** At the first clip of each beat, the beat name shows as a strap for 2.5 s, top left, in the
accent colour, clear of the captions. It is burned in with the captions, so the draft preview (captions drawn live)
does not show it.

**Chapters.** One per clip. YouTube shows chapters only when the first starts at 0:00 and none is under ten seconds,
so the intro and outro are not chapters of their own: the first clip's chapter starts at 0:00 and takes in the intro.

**Episode number.** Next = highest number in the library + 1 (1 for the first), editable at creation, saved in
`episode.json`, used in the intro, the folder name (`07-walking-on-stairs`) and the suggested titles.

**Mascot.** The host design sheet is now the mascot. `HostVisible` starts off for every clip. The script may turn it on
for explainer clips in non-footage beats only. The intro and outro always show the mascot picture.

**Teasers.** *Suggest teasers* is one fast-model call, made only on the button, with its token use logged like every
other script call. It gets the finished clips' titles, dialogue (fenced) and timings, and returns three suggestions. Each
is 1–3 moments (clip and start/end within it) adding up to 10 s ±0.5 s, plus a hook line of at most 8 words. The app
checks every moment against the real clip lengths and drops or trims ones that don't fit. With the mock writer, the
suggestions are made by rule: the first footage-first beat's opening, the last demo or result clip, and the last clip.
Each kept teaser is rendered at 1080×1920, 30 fps: the 16:9 picture centred over a blurred, enlarged copy of itself,
the hook line on top for the whole teaser (each of its lines drops in a moment after the one above and bounces to rest), the channel name in the bottom bar, the episode's sound for those moments,
and captions in the series style. No new media is generated for a teaser. Each card has a *Fill the frame* switch,
which crops the picture to 9:16 instead of the blurred background, and a ▶ that plays a 360×640 draft of the teaser
as it stands, cut from the preview's own clip renders and kept in the clip cache.

**Watching one clip.** A clip plays on its own at preview size with its sound, drawn exactly as the preview draws it
(same cache key), so after a preview it is instant; before one, only that clip is drawn. Its voice is recorded first
if this wording has not been, as *Audition* does. Renders wait while a clip or teaser is being got ready to watch,
since both draw into the same clip cache.

**What the app is doing, and what it has said.** A job under way shows in a bar that floats over the foot of the page,
so nothing on the page moves when it starts or ends: what it is, the exact step, how far, how long, and *Cancel*. The
outcome of the last action shows in the same place and goes by itself after ten seconds (not while the pointer is on
it); an error stays until it is closed. Everything said is kept, newest first, under the note at the bottom left of
the window (*Connecting…*, *Offline*, *FFmpeg missing* or *Messages*), beside the build version.

**Batches hold to the cost shown.** A batch of pictures (*Generate pictures*, *All of it*) stops before a clip that
would take its picture and AI-video spending past the estimate that was confirmed, or past the monthly budget. The
rest keep their title cards, and the message says how many and why. With no known price there is no figure to hold
to, and only the monthly budget applies.

**Light and sound in the app.** All drawn or worked out on this computer, never paid for. The light is off until
*Effects* is turned on in *Settings*, and stays out when Windows is set to show no animation; the sounds follow the
*Sounds* switch:
- a slow cloud of light in the accent colour lies behind the lesson page and the deck of clips; each card is frosted
  glass that leans towards the pointer, its picture seen through a lens that follows it;
- while a script is written, motes drift over the placeholders and gather into each one as its title arrives, with a
  note a step up a five-note scale each time;
- each clip of a batch done throws sparks off its card and sounds a tick a step higher, travelling left to right; one
  that does not come out puffs grey smoke with a sour note;
- auditioning a clip shows its voice as ribbons of light by band of pitch, with each word lit as it is spoken;
- while clips are drawn, a strip of film in the progress panel develops each clip like a print in the tray;
- a finished master video gathers its thumbnail out of a swarm of some twenty thousand motes of light, one for each
  point of the picture, swirling in from all round the window; as it comes together, confetti in the series colour and
  a fanfare.

**Settings.** One dialog from the header's gear: sounds and their volume, draft-quality pictures, the monthly budget
and what has been spent, *Effects*, *Advanced controls*, and the optional services. A confirmation that costs money says the month's spending
against the budget, when there is one. *Effects* and *Advanced controls* are off until turned on. *Advanced controls*
shows each clip's picture type, *Another take* and earlier takes, the robot's-eye *Look* and *Use a generated picture
instead*; the mix of picture types in *Series and look*; the 1080p 60 fps and 4K sizes; *Have an editor read the
script*; and *All of it: the finished video*. Hidden, every master is 1080p at 30 fps.

**Optional services.** Each has a switch in *Settings*, with the model it uses where it needs one: *AI video* (off),
*Stock photos* (on), *Small jobs on this computer* (off) and *Find passages by meaning* (off). They are saved in
`%LOCALAPPDATA%\PoAutoRobo\services.json`, which never holds a secret, and read as the app starts, so a change offers
*Restart now* (not while a job runs). A service's environment variable, when set, still names its model, and the
dialog says which are set.

**Preview keys.** L plays, then 2× and 4×; K pauses at normal speed; J slows a fast play or steps back five seconds
(the media player cannot play backwards smoothly); comma and full stop step one frame while paused.

**Kept as today.** Depth tiers A/B/C, drift checks, visual mix and kinds, takes, the media cache, cost estimates,
budget, undo and redo, caption presets, −16 LUFS loudness, chapters, `.srt`, the description file, thumbnails, and
"Remove unused media".

## 8. Stand-ins without a sign-in

| Service | Stand-in |
|---|---|
| Script writer | Canned R1 episode built from the chosen beat structure, with footage placed in footage-first beats in order |
| Narrator | Windows voice, word timings spread evenly |
| Pictures and video | None needed: a clip without a picture renders as a title card |
| Mascot | With no mascot picture, the intro and outro use the logo alone. With no logo either, the channel name alone. |
| Teaser suggestions | The rule-made three above |
| Watching, listening and the checks | Skipped: films are written to from their notes, typed captions are spread, scenes and pictures are kept as drawn |
| FFmpeg missing | Export and teasers disabled with a "Get FFmpeg" action |

## 9. Testing strategy

- xUnit against `PoAutoRobo.Core`. **At most 100 unit tests.** Removing General, Essay, Topic Radar and per-clip shorts
  frees room for the new behaviour. One test may cover several cases of one behaviour.
- Snapshot tests (Verify) for the new prompt text (beats, footage placement, teaser suggestion) and the new FFmpeg
  arguments (intro, outro, straps, "My sound" mix, teaser layout).
- Integration (FFmpeg for real): a mock episode with one footage clip of each sound choice exports with intro and outro,
  and three teasers export, all checked with `ffprobe`.
- Live tests stay opt-in and pass by doing nothing without a sign-in.
- No UI automation. The app is checked by running it.
- Strict TDD: failing test first, one commit per task.

## 10. Boundaries

**Always:** keep secrets in the vault; run long work off the UI thread with progress and cancel; cache generated media;
confirm cost before batches; keep `SPEC.md` in step with what is built.

**Ask first:** adding a NuGet package; creating, changing or deleting Azure resources; deleting episode folders or media;
any change to `episode.json` that is not an optional or `init` field.

**Never:** commit or log secrets; invent R1 joint names, rates or API details; skip, weaken or delete a failing test;
call a paid endpoint without an action by the owner; loosen `SceneRenderer`'s locked-down browser.

## 11. Out of scope

- Uploading to YouTube or any other site, or signing in to them. The app writes files and the owner uploads them.
- Any subject other than the R1 series; topic suggestion feeds.
- More than one series or brand kit.
- Trimming the owner's footage inside the app (beyond teaser moments); keyframes; a general video editor.
- Square video; languages other than English; lip-sync of the mascot.
- Controlling the robot or running simulations; MSIX packaging and auto-update.

## 12. Edge cases

| Situation | Behaviour |
|---|---|
| No brand kit yet | Without a channel name, export goes ahead with no intro or outro, and the message says how to add them. |
| Logo, sting or music file missing from the series folder | That part is left out, and a one-line notice names it |
| Footage unreadable, or shorter than 2 s or longer than 120 s | Rejected at intake with a plain message |
| "Narrate over it" footage outside 5–120 s | As today: rejected for fitting; switch to "My sound" offered |
| Script places a footage file twice or not at all | One repair retry, then the missing file is added at the end of its best beat by rule |
| A beat comes back empty | Allowed; its strap is skipped; a notice says which beat |
| Episode number already used | Allowed with a warning; the folder name gets a suffix |
| Teaser suggestion moment outside a clip, or total not 10 s ±0.5 s | Trimmed or padded from the same clip; dropped if it can't be fixed |
| Episode under 10 s of content | "Suggest teasers" disabled |
| Old General or Essay `episode.json` | Opens as R1 (§7) |
| Model declines, or its reply is cut off | As today: a plain message saying which, and nothing is saved |
| Export or teaser render cancelled, or FFmpeg fails | Partial file deleted; last 20 log lines shown |

## 13. Success criteria

1. With no sign-in and FFmpeg installed: a new episode with two footage files (one "My sound", one "Narrate over it")
   and the *Story* structure goes from lesson to exported MP4 with no errors.
2. The exported master starts with the intro (5 s ±0.5) showing the right episode number and title, and ends with the
   outro at the configured length (±0.5 s), checked with `ffprobe` durations and a frame grab.
3. Every footage file appears in exactly one clip, in a beat of the chosen structure, for both the mock and (live)
   real script writer.
4. A "My sound" clip's audio in the export is the footage's own sound, with no narration file recorded for it. A
   "Narrate over it" clip's narration is within ±1.0 s of the footage length.
5. Each beat's first clip carries a strap with the beat name (snapshot of the caption or overlay file).
6. A new episode's look, caption style and music bed match `series.json`. Changing the series afterwards leaves that
   episode unchanged until *Use series look*, which one Undo reverses.
7. The next episode number is the library's highest + 1, and appears in the folder name and the intro.
8. *Suggest teasers* returns 2–3 suggestions, each 10 s ±0.5 s, every moment inside its clip. Without a sign-in, the
   rule-made three are returned.
9. Each rendered teaser is 1080×1920, 30 fps, H.264/AAC, 10 s ±0.5, per `ffprobe`.
10. Rendering teasers makes no image, video or narration requests.
11. General, Essay and Topic Radar are gone from the UI and code. An old General or Essay episode opens as R1.
12. A Standard episode's script has 6–10 clips; outside 3–5 minutes a warning shows and export is still allowed.
13. Everything kept in §7 still behaves as before: the existing tests that cover it still pass, unweakened.
14. No secret appears in the repository, `episode.json`, `series.json` or logs.
15. `dotnet build`, the unit tests, the integration tests and `dotnet format --verify-no-changes` all pass.

## 14. Open questions

1. ~~**Captions on "My sound" clips.**~~ Resolved 2026-10-22: each is listened to when the episode is written (§7,
   footage sound). Not yet tried against the live service; a failure leaves the typed text, spread evenly, and a
   message says why.
2. **Intro and outro artwork.** A plain background in the series accent colour, or a generated backdrop made once
   (paid, confirmed)? Default: plain accent colour with a soft gradient, built by FFmpeg.
3. ~~**Teaser framing.**~~ Resolved 2026-10-22: the blurred copy stays the default, with a per-teaser *Fill the frame*
   switch for a centre crop; the card's ▶ shows whether the robot is still in shot.
4. ~~**Episode titles.**~~ Resolved 2026-10-22: every suggested title starts "<channel> #N: ", added in code
   (`PublishNotes.InSeries`) so it is always there.
5. ~~**Teaser card playback.**~~ Resolved 2026-10-22: each card's ▶ renders a small draft from the preview's clip
   renders, cached, so it costs a few seconds once and nothing after.
