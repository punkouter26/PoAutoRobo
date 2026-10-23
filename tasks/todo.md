# To do: the "Teaching the R1" series studio

How each task is built (see `plan.md`): failing test → pass → unit suite → build → `dotnet format` → one commit.
Unit tests stay at or under 100. The count after each task is shown where it changes.

## A. Clearing out
- [x] **T1 Move `IsWebLink` to `Files`.** Check: `ProjectStore` has no `TrendFeed` reference; its test lives with `Files`; 98 tests green.
- [x] **T2 Take Topic Radar out of the app.** Check: no radar UI, commands or `TrendFeed.Create()` in the app; the app builds and starts.
- [x] **T3 Delete `TrendFeed`, its tests and the Syndication package.** Check: `git grep Syndication` finds nothing; **94** tests green.
- [x] **T4 Stop offering subjects in the UI.** Check: no "About" box; every new episode is grounded in the repositories.
- [x] **T5 Remove `Subject` from Core and the spec notice.** Check: an old `"Subject": "General"` file loads; **93** tests green; SPEC §7/§12 updated.

## B. Series data
- [x] **T6 Episode number, structure, beat, own-sound fields; numbered folders.** Check: round trip, old file defaults, next number = highest + 1; **94**.
- [x] **T7 `SeriesKit` and `UseSeriesLook`.** Check: save and load, damaged-file fallback, pure look change; **95**.

## C. Script around the lesson
- [x] **T8 Lesson, beats and footage placement in the script writer; Standard length 6–10.** Check: prompt snapshot diff read and accepted; every footage file placed exactly once (mock and `Repair`); **96**.
- [x] **T9 Lesson page.** *(concept 1: form + library)* Check: on stand-ins, a Story episode with 2 footage files is created as `NN-slug`, with the series look and music copied.

## D. My sound
- [x] **T10 Footage sound replaces narration for own-sound clips.** Check: argument snapshot plus spreading; integration: master audio comes from the footage, no narrator call; **97**.
- [x] **T11 Sound switch per clip in the inspector and on the card.** Check: in the app; switching runs or skips the fit; undo restores it.

## E. Brand
- [x] **T13 Intro and outro FFmpeg arguments.** Check: snapshot with and without logo, mascot and sting; **98**.
- [x] **T12 Series dialog and "Use series look".** *(concept 5: dialog + intro preview; built after T13)* Check: in the app, the kit survives a restart and the files are copied into `series\`.
- [x] **T14 Intro and outro in the master; the first chapter starts at 0:00 and takes in the intro.** Check: integration length = 5 s + clips + outro ±0.2; export with no kit still works.
- [x] **T15 Section straps.** Check: caption snapshot gains the strap event (diff read).

## F. Teasers
- [x] **T16 `Teasers.Fit` and `Teasers.ByRule`.** Check: every kept teaser is 10 s ±0.5 inside real clip lengths, hook ≤ 8 words; **99**.
- [x] **T17 `SuggestTeasersAsync` (fast model; the mock uses the rules).** Check: teaser prompt added to the prompt snapshot; token use logged.
- [x] **T18 Teaser render replaces per-clip shorts.** Check: argument snapshot (replaces the shorts test); integration: 3 × 1080×1920, 30 fps, h264/aac, 10 s ±0.5, no paid requests.
- [x] **T19 Teaser phone cards on the Export step.** *(concept 8)* Check: in the app on stand-ins: suggest, edit, drop, render.

## G. Finish
- [x] **T20 Warning outside 3–5 minutes; titles start "<channel> #N".** Check: done in code, not in the prompt, so every title is sure to carry it (`PublishNotes.InSeries`); runtime and title rules asserted in the existing tests.
- [x] **T21 Bring `CLAUDE.md`, `SPEC.md` and this file up to date.** Check: `dotnet format --verify-no-changes`, unit and integration suites green.

## H. Afterwards
- [x] **T22 A draft in one go; batches hold to the cost shown.** *Make it all* ends on the preview or the master; a picture batch stops before passing the confirmed estimate or the budget. Check: `CostEstimate.Allows` and per-clip prices in the cost test; in the app, the menu and its plan.
- [x] **T23 Watch a clip or a teaser on its own; J/K/L on the preview; teasers can fill the frame.** Check: teaser snapshot gains the filled draft (diff read: earlier sections unchanged); integration: a clip plays from the preview's own render (one new cache file), a teaser draft is 360×640; in the app, ▶ on a card, *Watch clip*, and J/K/L.

## I. Light and sound
- [x] **T24 The video: swarm intro, made-up sting, whooshes into each beat, robot's eyes on footage, hook lines that drop.** Check: intro, clip, join and teaser snapshots (diffs read); one test for the swarm landing on the hit and the made-up sounds; integration: an export with robot's-eye footage and a whoosh per beat, the intro never silent.
- [x] **T25 The app: glass cards with a lens, gathering light while writing, sparks and a rising scale per clip, the budget as liquid, the voice as light, the darkroom strip, confetti and a fanfare.** Check: in the app on stand-ins (the gathering light only shows with the real writer, which streams titles).

## J. Layout and light, U1–U10
- [x] **U1 One Make button.** A click does the next thing still to make (voices, then pictures, then a draft); its menu has each job and *All of it*. Check: in the app on stand-ins, the label follows the episode (*Record voices* on an unrecorded one, *Make a draft* on a finished one); the episode title has its room back in a tight window.
- [x] **U2 One Series and look dialog.** Channel, Host and This episode tabs; the host and mix dialogs are gone, and the ⋯ menu keeps only this episode's own items. Check: in the app, each tab shows. Applying the mix from the tab was not tried.
- [x] **U3 A floating job bar and a status strip.** The job and the last message float over the foot of the page, so nothing moves; an outcome goes by itself after ten seconds; the notice and messages sit at the bottom left. Check: in the app, the message floats over the page and goes by itself.
- [x] **U4 Upright teaser cards.** A 9:16 poster with the hook over it, and each moment dragged along its clip. Check: in the app on stand-ins, the suggested cards show upright with their hooks. Dragging a moment was not tried.
- [x] **U5 Captions on the preview.** *Aa* on the picture holds the style, size, outline and colour; *Render master video* stays in view at the column's foot, with *Show the finished files* once there are some. Check: in the app, the flyout opens and both buttons show.
- [x] **U6 The clip strip.** The clips end to end under the deck against the 3–5 minute band; click, drag, arrows and Ctrl+arrows. Check: in the app it shows; dragging along it was not tried.
- [x] **U7 A fluid backdrop on the deck.** Advection, vorticity confinement and a pressure solve on the graphics card, stirred by the pointer, a dragged card and a finished clip. Check: in the app it shows and stays steady; a first version built up speed without end and is fixed.
- [x] **U8 The R1 as a hologram.** Ray-marched on the graphics card, posed from the lesson's words, on the lesson page and beside the placeholders while writing. Check: in the app, typing a lesson about standing up or walking changes what it rehearses; the one beside the placeholders was not seen, as writing a script would make a new episode.
- [x] **U9 The thread through the cards.** A Verlet rope behind the glass in running order; taut on a reorder, red at a clip of the wrong length. Found and fixed on the way: the cards' glide never reached its end, so a wider window left them in their old places. Check: the deck reflows to four columns.
- [x] **U10 The thumbnail gathers itself.** About twenty thousand motes, one per point of the picture, then the confetti. Check: in the app, a stand-in render of a one-clip episode (copied aside first and put back exactly).

## K. The models, A1–A10
- [x] **A1 Batches side by side.** Three pictures and four voices at once; narration locked per file, not app-wide; the spend check counts the pictures being drawn. Check: unit suite; in the app on stand-ins, voices record. A live picture batch was not run.
- [x] **A2 Plan, then write the clips.** The main model plans; clips are written four at a time from it, the first alone to warm the cache; "My sound" clips are not asked for. Check: prompt snapshot (diff read); writer tests rewritten for the plan.
- [x] **A3 The voice's pace is learned.** From recordings at the normal pace, kept in `pace.json`; narrated films get a word count. Check: pace rules in the timing test.
- [x] **A4 A model on this computer for small jobs.** `POAUTOROBO_LOCAL_MODEL`, falling back to the fast model. Check: drift test routes to it and falls back. Not tried against a running Ollama. Not done: Veo on Google Cloud (needs a Google Cloud project, an *ask first*), and the host's sheet as a video's first frame (it would open every video on a design sheet).
- [x] **A5 Prompts trimmed.** The leftover farming-and-war scope is gone; host pictures no longer carry the dialogue. Check: prompt snapshot.
- [x] **A6 The films are watched.** Eight frames to the fast model before the script, kept by the film; an eye button fills in a note. Check: writer test with a fake; integration: frames are taken. Not tried live.
- [x] **A7 "My sound" films are listened to.** Azure speech to text; kept beside the copy, used as timed captions. Check: integration: heard times become the captions, a silent film is not listened to. Not tried live.
- [x] **A8 Looked at before you see it.** Scenes checked once built and sent back once; host pictures compared with the design; *Rewrite to answer the note*. Check: writer test with a fake. Not tried live.
- [x] **A9 Better repository search.** Terms from a model; up to four documentation pages a repository by path. Check: grounding test with a fake. The documentation pages were not fetched live.
- [x] **A10 Series memory and a yardstick.** Up to ten earlier episodes go to the script; `ScriptQualityTests` scores three lessons. Check: project store test; the yardstick does nothing without `POAUTOROBO_LIVE=1`.

## Deferred (from SPEC open questions)
- Generated intro backdrop.
