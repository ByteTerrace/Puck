# The Tessellation, in the world — a handoff

**Goal.** Realize "The Tessellation" as a living 3D scene in `Puck.World`: a
landscape where the Puck.Maths primitives do the work for real — terrain from
noise, a day that loops on a deterministic clock, settlements and travellers,
weather and tides. This is **creative, greenfield World work.** Verify it by
**running the world and looking at it** — not by Post gates, and not by fussing
over determinism. Those protect the *primitives*; this is what we build *with*
them.

## The rule (non-negotiable)

**Everything here must be something an author can build from inside the editor.**
No hardcoded scene, no behavior baked into the frame source that an author
can't reach. This is Puck.World's **unification contract**: content is authored
and loaded *in-session* — through the `world.*` mutation verbs, the
`editor.*` selection/placement acts, and the `editor.sculpt.*` workbench — then
persisted with `world.save` and reproduced with `world.load`. There are **no
content-authoring CLI flags.**

The split to hold in your head for every arc:

- **The engine provides a *capability*** — a mechanism that reads authored data
  (a day-cycle driver, a terrain op, an animated placement). When an arc needs a
  new capability, it **must ship with the verb/section that exposes it**, or it
  fails the rule.
- **The author provides the *content*** — the values, the placements, the
  sculpted shapes — as document data, set live from the editor, saved into the
  `puck.world.def.v1` file.

If a capability can only be turned on by editing engine code, it is not done.
The test of every feature: *could an author make it from a blank world using
only editor verbs, and would `world.save` capture it?*

## What already exists (don't rebuild it)

- **The authoring machine** — `puck.world.def.v1` is molded through kind-tagged
  `WorldMutation`s over `world.*` verbs (see the README's *Moldable state*),
  each targeting a `WorldSection`, validated as a whole, journaled (undo), and
  saved canonically. Adding a new authorable knob = a new `WorldSection` + its
  validator rule + a `world.<thing>.set` command module (copy `world.motion.set`
  / `world.render.defaults`). This is the "add-a-section" ritual; follow it.
- **Placements & creations** — `editor.place` / `world.placement.set` drop
  instances; `editor.sculpt.*` builds `puck.creation.v1` shapes in the
  workbench; `WorldPlacementAnimator` replays framed creations. Towns,
  windmills, props are authored this way today.
- **Deterministic placement** — the 128-avatar population is distributed with
  Puck.Maths **R1/R2** low-discrepancy (`WorldAvatarCatalog`); reuse it for
  scatter and settlement layout.
- **Lights as data** — `SdfFrame.SunScale` / `SdfFrame.AmbientScale` are
  per-frame fields (`src/Puck.SdfVm/SdfFrame.cs`). `WorldFrameSource` builds the
  `SdfFrame` each tick (`src/Puck.World/Client/WorldFrameSource.cs`, the
  `new SdfFrame(...)` near line 531). **World sets neither today — it is
  statically lit.** The frame source is where a driver *reads the authored
  day-cycle section* and fills these; it is not where the values are hardcoded.
- **Field queries** — `Puck.SdfVm.Queries.SdfFieldEvaluator` answers ground
  height, gradient (gravity), and line-of-sight from the live program — "is this
  ground flat enough to build on / walk on."
- **Content ops in the ISA** (compose freely): `Displace` / `DomainWarp`
  (relief), `SampledRegion` + brick pool (baked heightfield / carves),
  `CellJitter` (integer-hash scatter), `RepeatPolar` (radial repeats), the
  `SymmetryPlane` / wallpaper folds. Placement patterns from
  `MetallicQuasicrystal` / `QuadraticQuasicrystal` (aperiodic) or `SymmetryLattice`
  (E₈). Each is emittable from authored data.

## The arcs (start runnable, build up)

Each arc must **boot, be visible, and be reachable from the editor.** Do them in
order; after each, prove it by authoring it live from a blank world and saving.

1. **Day/night — the beachhead.**
   - *Engine:* a new `dayCycle` `WorldSection` (day length in ticks, enabled,
     sun/ambient curve params). A driver in `WorldFrameSource` reads it and
     drives `SunScale`/`AmbientScale` from a `CyclicRotation` clock. Absent or
     disabled section = today's static light (nothing hardcoded).
   - *Author:* `world.daycycle.set { … }` sets it live; `world.save` captures
     it. **Run it: the world breathes day→night and loops exactly.**

2. **A sun that moves.** Sun *intensity* is per-frame data today; *direction* is
   a shader constant.
   - *Engine:* add a sun-direction field to `SdfFrame` + its shader lane; the
     day-cycle driver rotates it with the clock so shadows sweep.
   - *Author:* the sun's axis/tilt are fields of the same `dayCycle` section —
     the author tunes them; no new verb needed beyond arc 1's.

3. **Terrain.** Real ground shape, authored.
   - *Engine:* fast path — expose `Displace`/`DomainWarp` relief on the ground.
     Rich path — a **FieldNoise fBm** terrain op (the ISA defers "richer noise";
     `CellJitter`'s integer PCG3D hash is the basis) or a `SampledRegion`
     baked heightfield.
   - *Author:* a `terrain` section (or a placeable terrain creation) carrying
     seed / scale / height as data, set from the editor. Use `SdfFieldEvaluator`
     to mark flat-enough (buildable) vs steep ground.

4. **Settlements, scatter & windmills.** Already author-native.
   - *Author:* `editor.place` / `world.placement.set` for towns; `CellJitter` or
     a quasicrystal/E₈ pattern for trees and rocks; a **windmill** is a
     `editor.sculpt.*` creation with `RepeatPolar` blades, animated by a
     recorded timeline frame or a *faster* plane of the same `CyclicRotation`
     clock. Everything commits as one mutation and saves.

5. **Life & weather.**
   - *Engine/author:* travellers are bodies moving between settlements (the
     population already integrates motion — author their routes as data). Tides
     are a second `CyclicRotation` plane nudging an authored water plane's
     height, driven by the same day-cycle driver. One clock, everything loops
     together.

6. **(Far horizon, optional.)** Sharding as *real* multi-worker sim —
   `MonotonicPartitioner` over the "socket transport slots in without moving
   authority" seam. Genuine distributed-sim infrastructure, its own project; the
   author-facing part is a debug overlay colouring regions by owning worker
   (adding a worker moves only the minimal set).

## The one clock

Everything on the day cycle reads from a single `CyclicRotation`, sampled at
different plane speeds `{1, 7, 11, 13}` (the E₈ mandala's planes): plane 1 = the
day (sun, light, tide), a faster plane = windmills. It returns to the start
every period, so the whole world loops with no drift — worth surfacing in-world
(a sundial an author places) so people *see* the maths driving it.

## How to verify

The real test is the unification contract: **author it, save it, reload it.**

```
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 0
```

Then, from the console (or a scripted stdin session, the way the checked-in
example worlds are authored — see `scripts/expo-world.txt`), build the feature
live with `world.*` / `editor.*` verbs, `world.save` it, relaunch `--world
<that file>`, and confirm it came back. If an author can make it and a save
captures it, it's done. **No Post stage, no determinism gate** — greenfield
creative work; if it looks right in the running world, it's right.

## Starting pointers

- `src/Puck.World/README.md` — *Moldable state* (the mutation/section ritual),
  *Creations and placements*, and *The sculpt workbench*. Read these first —
  they are the authoring surface every arc plugs into.
- `src/Puck.World/Client/WorldFrameSource.cs` — where the per-frame `SdfFrame`
  is built; the day-cycle driver reads the authored section here (Arc 1).
- `src/Puck.SdfVm/SdfFrame.cs` — `SunScale`/`AmbientScale` (Arc 1), where a
  sun-direction field goes (Arc 2).
- The `world.motion.set` / `world.render.defaults` command modules — the
  template for a new authorable `world.daycycle.set` (Arc 1).
- `src/Puck.Maths/CyclicRotation.cs` — the day clock.
- `src/Puck.Maths/FieldNoise.cs` (+ `SampleGradient`) — terrain height and slope
  (Arc 3).
- The `sdf-world` skill — the SDF ISA and the content ops (Arcs 2–4).
