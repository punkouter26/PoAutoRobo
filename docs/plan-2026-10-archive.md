# PoAutoRobo — Phase 2 Plan

Approved 2026-10-07. Task list: [todo.md](todo.md). Spec: [SPEC.md](../SPEC.md).

## 1. Architecture decisions

| # | Decision | Why |
|---|---|---|
| A1 | Three projects: `PoAutoRobo.Core` (logic), `PoAutoRobo.App` (WinUI 3), `PoAutoRobo.Core.Tests` | All logic testable without a window |
| A2 | Core targets `net10.0-windows10.0.19041.0` | Lets Core use the Windows voice (mock narrator) and Credential Locker directly; the app is Windows-only anyway. **SPEC §5 says `net10.0`; I'll update it** |
| A3 | Interfaces only for the six external services (`IScriptWriter`, `INarrator`, `IImageGen`, `IVideoGen`, `ITrendFeed`, `IGrounding`), each with a real and a mock implementation | Mock fallback is a spec requirement; nothing else gets an interface |
| A4 | Real-vs-mock chosen once at startup per service from Settings; banner lists mocked services | One decision point |
| A5 | State is one immutable `Episode` record saved as `episode.json` (System.Text.Json), `.bak` written before each save | No database |
| A6 | Media cache = file named by SHA-256 of the full request (prompt + reference hash + size + model) | Criterion 11 with no cache index |
| A7 | FFmpeg driven by raw arguments built in a pure function (`FfmpegArgs`), run by a thin `FfmpegRunner` | Arguments are snapshot-testable; runner is the only process code |
| A8 | Title cards drawn by FFmpeg `drawtext` in Core; **Win2D is used only in the App for the live caption-style overlay** on the preview player | Keeps Core free of GPU dependencies; gives instant caption preview without re-rendering |
| A9 | Captions = generated ASS file from word timings; four presets are four ASS style templates | FFmpeg burns them in natively |
| A10 | Sora via plain `HttpClient` REST (create job, poll, download) | No stable .NET SDK surface to verify; three calls |
| A11 | Central package versions in `Directory.Packages.props`, pinned in T1 and written back to `SPEC.md` | Spec promise |

### Selected libraries

Fixed by spec: Windows App SDK 2.5.1, CommunityToolkit.Mvvm, Azure.AI.OpenAI, Microsoft.CognitiveServices.Speech, Azure.Identity, FFmpeg CLI, Microsoft.Extensions.Hosting, xUnit.
Chosen now: CommunityToolkit.WinUI controls, WinUIEx, Win2D, Microsoft.Extensions.Http.Resilience, System.ServiceModel.Syndication, Octokit, Verify, coverlet, NSubstitute.
Declined: FFMpegCore, SkiaSharp, NAudio.

## 2. Ten implementation examples (how the chosen tools get used)

Signatures are checked against current docs when each task starts; anything that differs is fixed there, not guessed.

1. **CommunityToolkit.Mvvm — cancellable long work with progress**
   ```csharp
   public partial class MainViewModel : ObservableObject
   {
       [ObservableProperty] private double _renderProgress;
       [RelayCommand(IncludeCancelCommand = true)]
       private async Task RenderAsync(CancellationToken ct) =>
           await _renderer.ExportAsync(Episode, new Progress<double>(p => RenderProgress = p), ct);
   }
   ```
2. **WinUI `ListView` — drag-reorder clip deck, no custom drag code**
   ```xml
   <ListView ItemsSource="{x:Bind ViewModel.Clips}" CanReorderItems="True" AllowDrop="True"
             CanDragItems="True" SelectedItem="{x:Bind ViewModel.SelectedClip, Mode=TwoWay}"/>
   ```
   `ObservableCollection.CollectionChanged` writes the new order back to `Episode`.
3. **CommunityToolkit.WinUI `Segmented` — tier pill**
   ```xml
   <controls:Segmented SelectedIndex="{x:Bind Clip.TierIndex, Mode=TwoWay}">
     <controls:SegmentedItem Content="A"/><controls:SegmentedItem Content="B"/><controls:SegmentedItem Content="C"/>
   </controls:Segmented>
   ```
4. **Azure.AI.OpenAI — schema-locked script output**
   ```csharp
   var options = new ChatCompletionOptions {
       ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("episode", BinaryData.FromString(EpisodeSchema), jsonSchemaIsStrict: true) };
   var result = await chat.CompleteChatAsync(messages, options, ct);
   var clips = JsonSerializer.Deserialize<ScriptDraft>(result.Value.Content[0].Text);
   ```
   Same pattern with a one-field schema (`coreChanged`) for drift detection.
5. **Azure.Identity — Entra first, key second**
   ```csharp
   AzureOpenAIClient client = settings.ApiKey is { Length: > 0 } key && !settings.PreferEntra
       ? new(settings.Endpoint, new ApiKeyCredential(key))
       : new(settings.Endpoint, new DefaultAzureCredential());
   ```
6. **Speech SDK — narration plus word timings in one pass**
   ```csharp
   var words = new List<WordTiming>();
   synth.WordBoundary += (_, e) => words.Add(new(e.Text, TimeSpan.FromTicks((long)e.AudioOffset), e.Duration));
   var r = await synth.SpeakSsmlAsync($"<speak ...><voice name='{voice}'><mstts:express-as style='excited'><prosody rate='{rate}'>{text}</prosody></mstts:express-as></voice></speak>");
   ```
   `rate` is the ±10% knob conformance uses for the last second.
7. **Http.Resilience — backoff on 429 for feeds, GitHub and Sora**
   ```csharp
   services.AddHttpClient("sora").AddStandardResilienceHandler(o => o.Retry.MaxRetryAttempts = 3);
   ```
8. **Syndication + Octokit — Topic Radar and grounding**
   ```csharp
   var feed = SyndicationFeed.Load(XmlReader.Create(stream));               // arXiv cs.RO, outlet RSS
   var hits = await github.Search.SearchCode(new SearchCodeRequest(topicKeywords) { Repos = OfficialRepos });
   ```
9. **FFmpeg arguments as a pure function, snapshot-tested with Verify**
   ```csharp
   [Fact] public Task Still_clip_gets_zoompan_xfade_loudnorm_and_captions() =>
       Verify(FfmpegArgs.Build(SampleEpisode.TwoStills, ExportPreset.Hd30));
   // filter graph: zoompan per still → xfade=duration=0.5 → ass=captions.ass ; audio: acrossfade=d=0.15 → loudnorm=I=-16
   ```
10. **NSubstitute + Win2D — proving cost rules and previewing captions**
    ```csharp
    await editor.RegenerateVisualAsync(clip, ct); await editor.RegenerateVisualAsync(clip, ct);
    await imageGen.Received(1).GenerateAsync(Arg.Any<ImageRequest>(), Arg.Any<CancellationToken>());   // criterion 11
    ```
    ```xml
    <Grid><MediaPlayerElement x:Name="Player"/><canvas:CanvasControl Draw="OnDrawCaption"/></Grid>
    ```
    `OnDrawCaption` draws the current words in the selected preset, so changing preset, size or colour updates instantly.

WinUIEx: `WindowEx` base class with `PersistenceId="Main"` for remembered size and position.

## 3. Dependency graph

```
T1 scaffold
 └─ T2 models+store
     ├─ T3 script(mock) ─┬─ T4 visual mix
     │                   └─ T5 narrator(mock) ── T6 captions ── T7 render ══ Checkpoint A
     └─ T8 app shell ── T9 clip deck ── T10 inspector/editing ── T11 timeline/export ══ Checkpoint B
T12 settings/auth ─┬─ T13 live script ─┐
                   ├─ T14 live voice ──┴─ T15 conformance + user video ══ Checkpoint C
                   ├─ T16 grounding ── T17 topic radar
                   └─ T18 images+cache ── T19 host setup + generate UI ─┬─ T20 AI video
                                                                       └─ T21 multi-panel + mix UI ══ Checkpoint D
T22 hardening + criteria proof
```

## 4. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `sora-2` / `gpt-image-2` not deployed (creation was blocked) | High | AI video and best images unavailable | Stills fallback and `gpt-image-1-mini` already in spec; T20 starts with a deployment check and stops to ask |
| Sora allowed durations and reference-image behaviour differ from docs | Medium | Shot stitching redesign | T20 opens with one live spike call before any design is locked |
| Host drifts between images despite reference | Medium | Core pillar weakened | Compare `gpt-image-2` and `gpt-image-2.5-sunburst` in T19; reference sent on every call |
| Keyless auth rejected by role | Medium | Dev blocked on live calls | Key fallback in T12; you load the key with the script from earlier |
| WinUI 3 unpackaged quirks (Credential Locker, Win2D, .NET 10 with SDK 2.5.1) | Medium | T8/T12 slip | T8 proves the shell builds and runs before UI work; file-based encrypted fallback (DPAPI) if Locker fails unpackaged |
| Complex FFmpeg graph slow or wrong at 4K60 with 20 clips | Medium | Export fails late | Render each clip to an intermediate file, then concatenate; integration test in T7 from the start |
| Conformance cannot converge to ±1.0s | Low | Criterion 7 | Rate knob ±10% after 3 rewrites; closest attempt kept and gap shown |
| Free feed formats change or rate-limit | Medium | Empty radar | Per-source isolation; bundled samples as fallback |
| Azure spend during development | Medium | Cost | Live tests opt-in only; cache on by default; I ask before any batch live run |

## 5. Checkpoints

- **A (after T7):** headless topic → MP4 on mocks. Proves criteria 1, 2, 6, 8, 9, 10. I show `ffprobe` and loudness output.
- **B (after T11):** the app is usable end-to-end on mocks. Criteria 3, 4, 5, 13, 14. Phase 3 design concepts are reviewed **before T8 starts**, since T8–T11 build the UI.
- **C (after T15):** live script and voice; user footage conformance. Criterion 7.
- **D (after T21):** live images, video, mix. Criteria 11, 12, 16.
- **Final (T22):** all 17 criteria with evidence; then Phase 5 reviews.

## 7. Verification

- Per task: `dotnet test PoAutoRobo.sln`, `dotnet build PoAutoRobo.sln`, `dotnet format PoAutoRobo.sln --verify-no-changes`.
- Checkpoint A: run the end-to-end test, then `ffprobe -v error -show_streams export.mp4` and `ffmpeg -i export.mp4 -af loudnorm=print_format=summary -f null -`.
- Checkpoints B–D: `dotnet run --project src/PoAutoRobo.App` and walk the SPEC §2 journeys; screenshots as evidence.
- Live checks: `$env:POAUTOROBO_LIVE=1; dotnet test --filter Category=Live`, run only with your go-ahead.
- Coverage: `dotnet test --collect:"XPlat Code Coverage"`, target 80% on `Pipeline/` and `Models/`.