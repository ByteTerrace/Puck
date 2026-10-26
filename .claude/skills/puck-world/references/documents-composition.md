# Document composition, boot-authored topology, the validator, serialization

Part of [`puck.world.definition.v1`](documents.md). Field names and defaults are
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
  portal facet's `destination` resolves against (the debug world's garden painting
  names the garden this way).
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
  authors no `Travel`; a null section resolves every facet to `Body`. A door
  fires when a body's origin passes through its glass, within the opening
  (`WorldFaceAperture.Box`'s slab is `[-Depth, 0]` behind the glass, entered
  only from the front), never on a band in front of it; while a body stands in
  the opening, pushes out of the door from this world's geometry behind the
  glass are taken back (`WorldPortalHoleContactField`), so a door on the ground
  drops a body that walks onto it
  ([decision](../../../../docs/decisions/engine-design.md#a-portal-is-a-plane-a-body-passes-through)).
  A door may lie at any orientation (a shape turned a quarter about +X lays it flat;
  `WorldFaceCatalog` snaps such a quarter-turn frame to exact axes); a mapped
  pair is refused at the crossing, by name, unless it maps world up onto itself
  (`WorldFrameIsometry.PreservesWorldUp`: two walls, or a floor and a ceiling).
  A destination whose reference names the world's own document resolves to the
  running world itself, and a mapped crossing there carries the body in place
  (`WorldInstanceHost.ApplyInPlaceCrossing` → `WorldBody.CrossInPlace`, taped
  as a `Crossing` replay entry) instead of transferring it. It is continuous
  motion, never a teleport: every world-space point and direction the body
  holds, its previous position included, maps through the pair, its momentum,
  followers and facts carry on, and the snapshot's continuity is `Carry` with
  the rigid motion (`WorldPoseCarry`). Presentation carries what it latched the
  same way: the mirrors' interpolation endpoints, `IWorldStampSource.Carries`
  for the stamp pool's followers, gait drivers and effectors and for the
  catalog avatars, while the chase boom is translation-invariant and turns
  through `CrossView` for a turning pair. The debug world's endless fall
  (`worlds/debug/paintings.puck`: a painting in the floor under its twin in the
  ceiling) and its pair of paintings facing each other are the live examples.
  A window's session source may author `ripple: true` (refused off
  `projection: window`): the opening shimmers and rings from every body that
  touches or passes through it, presentation only (`WorldOpeningRipples`
  contacts, `sdfOpeningRipple` in `shade/sdf-environment.hlsli`). A wall
  painting is a thin rim shape with its centre carved out, face 0, because the
  glass fills the face shape a centimetre short of its faces, and the wall
  behind it holds nothing back while a body stands in the opening.
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

## Worlds have no in-code definition

Every world is an authored document; the engine ships no content default. A
boot with no `--world` refuses and names what it needs; `--world worlds/debug`
boots the debug world's package (`worlds/debug/manifest.json` names its entry,
`debug.puck`), and a path that cannot be loaded refuses the boot by name
(`src/Puck.World/PuckWorldLoader.cs`, `src/Puck.World.Schema/WorldPackageManifest.cs`). The island
(`experimental/src/Puck.World/Assets/worlds/puck.world.json`), its district
modules and the game packages are quarantined prior art: read them for worked
examples, never boot or repair them.

Engine facts those worlds exercised, which hold for any world:

- **Placement primitives.** A placement `deal` facet deals one child placement
  per cell of a keyed row (`<template>/<key>`, laid out by the template's
  `distribution` region; `PlacementDealLawTests`). An observation writes rows of
  any cell kind and nothing else (`WorldObservationLawTests`). A `respond[].when`
  is a closed union of a lattice-field condition and a state-cell condition
  (`PlacementResponseLawTests`). An `inhabit` facet's `count` may name an Int
  cell: such a placement starts every structural install at zero live bodies,
  and `WorldPopulation.ReconcileInhabitCounts`, called on every state-value
  mutation apply, admits and retires bodies the same tick a rule writes the
  cell, clamped to the peer capacity and the distribution's sample count and
  never retiring a seat-driven body (`InhabitCountLawTests`).
- **Identity facts and machine content.** An owned identity carries a keyed
  Int `facts` row mirrored into the reserved body-scope `identity` row, written
  by the `setIdentityFact` effect and echoed by `identity.facts`
  (`IdentityFactsLawTests`). A `machine` screen's content may be a
  `puck.cartridge.v1` document compiled at bind by the brick's forge
  (`MachineCartridgeLawTests`).
- **Derived limits.** Sixteen drive-reach ordinals across channels and target
  registers; one physical field topology per document; and the rule budget
  `RuleCapacity.MaxWorkUnitsPerTick` (four million work units), where a
  body-pair interaction costs capacity squared.
- **Placement policy.** `placements.policy` is optional, and whole when
  authored (a partial block refuses at parse naming the missing member).
  Unauthored it derives (`WorldPlacementPolicyDefaults.DeriveFrom`): zero
  placement and screen headroom, no derived faces, no candidate ring, no
  preview deadline, and a scale envelope spanning exactly the rows' authored
  scales. Console placement mutations can grow the renderer beyond its initial
  reserve without an authored policy.
- **Author frame.** A `prototypes[].document`'s coordinates are author-frame:
  `puck.creation.v1` authors with +Z the front a shape faces, a half-turn about
  Y from the engine's −Z-forward, and `CreationFrame.ToEngine` converts once at
  `WorldPrototype.EngineDocument` (authored `[x, y, z]` lands at world
  `(−x, y, −z)`; `CreationAuthorFrameLawTests`).
- **Creation shapes and animation** (modifiers, `flare`/`shear`/`bumps`,
  blends, `cells`, `erode`, `volumes`, `panel`/`trims`/`detail`, sweeps, and
  the `drivers`/`swings`/`effectors` animation `WorldCreationInstances.PackTransforms`
  composes, read back with `body.rig`) are `sdf-authoring`'s:
  [shapes](../../sdf-authoring/references/shapes.md),
  [composition](../../sdf-authoring/references/composition.md),
  [rig](../../sdf-authoring/references/rig.md) and
  [surface](../../sdf-authoring/references/surface.md). All of it is
  presentation-only except the shapes' contact geometry.
- **Body scale.** `Server.WorldBody.Scale` (the body's own `state.body` Counter
  register that `bodies.scale` names, with a `range` envelope; the debug world's
  is `size`, `worlds/debug/size.puck`, imported by every world a body can walk
  into so the size crosses with the body) scales collider volumes, move speed
  and turn rate, hold probes, standoff and reach, a hold's own gravity and
  vertical envelope, and a pull's rate; a scaled portal pair's in-place crossing
  writes the register (`WorldServer.TryApplyInPlaceCrossing`), and the client
  multiplies the same register into the rendered rig and
  the seat chase camera's orbit distance and look-at height
  (`Client.WorldFramePresenter.ResolveCamera`). A rigid body's mass, inertia,
  bounding radius, centre of mass and rest threshold scale with it
  (`WorldBody.ScaleRigid`); `body.where`'s `scale=` and `com=` read it back.
  Only the adjacency sweep's remote side still reads an unscaled collider.
- **Contact.** `FixedContactPushMath` treats a non-walkable, non-ceiling
  normal as a wall: penetration resolves across `up` and the approach clamps
  horizontally. There is no authored step height; a capsule climbs only the
  rises its rounded bottom rides. A `Subtraction` carve (`max(a, −b)`) is exact
  only inside the subject: inside the void the field reads the carve box's own
  faces and the subject's carved faces as surfaces, which the contact solver
  grounds on, so a carve that must be exact is built from union geometry.
- **Interactions that write state.** A `Region` interaction that writes a cell
  is `Edge` mode: a `Level` trigger re-fires its `setState` every tick the
  co-occurrence holds, a mutation, a journal line and a client delivery per
  tick. `body.pose spawn:<id> [body]` is the console mirror of a rule's `pose`
  effect naming a spawn point.
- **Expressions.** `src/Puck.State/Expressions/ExpressionOperators.cs` owns
  context-free operator spelling, arity, type signature and cost; keep
  payload-bearing literal, state and board lowering specialized. The compiler
  folds successful constant subexpressions after full validation using the
  runtime evaluator, and never elides a live read or a refusing conditional
  branch. [Reductions](../../../../docs/reference/state/expressions.md#reductions)
  and [periodic bit masks](../../../../docs/reference/state/expressions.md#periodic-bit-masks)
  (`replicationMask`, `repeatBits`) are documented with `Puck.State`.

## Authoring a creation in code — the sculpting library

A creation may be authored in code instead of by hand, through the sculpting
library in `Puck.World.Authoring/Sculpting` (`CreationBuilder`/`StateHoisting`/
`SculptPatch`/`ICreationSculpt`/`CreationSculptRegistry`) — see that project's
README for the primitives; the shipped registry carries no sculpts, so one is
registered by a composition root or a test. The document
stays the source of truth either way: a sculpt only ever produces a patch a
caller applies, validates, and (for the live world) composes into ordinary
`WorldMutation` rows through the `world.row.set`/`.remove` section table under
the issuing principal (`creation.sculpt <name>`) or writes to disk offline
(`puck creation sculpt <name> --world <path>`) — it never bypasses
whole-document revalidation or the per-section `Mutate` grant check.

## Document composition (`basis` and `imports`)

`WorldDefinition.Basis` is the document-composition member: a file naming a `basis` (a file path resolved against
its own directory) is a DELTA over that document — templates/prefabs for similar worlds. `WorldDefinition.Imports`
is the fan-in half beside it: an ORDERED list of `{"document": "<path>", "as": "<alias>"}` entries (`WorldImport`;
each document resolved against the importing file's own directory, exactly like `basis`; `as` is optional), letting
several documents each own one disjoint slice of a world (the debug world imports `theme` and `controls`) rather
than forcing every slice through the single-parent basis chain. In `.puck`, `import "name"` always names, never
spells a file (`import "name.puck"` is PUCK121), and the named file decides what it brings
(`WorldDocumentVocabulary.TryResolveImport`): a module library, a source emitting no document
(`WorldSourceDeclaration.EmitsDocument` false), brings its `let`s and templates into the compile and lowers to
nothing, so a library such as `shapes.puck` hands its rows over through templates the importing file expands; any
other source or `.world.json` lowers to an `imports` entry. A library that imports a document is refused by name
(PUCK017), since that document would compose into nothing. A keyed
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
keyed the same way — or, for grant rows, the `grantee` + `capability` + `subject` triple taken together, so each
imported module can author its own grants) merges BY KEY in basis order — new keys
append, `{"<key>": …, "$drop": true}` tombstones remove (a stale tombstone refuses by name), a leading
`{"$replace": true}` row replaces wholesale. Any other list replaces wholesale too — notably an overlay's `chords`
(unkeyed rows, no settled identity field of their own): adding one chord means restating that row's whole list.
A present list is what replaces, empty included: `.puck` lowers a list only where its source writes one, so
`placements { policy { … } }` leaves `rows` absent and keeps the rows beneath it, and `rows []` clears them.
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
own authored content actually diverging, refuses. An empty list an import authors holds no row to collide with, so
it merges with a sibling's rows rather than refusing (the debug world's `devices.puck` authors `hud { panels [] }`
beside the addons' panels). See `WorldDocumentBasis.TryMergeImports`'s remarks for the exact recursive algorithm.

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
`docs/world-name-registry.md`, `puck registry --check`) becomes the generated name `<alias>$<name>` and every other
registered site in the fragment — bare name positions, `$` channel segments, `$cell:`/`cell:`/`$expr:` keys, infix and
postfix expressions, `state.<row>` bindings — is rewritten to match (`WorldModuleNamespace`); names the fragment does
not declare stay as written. Placements and prototypes are namespaced too, each in its own namespace; spawn points,
kits, looks and cameras stay flat. A host writes the document spelling (`world.state kart$lap`, a restated court
`{"id": "dive$diveCourt", ...}`); a `.puck` source writes `alias.name` (`puck-dsl`). An alias is a bare identifier (letter or underscore, then letters, digits, underscores).
One fragment composes twice under two aliases; an entry with no `as` composes its names unchanged. A fragment's
names are private by default: its `exports` record (`WorldExports` — `reads`, `actions`, `bindings`, each naming the
fragment's own declarations) is the surface a host may bind, and at compose time (`WorldModuleExports`, same pass)
every other layer — the importing file's body, its basis, each sibling import — refuses by name the first registered
site spelling a private name, quoting the JSON path and the export list that would admit it (the registry field's
`Facet`: effect destinations, transform targets, and a board facet's `occupancy` are `actions`; HUD/overlay/camera
bindings are `bindings`; every other read position is `reads`). The member is stripped from the composed tree, a
document loaded as a world with `exports` on it refuses at validation, and `world.imports` prints each layer's
`exports[...]`. Law suites: `tests/Puck.World.Games.Tests/ModuleExportsLawTests.cs`,
`tests/Puck.World.Schema.Tests/WorldNameRegistryLawTests.cs`, `tests/Puck.World.Schema.Tests/WorldExportsLawTests.cs`.
Law suite: `tests/Puck.World.Server.Tests/DocumentBasisLawTests.cs`,
`StorageCompositionLawTests.cs`.

**Shared content is authored, never an engine default.** The live debug world
and garden take the Moth avatar (`avatars/moth.puck`: body, channels, bindings,
kit, motion programs, camera, seat camera feel) as their basis and import
`skin.puck` (the debug world's skin; a world that imports none draws the
engine's neutral theme) and `controls.puck` (cursor, key hints, icons,
binding bar, play wheel) as documents, as the Moth imports `quality.puck`;
`skies.puck`, `shapes.puck` and `studio.puck` beside them are module libraries
whose templates the importing file expands. The quarantined
`standard.puck` library and the island built on it are prior art. Absent means
absent: `channels` resolves to NONE (a kit whose motion program claims
`MoveAdvance`/`MoveStrafe`/`Turn` refuses by name when nothing declares them),
and `views` resolves to `WorldViewDefaults.Absent`, a placeholder holding the
property non-null between parse and validation which the validator refuses for
any document whose `bodies.capacity` is nonzero — the same derived refusal
`kits` carries, so a seatless document may still author neither. A world that
wants only its own `layouts` and other sections over its basis authors just
those sections in its own body (the ordinary basis-refine rule above).

## The validator — the one thick gate

`WorldDefinitionValidator.cs`: `TryValidate(definition, out reason)` /
`Validate(definition)` run over the ENTIRE composed candidate — at boot, on
every live mutation, on whole-document swap, and on every undo-replay entry —
so builders and appliers never repeat semantic checks. Refusals are an
aggregated STRING list (`"Invalid WorldDefinition: …"`); the one
enum-reasoned section is HUD (`HudRowValidation.Refuse` appends a `HudRefusal`
reason as `hud.<Reason>: …`). There is no separate incomplete-document
refusal: an absent required section resolves through its accessor to the
section's own `Absent`/empty placeholder (`Hud`, `Views`, `Kits`, …), and the
validator refuses it BY NAME from whatever derived rule that placeholder
violates — `views` and `kits` refuse for any document whose
`bodies.capacity` is nonzero, so a seatless document may author neither.

Notable validator constants:
`MaxSurfaceDimension = 4096`, `MaxLookScale = 16f`. Screen indices are
validated unique, `< SdfProgramBuilder.MaxScreenSurfaces`, and leave room after the highest authored index for Authoring.DerivedFaceScreens. The derived band follows that authored index rather than reserving a fixed upper half.

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
  (`none`/`machine`/`producer`/`view`/`session`/`text`/`probe`; a `producer` names a registered image
  producer by id with its own settings object, validated by `WorldImageProducerVocabulary`),
  `WorldLookSource` (`catalog`/`creation`), `WorldAnchor`
  (`entity`/`entityPart`/`placement`/`group`/`seat`/`recentSpeaker`), `WorldCameraProgramOp`
  (`anchor`/`offset`/`lookAt`/`orbit`/`path`/`dynamics`/`clampPitch`/`fieldOfView`/`blend`/`selectProgram`) and
  `WorldCameraSubject` (`reference`/`placement`/`worldPoint`), `WorldSpeakerSource`
  (`none`/`machine`/`tune`/`synth`); `puck search -M 0 JsonPolymorphic src/Puck.World.Schema`
  lists the rest. A state row is not a `$type` union: its `kind` member picks the
  `CellKind`. `$type` failures do NOT all surface as
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
