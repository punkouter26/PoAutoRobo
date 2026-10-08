# PoAutoRobo

A single-user Windows desktop app (WinUI 3, .NET 10) that turns one robotics topic into a narrated, captioned explainer video about the Unitree R1. It is not a web app: there is no Blazor, Radzen, SignalR or browser code here.

@AGENTS.md

## Commands

```powershell
dotnet build PoAutoRobo.sln
dotnet test tests/PoAutoRobo.Core.Tests --filter "FullyQualifiedName~<TestClass>"   # run only the tests for what changed
dotnet format PoAutoRobo.sln --verify-no-changes
dotnet run --project src/PoAutoRobo.App
```

## Where things are

- [SPEC.md](SPEC.md) is the source of truth for behaviour. Keep it and [tasks/todo.md](tasks/todo.md) in step with the code.
- `src/PoAutoRobo.Core` holds all logic and has no UI references. `Models/` are immutable records, `Services/` is one file per outside service plus its mock, `Pipeline/` is everything between a script and a finished video.
- `src/PoAutoRobo.App` is the WinUI 3 shell. `ViewModels/MainViewModel.cs` owns the episode; every change to it goes through `Edit`, which saves it and records it for undo.
- `tests/PoAutoRobo.Core.Tests` tests Core only. The App layer is checked by running it.

## Rules that are easy to break

- **Money.** Script, voice and pictures are paid Azure calls. For any live check use the one-clip "Quick test" episode length. Never run a full episode or a batch of pictures to test something. Pictures are only ever made on an explicit user action.
- **Sign-in, not keys.** The app signs requests as the `az login` user. Do not add an API key back.
- **`episode.json`.** Ask before changing its format. A clip always has the depth it is on; depths A and C may be absent until first picked.
- **Test caps.** At most 100 unit tests and 50 integration tests. Live-service tests are opt-in with `POAUTOROBO_LIVE=1`.
- Warnings are errors, including analyzer and style warnings.
