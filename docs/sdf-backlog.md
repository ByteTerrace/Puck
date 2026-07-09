# The SDF backlog — open work, consolidated

*Created 2026-07-09 by the plan-doc consolidation (audited at `features/sdf-boom`
tip `5624161`, Post 51/51). This is the ONE living ledger of open SDF work,
distilled from the retired `sdf-accumulator-plan.md`, `sdf-vm-briefing.md`, and
`sdf-vm-evolution-plan.md` (all fully audited item-by-item before retirement —
git history keeps the full texts). The division of labor:
[sdf-sota-survey.md](sdf-sota-survey.md) is the ranked RESEARCH record,
[capability-catalog.md](capability-catalog.md) is what IS,
[sdf-bench-notes.md](sdf-bench-notes.md) is what was MEASURED — this doc is
what's NEXT. Items are orientation, not a work breakdown; re-negotiate shape at
arc start, leave room for whimsy.*

**The post-mask-first reality (reranks everything).** The O(instances) beam
wall fell 2026-07-09 (the mask-first instance cull; the O(n) was the cone
march's per-sample field enumeration, never the binning — see
sdf-bench-notes.md). Frames past ~1024 on-screen instances are now
**views-bound**: the per-pixel tape walk is the bottleneck. That PROMOTES
tape pruning (shorter per-tile tapes attack views directly) and DEMOTES the
pre-beam pyramid and proxy/LOD far-field (their "beam still dominates"
premises are now false).

## Performance levers (engine, Post-gated)

1. **Per-region tape pruning — REFUTED AS SPECIFIED (2026-07-09,
   measure-first build; see the wiki negative-results ledger).** The full
   candidate (per-tile world-segment masks riding the cull pass) was built,
   gated, and disproven on both axes: VALUE — the room is 284 single-segment
   instances + only 2 world segments (~1.04 seg/instance; the tape is already
   as short as instance masking makes it); CORRECTNESS — bit-identity is
   unreachable for auto-bounded world segments (pruned-vs-flat diff 1.51%,
   8700 px: a Union segment occludes softShadow/calcAO rays in tiles its
   geometric bound never touches; instance masks are bit-exact only because
   authors OVERSIZE bounds — world-instanced uses bound 4–5 for a 0.3-radius
   sphere — a discipline auto-analysis cannot replicate). If ever revisited,
   it needs authored per-world-segment influence bounds, and a content
   profile with many multi-segment chains. **Scale addendum (2026-07-09,
   user-prompted):** the refutation splits — the influence-vs-geometry margin
   truth holds at ANY scale (secondary shadow/AO rays wander outside the
   camera cone; bounds must cover geometry ⊕ shadow reach ⊕ AO ladder — the
   instance mask has always implicitly lived by this via authored oversized
   bounds), but the VALUE verdict is the ROOM's profile. Future content flips
   it: placed creations are ONE instance per placement carrying the whole
   composition's segment chain (up to the 64-shape envelope) — a Puckton
   street is exactly the multi-segment tape the room lacks. At world scales
   >> the 12-unit shadow reach, influence-inflated per-tile masks become both
   bit-exact and effective — for whole instances the landed grid cull ALREADY
   IS that pruner; the open remainder is WITHIN-instance segment pruning for
   placed creations, whose per-segment influence bounds belong at BAKE time
   (the creation bake knows its shapes — deterministic, authored-equivalent;
   aligns with tier-2 carve consolidation). Item 6 (profile the town) is the
   gating measurement. The measurement's real finding:
   **the room's views (92% of frame) is the SHADING EPILOGUE eval count**
   (softShadow's full per-lit-pixel sphere-trace is the largest term) — the
   promoted levers are #4 (compute shadow-cull), #10 (closestApproach
   factoring), #11 (cone-AO tier).
2. **Segment tracing** (Galin 2020 — the D-series' deferred "D"; survey row 13
   prerequisite) — per-segment directional Lipschitz bounds baked alongside
   the per-shape bounds → larger safe steps. The linear member of the
   forward-inclusion family; ships before any quadratic/IA case. Additive to
   the segment directory as always scoped.
3. **March/shade split** (survey row 17) — latent enabler; its
   register-footprint premise now has measured evidence (the fused grid
   attempt's 512 B/thread scratch taxed the co-resident cone march +12%).
   Builds when a divergent shade stage or per-tile tapes arrive.
4. **Compute shadow-cull** — the soft-shadow march has no acceleration; port
   the rt path's occluder fast-forward idea to the compute march (natural
   rider on the grid structure).
5. **Per-tile / per-segment Lipschitz refinement** — fold each visible warped
   instance's baked `L` into a per-tile max-L so only warp-bearing tiles slow.
   Eventually superseded by #2.
6. **The Puckton town ceiling — MEASURED (2026-07-09): a views-bound
   producer cost.** At the town reveal overview (16 placements, 12 lights):
   frame 147.5 ms = mask 0.03 (0%) + beam 13.6 (9%) + views 133.8 (91%).
   The grid cull holds at town scale (mask free, beam single-digit); the
   whole cost is per-pixel — placed-creation multi-segment tapes + the
   shading epilogue at full-screen coverage. The town renders artifact-free
   (artifacts/console/town-reveal.png); it is not a render-range mystery.
   The levers are exactly the promoted set: #4 shadow-cull, #10
   closestApproach, and (per item 1's scale addendum) bake-time
   within-instance segment bounds for placed creations. Attribution split
   (epilogue vs tape walk) is the storm/views instruments' first real job.
7. *Demoted, revisit only if a new beam-dominant regime appears*: the 64×64
   pre-beam pyramid (row 16 — near-moot post-grid); proxy/LOD far-field +
   Sphere Carving (row 14 — the far-field ceiling is views-bound now).
   Wavefront/compaction (row 18) stays gated on per-tile tapes. TLAS-over-
   instances stays REJECTED (nondeterministic build).

## Quality / visual (engine, Post-gated)

8. **Material blend factor at seams** (survey row 6, R5) — re-evaluate the
   top-of-tree blend at the confirmed hit, lerp operand albedos by the clamped
   `h`, route through `parityMaterialDelta`. The top unbuilt pursue-now.
9. **Ray-differential CRT texture filtering** (survey row 12, R9) —
   `SampleGrad` from closed-form footprint derivatives on the screen-slab hit;
   kills CRT shimmer/moire. Prerequisite: mip chains on screen-source
   textures.
10. **Factor `closestApproach()`** — AO / soft shadows / coverage-AA all
    landed sharing the hoisted `stepScale` clamp but the survey's unified
    helper was never factored; three separate walks remain. Do it BEFORE
    adding any fourth epilogue tap (cone-AO). Mind the divide-back foot-gun
    (each consumer handles `stepScale` deliberately differently).
11. **Cone-AO / bent-normal quality tier** (R1 tier-1) + hero analytic
    occluders — pursue when GPU-bound; rides #10.
12. **Bound-preserving fBm ISA op** (survey row 11) — still blocked on the
    PCG3D integer-hash noise-op basis; the gradient dual (landed) de-risks its
    analytic normals.
13. **Tier-1 coverage-AA continuation** — only if silhouette aliasing stays
    objectionable after the landed Tier-0.
14. **Moinet curvature stepping** (R6) — its first-derivative prerequisite now
    exists (`mapGradCore`); still needs the second directional derivative;
    payoff is volumetric-noise content we don't have yet.

## Destruction, tier 2 (deferred by explicit decision 2026-07-09)

15. **Carve consolidation** — Dreams-style incremental re-bake of accumulated
    carves into the base field (wiki notes) rides tape pruning (#1). Design
    notes welcome; the tier-1 budget (measured: ~1024 in-frame scattered
    carves at 60 fps; off-screen ~free; dense per-tile stacking is the real
    ceiling) is in sdf-bench-notes.md.

## Authoring surface (demo/run-doc side — greenfield, verify by running)

16. **The run-document op gap** — unauthorable from a data file: LogSphere,
    RepeatPolar, DomainWarp, Displace, SymmetryPlane (general normal), Vesica,
    and the entire 2D family (RoundedRectangle/RegularPolygon/Star/Trapezoid/
    Ellipse). `TransformOp`/`SceneObject` stop at cellJitter/bendY/bendZ and
    the legacy shapes. (The briefing's "a renderer that outgrew its authors,"
    narrowed but standing.)
17. **Run-document nesting / explicit field scopes** — per-object field-op
    scoping landed; a general grouping/nesting construct (PushField/PopField
    in JSON) has not. The VM primitive exists; this is pure surface. Includes
    dropping `WorldChamferStage`'s emit-intersections-first trick for a real
    scope.
18. **Depth > 1 field scopes** — grow the shader's `SdfFieldSave` save slot
    (already a `{distance, material, gradient}` struct — one migration) into
    an indexed stack + bump `MaxFieldScopeDepth` + a validator rule. Measured
    cost basis (preserved from the retired accumulator plan): +7 DXIL
    instructions at depth 1, +8 at depth 2 and 4, flat because HLSL vectors
    cap at 4 components — depth 4 is the natural ceiling.
19. **Creator modifier verbs** — the editor exposes `creator.onion` only; the
    renderer reserves two per-shape modifier slots (twist/dilate/bend/…).
20. **The 2D-primitive next tranche** — arc/pie, cross, moon, egg, heart:
    cheap, the `SdfLift` revolve/extrude framework exists.
21. **The diegetic UI arc (game-studio W6 — SHAPED 2026-07-09, user-ratified).**
    One SDF-rendered diegetic UI; the overlay console stays FOREVER as the
    convenience/agent surface (stdin/stdout is the control plane), the
    diegetic UI mirrors it for immersion; the action bar is not a separate
    item — same layout and concept, ROLLED INTO the diegetic UI. The
    staircase, in order:
    - **Tier 0 — the diegetic console terminal** (demo-greenfield, zero
      engine work, buildable now): an in-world terminal object whose screen
      is a screen-source feed mirroring the console history —
      `ProceduralFaceFeed` pattern + `Puck.Text` CPU rasterization + the
      4 unclaimed `ScreenSlotLedger` slots.
    - **Tier 1 — the MTSDF glyph op** (engine, Post-gated): sample the
      `Puck.Text` atlas as a DISTANCE-level field (median-of-3 is
      ~1-Lipschitz-bounded), unlike `ScreenSlab`'s material-level sampling —
      text becomes real world geometry: marchable, liftable, blendable,
      ENGRAVABLE (Subtraction of a glyph field) and embossable. Serves the
      UI, Puckton signage, cabinet marquees, and carved lettering alike.
      `Puck.Text` finally gains its first GPU consumer.
    - **Tier 2 — the action bar as camera-rig-mounted geometry**: the
      existing layout re-expressed as SDF instances riding a per-frame
      `DynamicTransform` pinned to the camera pose — a physical HUD, no
      ortho kernel, inherits cull/AA/normals for free; pad-first input means
      no pointer-picking is ever needed. Widgets = scoped groups (parkable
      when hidden, finite bounds via `MaxScopedFieldReach`).
    - The recursion prize: UI elements become authorable creations
      (`puck.creation.v1`) — the editor can sculpt its own chrome.
    - 2D-arc **option B (flat-2D/ortho kernel) is DEMOTED** to
      only-if-something-genuinely-needs-a-flat-field; the camera-rig approach
      removes its primary consumer. Post-grid-cull instance budget makes UI
      geometry cost ~nothing (a few hundred view-local instances).

## The adversarial-instrument wave (user-ratified 2026-07-09 — all four greenlit; build after the tape-pruning wave integrates)

- **The drift monolith + fuzz HUNT mode.** (a) One hand-built scene stacking
  every known parity amplifier (transcendental-heavy LogSphere paths,
  Droste-style field discontinuities, material-winner flips at blend seams,
  grazing silhouettes under footprint termination) — the constructed
  worst-case that becomes the calibration ceiling for real content. (b) Flip
  the existing cross-backend fuzzer from gate to MAXIMIZER: search for drift
  score, keep a ranked leaderboard, save each champion as a repro JSON + an
  amplified diff-heatmap PNG. Infrastructure (process-pair isolation,
  program generation, scoring) already exists. Stretch: a live
  `debug.view.drift` (two devices in one process — the `camera-share` stage
  proved zero-copy D3D12→Vulkan sharing) only after the offline hunter
  proves interesting.
- **`sdf.bench storm` — BUILT and MEASURED (2026-07-09): the cliff doesn't
  exist.** 4096 fully-moving instances = 8.7 ms beam+mask — the always-list
  is only a BINNING fallback, never a MASKING one (movers still get per-tile
  mask bits, so the mask-first march stays masked). **The GPU-built-grid
  fork's measure-gate did NOT fire; it stays closed** until a profile with
  ≫4096 simultaneous movers appears. Camera churn is free; the rebuild
  ladder bounds only the GPU side (instrument caveat: a wall-clock fps
  column is the missing piece if the CPU pack/upload ceiling ever matters).
  Full tables: sdf-bench-notes.md §2026-07-09 (c).
- **Edge-case debug views**: `debug.view` mask/cull density tint (per-tile
  kept-instance count — cull correctness by eye, and the natural way to
  watch the storm cliff) + an overshoot detector (clamped-vs-unclamped march
  disagreement per pixel — the liar's-spiral class as a live view).
- **`sdf.gallery` — the torture museum.** A cycling curated tour of every
  known-nasty scene (the liar's spiral, the Droste tunnel, CellJitter's
  neighbor-cell creases, the notch horizon, a deep smooth-chain, the drift
  monolith once it exists), each with a stdout plaque saying what to look
  for and what's settled about it. Doubles as the visual regression walk
  after any march change.

## The many-eyes arc (user-ratified 2026-07-09 — creative freedom is the mandate)

**The point (user's words): creator-mode players should feel free to go a
little wild** — many-eyed creatures, walls of monitors, TV-in-TV halls — so
the screen/camera/viewport caps must be creative budgets, not engine walls.
Three mechanisms, three moves (verified against code 2026-07-09:
`MaxViewports = 5`, `MaxScreenSurfaces = 8`, feed pool = 4):

- **Screen surfaces 8 → 32.** Sampling is per-covered-pixel (screens
  partition the frame — no multiplicative cost); the work is the side table,
  the 32-slot source binding model (derive descriptor pool sizes — the D3D12
  heap-packing discipline), probes, and the `ScreenSlotLedger` growing from
  "narrate a drop" toward "always room for one more." The machine fleet
  needs this regardless (8 surfaces can't light a fleet room's CRTs).
- **Registered feeds → 32–64 with visibility-gated uploads.** Split
  REGISTERED from LIVE-PER-FRAME: registration is cheap state; uploads gate
  on "is any on-camera screen showing this feed" + a per-frame refresh
  budget. Physical webcams stay 1–2 sharing the one session; the 32–64 case
  is procedural/emulator/viewport feeds.
- **Diegetic in-world cameras (viewport sources) → 32 via right-sizing +
  round-robin.** Naive scaling is NOT reasonable (32 full-grid slots ≈
  262 MB mask buffer at the 16384 cap + mostly-dead dispatch z-slices). The
  fix: per-viewport tile allocations sized to the REGION (a 256×192 monitor
  = 192 tiles ≈ 0.4 MB → 32 monitors ≈ 12 MB) + a K-refreshes-per-frame
  budget (monitors persist their last frame — diegetically honest for
  security CRTs) + finally WIRING the dormant GPU zero-copy viewport tier
  (built ahead for exactly this; keep-don't-delete ruling honored).
- **Creator-facing budgets**: creations declare eyes/screens; capacity
  probes grow accordingly; degrade by narrated refresh-rate sharing, never
  hard rejection, so "going wild" slows gracefully instead of erroring.
- **Measure gate**: the storm bench grows a `monitors` rung (N small
  viewports refreshing round-robin — the aggregate of per-viewport fixed
  costs is the honest unknown). Sequence after the adversarial-instrument
  wave; the fleet arc and the diegetic UI (every terminal is a screen
  surface) both inherit this.

## Verification gaps (Post stages — cheap gap-closers)

22. Missing stages, confirmed against the current registry:
    **SmoothIntersection** (appears nowhere in Post), **BendY/BendZ** (only
    BendX+TwistY gated), **Scale**, **RepeatLimited**, **Dilate** (only
    incidental via the scoped-reach margin test), a **chamfer √2 GPU solidity**
    gate (CPU pin only — minor asymmetry). CellJitter's Gaussian flavor is
    consciously argument-covered (Blue is gated); escalate only if a real
    Gaussian consumer appears. Per-stage enclosed-hole caps are individually
    calibrated — never harmonize without re-measuring.

## Live defects (preserved from the retired briefing — the authoritative wording)

23. **The ground-plane horizon notch — grazing-horizon TILE-QUANTIZATION, fix
    deferred.** Reproduced deterministically:
    [examples/scripts/notch-repro.console](examples/scripts/notch-repro.console)
    (drive with the `sdf.cam` pose verb). The far-ground silhouette against the
    sky steps in EXACT 16 px (one-tile) increments near `MaxDistance` instead
    of a smooth perspective curve. What the hunt SETTLED: `MaxSteps`
    exhaustion is REFUTED (every ground pixel terminates footprint-cyan, zero
    red); the "dips behind foreground objects" occluder framing is REFUTED
    (occluder-behind depth diffs are byte-identical; a grounded wall proves no
    gap for the four-bound teleport; a floating slab that DOES fire the
    teleport still leaves the ground unchanged); footprint-adaptive
    termination itself renders smooth depth in controlled scenes. The
    mechanism is the per-tile `marchStart`/beam-cull GRANULARITY leaking into
    the horizon — the fix is a per-pixel-marchStart / sub-tile-beam
    restructure with hero-`world` regression risk, deferred with the standing
    question whether it's worth fixing at all. Any doc still carrying the
    occluder framing is stale — this paragraph is authoritative.
24. **Unbounded Droste (LogSphere) fields render with tile-granular
    breakdown — and the gates are BLIND to it (found 2026-07-09).** Wherever
    rays traverse many shell boundaries the frame shatters into tile-size
    black holes: sky regions checkerboard (any upward ray crosses the
    infinite outer shells), and with twist > 0 the shell surfaces themselves
    shatter (the per-shell spin makes every boundary laterally
    discontinuous). Untwisted shells render clean; the artifacts live where
    shell-crossing density meets tile classification. THE GATES NEVER SAW
    IT: parity passes because both backends produce IDENTICAL artifacts
    (parity ≠ correct), and the solidity gate watches a different scene
    (the tunnel) — the Post `world-log-sphere` render has carried these
    artifacts unnoticed. Evidence: artifacts/rundoc-pass/world-log-sphere-v2
    (no floor, camera in-field: worst), -v3 (twist 0.6: shells shattered),
    -v4 (twist 0: shells clean, sky broken). REGRESSION SUSPECT: the
    four-bound gap teleport landed AFTER D2 and gap-searches the unmasked
    field — a discontinuous field is exactly what could make its landing
    unsound; bisect via the SDF_STRICT_MARCH compile-time fallback before
    assuming it's inherent. Candidate fixes, in order: (a) the teleport/
    relax guard — bake a per-program "discontinuous field" flag (LogSphere,
    CellJitter present) that disables the gap search and over-relaxation for
    that program; (b) `LogSphereLimited` — a shell-count clamp, the
    RepeatLimited analogue, bounding the field to k shells so sky rays
    escape cleanly; (c) framing-envelope guidance in the validator (warn on
    camera-inside-fold). Instruments: gallery exhibit #8 + the overshoot and
    termination views were built for exactly this dissection.
25. **`SDF_WPG_P4G` renders as `p4`** — no mirror classes survive (marked
    KNOWN DEFECT in `sdf-vm.hlsli`); recovery is a redesign of the wallpaper
    turn cocycle, not a tweak. Related deliberate roughness: the WallpaperFold
    gradient in the dual path is a shape-local FD of the fold map (no
    closed-form orthogonal extraction) — parity-safe, exact form optional.
26. **`SurfaceChart` (reserved op id 19)** — parked: the pixel-footprint half
    now exists (footprint-adaptive termination), the atan-determinism half
    does not.

## Cleanup seams (non-urgent, do when next in there)

27. **Shared emission seam** — `CreatorSceneRenderer.EmitShape` /
    `CompanionRenderer.EmitShape` are admitted near-clones kept in lockstep by
    hand, and per-consumer bound-margin constants are re-picked everywhere.
28. **Render-assembly Phase 5** (tracked in
    [sdf-world-render-centralization-plan.md](sdf-world-render-centralization-plan.md),
    the still-living plan-of-record): the live-camera child `IRenderNode`, the
    cross-backend `graph.produce` re-host, and hoisting the stale-bytecode
    guard + DXC compile pattern into a shared `.targets` for other
    shader-shipping projects.
