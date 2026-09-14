# `render`, `dynamics`, `curves`, pipelines, flock, crowd-scale, `rigid`

Part of [`puck.world.def.v1`](documents.md). Field names, defaults, and ranges
are generated (`puck schema`, or `Assets/worlds/schema/*.schema.json`); this
file is the decision/derivation prose the schema cannot state.

### `render` — the render defaults

`WorldRenderDefaults` (`WorldRenderDefaults.cs`), optional; `Absent` is the
inert section. The boot levers (`shadows`, `shadowCrowdRadius`,
`ambientOcclusion`, `renderScale`, `upscaleSharpness`, the `low`/`medium`/
`high` presets) seed `WorldRenderSettings` once at boot and move only through
their verbs afterwards; `world.save` folds the live levers back into the
section. Three members are read off the LIVE definition every frame instead,
so `world.row.set render {…}` lands on the next frame with no rebuild:
`lighting`/`sky`/`cycle` (`WorldRenderCycleTrack`) and `farDistance`
(`WorldRenderFarDistance.Resolve`). `farDistance` is the depth every camera
march ends at (the fine march's far exit, the beam's cone proofs, the fog and
depth ramps' reach): nullable, absent resolves to the engine's pinned 40
(`SdfFrame.DefaultFarDistance`) so an unauthored world marches exactly as
before the field existed; an authored value must lie in
[`WorldRenderDefaults.MinFarDistance` 1, `MaxFarDistance` 8192], refused by
`ValidateRenderFarDistance` as `render.farDistance <v> must be finite and
within [1, 8192].` Geometry past it is never marched, so an infinite plane
ends on a horizon curve at that depth unless `sky.fogDensity` has absorbed it
first. Read back with `world.row.set render` (the section's read arm) and
`world.budget`, which quotes the far distance with its derived costs: the
reach multiplier over the default, the horizon-ray step count per unit of
camera height against the primary march's 128-step budget, and the fog
remnant `exp(−fogDensity·far)` at the far plane. Renderer contract:
`sdf-world` skill, the FAR DISTANCE row.

`environment` (`WorldRenderEnvironment`, optional) and `tonemap`
(`WorldTonemap` {`none`, `filmic`}, optional) are also read off the LIVE
definition every frame, alongside `lighting`/`sky`/`cycle`. `environment`
carries `softboxes[]` (≤ `SdfEnvironment.MaxSoftboxes` 4 of `direction`,
`size` [w, h], `color`?, `weight`?, `blur`?) and `horizon` ({`low`?, `high`?})
— analytic studio reflections a GGX specular lobe catches; absent (or an
all-default section) contributes exactly 0, byte-identical to a world that
never authored it. `tonemap` absent is `none` — the stylized shaded color,
unchanged; `filmic` applies an ACES-fit filmic curve (no gamma encode — the
shading is already display-referred) to the frame's final color, hit or sky
alike (never a debug view). Read back with `world.lighting`.
Renderer contract: `sdf-world` skill, the `render.environment` row.

`lighting` (`WorldRenderLighting`, optional) carries `lights[]` (at most
`SdfEnvironment.MaxLights` 8, in slot order — a `render.cycle` key moves a
light by its slot and may not add, remove, or retype one) and `curvature`.
Each light's `$type` union:

| `$type` | Carries |
|---|---|
| `directional` | `direction`, `color`, `weight`, `angularRadius`, `shadows` — at most one shadowing light per world |
| `hemisphere` | `color`, `base`, `gradient` |
| `rim` | `color`, `weight`, `power` — a view-dependent silhouette brighten added after the material shade |
| `point` | `position`, `radius`, `color`, `weight`, optional `anchor` — inverse-square falloff with a soft core (`intensity = weight / (1 + (distance / radius)^2)`); lambert diffuse plus the material's GGX specular from the light's own direction, both scaled by ambient occlusion like every non-shadow light; no shadow march, and refused alongside `render.cycle` (its position lane cannot ride the arc interpolation every other light's direction lane takes) |

A point light's `anchor` is a `WorldAnchor.Placement` only (every other anchor
kind is refused by name): its position then rides that placement's — or, with
`shapeId`, one of the placement's creation shapes' — dynamic transform every
frame instead of the authored `position`, so the light follows the placement.
Resolved in `WorldFramePresenter` (`WorldStampPool.TryShapeTransformSlot`),
fresh every produced frame. Renderer contract: `sdf-world` skill, the
environment block row.

`WorldRenderLight.Occluder` uses ordinary light rows to attenuate nearby
surface illumination. It declares `position`, positive `radius`,
`weight`, and an optional entity, entity-part, or placement `anchor`.
Anchors resolve each frame; unavailable anchors give weight zero.
Point and Occluder positions interpolate linearly through render cycles.
`world.lighting` reads back the authored light definitions.

`CreationDocument.PaletteSize = 16` bounds `puck.creation.v1`'s `palette`
array. Each `PaletteEntryDocument` entry: `color` (`#RRGGBB` or a
`state.<row>[.<key>]` binding), `emissive` (null = 0), `specular` (null =
the `SdfMaterial` default 0; the GGX dielectric reflectance at normal
incidence before the `metal` mix), `roughness` (null =
`SdfMaterial.DefaultRoughness`; [0, 1], the GGX roughness-floor curve
parameter — see the sdf-world skill's material row), `sheen` (null = 0;
[0, 1], a fresnel edge-lift strength), `metal` (null = 0; [0, 1], mixes the
reflectance toward `color` and scales the diffuse term by `1 - metal`),
`coat` (null = 0; [0, 1], a fixed-roughness clearcoat GGX lobe). Each of
those four unit-range lanes is refused BY NAME at the canonicalizer when
non-finite or outside [0, 1] (`PaletteAdmissionLawTests`) — `SdfMaterial`'s
own `RequireUnitRange` would otherwise throw at stamp emission. A document
still spelling `shininess` is an unmapped member and is refused. A palette
entry's `weathering` declares `edge`, `lines`, `settle`, `reach`,
`seed`, `scale`, `floor`, `lane`, up to two ascending `under`
threshold/surface entries, and an optional `deposit` surface.
Each surface supplies `color`, `roughness`, and `metal`.
`inset` declares `origin`, `rotation`, `depth`, `ior`, and
`paint`: 1..4 ascending radial `{radius,color}` stops with `softness`,
`modulationAmplitude`, `modulationFrequency`, and `seed`.
Inset coordinates use the winning dynamic frame, or world space for a
static hit. `wrap`, `soften`, and `bounce` retain their shading roles.

### `dynamics` — the personality table

`DynamicsRow` (`Puck.State/DynamicsRow.cs`): named rows of `{name, f, zeta, r}` —
a t3ssel8r-style pole-matched second-order response every follower consumer
names by `name` rather than authoring inline, so one row can drive a look's
root/part followers, a camera boom, a kit's planar shaping, and a state cell's
eased read at once. `f` (Hz, positive, finite, ≤ `WorldDynamics.MaxFrequencyHz`
100) is the natural frequency; `zeta` (≥ 0, ≤ `WorldDynamics.MaxDamping` 16) is
the damping ratio — `0` rings forever, `<1` overshoots and rings down, `1` is
critically damped, `>1` is overdamped; `r` (`WorldDynamics.MinResponse`..
`WorldDynamics.MaxResponse`, ∓4) is the initial response — `0` eases in from
rest, `>0` reacts immediately to the target's own motion, `>1` overshoots the
target's motion before settling, `<0` anticipates. The section is OPTIONAL and
every reference to a row is nullable, so an unauthored world is unchanged.
Every consumer resolves a name through `WorldDefinitionRows.FindDynamics` and
refuses a dangling one by name (`'{name}' names no dynamics row.`); removing a
still-referenced row is refused the same way, naming the referrer. Authored
with `world.row.set dynamics {"name":"chase","f":0.9549,"zeta":1,"r":1}` /
`world.row.remove dynamics <name>`; read back with `world.dynamics`, which
reports every row's authored triple, the derived decay/oscillation/k3
constants through the SAME fixed-point derivation
(`Puck.Maths.SecondOrderDynamics.Create`) the simulation compiles from, and a
live reference count across cameras, looks, look parts, kits, and state.
Consumers: a look's `motion.dynamics`/`motion.partDynamics` (root and per-part
followers), a camera program's `dynamics` op (the boom ease), a kit shaping
row's `dynamics` facet (planar velocity shaping — exactly one of `dynamics`
or `along` on that row, never both, never neither — see
[documents-motion.md](documents-motion.md)), and a
`state` row/cell's `dynamics` trait (the eased read — see
[documents-state.md](documents-state.md)).

### `curves` — the curvature-first spline table

`WorldCurveRow` (`WorldCurves.cs`): named rows of `{name, closed, knots}`. An
author declares intent per knot — position, tangent direction, signed
curvature — and `Compiled` derives the machinery (the cubic-Bézier tangent
lengths that reproduce it exactly, Steven Wittens' curvature-continuous
construction; see the `maths-usage` skill for `Puck.Maths.CurvatureSpline`)
rather than authoring control points directly — no control-point document
shape ever ships. A knot's `position` is a `DocumentVector3` — X/Z the planar
curvature-solve inputs, Y an elevation lift carried outside the curvature/
arc-length solve as a linear grade; `tangentYaw` (radians, unit tangent
`(cos, sin)` in XZ) and `curvature` (signed, within
±`CurvatureSpline.MaxCurvature`, under the `cross2(a, b) = a.X·b.Z − a.Z·b.X`
convention) complete it — the SAME facing convention the engine's own facing
path uses, pinned once here so the camera `path` op and the sim curve-follow
target both read it rather than re-deriving one. An open curve needs at least
two knots, a closed one at least three, and at most `WorldCurves.MaxKnots`
(64); the section holds at most `WorldCurves.MaxRows` (64) rows. The section
is OPTIONAL and every reference to a row is nullable, so an unauthored world
is unchanged. The validator's per-field checks (coordinate/curvature range,
knot counts) catch authoring mistakes; the exact solve itself — chord length,
tangent/curvature consistency, an unreachable curvature, an interior cusp, Q32
carrier overflow — is the LAST gate, run once by compiling the row
(`WorldCurveRow.Compiled`, cached per row instance, the
`DynamicsRow.Compiled` precedent) rather than duplicated in the
validator. Every reference resolves through `WorldDefinitionRows.FindCurve`
and refuses a dangling name; removing a still-referenced row is refused naming
the referrer (`WorldDefinitionRows.EnumerateCurveReferences`). Authored with
`world.row.set curves {"name":"dolly","knots":[…]}` /
`world.row.remove curves <name>`; read back with `world.curves`, which reports
every row's authored shape, its compiled segment count and total arc length
through the SAME derivation the consumers read, and a live reference count
split by `cameras`/`follows`. Consumers: a camera program's `path` op (dollies
the eye/pivot along the curve by arc-length fraction; see views.md) and a
body-motion program's `curve` target source
(`Puck.Physics.Motion.BodyTargetSource.CurveFollow`) — a fixed-point, per-tick
arc-length follower feeding the SAME planar target-consuming op vocabulary a
`designated`/`sensed` target does.

### `views.pipelines` — shader-pipeline instances

`WorldViewPipeline` (`WorldViews.cs`) carries `{name, source, camera,
timeScale}`. Source is a pipeline JSON document or a one-off shader and
resolves relative to the world document. The pipeline's own shader paths
resolve relative to its document. `WorldViewSlot.pipeline` names the instance;
a slot cannot name both a camera and a pipeline. The row's camera supplies
optional shader camera inputs, with zero FOV denoting no paired camera.
`timeScale` seeds presentation time. `views.shaderToolchain` optionally names
the compiler directory; otherwise executable lookup uses the process path.

Rows use `world.row.set views.pipelines` and `world.row.remove views.pipelines`
through the same closed mutation vocabulary as `pipeline.load`. The host
reconciles only accepted document state. The runtime owns resources, history,
background compilation and frame-boundary installation; none belongs in the
schema. Use [the pipeline world](../../../../src/Puck.World/Assets/worlds/pipeline.world.json)
for the live three-pass editing workflow. The
[shader README](../../../../src/Puck.Shaders/README.md#shader-pipelines-and-live-development)
owns the GPU pipeline document contract.

### Kit producer `flock` — bounded local perception

`ProduceFlockIntent` requires `producers.<name>.flock` on the assigned kit:
range, separation radius, candidate budget, maximum retained neighbors,
perception interval in seconds, tangent/volume space, cone/line-of-sight policy,
and separation/alignment/cohesion/goal/inertia weights. It is mutually exclusive
with `ProduceSteeringIntent`/`FaceSensorTarget`. Target sources remain optional; when
present they use the ordinary sensing/target-register vocabulary.

The population freezes position/orientation/travel before any body advances.
Cadence limits neighbor and sensed-target updates, not designation/route/frame
blending. A sensed target shares the neighbor candidate budget over the larger
range and retains its last observed position between updates. Sampling is bounded
even in a coincident crowd; results are nearest within the inspected sample,
not globally nearest.
Use range-scaled grids independent of unrelated profiles; rebuild only levels
needed by this step's samples. Preserve caches across unchanged bindings and
invalidate them when the profile or target source changes. Checkpoint/hash the
unclamped neighbor contribution, timing residue, local sample ordinal, observed
target position/generation, and occupant generation. An optional `movementDomain` names a volume/medium domain
whose root-centered agentRadius encloses the kit's offset collider volumes.
Integrated locomotion is continuously checked; refused steps stop momentum.
This ends with the producer and does not cancel later impulse/contact/tether or
teleport operations, find an escape route, or implement a surface constraint.
The steering kernel itself confers no friendship or collision safety. Read it back
with `world.flock`; `world.budget` repeats the structural cost. Author changes
still use the one document door, not a separate flock mutation API.

Optional `cohesionAffinity`/`alignmentAffinity` use the ordinary Fixed postfix
expression evaluator. Left is the observer, right the retained neighbor; only
state-backed operands are admitted because body/channel/navigation reads
change during the movement pass. A belief row keyed by observer (`$left`) is
the ordinary way to feed one — see "Keyed belief rows and evidence dedup"
in [documents-rules.md](documents-rules.md). Missing expressions read one, results clamp
to [0,1], arithmetic failure reads zero with a counter. These are relative
weighted-mean inputs, not separation filters or absolute term strength. They
refresh with perception cadence, not on every belief-row update. Rebind compiled
state handles on every declaration installation; key
bindings by authored kit/producer names, not object identity (wire restore
deserializes fresh objects). Cached neighbor contributions already carry the
result through checkpoint/hash. Charge both programs and all indirect scans for
every retained neighbor in the worst-case simultaneous population refresh,
under the shared rule work ceiling — an O(population²) Distance interaction
firing an ordinary state-write effect on every pair is priced at the engine's
real per-write cost, not a bespoke cheap one; keep that shape to a linear
forEach/flock-affinity reach instead. See the
[authoring example](../../../../src/Puck.World.Schema/README.md#keyed-belief-rows-and-flock-affinities).

### Crowd scale policies

`WorldBodiesLimits.CapacityCeiling` is 4096. `kits.rows[].autonomy` independently
batches non-human `motionSeconds` and producer `steeringSeconds` (0..1; zero is
full authority rate), with deterministic per-body phasing and exact elapsed
engine-tick batches. Live/human/tape/pending-input bodies stay full-rate. Refuse
positive motion cadence with `bodyContact: solid`; deferred bodies cannot claim
per-tick dynamic contact. Large flocks use overlap contact.

`collision.events` bounds body-pair proximity events separately:
`candidateBudget` per body, `maxPairsPerBody` retained degree, and `beginBudget`
per tick. Existing pairs win continuity priority. `maxPairsPerBody: 0` disables
pair events while ordinary world contact remains live. Preserve these policies,
cadence phase, cached steering, and overlap latches through checkpoint/hash.

`collision.bodyContacts` separately bounds physical depenetration between
`solid` kits: at most 32 inspected candidates and 16 resolved pairs per body
(defaults 16/8). Dense saturation omits later stable-index pairs. Do not couple
these budgets to `collision.events`; sensing and physical correction are
independent authored costs. `rigidSubstepCeiling` (default 8, maximum 32)
bounds a rigid body's own per-tick continuous-collision substep count against
an authored `rigidSubstepTravelFraction` (default 0.5) — the count itself is
derived per body per tick from speed and collider size, never authored
directly. `rigidRestLinearSpeed`/`rigidRestAngularSpeed`/`rigidRestHoldSeconds`
(defaults 0.05/0.1/0.25) are the thresholds and hold window a grounded rigid
body's `Resting` fact latches against. `rigidManifoldIterations` (default 4,
maximum 16) bounds the sequential-impulse passes a box or capsule's own ground
support manifold (up to four box corners, or two capsule cap points, fewer
once tilted enough — `FixedRigidWitness.SupportManifold`) resolves over each
substep, so a normal impulse off-centre carries torque instead of only
friction. `rigidPairRestitutionSpeed` (default 0.05) floors a rigid-vs-rigid
pair's restitution at zero below that closing speed, so two resting bodies do
not micro-bounce apart every tick they are found touching.
`rigidPairIterationCeiling`/`rigidPairIterationBudget` (defaults 4/64) bound
how many EXTRA full broadphase-plus-narrowphase sweeps `ResolveDynamicContacts`
runs after its first pass in one tick — the count actually run is derived DOWN
from the ceiling by the budget divided by how many pairs the first pass routed
through the rigid impulse path, so an impulse chain (a rack break, a falling
domino line) can cross more than one pair-hop within the same tick instead of
propagating one body-hop per tick.

### A kit's `rigid` facet

`mass`, `restitution`, `friction`, `rollingFriction`,
`linearDamping`, `angularDamping` hand its bodies to the rigid solver
instead of a locomotion program — see
[the server reference](../../../../src/Puck.World.Server/README.md#rigid-dynamics-worldbodyrigidcs-worldpopulationrigidcs).
`mass` is required and positive; the other four are non-negative per-second
decay rates, never per-tick fractions. Requires `collider` (sphere, capsule,
or box — never `fromCreation`) and `bodyContact: solid`.
