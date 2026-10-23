# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# PoAutoRobo

A Windows desktop app for one YouTube series: its owner teaching a Unitree R1 one skill per episode. From the lesson
and the owner's own films it writes a script in fixed beats, records the voice (or keeps a film's own sound), makes a
picture for each other clip, wraps the episode in the series' intro and outro, and renders it with FFmpeg, plus
ten-second upright teasers. `SPEC.md` says what it does and why. **WinUI 3, unpackaged, .NET 10. Not Blazor and not a
web app:** there are no routes, components or browser APIs here. `AGENTS.md` is the working agreement for agents
(branching, commit wording, checking the app starts, how replies are summarised); read it too.

## Commands

```
dotnet build PoAutoRobo.slnx
dotnet test PoAutoRobo.slnx --filter "Category!=Integration&Category!=Live"   # unit tests, about a second
dotnet test PoAutoRobo.slnx --filter "Category=Integration"                   # run FFmpeg and Edge for real
dotnet format PoAutoRobo.slnx --verify-no-changes                             # CI runs this first
dotnet test PoAutoRobo.slnx --filter "FullyQualifiedName~FfmpegArgsTests"     # one class, or one test by its name
```

To run the app, start the built exe; its path is in `AGENTS.md`.

Warnings are errors, analyzer and style rules included, and so is a package with a known weakness. Rules are tuned in
`.editorconfig`; package versions live only in `Directory.Packages.props`.

## Layout

- `src/PoAutoRobo.Core`: everything that is not a window. Five folders, each a namespace every project imports
  already (see `Directory.Build.props`), so no file lists them:
  - `Library`: the episode and its edits, the lesson and its beats (`BeatPlan`), the series brand kit (`SeriesKit`),
    saving, settings, the spend log, file helpers.
  - `Script`: the script writer and its prompts, grounding in the official repositories.
  - `Voice`: narration and word timings.
  - `Pictures`: whatever a clip shows: generated pictures, AI video, stock photos, code-drawn scenes.
  - `Render`: FFmpeg command lines, captions, the intro and outro, teasers, the builder that turns an episode into a video,
    the intro's swarm of light (`Swarm`) and the video's made-up sounds (`Synth`).
- `src/PoAutoRobo.App`: the window (`MainWindow`, with `.Moments`, `.Dialogs` and `.Taskbar` beside it). One view
  model, `MainViewModel`, in partial files by concern (`.Steps`, `.Library`, `.Footage`, `.Edits`, `.Pictures`,
  `.Export`, `.Series`, `.Teasers`, `.Activity`); views in `Views/`. Five steps, Intro, Lesson, Clips, Preview and
  Publish, each with a checklist under the header (`MainViewModel.Guide`); name a step by its constant
  (`MainViewModel.ClipsStep`), never its number.
- `tests/PoAutoRobo.Core.Tests`: tests of the core. The app project has none.

## Rules that are easy to break

- **Every change to an episode goes through `MainViewModel.Edit`.** It saves, records undo, and brings the cards into
  line. The edits themselves are pure functions in `EpisodeEditor`; add one there, never mutate in the view model.
- **`Episode`, `Clip` and `VisualSpec` are saved as JSON and read back strictly.** Add fields as optional parameters
  or `init` properties with a default. New `VisualKind`s and `Look`s go on the end of the enum.
- **Media paths must be inside the episode folder.** `ProjectStore.KeepOwnMedia` drops any that are not, on load.
  Anything new that holds a path (see `Clip.AllMedia`) must also be handled there, in `ProjectStore.Duplicate`, and in
  `EpisodeBuilder.UnusedMedia`. The brand kit is not an episode's: it keeps bare file names of its own copies in
  `%LOCALAPPDATA%\PoAutoRobo\series`, and its music is copied into an episode's `imports` when used there.
- **Nothing that costs money happens without being asked for**, and a batch only after its cost is confirmed and
  the monthly budget checked (`OverBudget`). Generated media is cached by everything that went into the request
  (`MediaCache`), so asking again is free; a deliberate retry is a new *take* (`VisualSpec.Take`).
- **Text that did not come from the user is fenced** before it reaches a model: repository passages and anything a
  model wrote earlier, such as the finished script teasers are cut from. Use `AzureScriptWriter.Fenced`; the prompts in `ScriptSchemas` say not to obey it.
- **Model-written JavaScript runs only in `SceneRenderer`'s locked-down browser**: no network, a time limit on every
  frame. Do not loosen its flags or its page policy.
- **Working files go under `Files.ScratchRoot`**, never the episode folder (it is usually cloud-synced) and never
  loose in the temp folder. The app sweeps that folder at launch.
- **A script is planned, then written a clip at a time, side by side** (`AzureScriptWriter.WriteClipsAsync`). Every
  clip's request opens with the plan's request word for word, so keep anything that varies by clip after it.
- **Programs the app starts die with the job**: register `FfmpegRunner.Kill` on the cancellation token, as
  `FfmpegRunner` and `SceneRenderer` do, so closing the window leaves nothing running.
- **Clips are joined by copying**, so the intro, the outro and every clip are encoded exactly alike (`FfmpegArgs.Encode`)
  and each picture runs exactly its slot on the timeline. Every slot has a sound file: narration, a film's own sound
  (`FfmpegArgs.FootageSound`), the sting, or silence.

- **Anything drawn over other content uses a plain `CanvasControl` on a `FrameClock`**, never Win2D's
  `CanvasAnimatedControl`: that one hides whatever lies beneath it, so it is only for the backdrop (`NebulaBackdrop`).
- **Shader values cross to the graphics card as plain numbers.** ComputeSharp's `float2`/`float3`/`float4` do their
  arithmetic and swizzles only inside a shader; on the CPU they quietly give zeros, so work colours and positions out
  with `System.Numerics` and build the shader's vectors from the parts. A chain of shader steps that hands on values
  below 0 or above 1 needs `BufferPrecision` set to a float on every step, or Direct2D's 8-bit working buffers clip them.
- **Effects are off unless asked for.** Decorative light and motion is drawn only when `Ui.Effects` is true: *Effects*
  on in Settings and Windows showing animation. Ordinary movement (a card gliding, a panel sliding up) asks `Ui.Animated`.
- **The simplest app is the default.** A control most episodes never need binds its visibility to
  `MainViewModel.Advanced` (*Advanced controls* in Settings, off by default); a hidden choice must not change what is
  made, so its value goes back to the standard one when advanced controls are turned off.
- **Choices remembered between runs live in Settings** (`Views/SettingsDialog`, saved in `Prefs`), not in a flyout of
  their own.

## Conventions

- Comments and messages are written in plain words for the person using the app, and say *why*. Match that voice.
- Seams for tests are delegates (`JsonChat`, `SeeingChat`, `Hear`, `ImageMaker`, `RepoSource`, `Embed`, `FetchText`,
  `SecretSource`), not interfaces, unless
  there are two real implementations (`IScriptWriter`, `INarrator`).
- Sizes and spacing used on more than one page are named in `App.xaml`; use the name, not the number.
- A deliberate shortcut is marked `// ponytail:` with its ceiling and how to lift it.
- Prompt text and FFmpeg arguments are snapshot-tested with Verify. A changed `.received.` file is the new truth
  only once you have read the difference; then replace the `.verified.` file with it.
- Unit tests are capped at 100. One test may cover several cases of one behaviour; tests that run FFmpeg or Edge are
  tagged `Integration`, tests that call real services `Live` (they pass by doing nothing without a sign-in).

## Configuration

Secrets come from Azure Key Vault as the signed-in `az login` user; nothing is stored on disk. Without a vault or a
connection the app runs on stand-ins. AI video, stock photos, the local model and passages by meaning are switched
in Settings (`ServiceChoices`, saved in `%LOCALAPPDATA%\PoAutoRobo\services.json`, read as the app starts). Each of
these environment variables overrides a default, and a service's variable wins over its switch:

| Variable | What it sets |
| --- | --- |
| `POAUTOROBO_VAULT` | Key Vault address |
| `POAUTOROBO_CHAT_MODEL`, `POAUTOROBO_FAST_CHAT_MODEL` | Script model deployments |
| `POAUTOROBO_IMAGE_MODEL` | Picture model deployment |
| `POAUTOROBO_VIDEO_MODEL` | Video model deployment; unset means AI video is not offered |
| `POAUTOROBO_EMBEDDING_MODEL` | Embedding deployment; unset means passages are found by matching words alone |
| `POAUTOROBO_LOCAL_MODEL` | A model on this computer (for example `qwen3:8b` in Ollama) for the small jobs; unset means the fast model does them |
| `POAUTOROBO_LOCAL_URL` | Where that model answers in the OpenAI style; `http://localhost:11434/v1/` (Ollama) when unset |
| `POAUTOROBO_VOICE` | Narration voice |
| `POAUTOROBO_PEXELS_KEY` | Stock photo key, when not in the vault |
| `POAUTOROBO_CHAT_RATES`, `POAUTOROBO_FAST_CHAT_RATES` | Dollars per million tokens as `input,cached,output`; unset means script use is shown unpriced |
| `POAUTOROBO_VOICE_RATE` | Dollars per million characters spoken; unset means unpriced |
