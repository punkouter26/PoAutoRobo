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
- [ ] **T17 `SuggestTeasersAsync` (fast model; the mock uses the rules).** Check: teaser prompt added to the prompt snapshot; token use logged.
- [ ] **T18 Teaser render replaces per-clip shorts.** Check: argument snapshot (replaces the shorts test); integration: 3 × 1080×1920, 30 fps, h264/aac, 10 s ±0.5, no paid requests.
- [ ] **T19 Teaser phone cards on the Export step.** *(concept 8)* Check: in the app on stand-ins: suggest, edit, drop, render.

## G. Finish
- [ ] **T20 Warning outside 3–5 minutes; titles start "<channel> #N".** Check: publish prompt snapshot diff read.
- [ ] **T21 Bring `CLAUDE.md`, `SPEC.md` and this file up to date.** Check: `dotnet format --verify-no-changes`, unit and integration suites green.

## Deferred (from SPEC open questions)
- Automatic captions for own-sound clips (speech-to-text).
- Generated intro backdrop; a centre-crop choice for teasers.
