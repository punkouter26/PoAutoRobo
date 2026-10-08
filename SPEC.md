# PoAutoRobo — Automated Robotics Explainer Studio

Status: **Approved** · Last updated: 2026-10-08

## 1. Objective

A single-user Windows 11 desktop app that turns one topic into a 3–10+ minute, 16:9 explainer video, hosted by a consistent cartoon R1 character. It was built for videos about the Unitree R1 EDU humanoid, and that remains the default subject with all of its grounding; since 2026-10-08 an episode can instead be about any topic. The app finds or accepts a topic, grounds it in official repos, writes a 15–20 clip script at three depth tiers, narrates it, generates or accepts visuals per clip, and exports a captioned master video.

Users: robotics researchers, developers and content creators working alone on their own machine.

## 2. User journeys

1. **First run.** Be signed in to Azure (`az login`); the app reads its keys from the key vault by itself. Generate candidate character sheets for the host, pick one; it is locked for all episodes.
2. **Pick a topic.** Choose what the episode is about (Unitree R1, or any topic), then adopt a card from Topic Radar or paste a custom topic/abstract/code/outline. One topic per episode. The choice is remembered between runs.
3. **Generate the script.** The app fetches grounding snippets, then produces 15–20 clips (15–60s each) at Tier B, each with dialogue, a visual prompt and a host pose. Tiers A and C are written per clip the first time they are picked, which takes a few seconds.
4. **Direct the episode.** Drag clips to reorder, switch tier per clip, edit dialogue line by line, audition lines, toggle host visible/off-screen. Every change can be undone (Ctrl+Z) and redone (Ctrl+Y), up to 50 steps. Narration re-synthesizes on any text change; visuals are marked stale only when the edit changes the core action, tool or subject.
5. **Set the visual mix.** Choose percentages for still / multi-panel / AI video / title card. The app assigns a type to each clip; any clip can be overridden.
6. **Add own footage.** Drop a video onto a clip. The dialogue is expanded or condensed and re-narrated to match the footage duration.
7. **Generate visuals.** Per clip or "Generate all", after a cost-estimate confirmation.
8. **Preview and export.** Scrub the master timeline with captions, pick a caption preset, export 1080p or 4K at 30 or 60 fps.
9. **Reopen later.** Pick the episode from the library on the Topic page (thumbnail, clip count, running time) and continue offline with everything already generated. From the same list an episode can be copied, shown in File Explorer or sent to the Recycle Bin.

### Layout

The app has four steps, one page at a time, chosen from the selector in the header (Ctrl+1 to Ctrl+4), with a one-line hint of what to do on each: **1 Topic** (own topic and length on the left; saved-episode library and Topic Radar on the right), **2 Script** (compact clip deck; the inspector beside it holds depth, dialogue, audition and the sources the script was written from), **3 Pictures** (host, visual mix, generate pictures, own video), **4 Export** (preview with a waveform and clip markers, sized to the window; captions and master video beside it). The header also carries the clip count, running time and picture spend, Undo/Redo, a sound switch and a menu (episode folder, remove unused media). There is no footer: Back/Next were removed on 2026-10-08 because they duplicated the selector. A progress panel at the top shows any long job's name, exact step (for example "Drawing clip 7 of 16"), percentage, time spent and time left, with one Cancel button; only one long job runs at a time, and writing a script, recording all voices and fitting footage are long jobs too. Results and errors appear in one message area under the header on every step. A long job also shows on the taskbar button, chimes when it ends, and raises a Windows notification if the window is not in front. The app follows the Windows light or dark setting. Main controls carry hover tooltips.

## 3. Tech stack

| Area | Choice | Version / ID |
|---|---|---|
| Runtime | .NET | 10 (LTS) |
| UI | WinUI 3, pure XAML, unpackaged | Windows App SDK 2.5.1, WinUIEx 2.9.3; built for the machine's own architecture (x64 or ARM64) |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| Script LLM | Azure OpenAI via `Azure.AI.OpenAI` 2.1.0, JSON-schema structured outputs; a full episode takes about two minutes | deployment `gpt-5.4`; `gpt-5.4-mini` for drift checks |
| Narration | Azure AI Speech SDK through the same AI services resource and sign-in as the script; SSML `express-as` excited style, `WordBoundary` events | `Microsoft.CognitiveServices.Speech` 1.52.0, voice `en-US-DavisNeural` |
| Images | Azure OpenAI image REST API (`2025-04-01-preview`): edit endpoint with the character sheet as reference, generation endpoint otherwise | deployment `gpt-image-1-mini` today; switch to `gpt-image-2` once deployed |
| Video | Sora 2, Azure OpenAI v1 API, async job + poll, `input_reference` | deployment `sora-2`, 1280×720 |
| Assembly | FFmpeg CLI as a child process | 7.x or later (built and tested on 9.0.1), on PATH or downloaded on first run |
| Credentials | No API key is held. The resource address and GitHub token are read from Azure Key Vault `kv-poshared` at startup, and every script, voice and picture request is signed as the `az login` user (needs a Cognitive Services data role on the resource, for example Foundry User) | `Azure.Security.KeyVault.Secrets` 4.11.2, `Azure.Identity` 1.21.0 (`AzureCliCredential`) |
| Tests | xUnit, Verify, coverlet | xunit 2.9.3, Verify.Xunit 31.12.5, coverlet.collector 6.0.4 |

Azure resource: `po-aiservices-shared` (AIServices, East US 2, resource group `PoShared`). One endpoint serves chat, images, video and Speech.

Build checks: analyzers at `latest-recommended` and code style both run in the build with warnings as errors; exceptions are listed with reasons in `.editorconfig`. `.github/workflows/ci.yml` builds, tests, checks formatting and lists vulnerable packages on every push.

Radzen is **not used**: it cannot run in WinUI 3 XAML. This overrides the "Radzen First" standing rule by explicit decision.

Package versions marked "latest stable at scaffold" are pinned to exact numbers in the first build task and written back here.

## 4. Commands

```powershell
dotnet build PoAutoRobo.sln
dotnet test PoAutoRobo.sln
dotnet format PoAutoRobo.sln --verify-no-changes
dotnet run --project src/PoAutoRobo.App
```

## 5. Project structure

```
PoAutoRobo.sln
src/
  PoAutoRobo.Core/        # net10.0-windows class library: all logic, no UI references
    Models/               # Episode, Clip, TierScript, VisualSpec, CaptionStyle
    Services/             # one file per external service + its mock
    Pipeline/             # decomposition, conformance, mix assignment, render graph
  PoAutoRobo.App/         # WinUI 3: Views, ViewModels, App.xaml
tests/
  PoAutoRobo.Core.Tests/
tasks/                    # todo.md (open work only)
docs/                     # archived plan, screen guide
```

Episode folder on disk (default `Documents\PoAutoRobo\<episode-slug>\`):

```
episode.json              # full project state
grounding.json            # the repository passages the script was written from
spend.jsonl               # one line per picture paid for
audio/<clipId>-<hash>.wav # plus .words.json (word timings)
images/<hash>.png
video/<hash>.mp4
imports/                  # copies of user footage
export/                   # finished videos, each with .chapters.txt, .srt and .thumbnail.png beside it, and preview.mp4; render scratch files go to the system temp folder
```

The character sheet lives in `%LOCALAPPDATA%\PoAutoRobo\host\` and is shared across episodes.

### Capability map

One app, so this table replaces a separate `CAPABILITY-MAP.md`.

| Capability | Lives in | External dependency |
|---|---|---|
| Topic Radar | `Services/TrendFeed` | RSS, arXiv, GitHub, Reddit, Hacker News |
| Grounding | `Services/Grounding` | GitHub REST API |
| Script generation, tiers, drift, expand/condense | `Services/ScriptWriter` | Azure OpenAI chat |
| Narration + word timings | `Services/Narrator` | Azure AI Speech |
| Images + character sheet | `Services/ImageGen` | Azure OpenAI images |
| AI video (planned, task T20; not built) | `Services/VideoGen` | Sora 2 |
| Visual mix assignment | `Pipeline/VisualMix` | none |
| Duration conformance | `Pipeline/Conformance` | ScriptWriter + Narrator |
| Captions + render | `Pipeline/AssCaptions`, `Pipeline/FfmpegArgs`, `Pipeline/EpisodeBuilder`, `Services/FfmpegRunner` | FFmpeg |
| Publishing files (chapters, subtitles) | `Pipeline/PublishPack` | none |
| Spend log | `Services/SpendLog` | file system |
| Project persistence | `Services/ProjectStore` | file system |
| Settings + credentials | `Services/Settings` | Azure Key Vault, Azure sign-in |

## 6. Conventions

- File-scoped namespaces, nullable enabled, warnings as errors, `async` all the way with `CancellationToken` on every service call.
- Models are immutable `record`s; view models use CommunityToolkit source generators.
- One interface per **external** service only, because each has a real and a mock implementation. No interfaces for internal logic.
- No DI container: `App.xaml.cs` builds the handful of objects by hand. No mediator, no repository layer.
- UI text is plain language. No model names, prompts or FFmpeg flags appear in the UI.

```csharp
namespace PoAutoRobo.Core.Models;

public sealed record Clip(
    Guid Id,
    string Title,
    Tier ActiveTier,
    IReadOnlyDictionary<Tier, TierScript> Scripts,   // always holds ActiveTier; A and C may be absent until picked
    VisualSpec Visual,
    bool HostVisible);

public interface INarrator
{
    Task<Narration> SynthesizeAsync(string text, CancellationToken ct);
}
```

## 7. Behaviour rules

**Decomposition.** A full episode is 15–20 clips. At creation the user can instead choose Short (5 clips) or Quick test (1 clip, about 30 seconds) to try the whole workflow in a couple of minutes. Estimated clip duration is word count ÷ 165 wpm, replaced on the card and in the running time by the real audio duration once that wording has been recorded ("Record all voices" records every clip at once). Each tier's text must land in 15–60s. The script call writes Tier B only, streamed so progress shows as clips arrive; a topic longer than 8,000 characters is cut at that length.

**Subject.** Each episode is either a Unitree R1 episode or an any-topic episode; the choice is made on the Topic page and saved in `episode.json` (a file without it is an R1 episode). An R1 episode behaves exactly as described in the rest of this section. An any-topic episode differs in three ways only: no repositories are read, so it has no sources and no "Not in the sources" flags; the script prompt drops the R1 accuracy rules and tells the model to use specific numbers, names and dates only when they appear in the topic text; and Topic Radar shows the Hacker News front page, unfiltered. The host, the three depths, narration, pictures, captions and export are the same for both.

**Grounding.** R1 episodes only. Fixed repo list: `unitreerobotics/unitree_sdk2`, `unitree_sdk2_python`, `unitree_rl_lab`, `unitree_rl_mjlab`, `unitree_mujoco`, `isaac-sim/IsaacLab`, `google-deepmind/mujoco`. Fetch READMEs, docs and matching source via GitHub code search, rank by keyword overlap with the topic, pass the top snippets into the prompt with their URLs. The script prompt states that `unitree_rl_lab` does not list R1 (Go2, H1, G1-29dof only) and that R1 training is supported in `unitree_rl_mjlab`. The topic and every snippet are sent inside tags the prompt tells the model to treat as material, never as instructions. After writing, figures and code-style names in a clip's dialogue that appear in none of the snippets (or the topic) are flagged on its card as "Not in the sources"; this is a plain text check and only runs when the episode has snippets.

**Drift.** On a dialogue edit, a structured LLM call returns `coreChanged: bool` comparing old and new text for action, tool and physical subject. `true` marks the clip's visual stale; it does not regenerate automatically.

**Visual mix.** Four percentages summing to 100. Clip counts use largest-remainder rounding; assignment is shuffled with a seed stored in `episode.json` so it is reproducible. Clips with user footage are excluded from the mix. Manual overrides persist across re-rolls.

**Visual types.**
- Still: one 16:9 image, pan-and-zoom at export.
- Multi-panel: 2–4 stills cut evenly across the narration.
- AI video: 1280×720 Sora shots stitched to cover the narration, upscaled at export. If the last shot ends early, its final frame is held.
- Title card: rendered locally from the clip title, no API call. Also the fallback whenever an image or video is missing.

**Host.** Every image and video request with `HostVisible = true` sends the locked character sheet as the reference. Off-screen clips send no reference and ask for a diagram or simulation view.

**Conformance.** Target word count = footage seconds × 165 ÷ 60. Rewrite, synthesize, measure. Up to 3 rewrite attempts, then close the remaining gap with SSML speaking rate within ±10%. Footage under 5s or over 120s is rejected with a message.

**Cost guard.** Script and narration run automatically. Images and video run only on an explicit Generate action; batch runs show an itemised estimate first. Every output is cached by a hash of its full request, so an unchanged clip is never billed twice. A single-clip Generate is disabled while a batch runs, so the same picture cannot be requested twice at once. Each picture actually made is added to `spend.jsonl` and the total shows in the header (pictures only: script and voice prices are not known to the app).

**Housekeeping.** "Remove unused media" lists narration, pictures and imported footage no clip uses any more, shows the count and size, and deletes them only after confirmation; undo history is cleared with it.

**Captions.** Four presets (Karaoke Highlight, Two-Line Block, Clean Subtitle, Comic Banner) with font size, accent colour and stroke width. Rendered as an ASS subtitle file from word timings and burned in by FFmpeg.

**Export.** H.264 MP4 with AAC audio; 1920×1080 or 3840×2160; 30 or 60 fps. Narration is loudness-normalised to −16 LUFS with 150ms crossfades at clip joins. Each clip fades up from black and back down over 0.25s, which reads as a brief dip between clips. A chapter list (clip titles at their start times), an `.srt` subtitle file and a thumbnail (the first clip picture) are written beside the video. Clips are encoded once each, several in parallel, with their captions burned in, and then joined by copying; a cross-dissolve was dropped on 2026-10-08 because it forced a second encode of the whole episode (about 25 minutes for a full episode, against about 5.5 now).

**Topic Radar.** Refreshes on launch, on demand and when the subject is switched. For R1 episodes the sources are: arXiv, Hacker News, IEEE Spectrum and The Robot Report. A card is shown only if it names both "Unitree" and "R1" (as a whole word); general humanoid news, other Unitree robots and other makers' R1 products are dropped. Hacker News cards show points and comments; the others have no interest figure. Reddit (refuses anonymous readers) and GitHub activity (commit titles are not topics) are not used.

## 8. Mock fallbacks

Each external service has a mock chosen automatically when its credentials or binary are absent. A banner names what is mocked.

| Service | Mock behaviour |
|---|---|
| ScriptWriter | Canned 16-clip R1 balancing episode with all three tiers |
| Narrator | Windows built-in voice, word timings spread evenly |
| ImageGen | None needed: a clip with no picture renders as a title card |
| VideoGen | Falls back to a still |
| TrendFeed | Bundled sample cards |
| Grounding | Last saved copy of each repository; with none, the script is written without snippets |
| FFmpeg missing | Export disabled with a "Get FFmpeg" action; everything else works |

## 9. Testing strategy

- xUnit against `PoAutoRobo.Core`. Target 80% line coverage on `Pipeline/` and `Models/`; no coverage target on the App project.
- At most 100 unit tests and 50 integration tests. Unit: mix assignment, duration estimation, conformance loop, drift handling, cache keys, ASS caption generation, FFmpeg argument building, project save/load round trip.
- Integration: full pipeline on mocks from topic to an exported MP4, verified with `ffprobe`. Skipped when FFmpeg is absent.
- Live-service tests are opt-in with `POAUTOROBO_LIVE=1` and never run by default.
- No UI automation. The App layer is verified by running it.
- Strict TDD: failing test first, one commit per task.

## 10. Boundaries

**Always**
- Keep secrets in the key vault; the app reads them at startup and never writes them to disk.
- Run long work off the UI thread with progress and cancel.
- Cache generated media; show a cost estimate before batch image or video runs.
- Keep `SPEC.md` and `tasks/todo.md` in sync.

**Ask first**
- Creating, changing or deleting Azure resources or deployments.
- Adding a NuGet package not listed in the approved plan.
- Deleting an episode folder or generated media.
- Any change to the on-disk `episode.json` format after the first release of it.

**Never**
- Commit secrets, or write them to `episode.json` or logs.
- Invent API parameters, joint names or control rates; ground them or leave them out.
- Skip, weaken or delete a failing test.
- Call a paid image or video endpoint without a user action.

## 11. Out of scope

- X/Twitter ingestion, or any paid feed.
- Multi-user, cloud sync, accounts, telemetry.
- Vertical or square output; multiple languages; background music.
- A general video editor: no trimming of user footage, no manual keyframes.
- Lip-sync or true character animation of the host.
- Packaged (MSIX) distribution and auto-update.
- Controlling a real robot or running simulations.

## 12. Edge cases and error states

| Situation | Behaviour |
|---|---|
| No credentials | Service runs on its mock; banner says which |
| Key vault unreachable or not signed in | Every service runs on its mock; the banner gives the reason |
| LLM returns fewer than 15 or more than 20 clips | Too many: trimmed, no second call. Too few: one retry, then ask to regenerate |
| LLM declines the request or its reply is cut off | A plain message saying which; nothing is saved |
| Tier text outside 15–60s | Clip flagged; export still allowed |
| Content filter blocks an image or video | Clip keeps its title card and shows the reason |
| Sora job fails or times out (10 min) | Clip falls back to a still; error shown on the card |
| Rate limit (429) | Script calls retry once, then the error is shown. Changed from three retries: each retry of a long script call is paid for |
| GitHub rate limit without a token | Use cached snippets; prompt to add a token |
| Feed source unreachable | Skip that source; show the rest |
| Conformance cannot hit the target | Keep the closest attempt and show the gap |
| User footage unreadable or out of 5–120s | Reject with a plain message; clip unchanged |
| Episode total under 3 min | Warning in the command bar; export allowed |
| Export cancelled or FFmpeg exits non-zero | Partial file deleted; last 20 log lines shown |
| `episode.json` missing or corrupt | Offer to restore from `episode.json.bak` |
| Disk full | Stop generation and report the path |

## 13. Success criteria

1. With no credentials and FFmpeg installed, a new episode goes from custom topic to an exported MP4 without errors.
2. A generated episode has 15–20 clips at Tier B; picking A or C on a clip writes that tier for that clip only; every tier's estimated duration is 15–60s.
3. All clips start on Tier B; switching tier updates dialogue, duration and visual prompt for that clip only.
4. Drag-reorder changes the running order, survives save and reload, and changes the export order.
5. Editing dialogue re-synthesizes that clip's audio only. A phrasing edit leaves the visual untouched; a subject change marks it stale.
6. For percentages summing to 100, assigned type counts match largest-remainder rounding exactly, and the same seed gives the same assignment.
7. After dropping footage of 5–120s on a clip, narration duration is within ±1.0s of the footage duration.
8. The exported file is H.264/AAC at exactly the chosen resolution and frame rate, per `ffprobe`.
9. Exported narration measures −16 LUFS ±1 integrated.
10. Every caption appears within 100ms of when its first word is spoken, for all four presets; the karaoke preset highlights each word within 100ms of its spoken start.
11. Regenerating an unchanged clip makes zero image or video API calls.
12. No batch image or video run starts without the cost-estimate confirmation.
13. The UI stays responsive during generation and export, and every long operation shows progress and can be cancelled.
14. Closing and reopening an episode restores all state and media with no network access.
15. No secret value appears in the repository, `episode.json` or logs, and the app holds no API key for the AI resource.
16. With live credentials, every image of a host-visible clip is generated with the character sheet as reference.
17. `dotnet build`, `dotnet test` and `dotnet format --verify-no-changes` all pass.

## 14. Open questions

1. `gpt-image-2` and `sora-2` are not yet deployed on `po-aiservices-shared`. Quota allows 2 and 9 requests per minute. Until they exist, images use `gpt-image-1-mini` and AI video falls back to stills.
2. Allowed Sora 2 `seconds` values on this deployment are unconfirmed (docs samples show 4, 8 and 12). Confirm with one live call before building shot stitching.
3. Resolved 2026-10-07: the app reads the resource endpoint and key from `kv-poshared` at startup. The vault's separate `AzureSpeech-*` secrets are stale (401) and are not used; voice goes through the shared resource.
4. Host voice: default is an en-US neural voice with the `excited` style, with a picker in Settings. Final voice to be chosen by ear.
5. `gpt-image-2.5-sunburst` is available in the region and is described as stronger at reference-faithful edits. Worth a side-by-side once the character sheet exists.
