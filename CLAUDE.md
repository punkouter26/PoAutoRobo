# PoAutoRobo

A Windows desktop app that turns a topic into a narrated, captioned explainer video: it writes a script, records the
voice, makes a picture for each clip and renders the result with FFmpeg. **WinUI 3, unpackaged, .NET 10. Not Blazor and
not a web app:** there are no routes, components or browser APIs here.

## Commands

```
dotnet build PoAutoRobo.slnx
dotnet test PoAutoRobo.slnx --filter "Category!=Integration&Category!=Live"   # unit tests, about a second
dotnet test PoAutoRobo.slnx --filter "Category=Integration"                   # run FFmpeg and Edge for real
dotnet format PoAutoRobo.slnx --verify-no-changes                             # CI runs this first
```

Warnings are errors, analyzer and style rules included, and so is a package with a known weakness. Rules are tuned in
`.editorconfig`; package versions live only in `Directory.Packages.props`.

## Layout

- `src/PoAutoRobo.Core`: everything that is not a window. Five folders, each a namespace every project imports
  already (see `Directory.Build.props`), so no file lists them:
  - `Library`: the episode and its edits, saving, settings, the spend log, file helpers.
  - `Script`: the script writer and its prompts, topic feeds, grounding in the official repositories.
  - `Voice`: narration and word timings.
  - `Pictures`: whatever a clip shows: generated pictures, AI video, stock photos, code-drawn scenes.
  - `Render`: FFmpeg command lines, captions, the builder that turns an episode into a video.
- `src/PoAutoRobo.App`: the window. One view model, `MainViewModel`, in partial files by concern (`.Library`,
  `.Edits`, `.Pictures`, `.Export`, `.Activity`); views in `Views/`.
- `tests/PoAutoRobo.Core.Tests`: tests of the core. The app project has none.

## Rules that are easy to break

- **Every change to an episode goes through `MainViewModel.Edit`.** It saves, records undo, and brings the cards into
  line. The edits themselves are pure functions in `EpisodeEditor`; add one there, never mutate in the view model.
- **`Episode`, `Clip` and `VisualSpec` are saved as JSON and read back strictly.** Add fields as optional parameters
  or `init` properties with a default. New `VisualKind`s and `Look`s go on the end of the enum.
- **Media paths must be inside the episode folder.** `ProjectStore.KeepOwnMedia` drops any that are not, on load.
  Anything new that holds a path (see `Clip.AllMedia`) must also be handled there, in `ProjectStore.Duplicate`, and in
  `EpisodeBuilder.UnusedMedia`.
- **Nothing that costs money happens without being asked for**, and a batch only after its cost is confirmed and
  the monthly budget checked (`OverBudget`). Generated media is cached by everything that went into the request
  (`MediaCache`), so asking again is free; a deliberate retry is a new *take* (`VisualSpec.Take`).
- **Text that did not come from the user is fenced** before it reaches a model: feed stories, repository passages,
  and anything a model wrote earlier. Use `AzureScriptWriter.Fenced`; the prompts in `ScriptSchemas` say not to obey it.
- **Model-written JavaScript runs only in `SceneRenderer`'s locked-down browser**: no network, a time limit on every
  frame. Do not loosen its flags or its page policy.
- **Working files go under `Files.ScratchRoot`**, never the episode folder (it is usually cloud-synced) and never
  loose in the temp folder. The app sweeps that folder at launch.
- **Programs the app starts die with the job**: register `FfmpegRunner.Kill` on the cancellation token, as
  `FfmpegRunner` and `SceneRenderer` do, so closing the window leaves nothing running.

## Conventions

- Comments and messages are written in plain words for the person using the app, and say *why*. Match that voice.
- Seams for tests are delegates (`JsonChat`, `ImageMaker`, `RepoSource`, `Embed`, `FetchText`), not interfaces, unless
  there are two real implementations (`IScriptWriter`, `INarrator`).
- Sizes and spacing used on more than one page are named in `App.xaml`; use the name, not the number.
- A deliberate shortcut is marked `// ponytail:` with its ceiling and how to lift it.
- Prompt text and FFmpeg arguments are snapshot-tested with Verify. A changed `.received.` file is the new truth
  only once you have read the difference; then replace the `.verified.` file with it.
- Unit tests are capped at 100. One test may cover several cases of one behaviour; tests that run FFmpeg or Edge are
  tagged `Integration`, tests that call real services `Live` (they pass by doing nothing without a sign-in).

## Configuration

Secrets come from Azure Key Vault as the signed-in `az login` user; nothing is stored on disk. Without a vault or a
connection the app runs on stand-ins. Each of these environment variables overrides a default:

| Variable | What it sets |
| --- | --- |
| `POAUTOROBO_VAULT` | Key Vault address |
| `POAUTOROBO_CHAT_MODEL`, `POAUTOROBO_FAST_CHAT_MODEL` | Script model deployments |
| `POAUTOROBO_IMAGE_MODEL` | Picture model deployment |
| `POAUTOROBO_VIDEO_MODEL` | Video model deployment; unset means AI video is not offered |
| `POAUTOROBO_EMBEDDING_MODEL` | Embedding deployment; unset means passages are found by matching words alone |
| `POAUTOROBO_VOICE` | Narration voice |
| `POAUTOROBO_PEXELS_KEY` | Stock photo key, when not in the vault |
| `POAUTOROBO_CHAT_RATES`, `POAUTOROBO_FAST_CHAT_RATES` | Dollars per million tokens as `input,cached,output`; unset means script use is shown unpriced |
| `POAUTOROBO_VOICE_RATE` | Dollars per million characters spoken; unset means unpriced |
