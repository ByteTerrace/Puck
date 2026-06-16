# World debug visualization & validation log

In-engine math validation for the compute world pass: toggleable visualizations, a
GPU-accumulated stats buffer, and a JSONL validation log. The shipping kernels stay
BYTE-IDENTICAL — the debug hosts (`world-view-debug.comp`, `world-view-rt-debug.comp`)
define `Puck_DEBUG_VIZ` and include the same body; every shared-file edit lives
inside that `#ifdef`.

## Controls

- **F4** cycles the debug view mode (wraps through off); **F5** toggles the validation
  log. F6 toggles the creature-eye camera (not part of the debug layer); F7–F8 are
  plumbed through the input chain but unbound.
- **Console** (backtick): `debug.view <list|off|name|index>`, `debug.log <on|off|status>`,
  `debug.wallpaper <list|name|index>`, and the shape inspector:
  `debug.shape list [asset]` (CPU-side instruction-stream dump: slot, authoring name,
  opcode, shape, blend op, material, gateability) and
  `debug.shape <isolate|highlight|bounds> <slot>` (activates mode 13 on that slot;
  `debug.shape off` returns to off).
- **Startup / capture runs:** `AvatarDebug:WorldDebugMode` (name or index) and
  `AvatarDebug:WorldDebugLog`, env-overridable as `Puck_AvatarDebug__WorldDebugMode`
  / `__WorldDebugLog`. Bare `Puck_*` vars stay reserved for renderer-path toggles.
- **Scheduled sweeps:** `AvatarDebug:WorldDebugModeSchedule` (`mode:frames,...`, e.g.
  `off:24,beam-check:16`) walks the view mode through segments keyed on the
  captured-frame index — capture runs present one frame per animation tick, so the
  sweep is tick-exact and deterministic. Overrides `WorldDebugMode` from frame 0;
  unparseable text fails at startup. One run can thus cover every mode in the catalog.
- Encoding: push-constant offset 44 (`pc.world.w`) = float-encoded word, bits 0–7 mode,
  bit 8 stats flag, bits 9+ the active mode's payload (wallpaper: group in 9–13;
  shape: slot in 9–16, sub-mode in 17–18 — the word stays below 2^19, exact in
  float32); defaults encode to `0.0f`. A debug pipeline is bound only while
  mode != off or the log is on, so default captures stay hash-stable.

## Mode catalog (KEEP IN SYNC: WorldDebugViewMode × world-debug.glsl)

| Idx | Name | Family | Hosts | Shows / counters fed | Clean scene |
|---|---|---|---|---|---|
| 0 | off | — | shipping | passthrough (stats-only logging renders normally) | baseline hash |
| 1 | depth | geometry | both | nearest-hit T, turbo ramp near→8 wu; sky dark / min-max nearestT | deterministic |
| 2 | normals | geometry | both | terrain normal RGB via the exact shadeGround formula | deterministic |
| 3 | raydir | geometry | both | rayDirection·0.5+0.5 (camera quaternion/ray gen) | deterministic |
| 4 | tiles | geometry | both | tile checker + coord gradients + grid lines + magenta pane border + yellow extent border; doubles as the mode-decode self-test | borders at edges |
| 5 | terrain-cost | cost | both | primary-march iterations /128 on turbo; sentinel tiles dark blue / terrainIter* | deterministic |
| 6 | creature-cost | cost | both | creatures marched per pixel, 6-color discrete / creaturesTested | deterministic |
| 7 | beam-check | invariant | both | re-march from nearPlane: RED sentinel violation, ORANGE hit before startT−tol (tol = 0.004+1%·t), YELLOW informational graze miss (never counted) / beamViolations | counter 0 |
| 8 | bounds-check | invariant | both | MAGENTA = creature hit outside padded bound sphere (1.02·r²+4e-4) / boundsViolations | counter 0 |
| 9 | nan-check | invariant | both | RED NaN / YELLOW Inf in pre-resolve color/depth / nanPixels, infPixels | counter 0 |
| 10 | cull-parity | invariant | rt-debug only | RED = HIT creature missing from the tile mask (counted); AMBER = raw TLAS candidates beyond the mask (expected cube-corner conservatism); BLUE = tile-mask slack / cullParityViolations. Tile-mask host renders a diagonal hatch | counter 0 |
| 11 | wallpaper | self-test | both | pane-space harness for the avatar VM's `sdoWallpaperFold` (the production function, not a copy): F-glyph lattice, parity-hued cells, pink tint on reflected copies (fold det −1), striped header bar = group index + 1. Group = captured frame % 17, driven by the mode-schedule driver through bits 9–13 of the debug word, so the validate tiers' 17-frame tail segment sweeps all 17 groups (12 rect + 5 hex) | deterministic |
| 12 | chart | geometry | both | creature surface-chart QA: charted surfaces render the chart checker (grayscale; swimming/stretching/seams read directly) tinted by the pixel-footprint heatmap (cool = sub-cell pixels, warm = band-limiting territory); chartless creature surfaces render flat dark. World renders normally — the mode resolves inside the avatar VM (`g_vmChartDebugMode`) | deterministic |
| 13 | shape | geometry | both | per-instruction shape inspector, driven by `debug.shape` (the slot rides bits 9–16 of the debug word, the sub-mode bits 17–18; the slot is the GLOBAL packed-instruction index, so it selects (asset, shape) and every instance of that species shows it). Sub-modes: **isolate** = render ONLY the targeted SHAPE_BLEND, normal-shaded (other species render empty); **highlight** = normal render, hot-pink tint where the targeted instruction WINS the running blend, dimmed elsewhere; **bounds** = composite the slot's posed culling AABB (cyan faces, yellow edges) against the marched surface — the visible gap is the gating slack. Manual tool: parameterized like rain, NOT part of the validate sweeps | deterministic per slot |
| 14 | shape-gallery | self-test | both | every pure-math VM shape primitive traced through the production `evaluateSdfShape`/`evaluateSdfShapeGradient` at three pinned, loader-admissible parameter sets each (smallest admitted / typical / contract-boundary), in a 7×6 pane-space grid (rows 0–2 = sets of shapes 0–6, rows 3–5 = shapes 7–13; MSDF_GLYPH deliberately absent — its field samples atlas content, which would couple the baselines to the console font). Per hit pixel: RED = the gradient path's distance disagrees with the plain path, ORANGE = analytic vs central-difference gradient direction mismatch (crease-guarded via numeric magnitude), MAGENTA = NaN/Inf anywhere; clean cells render normals. Pure function of the pixel, so the frame is byte-identical across culling tiers (one pinned frame per full tier, carved from the off head). Positive control verified: a flipped torus gradient lights its column orange | deterministic, tier-identical |
| 15 | shape-solo | inspect | both | ONE bare primitive fullscreen, interactive: `debug.shape solo <shape>` picks the primitive (sensible defaults), `debug.shape set <p0> [p1] [p2] [p3]` tunes its four `data0.xyzw` params live, `debug.shape off` exits. Same fixed view + production `evaluateSdfShape`/`evaluateSdfShapeGradient` as the gallery, normal-shaded; NaN/Inf params → magenta. The render early-returns before the world march, so the four params ride the lanes that frees up: `world.xyz` + `misc.w` (decoded in `world-debug.glsl`/backend; shape type in debug-word bits 9–13). Round cone derives its 2nd radius from the 1st (`data0` is fully spent); MSDF_GLYPH unavailable (needs a glyph-atlas slice). Manual tool, NOT in the validate sweeps | deterministic per (shape, params) |

### Pane-surface effects (rain) — composite-pass debug, not a kernel mode

The rain-on-glass effect lives in the pane compositor's own pipeline
(`pane-composite.frag`, profile `PaneComposite`), not in the world kernels, so its debug
views ride the `rain.*` console commands instead of the catalog above:

- `rain.amount <0..1|status>` — drives everything: 0 (default) is the hash-stable OFF
  state (no mip chains, effect id 0 on the composite quads, bytes identical to a build
  without the effect); anything above 0 turns on per-frame re-records, full mip chains
  on the per-view offscreen images (blit-downsampled after every world pass), and the
  drop/trail/fog composite.
- `rain.speed <0.05..4>` scales the deterministic drop time (frame index / 60 Hz —
  never wall clock, so captures replay bit-identically); `rain.fog <0..2>` scales the
  glass-blur LOD range.
- `rain.debug <0..3>`: 1 = drop-field height mask, 2 = trail mask, 3 = chosen blur LOD
  as a cosine-palette heatmap (cool = wiped sharp, warm = full fog).
- Feed-slot panes have no mip chain (producer-owned images), so they get refraction
  without fog; at render scales below 1.0 the refraction step grows with the texel size.

CPU↔GPU parity probes are not a visual mode: whenever the stats flag is set, view 0's
invocation (0,0) writes hills() at 8 fixed track-local points and the kernel quaternion
rotation for 4 fixed pairs into the stats SSBO; the log compares against
`AvatarTrackProfile.Hills` (expectations flow through `WorldDebugOptions` — core never
duplicates terrain math) and a literal C# mirror of `worldRotateByQuaternion`.
Tolerances: hills 5e-4 abs (Vulkan sin/cos ~2^-11), quat 1e-5 abs.

## Stats SSBO + JSONL log

Set 0 binding 5 (always in the layout — unused storage-buffer bindings are legal and the
shipping kernels don't declare it), 256 B host-visible|coherent, persistently mapped,
**uint atomics only** (determinism rule); cleared to zeros except
`minNearestTBits = 0x7F7FFFFF`. Read+cleared at the existing post-fence-wait re-record
point — no new sync, no teardown GPU access. One JSONL `tick` record per animation tick
to `artifacts/world-debug/world-debug-<utc>-<pid>.jsonl` (gitignored) + one `summary`
line + a Console.Error digest at exit. Identical capture runs produce identical logs
after stripping the `utc` field.

When capture and logging are both active, every captured frame additionally writes one
`capture` record: the SHA-256 of the exact RGBA bytes handed to the capture encoder,
plus the view mode the frame was rendered with. The per-frame hashes strictly subsume
an APNG-file hash (a divergence names the first differing frame and its mode segment),
which is what lets validation runs skip APNG encoding entirely via
`Puck_TerminalCapture__Format=Null` (frames are counted and observed, nothing is
encoded or written).

## Fast validation gate (tools/Tools.cs validate)

Coverage is tiered to keep the gate lean: the full-length `rt` and `tile` runs
(96 frames) own all kernel-math coverage — every mode above (including the
single shape-gallery frame, whose hash must come out identical in both tiers)
plus the 17-group wallpaper tail — once per CULLING tier, because the culling
source is what forks the kernels. The `rt-split` / `tile-split` / `feed-split` runs exist to prove
layout and feed invariance, which more kernel sweeps cannot strengthen, so they
run 32-frame schedules of only the layout-sensitive modes (off, tiles, the
always-on invariants, cull-parity where ray query exists).

`dotnet run tools/Tools.cs -- validate` (a subcommand of the single-file .NET 10
verification toolbox) is the everyday regression gate — five ~13 s capture runs
(ray-query tier, `Puck_RAY_QUERY=0` tile tier, then both again as `rt-split` /
`tile-split` under the FourWaySplit view configuration via
`Puck_AvatarDebug__ViewConfiguration` — split screen is scene data only, so the
same kernels, counters, and parity probes must hold across layouts; the split tiers
pin the multi-view path: 4 view-table entries, per-pane dispatches and scissors —
and finally `feed-split` under FourWaySplitFeed: the bottom-right pane composites
feed slot 3 instead of rendering the world, the deterministic test-card producer
fills the slot keyed on the animation tick, and the other panes' in-world screens
watch the same slot, pinning the whole external-feed path: the slot-region upload,
the skipped dispatch/feed-copy for the feed pane, and both consumers' sampling).
Each run sweeps the full mode catalog through a `WorldDebugModeSchedule` with a long
`off` head (parity probes + default-path pixels), then the geometry/cost modes, then
every invariant mode (`cull-parity` on rt tiers only). Per tier it asserts, all from
the JSONL:

- zero invariant counters and zero parity-failure ticks (semantic, baseline-free);
- 96 capture records and a byte-exact match of the normalized log against the
  checked-in baseline (`tools/baselines/validate-<tier>.jsonl`) — this covers pixels
  (per-frame hashes) AND cost telemetry (`terrainIterTotal`, `creaturesTested`, …),
  so culling/march-cost regressions fail the gate even when pixels are identical.

It also enforces the shipping-kernel byte-identity check after the build (override
with `-- -AllowShaderDrift` for intentional visual changes). `-- -Bless` re-records
the baselines — do that (and regenerate the showcase APNGs via the `capture-parity`
subcommand)
on any intentional visual change; `-- -BlessMissing` records only tiers that have no
baseline yet while gating the rest (how the split tiers were first recorded).
WideOverTwo is reachable for manual sweeps the same way
(`Puck_AvatarDebug__ViewConfiguration=WideOverTwo`).

The default Debug capture (the showcase APNG, `appsettings.Debug.json`) rotates
through all three layouts in equal 32-frame slices via
`AvatarDebug:ViewConfigurationSchedule` (`Standard:32,FourWaySplit:32,WideOverTwo:32`
— `configuration:frames,...`, the layout analog of the mode schedule, keyed on the
captured-frame index). An explicit `ViewConfiguration` wins over the schedule and
pins a single layout for the whole run — which is how every validate tier (including
`rt`/`tile` with `Standard`) stays immune to the showcase schedule.

Baselines are GPU-specific (recorded on the RTX 4070
box). Note the gate's `off` segment exercises the debug-host passthrough, not the
shipping pipelines — shipping-kernel shader coverage comes from the byte-identity
check, and C# host changes hit both paths equally; the `capture-parity` subcommand
remains the shipping-pixel gate for pre-merge sign-off.

## Performance benchmark (tools/Tools.cs bench)

`dotnet run tools/Tools.cs -- bench [-Release] [-Frames N] [-Modes a,b,…] [-Tiers rt,tile]`
drives one **real-engine** run per (culling tier × view mode), with the aggregating
`TerminalPerformanceSummarySink` on (`OperatorStatus:EnablePerformanceSummary`), and
tabulates the per-frame timing. It is a **measurement tool, NOT a gate** — wall-clock
figures are noisy and box-specific, so nothing here is asserted against a baseline (the
`validate` gate owns determinism; this owns speed). Each run's full per-stage JSON lands
in `artifacts/perf/run-<cfg>-<tier>-<mode>.json`; the combined table in
`artifacts/perf/bench-<cfg>.json`. Mode `off` benches the **shipping kernel** (the real
engine); a debug mode benches its debug kernel.

**It benches the engine, not the infrastructure.** Capture is OFF — there is no per-frame
framebuffer readback (a render-scale sweep proved the readback is a fixed ~30 ms floor,
render-independent, that buries the real cost; an earlier capture-path version of this
bench mostly measured that floor). Instead each run sets two benchmark-only knobs:
`PresentEveryFrame` (the skip-unchanged-presentation optimization keys on layer-command
equality, which the compute world's per-pixel content never changes — so without this an
animating world idles at the ~30 fps skip floor) and `WaitForGpuEachFrame` (a per-frame
full device-idle wait, so the timer captures the GPU-complete frame cost rather than
returning early under the non-blocking Mailbox present, which would clock CPU submit cost
~0.04 ms). Cadence is uncapped, so **wall fps == render rate**. The GPU sync serializes
the pipeline (no CPU/GPU overlap), making this a frame-COST measurement — slightly
conservative vs a pipelined production run, but the engine is GPU-bound here so the gap
is small. A per-dispatch GPU-timestamp number (overlap-independent, isolates the march
from the present blit) is the documented next step — see `Puck-perf-benchmark`.

**First baseline (RTX 4070, 1600×900 window, 240 frames, frame ms p50 → fps):**

| tier | off | terrain-cost | creature-cost | beam-check | chart | wallpaper | shape-gallery |
|---|---|---|---|---|---|---|---|
| rt (ray-query, default) | 2.5 / **~400** | 2.5 | 2.5 | 2.6 | 2.5 | 2.6 | 2.6 |
| tile (beam-mask fallback) | 7.3 / **~135** | 7.4 | 7.4 | 7.4 | 6.3 | 7.4 | 7.6 |

(Release; Debug is within ~5 % — see below. fps shown for `off`; other modes ≈ same ms.)

Findings:
- **The engine is GPU-bound.** Debug (rt ~2.5 ms, tile ~6.4 ms) ≈ Release (rt ~2.5 ms,
  tile ~7.4 ms): the C# config barely moves it because the full-screen world SDF march on
  the GPU dominates and CPU command recording is only ~0.4–0.7 ms.
- **Ray-query culling is ~2.7× faster than the tile-mask fallback** (~400 vs ~135 fps).
  This is the real-engine evidence behind making ray-query the default world pass — the
  per-frame TLAS out-culls the beam prepass's per-tile bitmasks.
- **Mode cost is dominated by the march, not the debug overlay** — within a tier every
  mode lands within noise of `off`, so the debug visualizations are cheap; the cost you'd
  optimize is the world march itself (and the culling tier that feeds it).

The reusable pieces delivered: the `TerminalPerformanceSummarySink` infrastructure, the
per-stage JSON, and the `PresentEveryFrame`/`WaitForGpuEachFrame` benchmark knobs the
GPU-timestamp expansion will reuse.

### Watch FPS live, unlocked (manual run)

**This is the Release default** — `appsettings.Release.json` sets the four knobs below, so
a plain `dotnet run -c Puck.Avatars … -c Release` runs unlocked with the live FPS
readout on, no env needed. Debug keeps the capped/showcase behavior (the gate and
`sample-avatar.png` regeneration run Debug, so they are unaffected). The knobs, for
reference / overriding (env wins over appsettings):

```
Puck_AvatarDebug__ShowFpsOverlay=true           # live readout, top-left corner
Puck_AvatarDebug__InteractiveFrameCadenceHz=0   # uncapped (Debug default 120)
Puck_AvatarDebug__PresentEveryFrame=true        # render every frame (see below)
Puck_AvatarDebug__WaitForGpuEachFrame=true      # spin-free + honest counter (see below)
# capture stays off by default outside appsettings.Debug.json
```

Two of those knobs are not cosmetic — they are *required* for the unlocked run to be sane
and the number real, because of how the engine presents:

- **`PresentEveryFrame`** — the skip-unchanged-presentation optimization keys on
  layer-command equality, and the compute world's per-pixel animation never changes a
  layer command, so without this the animating world idles at the ~30 fps skip floor (it
  only re-presents on the 33 ms timer). This is a real engine characteristic: an
  always-animating compute-world showcase needs present-every-frame to run smooth.
- **`WaitForGpuEachFrame`** — the present mode is the non-blocking Mailbox (async; the
  CPU-side acquire is semaphore-based, so it does not block). Truly uncapped, the loop
  therefore **spins a CPU core at tens of thousands of submits/sec**, the GPU renders
  ~400/sec, and Mailbox drops the rest — wasteful, and the overlay (which counts dispatch
  loops) would read the spin rate, not what the eye sees. Stalling on the GPU each frame
  throttles the loop to the GPU rate: spin-free, and the counter shows the true GPU-bound
  render rate (~400 fps rt on the baseline box). The cost is CPU/GPU pipelining (~10–15 %;
  a fully pipelined release could be marginally higher, but it's GPU-bound so the gap is
  small). This is why it's a sane default rather than just a measurement crutch — without
  it "unlocked" means "peg a core for dropped frames."

The overlay defaults off and emits nothing when off, so capture and `validate` runs are
unaffected.

## Verifying a kernel change

1. **Byte-identity gate** (after any `world-view-body.glsl` / `world-common.glsl` edit):
   rebuild; the three shipping `.spv` files must be UNCHANGED in `git status`. A diff
   means debug code leaked outside `#ifdef Puck_DEBUG_VIZ`.
2. **Default parity:** a defaults run must reproduce the prior
   `docs/images/sample-avatar.png` hash (run twice to confirm determinism first).
   The tile fallback tier has its own showcase, `sample-avatar-tile.png`
   (`Puck_RAY_QUERY=0`); the two are byte-identical today and fork when the
   tiers' shading diverges — regenerate both on any visual change.
3. **Counter sweep:** capture with the relevant invariant mode + `WorldDebugLog=true` to
   an absolute temp `Puck_TerminalCapture__OutputPath` (never overwrite the
   showcase); the exit digest must show zero violations and green parity.
4. **Detector positive controls** (prove a detector still detects): beam → `+0.05` on
   `terrainStartT` in the beam kernel (hot reload); parity → bias a constant in
   `AvatarTrackProfile.Hills` by 1e-3; NaN → inject `sqrt(-pc.world.x)` in debug-only
   code; bounds → tighten the containment factor; cull-parity → shrink the
   `collectCreatureMask` radius under `Puck_RAY_QUERY=1`. Revert and re-zero after.

## Known behaviors & traps

- Toggles apply on the next re-record (≤ ~83 ms live at the 12 fps animation cadence;
  instant in capture runs).
- The jumbotron/thumbnailtron feeds show debug colors recursively — expected.
- Mid-run-keypress captures are wall-clock-timed and not hash-comparable; determinism
  claims come from startup-mode captures only.
- `beam-check` doubles the terrain-march cost while active; with everything off the
  shipping pipelines run and the debug layer costs nothing.
- **glslang trap:** never give the stats SSBO a member shaped `vec4[4]` or `float[4]` —
  glslang shares array types across storage classes, the body's local creature arrays
  would inherit the ArrayStride decoration, and vulkan1.2's spirv-val rejects the module
  (VUID-StandaloneSpirv-None-10684). The quat probes are packed float triplets for
  exactly this reason.
- **TranslateMessage trap:** posted WM_KEYDOWNs still get translated into WM_CHARs by
  the message pump. The console-toggle key's own character is suppressed one-shot in
  `Win32NativeWindow`; PostMessage-driven console tests must post the toggle keydown
  ONLY (posting an extra '`' WM_CHAR double-types it — the duplicate lands as text, by
  design).
