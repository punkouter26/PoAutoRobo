# PoAutoRobo — Open work

Only what is still to do. Finished tasks T1–T19, T21, T22 and the 2026-10-08 change list are in the git history; the original plan is archived at [docs/plan-2026-10-archive.md](../docs/plan-2026-10-archive.md).

Every task: failing test first, then code, the related tests, `dotnet build`, one commit. Paths are under `src/PoAutoRobo.Core/` (Core), `src/PoAutoRobo.App/` (App), `tests/PoAutoRobo.Core.Tests/` (Tests).

| Status | ID | Slice | Files (≤5) | Acceptance | Deps |
|---|---|---|---|---|---|
|  | T20 | AI video: live spike, job client, shot planning | `Core/Services/IVideoGen.cs`, `Core/Services/AzureVideoGen.cs`, `Core/Pipeline/ShotPlan.cs`, `Tests/ShotPlanTests.cs` | Shots cover narration, last frame held; timeout/failure falls back to still. **Starts by asking you about the `sora-2` deployment.** Until then an "AI video" clip gets one still | — |
|  | — | **Checkpoint D** | | | T20 |

## Standing notes

| Kind | Note |
|---|---|
| NOTE | No long episodes or picture batches on the paid services; use the one-clip quick test for live checks (user instruction 2026-10-08) |
| OPEN | Drag-reorder of clip cards did not respond to simulated mouse drags; needs trying by hand. The Move earlier/later buttons (Alt+Left / Alt+Right) are the dependable route |
| OPEN | Host candidate dialog not exercised since it was built (costs about 5 cents; host already locked) |
| OPEN | Not yet tried by hand after the 2026-10-08 audit changes: light theme by eye, the Windows notification when a job ends with the window hidden, button sounds, "Remove unused media" with something to remove, the GitHub Actions workflow (never run) |
| OPEN | The spend figure counts pictures only. Add script and voice once their prices are confirmed |
