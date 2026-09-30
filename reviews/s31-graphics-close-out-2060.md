# S31: graphics close-out review on the RTX 2060

This is a review artifact for routing fixes; it isn't meant to merge into the manual.

- Base: `origin/housekeeping/codex-review-base` (cbd39be0c), on branch `s31-review`.
- Machine: RTX 2060 (NVIDIA 610.74), Vulkan and Direct3D 12.
- Build: Release, 0 warnings, 0 errors.
- Every gate ran through a scratch copy of that head's own `Puck.Cli.dll`.

The lead's known findings are referred to as G1 (pane publication: `portal-window` and `view-screens` pick nothing) and G2 (`counters --check` missing the resolve and copy rows). The other known items are:

- stale picks and no picker backpressure;
- temporal jitter drift and a standing jittered image after convergence;
- layout transitions causing two full graph rebuilds;
- D3D12 without dxc freezing reduced-scale views;
- GPU timing able to crash the World.

## Gate results

| Gate | Result | Against known findings |
|---|---|---|
| `puck parity` | PASS: 16 captures held every verdict | — |
| `puck canary --merge` (both backends) | FAIL: 5 proofs, listed below | 2 match G1; 3 new (N2, N3) |
| `puck test worlds --reproduce` | PASS: 79 test worlds; each world's two runs exported identical bytes | — |
| `puck counters --check` | FAIL: 66 lines, all "no ceiling recorded"; none over a ceiling, no required zero broken, none unmeasured | matches G2 exactly |
| Full test solution (Release) | 4 SdfVm laws fail under full-solution load; SdfVm alone passes 183/183 | new, test harness only (N4) |
| Debug layers (Vulkan, synchronization validation on) | 22 legs over 11 new canaries: "validation layer reported nothing" on every leg; no SYNC-HAZARD and no VUID | — |

### `canary --merge` failures

| Proof | Backend | Legs | Verdict |
|---|---|---|---|
| portal-window | Vulkan and D3D12 | positive and opposite red | G1: `a-pick-on-the-marker-lands-on-its-surface-through-the-window` and `a-pick-beside-the-marker-lands-on-the-backdrop` |
| view-screens | Vulkan and D3D12 | positive and opposite red | G1: `a-walk-through-the-{left,right}-screen-lands-on-the-{eye,sky}` |
| sdf-picking | Vulkan: both red. D3D12: opposite red | | N2 |
| seamless-four-corners-circuit | — | positive: timeout at 60 s | N3 (load) |

### `counters --check` rows with no ceiling (G2)

The rows are the same on both backends:

- 18 per backend: every kind of the new `sdf.world$resolve` pass on node `world`, including 2,073,600 texels written and 1 dispatch.
- 15 per backend: the new `gpu.copies.buffer-bytes` kind on every pass. The nonzero ones are `outside` on `world` (176) and `outside` on `main` (64).

No existing ceiling moved: primary, beam and surface march steps and texels all still hold. Recording after the fixes will add exactly these rows.

## New findings

### N1: the first frame after a resize renders the new camera aspect into the old extent's targets

**Observed** on both backends, offscreen, on the shipped world. Each resize is followed by a `world.screenshot` in the same tick, which lands on the next composed frame:

| Shot | Captured at | Content |
|---|---|---|
| `t0` | 1920×1080 | correct |
| after `world.resize 800 800` | 1920×1080 | the square camera stretched across a 16:9 target: magnified horizontally only |
| 5 ticks later | 800×800 | correct |
| after `world.resize 1920 1080` | 800×800 | the 16:9 camera squeezed into a square target |
| 5 ticks later | 1920×1080 | correct |

The stress run's "Quarter" and odd-size shots showed the same horizontal-only magnification for the same reason: each screenshot was issued in the tick before a resize. A controlled render-scale sweep without resizes (half, quarter, quarter, half, native, three-quarter) framed correctly every time, so render scale alone is not the cause. Images are in the session scratch folder.

**Cause:**

- `src/Puck.World/WorldOffscreenCommandModule.cs:59` calls `presenter.ResizeDisplay` immediately, as its comment says: "so it learns the new one here rather than one frame late".
- `WorldFramePresenter.Convergence.cs:50-51` then composes the next frame's cameras at the new display extent (`m_displayExtentSupplied`).
- The view nodes' outputs change extent only when the rebuilt graph installs (`root.Resize` at `WorldOffscreenCommandModule.cs:49`, then the node builds off the frame thread).
- Until then the installed graph renders the new-aspect camera into the old-extent targets, and presents or captures that frame.

**Failure scenario:** every window or offscreen resize shows at least one distorted frame, and a capture armed on that frame records it. The frame also counts as rendered, so cadence and history treat it as valid.

**Relationship to the known items:** it rides the "two full graph rebuilds on layout transitions" item but is a separate, visible defect.

**Direction:**

- Compose each view's camera at the extent its installed node renders this frame, not the requested display, or hold the last image until the node's new extent installs.
- A law: resize a composed presentation and assert no frame's camera aspect differs from its output target's aspect.

### N2: sdf-picking's first pick is unanswered after 5 ticks on the 2060 (Vulkan), and a taken request can be lost

**Observed:**

- In `canary --merge` and again under `--debug-layers`, Vulkan's first `world.view.pick world 0.305 0.47` still read `pending` 5 ticks later, although the engine was ready at tick 8. Later picks answered within 5 ticks.
- D3D12's positive leg passed. Vulkan's positive and opposite legs, and D3D12's opposite leg, failed on the missing first answer.
- A direct run of the canary world (cold driver cache) had the engine not ready after 60 s ("11 of 12 pipelines created"). The first pick then never answered, even 65 ticks later.
- A second, warm run answered the first pick only after the 60-tick wait.

**Code read:**

- `src/Puck.SdfVm/SdfWorldPassRecorder.cs:288`: `Prepare`, and so `SdfWorldPicker.Take`, runs only when the views part records. Every frame whose views pass does not record (a rebuild, a held or standing frame) leaves the request untaken. This is probably the known two-rebuilds or backpressure item seen from the picker's side.
- `src/Puck.SdfVm/SdfWorldPickReadback.cs:36`: the next `Prepare` on a slot clears `Record` for a request taken on a frame whose readback never submitted. `Take` has already cleared `m_pending`, so nothing retries it.
- `src/Puck.SdfVm/SdfWorldPicker.cs:91-94`: `Follow`, on a program, pick-map or cut change while a request is in flight (`m_pending` false), calls `Clear()`. That bumps the request id and drops the request instead of re-queuing it.
- `SdfWorldPicker.cs:121`: `Publish` discards an answer copied against a replaced program but doesn't re-queue the request. The caller keeps seeing `pending` for an id that will never answer.
- `src/Puck.World/WorldViewCommandModule.Picking.cs:50`: the verb prints `pending` for both "still in flight" and "dropped", so a lost pick is indistinguishable from a slow one.

**Failure scenario:** a pick issued while the program or pick map settles (bakes landing, a rebuild) is dropped silently and reports `pending` forever. On the 2060 the settle window after "ready" exceeds the canary's 5-tick wait.

**Direction:**

- Re-queue (set `m_pending`) instead of `Clear()` when an in-flight request's identity is invalidated, and after a discarded `Publish`.
- Report `dropped` or a superseding id rather than a bare `pending`.
- Whether the canary's 5-tick first wait is fair on the floor GPU is the fixer's call once picks can't be lost.

### N3: seamless-four-corners-circuit times out under `--jobs 3` on six cores

**Observed:**

- In `canary --merge` the positive leg timed out at 60 s after three of its four crossings (`accounted body.where: 3 response(s) for 4`).
- Run alone twice, it passed both times within its budget.

**Assessment:** CPU contention from three concurrent Worlds. It's the same family as the four-corners-sharded budget issue, with no regression implied.

**Direction:** a margin or `--jobs` question, not an engine fix.

### N4: four SdfVm laws time out under full-solution load

**Observed:** in `dotnet test Puck.slnx`, four laws failed:

- `SdfWorldPipelineCatalogLawTests.TwoResidenciesOverOneCatalogRenderWithOneCreationPerPipeline`
- `SdfWorldResidencyShaderReloadLawTests.ATreeCarriesTheKernelsItReplacesAsBytecodeOrAsSourcesTheReloadCompiles`
- `SdfWorldResidencyWorkLawTests.AProducedFrameIsPublishedByTheNextProducedFrame`
- `SdfWorldPipelinesLawTests.ASetDisposedWhilePipelinesAreInTheDriverWaitsOnlyForThoseAndCreatesNoMore`

The SdfVm assembly took 1 min 15 s there, against 10 s alone. Alone, it passed 183/183. Under concurrent World and SignedDistance suites it reproduced: `the engine's pipeline set is building (6 of 11 pipelines created)` at `tests/Shared/SdfTestPipelines.cs:91`.

**Cause:** `ProduceFirstFrame` spins for a fixed 30 s (`SdfTestPipelines.cs:97`) while the fake device's pipeline build waits on a starved thread pool. The helper predates the reviewed range, so this is not a regression.

**Direction:** a test-harness liveness bound; it doesn't block the close-out.

## Device results on the 2060

### Memory headroom: shipped world, floor tier (`world.quality low`: half render scale), offscreen 1920×1080, after 120 ready ticks

| | Device-local allocated | Device-local peak | Aperture allocated | Aperture peak |
|---|---|---|---|---|
| Vulkan | 655.3 MB | 647.5 MB | 36.8 MB | 36.6 MB of the 214 MB heap |
| Direct3D 12 | 669.9 MB | 665.9 MB | 0 | 0 |

- The aperture has ample headroom: history, motion and extent targets are device-local, not in the aperture.
- Device-local is about 160 MB above the pre-P15 shipped-world figure measured at default quality (485.7 MB peak), even though the floor tier renders at half scale. The history, motion, resolve and output-extent targets account for it. On a 6 GB card that's acceptable.
- After the resize and seat storm (1×1, 3×7, 1919×1079, 641×359, Quarter, a seat join and leave, back to 1920×1080 native):
  - Vulkan: peak 813.1 MB; 1,827.1 MB allocated and 1,109.8 MB released, so 717.3 MB held at exit.
  - D3D12: peak 832.1 MB; 736.0 MB held.
- The run ends at native render scale against a half-scale start, so the higher end state isn't clearly a leak. A same-scale before/after would settle it.

### Odd extents and resizes

- **Offscreen**, both backends, Vulkan under debug layers: render scale down to Quarter, resizes to 1×1, 3×7, 1919×1079 and 641×359, a seat join and leave. No crash, no validation message, no error line, 0 wire errors. The only defect is N1's distorted frame on each resize.
- **Windowed** shipped world, Vulkan with debug layers, at Quarter: the window was resized to 1×1, which clamps to a 120×0 client, then 3×7, 1279×721, 641×359, 1×1 again, 1920×1080, 799×601 and 1280×800.
  - No crash, no validation message, 0 wire errors.
  - The world's own tick-scheduled captures that overlapped the resizes were refused. Plaza (tick 60) was stale, served at tick 68. Arena (tick 210) was stale, served at tick 315, and held the render chain, so arcade, granaries and market were refused as busy.
  - In the same run without resizes every capture was written. That's low severity: a zero-height client presents nothing, but a capture stuck for about 100 ticks blocks every later one.

### Cold pipeline build

In one direct boot of `sdf-mesh-visibility/fixture.puck` on a cold driver cache, the engine was not ready after 60 s: `11 of 12 pipelines created`. The canary runner warms the cache first, so the suite isn't affected. A player's first boot on a 2060 can take over a minute before the world view appears.

## Not covered

- D3D12 debug layers were not run.
- A windowed resize on D3D12 was not run.
- A same-scale before/after memory comparison was not taken, so I can't say whether the post-storm held growth is a leak.
