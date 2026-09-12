# Document composition, boot-authored topology, the validator, serialization

Part of [`puck.world.def.v1`](documents.md). Field names and defaults are
generated (`puck schema`); this file is the decision/derivation prose the
schema cannot state.

## Boot-authored-only topology/timing members

These carry facts decided once at boot — none has a `WorldSection` axis or a
`MutationKind` ordinal, so nothing mutates them in session and no grant
subject names them:

- **`References`** (`WorldReferences.cs`) — `IReadOnlyList<WorldReference>?`,
  each row `(SafeName Name, string? Document, Guid? Owner, SafeName?
  World)` — exactly one of `Document` (a local document path) or
  `Owner`+`World` together (a remote owner-named world; worlds ARE users) is
  authored, never both, never neither. `NeighbourKey` (computed, never
  serialized) folds whichever arm was authored into the one opaque string
  every `IWorldNeighbourResolver` call site resolves against. This is what a
  portal facet's `destination` resolves against: the nexus's own `references`
  section names the three dungeons by document path.
- **`Gravity`** (`WorldGravity.cs`) — an acceleration field, deliberately not
  geometry. `uniform` is an authored acceleration vector. `attractors` names a
  placement plus explicit mass; optional `points` instead names a placement,
  positive `surfaceGravity`, and positive `referenceRadius`, then lowers that
  promise through the actual softened Q48.16 Plummer kernel into a mass. The
  thick validator refuses an unrepresentable lowering before boot, requires
  positive `gravitationalConstant` only when `points` is nonempty, and refuses
  a placement duplicated across the two source spellings. Compilation keeps
  explicit attractors first and point presets second, preserving authored order
  within each. A positive constant activates body-to-body gravity even with no
  static sources; a lone body participates with a zero global answer. A source reads only its placement transform — never its SDF or
  solidity. Read back authored/derived values and last deterministic solver
  work with `world.gravity`; `world.budget` echoes the source and evaluation
  price. Optional `areas` are the bounded-local layer over that SAME answer:
  each rides a placement, declares a priority and explicit `Combine`/`Replace`,
  an inclusive analytic `sphere` or yaw-local `box` bound (scaled by the
  placement), and either a placement-local `directional` vector or constant-
  magnitude inward `radial` acceleration. Static rows use authored pose;
  attached rows use `WorldPlacementAttachment.TryResolve` each tick and
  contribute nothing while their carrier is inactive. The fixed fold begins
  with uniform + global solved gravity, then applies areas ascending by
  `(priority, authored index)`; later equal-priority rows therefore apply later.
  A matching zero Replace, exact cancellation, or radial center is participating
  authored zero-G; outside every area in an areas-only field retains kit fallback.
  Global + uniform + Combine composition saturates componentwise at Q48.16
  extrema rather than wrapping, while a later Replace resets the fold.
  Capped at 64 rows. Arbitrary SDF bounds and per-body masks are the future
  asset/query extension seam, not implicit geometry-derived gravity.
- **`Portals`** (`WorldPortals.cs`) — `WorldPortalsSection(WorldPortalDefaults
  PortalDefaults)`, whose `travel` is `Party` (the traveling seat's whole
  active local-seat party) or `Body` (one seat). It is the world-scope default
  a placement face's own `WorldPlacementPortal` facet falls back to when it
  authors no `Travel`; a null section resolves every facet to `Body`.
- **`Simulation`** (`WorldSimulationDefaults`, `WorldDefinition.cs`) — one
  field, `RateHz` (required in an authored section), the authoritative
  server's fixed step rate in Hz. Read through
  `WorldDefinition.SimulationRateHz` (`Simulation?.RateHz ?? UnauthoredSimulationRateHz` —
  absence runs the shipped rate, 30 Hz; the distinct rate-0 resident world is
  reached only by authoring `rateHz` 0 by name) — never `Simulation` directly, since every consumer
  wants the resolved value, not the presence/absence of
  the section. MUST be 0 or a positive divisor of
  `Puck.Maths.FixedTickConversion.TicksPerSecond` (50400) exactly, refused
  by `WorldDefinitionValidator.ValidateSimulation` otherwise (naming the
  nearest valid rates). Boot-time only, deliberately: nothing in this codebase
  needs a mid-session rate change today, and building that (recompiling every
  cached tick-derived table live) is real additional scope this field does not
  take on. The derived-floor validation (physics floor from body size/speed,
  interactivity floor from input latency, the substep-derived contact clamp
  `contactHertz <= RateHz * n / 8` at substep count `n` — it coincides with
  `RateHz / 4` only at `n` = 2 — and the representable band) is NOT built;
  `n` is a solver parameter, so `WorldSimulationDefaults` is the seam the
  solver landing that introduces it adds the validator to.
- **`Adjacencies`** (`WorldAdjacencies.cs`) — reciprocal rectangular ownership
  boundaries layered over global persisted `Destinations`. Authors declare
  each destination, counterpart, frame, unavailable treatment, and optional
  failure channel; the compiler derives overlap and diagonal corner interest.
  Runtime, transfer, and verification details live in
  [adjacency-and-federation.md](adjacency-and-federation.md).
- **`Metadata`** (`WorldMetadataSection`, `WorldMetadata.cs`) — free-form
  author-facing facts: `title`, `description`, `authors` (each an optional
  UUID `oid`, checked with `WorldObjectId.IsValid`), `tags`, and a
  `custom` bag (`IDictionary<string, JsonElement>`). Nothing in the engine
  reads any member here. `title`/`description` cross to a Presentation-tier
  peer as `WorldProjectedMetadata`; `authors`/`tags`/`custom` never do (see
  [documents.md](documents.md)'s Disclosure section). `custom` follows `WorldDocumentBasis`'s ordinary
  nested-object merge rule with its two carve-outs: a key literally named
  `$drop`/`$replace` refuses at validation, and a JSON `null` under a key in
  a delta deletes the inherited key rather than storing a literal null.

## Worlds have no in-code definition — the shipped `puck.world.json` walkthrough

A boot with no `--world` override loads
`src/Puck.World/Assets/worlds/puck.world.json` — the bare walker world, a delta over
`standard.basis.json`. The basis carries the standards, defined AS STATE — a `transforms` text row
(`identity`/`origin`/`unit`) and a `colors` text row that document values reference by
`state.<row>.<key>` instead of restating literals — the standard `theme` section (an ABSENT theme
resolves to `WorldThemeSection.Absent`, all zeros, which the console panel draws as a 120×16 px
black corner: 1 px glyph cells, no chrome) — plus the infinite SAFETY NET and its debug
texture: one SOLID Plane placement (`groundPlane`) at y = −16, catching anything that falls (never
the level's own floor), its single shape rendered and collided from the same declaration, under the
unbounded `groundTexture` checkerboard (one tile wallpaper-folded with a parity `materialStride`
over `state.colors.groundPrimary`/`groundSecondary`, a NON-SOLID placement — a solid placement
carrying a wallpaper fold refuses by name, so the plane stays the sole collision truth). `placements.policy` is OPTIONAL —
authored only by a world that wants live placement authoring, and then whole (a partial block
refuses at parse naming the missing member); unauthored it derives (`WorldPlacementPolicyDefaults.DeriveFrom`):
no live authoring (zero headroom, no derived faces, no candidate ring, no preview deadline) and a
scale envelope spanning exactly the rows' authored scales, so the basis's static rows need no
policy at all. The pip prototype's shape rotations are
`state.transforms.identity`, its palette is `state.colors.*`, the seat rig's pivot is
`state.transforms.origin` (the `IDocumentSpatialValue` machinery: `DocumentVector2`/`DocumentVector3`/
`DocumentQuaternion` fields accept a literal array OR a text-cell reference, resolved at the
completed-document boundary, reference preserved on canonical write-back). A `prototypes[].document`'s
coordinates are AUTHOR-frame, not world-frame: `puck.creation.v1` authors with +Z the front a shape
faces — a half-turn about Y from the engine's −Z-forward — and `CreationFrame.ToEngine` converts once
at `WorldPrototype.EngineDocument` (authored `[x, y, z]` lands at world `(−x, y, −z)`; pinned by
`CreationAuthorFrameLawTests`, documented in `Puck.World.Authoring`'s README). Every shape modifier
— `rounding`/`chamfer` (edge radii), `dilate`/`onion` (inflation radius, shell thickness), `smooth`
(the blend radius against neighboring shapes), and a `panel`'s `inset`/`depth` — is authored in
creation units and scales with the placement on both emission paths: the static stamper's
`Scale(transform.Scale)` chain op re-multiplies a modifier baked into the primitive's own local
geometry (rounding/chamfer/a panel's inset/depth), while `dilate`/`onion`/`smooth` act on the running
world-space field directly and so are multiplied by the placement scale explicitly before emission;
the animated stamp pool mirrors both rules against the body look's scale. Pinned by
`WorldStampPoolShapeUnitsLawTests`/`CreationStampEmitterUnitsLawTests`. A shape's `flare` (`ShapeFlareDocument`) declares `axis` (0..2), positive
`startScale`, `amount`, `bulge`, `top`, and positive `span`.
Its perpendicular scale is startScale + amount*t + bulge*sin(pi*t), with
t=clamp((top-p[axis])/span,0,1). `shear` is an object with `linear`,
`quadratic`, `cubic`, and distinct `target`/`driver` axes in 0..2.
`bumps` is a bounded list of `{center,radii,push}` Gaussian displacements;
all radii are positive. Both emission paths bound the composed inverse warp
and use a per-shape scope when no outer scope is active.

`blend` accepts Union, SmoothUnion, Subtraction, Intersection, Xor,
SmoothIntersection, SmoothSubtraction, ChamferUnion, ChamferIntersection,
ChamferSubtraction, GrooveUnion, and PipeUnion. The round seam blends use
`smooth` as their single radius. There is no `groove` width field.

`cells { amplitude, frequency, mode, randomness, seed }` adds cellular
relief in the shape's rigid frame. F1 admits randomness in [0,0.46];
F2MinusF1 admits [0,0.20]. Frequency is in (0,8], amplitude in [0,4], and
the derivative factor 1+amplitude*frequency*L must be at most 8
(L=1 for F1; L=2 for F2MinusF1).
A closed primitive and per-shape scope are required: Sweep, grouped/detail shapes, and creations requiring
an outer scope refuse cells. Both emitters scale frequency inversely and
amplitude directly with placement scale. Bounds include amplitude/2
outward relief. Worked seam and cell examples live in
[the authoring README](../../../../src/Puck.World.Authoring/README.md#round-seams-and-cellular-relief).

`erode { lane, from, to, noise? }` selects an integer lane 0..3.
t=saturate((lane-from)/(to-from)); t>=1 skips the shape. Noise is tapered
by 4*t*(1-t), leaving both endpoints exact. Unequal finite endpoints are
required; reversed ranges grow the shape as the lane rises. Unbound lanes
read zero. Erosion is render-only and uses the same scope as the shape's warps.

A creation's `volumes` list declares `flow` media with `position`,
`rotation`, positive `halfExtent`, optional `parent`, and a required
`ramp` of 1..4 ascending `{density,color}` stops. Controls are `axis`,
`width`, `speed`, `seed`, `steps`, `intensity`, `extinction`,
`pulseAmplitude`, `pulseFrequency`, and optional `intensityLane`
(0..3; absent means unit gain). Media composite behind opaque-depth clipping
and have no collider. See the authoring README's volume reference.

A creation also carries its own animation, in three composable parts: a creation-level `drivers` list (≤ 8 —
`{name, signal, cadence, when}`, where `signal` is `planarTravel`/`travel`/`time` (integrating) or
`speed`/`verticalSpeed`/`turnRate` (instantaneous) and `when` is one token or an array of ≤ 4 that
must all hold — a `Puck.Physics.Motion.BodyFacts` name, `always`, the client-derived `moving`/
`still` (eased rendered speed against `WorldGaitDrivers.MovingSpeed`), or a `state.<row>[.<key>]`
reference that holds while the cell's STORED value is nonzero — so a walker gated
`["Grounded", "moving"]` returns its limbs to rest on a stop with no sim fact involved), and
per-shape `swings`/`slides` (≤ 4 each) naming a driver, an `axis` (plus a
`pivot` for a swing), an `amplitude`, a `phase`, and a `wave` (`sine`/`halfSine`/`linear`/`constant` —
`constant` is the POSE BLEND, `amplitude · w`, how a climbing posture comes in on the `HoldingUnwalkable` gate;
`curve:<row>` samples the world's `curves` row by arc fraction, Z as the value — see
[documents-render.md](documents-render.md)), plus a per-shape
`parent` naming an EARLIER shape whose motion carries it (pivots included — the joint chain of a
limb). A driver's `cadence` and a facet's `amplitude`/`phase` may reference a numeric state cell
(`DocumentScalar`, resolved by the same walk as every document reference — a numeric cell is offered
as its decimal spelling), and a driver's `signal` may be `state.<row>[.<key>]`, read at the frame's
tick (a `cycle` row is a shared clock; a cell carrying a `dynamics` trait reads its EASED sample, so
a second-order follower on a rule-written target is a pose blend the simulation owns). In a driver
signal, a gate token, an effector's `state` target, and a look's `motion.poses`, `$body` in the key is
the wearing body's 0-based index (`state.airPose.$body` on a row keyed by body); a body with no such
cell reads 0. A look's `motion.poses` maps a creation frame name to such a reference: the frame holds
while the cell's STORED value is nonzero (a pose is a truth, never an eased sample) and overrides the
cue and replay cursor (a blink a `scheduleState`/`generate` rule pair schedules). The world validator refuses an undeclared curve, a non-numeric signal, gate, or pose
row, and a pose naming no frame (`ValidateCreationBindings`, `ValidateLooks`). A look's `motion.lanes` is an array of up to four optional
`ValueExpression` entries. Indices 0..3 map directly to
`DynamicTransform.Lanes` on every dynamic slot owned by the look.
Interior nulls remain in place; absent and trailing null entries read zero.
Expressions use eased state. Invalid arithmetic, failed reads, or unsupported
operators produce zero. Erosion, weathering, and flow intensity select a lane
explicitly; the engine assigns no semantic names to those indices. One primitive covers a walker's limbs, a climber's, a wheel, a rotor, a
tail, and a bobbing hull. It is presentation-only: `WorldStampPool.PackTransforms` composes it onto
the per-frame dynamic transforms and nothing else reads it, so the SDF program, the colliders, the
solid field, and simulation state are untouched — pinned by `CreationAnimationLawTests`, with the
grammar and the worked rigs in `Puck.World.Authoring`'s README. A fourth, composing AFTER the
drivers and the parent chain: an `effectors` list (≤ 8 — `{name, chain, tip, target, when, weight,
plant}`) corrects the driver-posed skeleton so a named tip reaches a target. `chain` names the bones
root→tip, each descending from the one before through `parent` (a bone's joint is the pivot of its
first swing, its authored `joint` when it swings nothing, else its own position); two bones close
analytically in the plane the driver-posed limb already bends in, three to eight sweep by cyclic
coordinate descent. `target.kind` is `surface` (march the client's ONE shared static-scene
`SdfFieldEvaluator` — the same one the chase camera's clearance sweep reads, held on `WorldClient`
— from the posed tip along an author-frame `direction` up to `reach`, landing `standoff` off the
hit's own normal; a `wallpaper` domain fold anywhere in the world makes that field unbuildable, so
every probe there misses), `body` (another entity's root plus `offset`, in that body's attitude), or
`state` (a text cell spelling a world `[x, y, z]`). `when`/`weight` gate and ease it exactly as a
driver's do, blending the GOAL rather than the pose. `plant` holds the world target where it was
when the named driver's phase entered `window` — a stance foot, a hand on a hold, one mechanism.
Presentation-only on the same terms, pinned by `CreationEffectorLawTests`. A shape's `panel`
(`ShapePanelDocument`) is a second-material inset face region — a plate that reads as two shapes
(an ivory inset in a lilac armor plate, say) for the price of one against `CreationDocument.StampShapeCount`'s
per-stamp budget (a panelled shape charges 2). `inset` erodes a copy of the shape on every local axis (clamped
to the shape's smallest local half-extent, `SdfSolidGeometry.HalfExtent`); `depth` places it along `face`
(a local direction, normalized; null = `+Z`; zero-length refused by name) and composes it with `material` —
positive recesses it (Subtraction, the floor exactly `depth` below the plate's face), negative raises it proud by
exactly `|depth|` (Union) — clamped to `±2·h′` (`h′` = the eroded copy's own half-extent along `face`; deeper is
an enclosed void or a detached slab). Both are creation units, scaled by the placement on both emission paths (the
static stamper's own `Scale(transform.Scale)` chain op reaches them automatically; the animated pool multiplies them
by the placement scale explicitly before resolving); `ShapePanelDocument.Resolve` is the one derivation both emission
paths read. Render-only: the deterministic fixed-point contact
evaluator never reads it, so a panelled solid placement's collider is unchanged. Refused by name on a Plane, a
domain-folded or grouped shape, and a creation that otherwise needs its own field scope (any other shape's
non-Union blend, an engraved text run, or a noise facet) — a panel needs a one-deep field scope of its own and
none of those leave one to nest into. A shape's `trims` (`ShapeTrimDocument[]`, at most 4) paint a second-material
band on the shape's own surface wherever it sits near another, EARLIER-declared shape (`shape`, matched by name) —
a Subtraction cutter riding the host's own `group` is the common case, or a plain plane-like Box authored purely to
mark a seam. Each trim charges 2 against `StampShapeCount` (the host's own copy plus the reference's), opens a
scope of its own AFTER the host's own emission (one per trim, sequential — never nested), and composes: the host's
own copy nudged narrowly closer than its own plain (already-present) instance by `inset` (creation units, `[0,
ShapeTrimDocument.MaxInset]`, default `0.003`) — a real Dilate at a POSITIVE radius, so it loses to the plain
surface everywhere by default and wins only where the Intersection with the reference's own copy — its own pose,
scale grown by `width` (creation units, positive) — does not push the candidate past it, i.e. near the reference;
`max(a, b) >= a` is why a genuine erosion could never win there. Refused by name on a shape carrying `domain` ops
or a non-zero `group`, and on a creation that otherwise needs its own field scope — the same reasons `panel`
refuses them; and refused by name when the REFERENCE carries `domain` ops or a non-zero `group`, since its copy is
re-emitted from its own slot and authored pose alone (a fold's slot carries only its parent's delta frame, a grouped
member rides its group's chain) and would land in the wrong place. Render-only, like `panel`: the deterministic fixed-point contact evaluator never reads it. A shape's
`detail` (bool, null = false) marks it SHADING-ONLY: the SDF VM
skips it entirely during the beam and fine march (so it never carves the silhouette or holds contact geometry —
`CreationStampEmitter.EmitFixed`/`VisitFixedPrimitiveCopies` skip a detail shape outright, unlike Panel/Trims,
which only skip their SECOND copy) and includes it only in the shading evaluation at an already-found hit, where
its own material and its perturbation of the surface normal paint its footprint — a thin subtraction (a seam) or
a small union carrying its own material (a rivet) that stays a crisp mark at any distance instead of the dots a
march-carved groove thinner than the footprint-relative acceptance produces. Refused by name alongside `panel` or
`trims` on the same shape — both compose a SECOND shape instance detail cannot separately describe. A shape's
`secondary` (bool, null = true) is `detail`'s OPPOSITE exclusion set: false drops it from ONLY the soft-shadow and
ambient-occlusion field walks (sdf-world.hlsli's `softShadowVisibility`/`calcAO`/`calcFastAO`, gated on
`sdfSecondaryMarchActive`) — it still marches for the camera, still carves the silhouette, and
`CreationStampEmitter.EmitFixed`/`VisitFixedPrimitiveCopies` still hold it as ordinary contact geometry. No Panel/
Trims restriction, since it packs its own bit on the SAME instruction rather than composing a second shape. A shape's
`curve` (`ShapeCurveDocument`) is a quadratic Bezier — control points `a`/`b`/`c` (each `state.<row>[.<key>]`-bindable),
a radius tapering linearly between `radiusStart`/`radiusEnd` plus a mid-span `bulge`, optionally 1-4 `strands` orbiting
the curve at `strandOffset` with rate `twist` — admitted ONLY on, and required on, `type: sweep` (refused elsewhere,
and a `sweep` with no `curve`, both by name). The primitive's control points and radii already carry creation-unit
dimensions directly, so `scale` must be uniform (refused otherwise, by name) and bakes onto them as one multiplier,
unlike every other primitive's unit-size law (`SdfSolidGeometry`'s dimension table). Refused by name alongside
`panel`/`trims`/`flare`/`shear`/`bumps`/`domain` on the same shape — a sweep is not a closed solid
(`CreationStampEmitter.EmitFixed`/`VisitFixedPrimitiveCopies` skip it outright, like `detail`) and needs none of
their scopes. `bulge`/the radius taper/`strandOffset` are each refused past a ratio to the authored radii
(`SdfProgramBuilder.MaxSweepBulgeRatio`/`MaxSweepTaperRatio`/`MaxSweepStrandOffsetRatio`) — the envelope the shape's
conservative margin is numerically calibrated against (see the sdf-world skill's Sweep row). `strands` above 1 is
render-only, refused for deterministic field contact by name. `body.rig [body]` is the read-back (Immediate, client-local — the values live only on the stamp pool): per driver its phase and eased weight, per effector its weight, whether its latch is holding, and the WORLD point its tip is being asked for (`target=(x, y, z)` or `none`), so a piped run fences twice and asserts a planted foot's target is unchanged while `body.where` moved. A body-rooted part anchor (`WorldStampPool.TryBodyPartAuthoredPose`) reports the COMPOSED pose — drivers, parent chain, effector — so an anchor consumer and the rendered geometry never disagree. Everything else — the
census, simulation (30 Hz), host (windowed, loopback-default — `--listen` binds
QUIC), collision, gravity, channels, the `walk` body-motion program, the `walker` kit
(`defaultSeatKit`), keyboard/gamepad bindings, the chase seat rig, the pip look, and grants — is the
world document's own. A field trait's `color` speaks the same grammar (resolved live at
emit — a state cell write recolors a height field on the next frame, no re-bake, bricks hold only
distances; `world.fields` echoes the authored token).
The world's one placement is the `debugRoom` prototype at origin: a 48 m platform (top at y = −0.5)
carrying one fixture per contact contract, each with a `spawnPoints` row (engine frame) that stands
the body in front of it — `origin`, `ramps`, `stairs`, `wall`, `pit`, `ladder`, `edge` —
reachable by `body.pose spawn:<id> [body]` (the console mirror of a rule's `pose` effect naming a
spawn point; seats still spawn at `origin` by the absent-`seatSpawns` derivation). Author-frame
layout, +Z ahead of the origin spawn and +X the player's LEFT (the half-turn flips X): the scale
ladder (0.5/1/2 m cubes) ahead-left, the slope fan (30/45/55/65/75°, bracketing
`collision.maxSlopeDegrees` 60) ahead-right, the step stairs (rises 0.1/0.25/0.5/1 m) to the left,
the wall with a 1.5 m-clearance and a 0.5 m-clearance overhang at the right edge (a 1 m column,
`pillarUnderhang`, joins the floor to the low overhang's underside so a whole-sphere pull can crawl
floor → column → ceiling without leaving contact), the pit (a `Subtraction` carve) behind, and 1 m compass posts on the platform's axis midpoints — `axisX` red,
`axisZ` blue, a post at the ENGINE-positive end and a flat disc at the negative — with `farPillar`
120 m ahead on the net as the fog/far-distance landmark. What it measures (`body.fly` from each
spawn, `body.where` samples): the 30/45/55° ramps climb at walking speed, the 65/75° faces stop the
body at their foot (`FixedContactPushMath` treats a non-walkable, non-ceiling normal as a WALL —
penetration resolved across `up`, the approach clamp horizontal only — pinned by
`FixedContactPushMathLawTests`); the 0.1 and 0.25 m rises are stepped by the capsule's rounded
bottom and the 0.5 m rise blocks (there is no authored step height); a wall stops at radius + skin
with y, pitch and roll unchanged; the high overhang is inert and the low one blocks; the pit and the
edge both land the body upright on the net. The pit carries an SDF fact worth knowing before
authoring any carve: `max(a, −b)` is exact only inside the subject, so inside the void the field
reads the CARVE BOX'S OWN faces and the subject's carved-away faces as surfaces (a phantom floor the
contact solver grounds on, a phantom lip ~radius wide at the rim) — the carve extends from below the
net to above head height for exactly that reason, and a void that must be exact is built from union
geometry instead. The chase rig's orbit yaw is `state.look.behind`, world-referenced, so a spawn's
`yawDegrees` turns the body, never the camera. Three population creatures live on the platform as
`inhabit` rows with `wander` producers (`spiderDen`, `dragonflyPerch`, `houndRun`), each a different
hold list over the same primitives (see [documents-motion.md](documents-motion.md)): the spider (`spiderKit`) pulls any face in a `[0, 180]` cone,
the dragonfly (`dragonflyKit`, the same `walk` program every kit here shares) holds the air on full
lift with its own row's `thrust` climbing, and keeps its altitude
through the producer's `altitudeGain`, the hound (`houndKit`) walks the ground hold with a four-beat
gait and planted paws; Wren's own hold list is `wall` (pull, `spend` against the `stamina` body slot,
`jump` releases), `ground`, `air`, and her hands and feet are `effectors` on surface targets. Their
homes sit away from the pit and the platform edge — a wander radius that reaches either walks the
creature onto the safety net, which it then paces beneath the platform.
Away from the debugRoom cluster, near the pond, a `drinkMe` bottle placement (colors from the
`drinkMeColors` text row) carries a `region`, and a second, physically separate `eatMe` cake placement
carries its own region straddling the approach back toward the `table` spawn point. Two `Region`
INTERACTIONS — `left: "wren"` (a one-cell carrier row naming body 0, never the aggregate
`$region:<placementId>` occupant count, which fires for ANY body standing in the region regardless of
who), both Edge mode — write body 0's cell of the keyed `scale` state row (`bodies.scaleRow`, envelope
`[0.05, 1]`): `drink-me-shrink` (`right: "drinkMe"`) sets it to 0.15 once on entering the bottle's
region, and `eat-me-restore` (`right: "eatMe"`) sets it back to 1 once on entering the cake's. Edge is
load-bearing, not a style choice: a `Level` trigger re-fires its `setState` every tick the co-occurrence
holds, which for a body simply standing in the region is a document mutation, a stderr journal line, and
a client definition delivery EVERY tick — the same per-tick-write anti-pattern `ActionTriggerMode`'s own
remarks warn a Level-triggered write against. The two regions are never nested or otherwise contained
one inside the other — the shrink region has no reach into where the restore region would fire, and the
restore region has no reach back into the shrink region's own interior — so leaving one always reaches
the other's edge before either could re-trigger itself. `Server.WorldBody.Scale` scales collider
volumes, move speed/turn rate, hold probes/standoff/reach, a hold's own gravity fall/rise and its
vertical-channel envelope (including a medium's idle/settle target), a wall hold's travel speed, and a pull's own rate; the client multiplies the same live cell into the
rendered rig AND the seat chase camera's orbit distance and look-at height
(`Client.WorldFramePresenter.ResolveCamera`), so a shrunk body stays framed instead of shrinking to a
speck on screen. A `tabletop` placement (a solid pedestal table, 1.2 m clearance under its top) carries
the chess board (below); `drinkMe` sits north of it, its region kept clear of the tabletop's own
footprint and of every resting piece's contact radius, so shrinking never jostles the board. `eatMe`
and the `table` spawn point sit south of the tabletop, inside the `eatMe` region already, so an
unshrunk arrival reads `scale=1` from the first tick. `body.where`'s `scale=` echo is the read-back.
Body-vs-body contact, overlap events, the cross-boundary continuum trajectory, the adjacency sweep's
LOCAL side, and the self-collision sweep all read each body's live-scaled collider volumes; a rigid
body's mass, inertia, bounding radius, centre of mass (`com=` on `body.where`), and linear rest
threshold scale with it too (`WorldBody.ScaleRigid`). Only the adjacency sweep's REMOTE side (a
neighbour authority's own entities) still reads an unscaled collider, since no delivered snapshot yet
carries a remote entity's live Scale.

The tabletop's `chessBoard` Grid topology (8x8, 0.2 m cells) carries 32 `piece`-kit rigid bodies.
Its candidate matcher unions four lossless bit-plane differences, rejects changes outside the move
footprint, and directly compares the at most four affected cells. Every pre-move read uses `lastLegal`, never a refused observation.
Local bindings hold attack and geometry intermediates. A legal move commits turn, board, four
castling rights, en passant target, accepted check values, and promotion completion together.
Unfinished promotion does not advance turn. `boardCollisions` prevents duplicate occupants from
being accepted, including identical-code overlaps: it is the sampled on-board
piece count minus occupied squares. A `:between:0:63` count reduction over
`pieceCell` supplies the census without sampling writes; the occupied count is
64 minus the empty-square mask's population. The [state reduction contract](../../../../src/Puck.State/README.md)
owns range bounds, `:where:` composition, live reads, and pricing.
Board-history fingerprints remain diagnostic, not repetition adjudication.
The [chess authoring notes](../../../../src/Puck.World/README.md#the-world-as-data) own these contracts.
They also own the closed -6..7 encoding, the topology shifts, and the bit-extract/deposit
projection of home-piece losses into castling rights. The existing two-rule settle counter
avoids per-moving-tick epoch writes; `$physics:quiescent` is bool-kind, so an integer
conditional expression cannot combine its gates.
Periodic expression masks use `replicationMask(width)` and `repeatBits(pattern, width)`;
[Puck.State](../../../../src/Puck.State/README.md) owns their Int-only domain and refusal contract.
`Puck.State/ExpressionOperators.cs` owns context-free operator spelling, arity,
type signature, and cost; keep payload-bearing literal/state/board lowering specialized.
The compiler folds successful constant subexpressions after full validation,
using the runtime evaluator; never elide a live read or a refusing conditional branch.
Chess's fixed transit masks use hexadecimal base patterns and `byteSwap` for rank reflection.
The board is
rendered as 64 `boardSquareLight`/`boardSquareDark` placements, one per cell, colors from the
`boardColors` text row (the SAME `state.<row>.<key>` palette binding the piece prototypes use for
`pieceSetColors`) — the tabletop otherwise renders as a bare top with no board pattern. A body's walker
capsule (radius 0.35, live-scaled by `Scale` like every other collider — `scale=1` near the table, so
its full radius applies there) still reaches roughly 0.4 m from its own center, well past a single
0.2 m cell — so a body cannot stand ANYWHERE on the board's own 1.6 m footprint (let alone tread among
the pieces) without risking contact; the garden's own proof keeps Wren at a safe standoff beside the
table and moves pieces by console verb (`body.impulse`/`body.pose`), never by having her body touch
one. The `plan` row is a rendered-nothing seam: an addon may write candidate cell keys into it and
`world.tabletop` echoes them back, but no client code paints a highlight from it — chess set style and
board rendering are this lane's; painting `plan` is deliberately left to a future addon.
An explicit path or the shipped default that cannot be loaded refuses the boot by name. The loader is
`src/Puck.World.Schema/WorldDefinitionLoader.cs`.

## Document composition (`basis` and `imports`)

`WorldDefinition.Basis` is the document-composition member: a file naming a `basis` (a file path resolved against
its own directory) is a DELTA over that document — templates/prefabs for similar worlds. `WorldDefinition.Imports`
is the fan-in half beside it: an ORDERED list of `{"document": "<path>", "as": "<alias>"}` entries (`WorldImport`;
each document resolved against the importing file's own directory, exactly like `basis`; `as` is optional), letting
several documents each own one disjoint slice of a world — the garden's
own `src/Puck.World/Assets/worlds/games/{chess,poker,dominoes,billiards,bowling,tictactoe,hexlines}.world.json`, each
imported by `puck.world.json` — rather than forcing every slice through the single-parent basis chain. A keyed
list assembled this way (every import's rows concatenated in import order, then the importing file's own new
rows appended last) never reproduces a monolithic predecessor's own interleaved authoring order — order is not
preserved across a split, only content is; compare two composed trees by canonicalizing each keyed list (sort by
its identity key) before a `JsonNode.DeepEquals`, never by raw array order. The mechanism is
`WorldDocumentBasis` (`Puck.World.Schema/WorldDocumentBasis.cs`), invoked from `WorldDefinitionFileSource` on EVERY
file load (boot, `world.load`/`world.reload`, the replay re-drive's apply-boundary re-read, and both neighbour
resolvers), composing on the raw JSON trees BEFORE the strict parse — a partial template or import fragment cannot
model-parse (required members), so the model only ever sees the finished composition, and the consumed `basis`/
`imports` members are stripped: a LIVE document always carries `Basis == null` and `Imports == null`, the
validator refuses anything else, and every wire egress (replica, replay embed) is self-contained by construction.
Composition order is the basis chain first (each ancestor's own `basis`/`imports` resolved recursively through the
same mechanism), then each import fully resolved and folded left to right in list order, then the file's own body
last — each step an ordinary refine (basis, then the folded import layer, then the file's own body, each one
overriding the layer before it, per the merge rules below).

Merge rules: objects merge member-wise (recursive), omitted inherits, authored `null` clears, a `$type`-changed
union object replaces wholesale, and a row list whose rows all carry the settled identity vocabulary (first of
`id`/`name`/`key`/`index` on every row of BOTH sides — `key` covers a state row's own `cells` and any other list
keyed the same way, which used to replace wholesale under this vocabulary) merges BY KEY in basis order — new keys
append, `{"<key>": …, "$drop": true}` tombstones remove (a stale tombstone refuses by name), a leading
`{"$replace": true}` row replaces wholesale. Any other list replaces wholesale too — notably an overlay's `chords`
(unkeyed rows, no settled identity field of their own): adding one chord means restating that row's whole list.
`$drop`/`$replace` are compose-time vocabulary only; the basis-and-import graph is depth-capped
(`WorldDocumentBasis.MaxChainDepth`, 8, shared across both edge types on any one resolution path) and cycles
refuse by name.

**Collision policy — basis refines, imports collide.** Within the basis chain a derived row REFINES its same-key
basis row (the rule above, unchanged: basis is a single-parent relationship, so there is never an ambiguity about
which side wins). Imports are the opposite: siblings with no priority order between them, so a same-key row, a
same-name object member, or the same list authored by TWO imports is a refusal by name (naming the two colliding
import files) UNLESS the importing file's own body ALSO declares that same path — the explicit resolution, since
the importing file's own body always wins the final override regardless of what the imports disagreed on. Two
imports agreeing on a value (typically because they share a common ancestor somewhere in their own basis/import
graphs — a shared basis diamond) never collide; only a genuine disagreement, which can only arise from each side's
own authored content actually diverging, refuses. See `WorldDocumentBasis.TryMergeImports`'s remarks for the exact
recursive algorithm.

The content pin folds every touched file's raw bytes (`ComputeChainContentHash`, length-delimited, derived-first,
basis chain then each import's own touched bytes in authored order), so editing a template or an imported fragment
moves every dependent document's pin — flat documents keep the undelimited single-file pin unchanged. `world.save`
preserves the derivation of the file it OVERWRITES (`SavePreservingBasis`): it peeks the target's `basis` AND
`imports` at save time (the file is the one truth — nothing caches derivation between load and save), composes the
full basis-plus-imports stack (`WorldDefinitionFileSource.TryComposeStackTree`), computes the merge-inverse diff
against that stack (`WorldDocumentBasis.Diff`), PROVES it by re-merging before writing, and degrades to a flat save
with a named note when it cannot (basis/import unreadable, deleted, or the delta cannot reproduce the document).
Read-backs: `world.status` echoes `basis <path|none>`; `world.imports` prints the whole resolved composition stack
in merge order, each file's path paired with its alias and the top-level keys its own JSON declares; `world.save`'s echo names
the preserved basis/import count or the flat-save note. Storage composes a synced delta too, over `basis` only —
`imports` is a local-directory-load feature today, not yet extended to the cloud sync path:
`IWorldDocumentSource`/`WorldDefinitionFileSource.TryComposeChain` generalize the chain walk onto any byte source,
and `Puck.World.Server`'s `WorldStorageDocumentSource` resolves basis members against a flat cloud
`puck/worlds/basis/{name}` namespace (`WorldOwnedWorldSync.BasisAddressFor`) — the storage neighbour resolver and
`storage.pull` both compose before parsing, exactly like a directory load. `storage.push` pushes the whole chain
(each link its own blob) via `WorldDefinitionFileSource.TryResolveChainFiles`, deduplicated per push call when two
owned worlds share a basis. An entry's `as` composes the fragment under a namespace: every name the fragment declares at a `Declares` site of
`WorldNameRegistry` (`src/Puck.World.Schema/WorldNameRegistry.cs`; the generated table is
`docs/world-name-registry.md`, `puck registry --check`) becomes `<alias>_<name>` and every other registered site in
the fragment — bare name positions, `$` channel segments, `$cell:`/`cell:`/`$expr:` keys, infix and postfix
expressions, `state.<row>` bindings — is rewritten to match (`WorldModuleNamespace`); names the fragment does not
declare stay as written. An alias is a bare identifier (letter or underscore, then letters, digits, underscores).
One fragment composes twice under two aliases; an entry with no `as` composes its names unchanged. A fragment's
names are private by default: its `exports` record (`WorldExports` — `reads`, `actions`, `bindings`, each naming the
fragment's own declarations) is the surface a host may bind, and at compose time (`WorldModuleExports`, same pass)
every other layer — the importing file's body, its basis, each sibling import — refuses by name the first registered
site spelling a private name, quoting the JSON path and the export list that would admit it (the registry field's
`Facet`: effect destinations, transform targets, and a board facet's `occupancy` are `actions`; HUD/overlay/camera
bindings are `bindings`; every other read position is `reads`). The member is stripped from the composed tree, a
document loaded as a world with `exports` on it refuses at validation, and `world.imports` prints each layer's
`exports[...]`. Law suites:
`tests/Puck.World.Tests/ModuleAliasImportLawTests.cs`, `tests/Puck.World.Tests/ModuleExportsLawTests.cs`,
`tests/Puck.World.Schema.Tests/WorldNameRegistryLawTests.cs`, `tests/Puck.World.Schema.Tests/WorldExportsLawTests.cs`.
Law suite: `tests/Puck.World.Tests/DocumentBasisLawTests.cs`,
`StorageCompositionLawTests.cs`.

**`standard.basis.json` — the standard library, not a world.** The engine ships
no content default: the standard bindings, movement channels, chase rig,
icon/badge table, theme, seat modes and markers are AUTHORED, in
`standard.basis.json`, beside the kits, body-motion programs and state rows it
carries. Every shipped world names it as its `basis` (directly, or through
`quilt-base`). Absent now means
absent: `channels` resolves to NONE (a kit whose motion program claims
`MoveAdvance`/`MoveStrafe`/`Turn` refuses by name when nothing declares them),
and `views` resolves to `WorldViewDefaults.Absent`, a placeholder holding the
property non-null between parse and validation which the validator refuses for
any document whose `population.capacity` is nonzero — the same derived refusal
`kits` carries, so a seatless document may still author neither. A world takes
the standard set by naming `standard.basis.json` as its `basis`; a world that
wants only its own `layouts` and other prototype-specific sections over that
basis authors just those sections in its own body — the ordinary basis-refine
rule above, no second file involved.

## The validator — the one thick gate

`WorldDefinitionValidator.cs`: `TryValidate(definition, out reason)` /
`Validate(definition)` run over the ENTIRE composed candidate — at boot, on
every live mutation, on whole-document swap, and on every undo-replay entry —
so builders and appliers never repeat semantic checks. Refusals are an
aggregated STRING list (`"Invalid WorldDefinition: …"`); the one
enum-reasoned section is HUD (`HudValidationException` carrying `HudRefusal`,
folded in as `hud.<Reason>: …`). There is no separate incomplete-document
refusal: an absent required section resolves through its accessor to the
section's own `Absent`/empty placeholder (`Hud`, `Views`, `Kits`, …), and the
validator refuses it BY NAME from whatever derived rule that placeholder
violates — `views` and `kits` refuse for any document whose
`population.capacity` is nonzero, so a seatless document may author neither.

Notable validator constants: `cameras` count ≤ `OffscreenRenderBudget.RegisteredViews` (64),
`MaxSurfaceDimension = 4096`, `MaxLookScale = 16f`. Screen indices are
validated unique, `< SdfProgramBuilder.MaxScreenSurfaces`, and outside the
reserved derived-face band (`WorldPlacementPolicy.DerivedFaceBase` +
`Authoring.DerivedFaceScreens`).

This is what a compiled `.puck` document's `puck compile --validate` runs
before writing JSON — see [documents.md](documents.md)'s "Verifying a change
here".

## Serialization (`WorldDefinitionSerialization.cs`)

- `WorldJsonContext` — System.Text.Json source-generated: camelCase members,
  `UnmappedMemberHandling = Disallow` context-wide, `WriteIndented`.
- **Strict parse, precisely.** An unmapped JSON member on any NESTED row is a
  hard parse failure naming the member and row type. At the document ROOT,
  unknown keys land in `Extensions` and then VALIDATION refuses them unless
  the key starts with `$` or `_` (`DocumentExtensionsPolicy.IsReservedKey`);
  reserved-prefix keys round-trip untouched and are never interpreted.
- **Enums by name** through `StrictEnumConverter<T>` — an unknown or numeric
  enum token is a parse error, never a silent default. (`CommandPhase` from
  `Puck.Commands` registers as a closed generic on the context's `Converters`
  because it cannot carry the attribute.)
- **`Vector2`/`Vector3`/`Quaternion` literals as `[x, y]`/`[x, y, z]`/`[x, y, z, w]`**
  via `Puck.Assets.Documents.Vector2JsonConverter`/`Vector3JsonConverter`/
  `QuaternionJsonConverter` — the one literal spelling every document family
  (world, creation, audio, synth) shares; the object form is refused. World and
  embedded-creation spatial fields additionally use `DocumentVector2`/`DocumentVector3`/
  `DocumentQuaternion`, accepting a `state.<row>[.<key>]` string that names a
  Text cell holding the matching array. `WorldStateDocumentValues` resolves
  those only after the whole world parses, retains the reference for canonical
  write-back, and rehydrates a fresh candidate when a referenced state row is
  mutated — or when a row write introduces a reference (`WorldServer.TryCompose`;
  the `creations` arm resolves a private copy of the submitted row first, since
  its canonicalizer reads bound values) — so a rejected candidate cannot alter
  the live value holder.
- **State-backed identifiers** use `DocumentIdentifier`: an ordinary string
  remains literal, while a `state.<row>[.<key>]` string reads its identifier
  from a Text state cell. This includes binding-group identifiers on chord,
  context, and wheel rows (see [documents-binding.md](documents-binding.md)); creation row/document/shape names; look names and
  creation sources; and kit/look assignment row entries. Put linked names
  behind one cell when they must rename atomically. Resolution precedes
  validation and binding composition, and a live cell write rehydrates,
  re-resolves, recompiles, and validates the complete candidate.
  `WorldStateDocumentValues.TryResolve` is the ONE resolution door and every
  path that turns bytes into a live document runs it — `WorldJsonPayload.
  TryParse` (file loads, both neighbour resolvers, console inline JSON),
  `WorldDefinitionSerialization.Deserialize` (replay embeds, checkpoints,
  the federation replica leaf, a delivered identity document), and
  `WorldProjection.TryToDefinition` (the presentation leaf) — so a DELIVERED
  definition is indistinguishable from a file-loaded one. A new delivery path
  that decodes a document without it is the defect this door exists to
  prevent.
- **`$type`-discriminated unions:** `ActionPredicate`, `ActionEffect`,
  `WorldScreenSource`
  (`none`/`testPattern`/`machine`/`camera`/`view`/`capture`/`console`/`qr`),
  `WorldLookSource`, `WorldSpawnPolicy`, `WorldAnchor`
  (`entity`/`entityLeaf`/`placement`/`group`), `WorldCameraProgramOp`
  (`anchor`/`offset`/`lookAt`/`orbit`/`dynamics`/`clampPitch`/`fov`/`blend`) and
  `WorldCameraSubject` (`reference`/`placement`/`worldPoint`), `WorldSpeakerSource`
  (`none`/`machine`/`tune`/`synth`), `WorldStateRow`
  (`int`/`fixed`/`bool`/`text`). `$type` failures do NOT all surface as
  `JsonException` — `WorldJsonPayload.IsParseFailure` is the complete set;
  route author-supplied JSON through `WorldJsonPayload.TryParse`.
- **Canonical write-back** (`Serialize`/`Save`): UTF-8 no BOM, LF newlines,
  two-space indent, record-declaration member order, invariant shortest
  round-trip numbers, exactly one trailing newline. A load→save of an
  untouched world reproduces the file byte-for-byte — a useful observation,
  never an acceptance gate.
- Embedded Forge documents (creations) bridge through
  `Puck.Assets.Documents.DocumentJsonOptions.Shared` so the inline embed
  carries exactly the vocabulary its canonicalizer hashes. Tunes/patches are
  never embedded — `WorldTune`/`WorldPatch` are name/source/hash reference
  rows resolved off disk by `WorldAssetRowLoader`, the same shape
  `WorldMusicRow` already uses.

**Adding a schema field — the sweep direction.** Adding a top-level SECTION
refuses at boot until every shipped world carries it (through its own
`Absent`/empty placeholder's derived validator rule — see "The validator"
above). Adding a NESTED member does not refuse at parse — it silently defaults, and
usually (not always) refuses at validation — so sweep the shipped worlds
either way. Adding a JSON key with no model member always refuses. Renaming a
member is doubly fatal. One `world.save` re-canonicalizes a file to the
current model.
