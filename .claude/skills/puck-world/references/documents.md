# The document families

Three versioned JSON families, all owned by `src/Puck.World.Schema`.
`puck.world.def.v1` (`WorldDefinition.cs`) describes a world. An identity is not
a separate schema — it is an ordinary `WorldDefinition` document carrying an
`identity` section, one file per owned world (see "Owned-world identities"
below). The other two are EGRESS documents, never authored by hand and never
loaded as a world: `puck.world.projection.v1` (`WorldProjection.cs`) and
`puck.world.counterpart.v1` (`WorldCounterpartAttestation.cs`) — see
"Disclosure" below. All three carry a root `Extensions` bag under
`Puck.World.DocumentExtensionsPolicy`.

**Field names, enums, defaults, and validation ranges are generated, not
restated here.** `puck schema` derives `puck.world.def.v1.schema.json` (root),
`puck.world.projection.v1.schema.json`, and one file per document section
under `Assets/worlds/schema/*.schema.json` directly from `WorldDefinition.cs`
over the same source-generated `WorldJsonContext` the engine parses a world
through (`System.Text.Json`'s `JsonSchemaExporter` — never hand-maintained);
`puck schema --check` fails the build the moment a checked-in schema file
drifts from the code, and `puck schema --bundle` emits the single-file,
fully-`$ref`-resolved form for a quick read. When this reference states a
field name, an enum member list, or a numeric default with no accompanying
reason, treat it as a drift hazard and read the schema instead — the
`Machines`/`WorldSection` gap fixed below is exactly what letting a hand-kept
catalog drift looks like. This file's job is what the schema cannot say: why a
shape is what it is, an ordering rule, a refusal condition, a worked trap.

**`.puck` is the authoring surface; JSON is the compiled wire form.** Every
JSON block in this reference and its split files is what a `.puck` source
compiles to, not the recommended way to type it — except the flagship live
world (`Assets/worlds/puck.world.json`) and one AGB cartridge, which are
authored and shipped as raw JSON today with no `.puck` source in the tree; say
so plainly rather than treating DSL authorship as already universal. The `.puck`
language itself — grammar, `let`/`template`/`import`/`export`, units, the
compile-time collection builtins, string/indexing forms, and the
`compile`/`decompile`/`lint`/`fmt`/`lsp` CLI verbs and `PUCKnnn` diagnostics —
is [`puck-dsl`](../../puck-dsl/SKILL.md)'s to teach; this file only states what
the WORLD vocabulary's own blocks mean (`rule`/`decision`/`shape`/`placements`/
`prototypes` sugar, section semantics, refusals) and shows worked examples in
`.puck` with their compiled JSON alongside. See "Verifying a change here"
below for the loop: `lint` → `compile --validate` → run `Puck.World`.

## Disclosure — what leaves an authority

`WorldDisclosureTier` (`Protocol/WorldAdmission.cs`) is the whole vocabulary:
`frames` (no document), `presentation` (a `puck.world.projection.v1`
document), `replica` (the `puck.world.def.v1` document verbatim, hash-identical
— the sanctioned download). The tier is authored per `admission` row as
`disclosure` and decided ONCE at the admission door; `WorldAdmissionVerdict.Tier`
carries it, and every remote egress reads it and nothing else. An absent
`disclosure` resolves to `presentation`
(`WorldAdmissionEntry.Tier`), so no world already checked in hands out a
replica. A `frames` row that also mints grants refuses by name.

`WorldProjection.Compose(definition, tier, authority, revision)` is the one
egress composer, answering `null` at `replica`/`frames` so the caller sends the
definition verbatim or nothing. `WorldProjectionDocument`'s MEMBER LIST is the
disclosure decision: it has no member for `rules`, `grants`, `state`,
`admission`, `generation`, `generators`, `groups`, `properties`, `addons`,
`storage`, `host`, `authoring`, `identity`, `inputHold`, `targetRegisters`,
`bodyMotionPrograms`, or `portals`, and its `WorldProjectedKit` row has none for
a kit's `producers`/`actions`. `adjacencies`/`destinations`/`references`/
`interactions` DO cross: `WorldAdjacencyPolicy.TryDeriveOverlap` reads them from
both sides of a seam and must derive the same depth on each. `metadata` crosses
in reduced form — `WorldProjectedMetadata` carries `title`/`description` only;
`authors`, `tags`, and `custom` never cross (`custom` is an unbounded author
scratch bag that may hold notes never meant to leave the authority).
`WorldProjection.TryToDefinition` hydrates a received projection back into a
`WorldDefinition` (undisclosed sections take their neutral built-in defaults) so
no downstream consumer changed type; a hydrated document is never saved,
journaled, or an authority. A projection is FLAT: because it discloses no
`state` section, `Compose` answers every `state.<row>[.<key>]` document
identifier or spatial value from the composing authority's own state and sends
the literal (`WorldStateDocumentValues.TryFlatten`, on a rehydrated copy so the
live document keeps the authored reference canonical write-back preserves), and
`TryToDefinition` refuses BY NAME a peer that still names a cell.

On the wire a document leaf is `[tier byte][document bytes]`
(`WorldFederationCodec.EncodeDocument`/`TryDecodeDocument`), so a receiver names
what it was handed rather than sniffing it — the observation lane narrates it
once per tier change on stderr. A traveler's reservation carries a
`WorldIdentityProjection` (id, name, colour, move/turn rate), never its owned
document. `population.disclosure` (`WorldObserverDisclosure` — `all` (the
unauthored default), `radius`, `selfOnly`) redacts snapshot ENTRIES per sink at
`WorldOutputHub`, never inside the tick. `updateSeconds` samples remote QUIC
projections (default 0.03 s; zero means every authority tick) while coalescing
skipped field writes and pose-discontinuity hints. Local sinks remain full-rate.
Read back with `world.projection`,
`world.peers`' tier column, and `world.admission`'s disclosure column.

## Contents

This file is the entry point. It states the document families, the schema/DSL
framing above, the top-level `puck.world.def.v1` member and section-grant
catalogs (with a pointer to the generated schema for everything derivable), the
capacity constants, and the routing map — then hands off to the split files
below, each covering one coherent slice of decision/gotcha prose that the
schema cannot state on its own:

| File | Read it for |
|---|---|
| [documents-rules.md](documents-rules.md) | The `rules` section: predicates, effects, zones, tables, the expression/function vocabulary a rule's operands and `score` can call, transactions, decisions. |
| [documents-state.md](documents-state.md) | The `state` section: rows/cells, kinds, envelopes, the `advance`/`dynamics`/`cycle` traits, keyed-row eviction, reserved `$` names, `state.lattices` + the `lattice` trait, authored randomness (source/site/moment), `search`, `navigation`, discrete-board patterns. |
| [documents-render.md](documents-render.md) | `render` defaults, `dynamics` (the personality table), `curves`, `views.pipelines`, kit producer `flock`, crowd-scale policies, the `rigid` facet. |
| [documents-motion.md](documents-motion.md) | A kit's `motion` row: `holds` (the only spelling of a vertical channel), `shaping` (velocity response), the worked kart example, body facts on the wire, `views.seatRig`. |
| [documents-composition.md](documents-composition.md) | Boot-authored topology/timing members (`References`, `Gravity`, `Portals`, `Simulation`, `Adjacencies`, `Metadata`); the worked `puck.world.json` walkthrough (debugRoom, chessBoard, drinkMe/eatMe); `basis`/`imports` composition and merge rules; the validator; serialization conventions. |
| [documents-identity.md](documents-identity.md) | Owned-world identity documents: the catalog, seeding, discard/refusal classes, `WorldIdentity`, facts, seating, cross-document durable-state writes. |
| [documents-binding.md](documents-binding.md) | `WorldBindingComposer`: layer merge rules, context rows, wheel rows, the binding bar. |

## `puck.world.def.v1`

`WorldDefinition` is one aggregate record whose positional section members are
now ALL optional — every one carries `[JsonIgnore(Condition =
WhenWritingNull)]` and a `= null` default, so a document declaring none of
them still parses, and each section's own resolving accessor (the plain-named
property beside the `…Raw` constructor parameter) answers its own documented
ABSENT behavior. There is no longer a required/optional split to enumerate by
count; declaration (= canonical-write) order is still the contract a
`with`-expression or a new member's insertion point must respect. Reading
selectively: `Motion`, `SpawnPoints`, `Render`, `Screens`, `Machines`
(`WorldMachine.cs` — named deterministic devices whose lifetime is independent
of their output consumers: `name`/`engine`/`configuration` — provider-validated
— `running`, ordered `memory` bindings to Int world-state cells, and an
optional `cable` endpoint; `gaming-bricks`/`rom-forge` own the hosted-engine
contract, and `WorldScreenSource`'s `machine` case is how a screen displays
one), `Cameras`, `Population`, `PlayerDefaults`, `Channels`, `TargetRegisters`,
`BodyMotionPrograms`, `Kits`, `DefaultSeatKit`, `Assignment`, `Addons`,
`BindingOverlays`, `Storage`, `Creations`, `Placements`, `Authoring`,
`Speakers`, `Tunes`, `Patches`, `Audio`, `Collision`, `Gravity`, `Host`, `Views`,
`Looks`, `LookAssignment`, `Dynamics`, `Curves`, `Grants`, `Hud`, `State`,
`InputHold` (its own type, `WorldInputHoldAuthoring`, is the AUTHORED seconds
shape — `WorldDefinition.CompiledInputHold` is the compiled ticks form
runtime code consumes; see `WorldInputHoldSettings`'s remarks), `Rules`,
`Identity`, `Groups`, `Properties`, `Interactions`, `Generation`,
`Generators`, `References`, `Portals`,
`Simulation`, `Destinations`, `Admission`, `Adjacencies`, `Text`, and
`Metadata` — plus `Schema` and the `[JsonExtensionData]` `Extensions` bag.
There is no `Wander`/`Scene` member and no `WorldSceneRow` type any more —
both retired; scenery is authored through `Placements` now.

`Dynamics`, `Curves`, `Rules`, `References`, `Gravity`, `Portals`,
`Simulation`, `Adjacencies`, and `Metadata` each carry decision/derivation
prose the schema cannot state — see
[documents-render.md](documents-render.md), [documents-rules.md](documents-rules.md),
and [documents-composition.md](documents-composition.md) respectively. Every
field NAME, type, and default not called out in one of those files is the
generated schema's to answer.

The `WorldSection` enum (`src/Puck.World.Schema/WorldGrant.cs`, declared
order — read the enum for the current member list): `Kits, Screens, Machines,
Cameras, Spawns, Motion, Population, Render, Addons,
Bindings, Creations, Placements, Authoring, Speakers, Tunes, Patches, Audio,
Collision, Host, Views, Looks, Grants, Hud, State, InputHold, Rules,
Groups, Properties, Interactions, PlayerDefaults, Probes, Dynamics,
Curves, Tables`.
It is the grant subject vocabulary
(`section:<name>`) and the mutation dispatch axis — narrower than
`WorldDefinition`'s own member list above: `Channels`,
`TargetRegisters`, `BodyMotionPrograms`, `Storage`, `Identity`,
`Generation`, `Generators`, `References`, `Portals`, `Simulation`,
`Destinations`, `Admission`, `Adjacencies`, `Text`, and `Metadata` carry no dispatch axis of their own (some
names also differ — `SpawnPoints`/`BindingOverlays`/`LookAssignment`/
`DefaultSeatKit`/`Assignment` dispatch through `Spawns`/`Bindings`/`Looks`/
`Kits` respectively; `PlayerDefaults` dispatches through
`WorldMutation.SetPlayerDefaults`). `Probes` is boot-authored
only — no `WorldMutation` kind targets it, so the section-scoped grant hold is
its whole authority surface.

## Capacity constants

- `WorldBodiesLimits` (`Puck.World.Schema`): `CapacityCeiling = 4096`,
  `LocalSeatCount = 4` (indices 0–3) — single-sourced against
  `WorldClient.EntityCapacity` (the F3 reconciliation, 2026-08-06; see
  [SKILL.md](../SKILL.md)'s "Boundaries" section). There is no
  `MaxPopulation`/`MaxPopulationSimulated` constant. The client reserves full
  catalog detail for the first `DetailedRenderBand` (128) indices and emits
  later active bodies as one-instance coarse capsules. That hybrid bounds
  storage and SDF inputs; it
  does not establish dense-crowd frame time. Hard presentation targets require
  a non-per-creature SDF lane (for example raster impostors or an authored
  aggregate). Existing shipped worlds may still author
  `networkPlayers: 124` as ordinary document data, not an engine ceiling.
- `WorldLookSource.Catalog.RigCount` is the reusable appearance count, not
  body capacity. `DefaultIndex` cycles fresh slot picks through that catalog;
  an admitted occupant carries its pick across transfers. The client reserves
  a maximum-sized transform range per body and probes the largest rig in every
  range, so a repeated or transferred look is neither truncated nor duplicated.
  Each catalog leaf retains a separate cull instance with a primitive-sized
  bound plus its unscaled local offset. The instance ceiling remains distinct
  from population capacity. Do not use live instance count to size reserved
  bone storage.
- `WorldHudCapacity` (`WorldHud.cs`): see [hud.md](hud.md).
- `StateCapacity` (`Puck.State/StateRow.cs`): `MaxRows = 256`,
  `MaxCellsPerRow = TopologyCompilation.MaxCells` (an authored `capacity` may only narrow it),
  `MaxTextValueLength = 256` (UTF-16 units, a text cell's value), and
  `MaxBodySlots = 128` across the `body` and `identity` lanes (the fixed
  per-body register/checkpoint width).
- `WorldDynamicGeometryCeilings.MaxContributedDynamicInstances = 16000`,
  the document-global CPU/instance-grid admission ceiling. The recorded
  GPU-bound measurement is 0 but does not govern admission.
- `WorldPlacementPolicy`: `MaxShapesPerStamp = 367`,
  `MaxStampRegistrations = WorldBodiesLimits.DetailedRenderBand` (128, an
  independent value), `TimelineSecondsPerFrame = 8f/60f`, and the reserved
  derived-face screen band. The stamp pool's worst-case instance draw is
  `MaxStampRegistrations x MaxShapesPerStamp` (46976) against
  `Puck.SignedDistance.SdfProgramBuilder.MaxInstances` (65536); 367 is the
  measured ceiling that keeps the shipped overworld's whole COMPOSED boot
  probe (the presenter's four emitters: the scene — stamp pool, avatar
  catalog, static placements, screens — plus the SDF-document, adjacency-band,
  and field reservations) at least 4096 instances under that cap (61392
  composed at 367, headroom 4144; 4000 at 368), measured by
  `WorldRenderEnvelopeLawTests.ShippedWorldBootProbeInstancesFitTheEngineCeilingWithHeadroom`.
- `WorldRenderEnvelope.cs` — the render-capacity oracle: `Configure` at boot
  from the probe, `TryFit(candidate)` at every apply; unconfigured reads as
  "fits".
- `CreationDocument.PaletteSize = 16` bounds `puck.creation.v1`'s `palette`
  array — see [documents-render.md](documents-render.md) for the entry shape
  (weathering/inset lanes), which is decision prose, not a bare catalog.
- `WorldRenderLight.Occluder` uses ordinary light rows to attenuate nearby
  surface illumination; see [documents-render.md](documents-render.md).

## Routing map (one line each, all under `src/Puck.World.Schema/`)

- `WorldJsonPayload.cs` — the single door for author-supplied JSON text
  (`TryParse`, `IsParseFailure`, 120-char elided rejections).
- `RefusalTaxonomy.cs` — `RefusalKind` (protocol-fault vs verdict), the
  `[Refusal(door, condition, kind)]` attribute, and the catalog entry shape
  `world.refusals` prints.
- `WorldAnchor.cs` — WHERE a placeable thing rides (shared by cameras and
  speakers); `WorldCameraProgram.cs` — HOW a camera frames, as an authored op
  list (`Puck.World.Client.WorldCameraRigCompiler` translates it to the
  document-blind IR in `Puck.SdfVm.Views`; see [views.md](views.md)).
- `WorldViews.cs` — the `views` section (slots, layouts, seat framing).
- `WorldState.cs` — the `state` section: `WorldStateSection` (the document's
  `IStateSection` — `world` rows, `body`/`identity` slot lanes, `lattices`) and
  `WorldStateRow` (a `Puck.State.StateRow` plus the `gatesDrive` and `field`
  traits). The row/cell substrate (`StateRow`, `StateCell`, `StateCapacity`), the
  domains (`StateDomain`, a closed union over `Puck.State/Union.cs`'s `[Union]`
  marker), the traits, the catalog (`StateCatalog`: ownership lane, storage shape,
  value kind, stable handle and lane-local ordinals), the topologies, the
  generators, and the patterns live in `src/Puck.State/`. See
  [documents-state.md](documents-state.md).
- `WorldDefinitionRows.cs` — the one row-find per section
  (`FindCreation`/`FindPlacement`/`FindKit`/`FindSpawnPoint`/`FindStateRow`),
  ordinal and allocation-free.
- `WorldStateReader.cs` — the definition-anchored door to `Puck.State.StateReader`,
  the ONE `(rows, rowName, key, tick)` → `(row, rawValue, text)` read over the
  `state` section. See [documents-state.md](documents-state.md).
- `WorldSpeaker.cs` — speaker rows, feeds, emission facets, tune/patch asset
  rows, audio defaults.
- `WorldColor.cs` — golden-ratio index palette for simulated avatars.
- `WorldHostTokens.cs` — the one spelling for backend/surface-format tokens
  (JSON converters + the `world.row.set host` payload grammar).
- `WorldAdjacencies.cs` — the authored boundary rows, fixed frame compilation,
  crossing test, derived overlap, reciprocal hysteresis, and corner topology;
  see [adjacency-and-federation.md](adjacency-and-federation.md) and
  [documents-composition.md](documents-composition.md).
- `ShadowTier.cs` — the tier↔scale map `world.save` folds live shadow reach
  through.
- `BindingVocabularyHook.cs` — the static injection seam the composition
  root wires with a `[ModuleInitializer]` (`WorldDataHookInstaller` in
  `Puck.World`) so validators reach the input vocabulary without this
  project referencing `Puck.Input`. See [documents-binding.md](documents-binding.md).

## Verifying a change here

**`.puck` sources first.** Edit the `.puck` file, then `puck lint <file> --strict`
(static + reference checks; self-installs the machine catalog so `Machines`/
cartridge-hosted checks resolve — no separate registration step), then
`puck compile <file> --validate` (lowers to JSON AND runs the SAME semantic
engine schema rules `WorldDefinitionValidator` enforces at boot — this is the
DSL-side closure with "The validator" in
[documents-composition.md](documents-composition.md); `--bundle` inlines
imports, the DSL-side counterpart of `basis`/`imports` composition there).
`puck decompile <path.json> [-o path.puck]` runs the reverse for an
existing hand-authored or generator-produced JSON world. None of the three
verbs touch a running `Puck.World`. Grammar, flags, and diagnostics beyond
this are [`puck-dsl`](../../puck-dsl/SKILL.md)'s.

**Then the engine.** No engine gate. Build (`dotnet build Puck.slnx -c
Release` — architecture lanes + XML-doc diagnostics) and RUN `Puck.World`,
round-tripping the affected document over stdin (`world.status`, `world.save`,
`world.load`). Proven in-process by
`tests/Puck.World.Tests/StrictParseLawTests.cs`. Validate HUD document changes
by running the app — see [hud.md](hud.md)'s "Verifying" section for the
recipe. A `.puck` source compiled through `--validate` still needs this step:
`--validate` runs the same rules the boot path does, but only running the app
proves a document plays.
