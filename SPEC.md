# PoAutoRobo — Automated Robotics Explainer Studio

Status: **Approved** · Last updated: 2026-10-07

## 1. Objective

A single-user Windows 11 desktop app that turns one robotics topic into a 3–10+ minute, 16:9 explainer video about the Unitree R1 EDU humanoid, hosted by a consistent cartoon R1 character. The app finds or accepts a topic, grounds it in official repos, writes a 15–20 clip script at three depth tiers, narrates it, generates or accepts visuals per clip, and exports a captioned master video.

Users: robotics researchers, developers and content creators working alone on their own machine.

## 2. User journeys

1. **First run.** Be signed in to Azure (`az login`); the app reads its keys from the key vault by itself. Generate candidate character sheets for the host, pick one; it is locked for all episodes.
2. **Pick a topic.** Adopt a card from Topic Radar, or paste a custom topic/abstract/code/outline. One topic per episode.
3. **Generate the script.** The app fetches grounding snippets, then produces 15–20 clips (15–60s each), each with Tier A/B/C dialogue, a visual prompt and a host pose. All clips start on Tier B.
4. **Direct the episode.** Drag clips to reorder, switch tier per clip, edit dialogue line by line, audition lines, toggle host visible/off-screen. Narration re-synthesizes on any text change; visuals are marked stale only when the edit changes the core action, tool or subject.
5. **Set the visual mix.** Choose percentages for still / multi-panel / AI video / title card. The app assigns a type to each clip; any clip can be overridden.
6. **Add own footage.** Drop a video onto a clip. The dialogue is expanded or condensed and re-narrated to match the footage duration.
7. **Generate visuals.** Per clip or "Generate all", after a cost-estimate confirmation.
8. **Preview and export.** Scrub the master timeline with captions, pick a caption preset, export 1080p or 4K at 30 or 60 fps.
9. **Reopen later.** Open the project folder and continue offline with everything already generated.

## 3. Tech stack

| Area | Choice | Version / ID |
|---|---|---|
| Runtime | .NET | 10 (LTS) |
| UI | WinUI 3, pure XAML, unpackaged | Windows App SDK 2.5.1, WinUIEx 2.9.3; built for the machine's own architecture (x64 or ARM64) |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| Script LLM | Azure OpenAI via `Azure.AI.OpenAI` 2.1.0, JSON-schema structured outputs; a full episode takes about two minutes | deployment `gpt-5.4`; `gpt-5.4-mini` for drift checks |
| Narration | Azure AI Speech SDK, SSML `express-as`, `WordBoundary` events | `Microsoft.CognitiveServices.Speech`, latest stable at scaffold |
| Images | Azure OpenAI image edit endpoint with character sheet as reference | deployment `gpt-image-2` (fallback `gpt-image-1-mini`) |
| Video | Sora 2, Azure OpenAI v1 API, async job + poll, `input_reference` | deployment `sora-2`, 1280×720 |
| Assembly | FFmpeg CLI as a child process | 7.x or later (built and tested on 9.0.1), on PATH or downloaded on first run |
| Credentials | Read from Azure Key Vault `kv-poshared` at startup as the signed-in Azure user; held in memory only | `Azure.Security.KeyVault.Secrets` 4.11.2, `Azure.Identity` 1.21.0 |
| Tests | xUnit, Verify, coverlet | xunit 2.9.3, Verify.Xunit 31.12.5, coverlet.collector 6.0.4 |

Azure resource: `po-aiservices-shared` (AIServices, East US 2, resource group `PoShared`). One endpoint serves chat, images, video and Speech.

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
tasks/                    # plan.md, todo.md
```

Episode folder on disk (default `Documents\PoAutoRobo\<episode-slug>\`):

```
episode.json              # full project state
audio/<clipId>-<hash>.wav # plus .words.json (word timings)
images/<hash>.png
video/<hash>.mp4
imports/                  # copies of user footage
export/                   # finished videos and preview.mp4; render scratch files go to the system temp folder
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
| AI video | `Services/VideoGen` | Sora 2 |
| Visual mix assignment | `Pipeline/VisualMix` | none |
| Duration conformance | `Pipeline/Conformance` | ScriptWriter + Narrator |
| Captions + render | `Pipeline/Render` | FFmpeg |
| Project persistence | `Services/ProjectStore` | file system |
| Settings + credentials | `Services/Settings`, `Services/ServiceSelector` | Azure Key Vault |

## 6. Conventions

- File-scoped namespaces, nullable enabled, warnings as errors, `async` all the way with `CancellationToken` on every service call.
- Models are immutable `record`s; view models use CommunityToolkit source generators.
- One interface per **external** service only, because each has a real and a mock implementation. No interfaces for internal logic.
- No DI container beyond `Microsoft.Extensions.DependencyInjection` defaults; no mediator, no repository layer.
- UI text is plain language. No model names, prompts or FFmpeg flags appear in the UI.

```csharp
namespace PoAutoRobo.Core.Models;

public sealed record Clip(
    Guid Id,
    string Title,
    Tier ActiveTier,
    IReadOnlyDictionary<Tier, TierScript> Scripts,
    VisualSpec Visual,
    bool HostVisible);

public interface INarrator
{
    Task<Narration> SynthesizeAsync(string text, CancellationToken ct);
}
```

## 7. Behaviour rules

**Decomposition.** 15–20 clips. Estimated clip duration is word count ÷ 165 wpm, replaced by the real audio duration once synthesized. Each tier's text must land in 15–60s.

**Grounding.** Fixed repo list: `unitreerobotics/unitree_sdk2`, `unitree_sdk2_python`, `unitree_rl_lab`, `unitree_rl_mjlab`, `unitree_mujoco`, `isaac-sim/IsaacLab`, `google-deepmind/mujoco`. Fetch READMEs, docs and matching source via GitHub code search, rank by keyword overlap with the topic, pass the top snippets into the prompt with their URLs. The script prompt states that `unitree_rl_lab` does not list R1 (Go2, H1, G1-29dof only) and that R1 training is supported in `unitree_rl_mjlab`.

**Drift.** On a dialogue edit, a structured LLM call returns `coreChanged: bool` comparing old and new text for action, tool and physical subject. `true` marks the clip's visual stale; it does not regenerate automatically.

**Visual mix.** Four percentages summing to 100. Clip counts use largest-remainder rounding; assignment is shuffled with a seed stored in `episode.json` so it is reproducible. Clips with user footage are excluded from the mix. Manual overrides persist across re-rolls.

**Visual types.**
- Still: one 16:9 image, pan-and-zoom at export.
- Multi-panel: 2–4 stills cut evenly across the narration.
- AI video: 1280×720 Sora shots stitched to cover the narration, upscaled at export. If the last shot ends early, its final frame is held.
- Title card: rendered locally from the clip title, no API call. Also the fallback whenever an image or video is missing.

**Host.** Every image and video request with `HostVisible = true` sends the locked character sheet as the reference. Off-screen clips send no reference and ask for a diagram or simulation view.

**Conformance.** Target word count = footage seconds × 165 ÷ 60. Rewrite, synthesize, measure. Up to 3 rewrite attempts, then close the remaining gap with SSML speaking rate within ±10%. Footage under 5s or over 120s is rejected with a message.

**Cost guard.** Script and narration run automatically. Images and video run only on an explicit Generate action; batch runs show an itemised estimate first. Every output is cached by a hash of its full request, so an unchanged clip is never billed twice.

**Captions.** Four presets (Karaoke Highlight, Two-Line Block, Clean Subtitle, Comic Banner) with font size, accent colour and stroke width. Rendered as an ASS subtitle file from word timings and burned in by FFmpeg.

**Export.** H.264 MP4 with AAC audio; 1920×1080 or 3840×2160; 30 or 60 fps. Narration is loudness-normalised to −16 LUFS, with 150ms crossfades at clip joins and 0.5s visual transitions.

**Topic Radar.** Refreshes on launch and on demand. Interest metric is stars, upvotes or comment count depending on the source.

## 8. Mock fallbacks

Each external service has a mock chosen automatically when its credentials or binary are absent. A banner names what is mocked.

| Service | Mock behaviour |
|---|---|
| ScriptWriter | Canned 16-clip R1 balancing episode with all three tiers |
| Narrator | Windows built-in voice, word timings spread evenly |
| ImageGen | Locally rendered title card |
| VideoGen | Falls back to a still |
| TrendFeed / Grounding | Bundled sample cards and snippets |
| FFmpeg missing | Export disabled with a "Get FFmpeg" action; everything else works |

## 9. Testing strategy

- xUnit against `PoAutoRobo.Core`. Target 80% line coverage on `Pipeline/` and `Models/`; no coverage target on the App project.
- Unit: mix assignment, duration estimation, conformance loop, drift handling, cache keys, ASS caption generation, FFmpeg argument building, project save/load round trip.
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
| LLM returns fewer than 15 or more than 20 clips | One retry, then trim or ask to regenerate |
| Tier text outside 15–60s | Clip flagged; export still allowed |
| Content filter blocks an image or video | Clip keeps its title card and shows the reason |
| Sora job fails or times out (10 min) | Clip falls back to a still; error shown on the card |
| Rate limit (429) | Retry with backoff up to 3 times, then surface it |
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
2. A generated episode has 15–20 clips, each with three tiers, and every tier's estimated duration is 15–60s.
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
15. No secret value appears in the repository, `episode.json` or logs.
16. With live credentials, every image of a host-visible clip is generated with the character sheet as reference.
17. `dotnet build`, `dotnet test` and `dotnet format --verify-no-changes` all pass.

## 14. Open questions

1. `gpt-image-2` and `sora-2` are not yet deployed on `po-aiservices-shared`. Quota allows 2 and 9 requests per minute. Until they exist, images use `gpt-image-1-mini` and AI video falls back to stills.
2. Allowed Sora 2 `seconds` values on this deployment are unconfirmed (docs samples show 4, 8 and 12). Confirm with one live call before building shot stitching.
3. Whether the Foundry User role allows keyless data-plane calls is unconfirmed. If not, development uses the key from `kv-poshared` via `dotnet user-secrets`.
4. Host voice: default is an en-US neural voice with the `excited` style, with a picker in Settings. Final voice to be chosen by ear.
5. `gpt-image-2.5-sunburst` is available in the region and is described as stronger at reference-faithful edits. Worth a side-by-side once the character sheet exists.
