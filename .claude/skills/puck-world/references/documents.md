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
disclosure decision: it has no member for `rules`, `grants`, `state`, `market`,
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
`WorldOutputHub`, never inside the tick. Read back with `world.projection`,
`world.peers`' tier column, and `world.admission`'s disclosure column.

## Contents

- Disclosure — what leaves an authority
- `puck.world.def.v1`
  - Authored randomness — SOURCE x SITE x MOMENT
- Document composition (`basis`)
- The validator — the one thick gate
- Serialization (`WorldDefinitionSerialization.cs`)
- Identity conventions
- Owned-world identities
- Binding composition (`WorldBindingComposer.cs`)
- Capacity constants
- Routing map
- Verifying a change here

## `puck.world.def.v1`

`WorldDefinition` is one aggregate record whose positional section members are
now ALL optional — every one carries `[JsonIgnore(Condition =
WhenWritingNull)]` and a `= null` default, so a document declaring none of
them still parses, and each section's own resolving accessor (the plain-named
property beside the `…Raw` constructor parameter) answers its own documented
ABSENT behavior. There is no longer a required/optional split to enumerate by
count; declaration (= canonical-write) order is still the contract a
`with`-expression or a new member's insertion point must respect. Reading
selectively: `Motion`, `SpawnPoints`, `Render`, `Screens`, `Cameras`,
`Population`, `PlayerDefaults`, `Channels`, `TargetRegisters`,
`BodyMotionPrograms`, `Kits`, `DefaultSeatKit`, `Assignment`, `Addons`,
`BindingOverlays`, `Storage`, `Creations`, `Placements`, `Authoring`,
`Speakers`, `Tunes`, `Patches`, `Audio`, `Collision`, `Gravity`, `Host`, `Views`,
`Looks`, `LookAssignment`, `Dynamics` (see below), `Grants`, `Hud`, `State`,
`InputHold` (its own type, `WorldInputHoldAuthoring`, is the AUTHORED seconds
shape — `WorldDefinition.CompiledInputHold` is the compiled ticks form
runtime code consumes; see `WorldInputHoldSettings`'s remarks), `Rules` (see
below), `Identity`, `Groups`, `Properties`, `Interactions`, `Generation`,
`Generators`, `Water` (the standing-water medium — one waterline `level`;
null IS the dry world; `WorldWater.cs`), `References`, `Portals`,
`Simulation`, `Destinations` (`WorldDestinations.cs`), `Admission`
(`Protocol/WorldAdmission.cs`, the one trust list every ingress crosses —
key-bearing rows for the TCP identity door, keyless `federatedAuthority` rows
for travellers an authenticated authority hands over; deny-by-default, an
absent/empty section admits neither), `Market` (`WorldMarketSection`,
`WorldMarket.cs` — the local auction house's config and live listing ledger;
null IS today's no-market behavior, falling back to
`WorldMarketSection.Empty`), `Adjacencies` (`WorldAdjacencies.cs` — invisible
reciprocal authority boundaries; null names no seamless neighbours), `Text`
(`TextFontCatalogDefinition` — the named, hash-pinned world-space font
catalog; null declares no fonts), and `Metadata` (`WorldMetadataSection`,
`WorldMetadata.cs` — author-facing `title`/`description`/`authors`/`tags`
plus a free-form `custom` bag; nothing in the engine reads or dispatches on
it, and it is distinct from `Extensions` below, which exists to catch a
misspelled top-level section name rather than to hold content) — plus
`Schema` and the `[JsonExtensionData]` `Extensions` bag. There is no
`Wander`/`Scene` member and no `WorldSceneRow` type any more — both retired;
scenery is authored through `Placements` now.

The topology/timing members carry facts that are BOOT-AUTHORED ONLY —
none has a `WorldSection` axis or a `MutationKind` ordinal, so nothing
mutates them in session and no grant subject names them:

- **`References`** (`WorldReferences.cs`) — `IReadOnlyList<WorldReference>?`,
  each row `(WorldSafeName Name, string? Document, Guid? Owner, WorldSafeName?
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
  within each. A source reads only its placement transform — never its SDF or
  solidity. Read back authored/derived values and last deterministic solver
  work with `world.gravity`; `world.budget` echoes the source and evaluation
  price.
- **`Portals`** (`WorldPortals.cs`) — `WorldPortalsSection(WorldPortalDefaults
  PortalDefaults)`, whose `travel` is `Party` (the traveling seat's whole
  active local-seat party) or `Body` (one seat). It is the world-scope default
  a placement face's own `WorldPlacementPortal` facet falls back to when it
  authors no `Travel`; a null section resolves every facet to `Body`.
- **`Simulation`** (`WorldSimulationDefaults`, `WorldDefinition.cs`) — one
  field, `RateHz` (required in an authored section), the authoritative
  server's fixed step rate in Hz. Read through
  `WorldDefinition.SimulationRateHz` (`Simulation?.RateHz ?? 0` — absence is
  a rate-0 resident world; the standard 240 Hz is authored in
  `standard.world.json`) — never `Simulation` directly, since every consumer
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
  Entra `oid`, checked with `WorldEntraObjectId.IsValid`), `tags`, and a
  `custom` bag (`IDictionary<string, JsonElement>`). Nothing in the engine
  reads any member here. `title`/`description` cross to a Presentation-tier
  peer as `WorldProjectedMetadata`; `authors`/`tags`/`custom` never do (see
  "Disclosure" above). `custom` follows `WorldDocumentBasis`'s ordinary
  nested-object merge rule with its two carve-outs: a key literally named
  `$drop`/`$replace` refuses at validation, and a JSON `null` under a key in
  a delta deletes the inherited key rather than storing a literal null.

The `WorldSection` enum (`Protocol/WorldGrant.cs`, 32 members, declared
order): `Kits, Screens, Cameras, Spawns, Motion, Population, Render, Addons,
Bindings, Creations, Placements, Authoring, Speakers, Tunes, Patches, Audio,
Collision, Host, Views, Looks, Grants, Hud, State, InputHold, Rules,
Groups, Properties, Interactions, PlayerDefaults, Market, Probes, Dynamics`.
It is the grant subject vocabulary
(`section:<name>`) and the mutation dispatch axis — narrower than
`WorldDefinition`'s own member list above: `Channels`,
`TargetRegisters`, `BodyMotionPrograms`, `Storage`, `Identity`,
`Generation`, `Generators`, `Water`, `References`, `Portals`, `Simulation`,
`Destinations`, `Admission`, `Adjacencies`, `Text`, and `Metadata` carry no dispatch axis of their own (some
names also differ — `SpawnPoints`/`BindingOverlays`/`LookAssignment`/
`DefaultSeatKit`/`Assignment` dispatch through `Spawns`/`Bindings`/`Looks`/
`Kits` respectively; `PlayerDefaults` dispatches through
`WorldMutation.SetPlayerDefaults`, and `Market` through the
`CreateMarketListing`/`PlaceMarketBid`/`BuyoutMarketListing`/
`CancelMarketListing`/`SettleMarketListing` family). `Probes` is boot-authored
only — no `WorldMutation` kind targets it, so the section-scoped grant hold is
its whole authority surface.

`rules` (`WorldRule`, `Puck.World.Schema/WorldRules.cs`) is the OPTIONAL
world-scoped rule section — the SAME `ActionPredicate`/`ActionEffect`/
`ActionTriggerMode` primitive a kit's per-body actions use, one level up.
Optional deliberately: a new REQUIRED section would refuse every existing
document at boot for declaring nothing. Only `all`/`compareState` predicates and
`setState`/`addState`/`countdownState`/`generate`/`pose`/`save` effects are
admissible at world scope — plus,
each admitting an EXISTING `WorldMutation` kind into the rule effect set (riding
the exact seam `generate` proved, never a new door), `upsertHudPanel`/
`removeHudPanel` (a world-scoped HUD row) and `upsertPlacement`/`removePlacement`
(a placement row); the rest read or write per-body state (velocity/impulse/
designate/timer) and are refused BY NAME by `WorldRuleCompiler`. `pose` is the
rule-side `body.pose`: `{"$type":"pose","key":"<body>","spawnPoint":"<id>"}` or
`position` + `yawDegrees`/`pitchDegrees`/`rollDegrees` (exactly one of the two),
applied through `WorldBody.Pose` as the world's own act — no `WorldMutation`, no
journal, and deliberately outside the `gatesDrive` check, which is what lets a
`dead == 1 && respawnIn <= 0` rule move a body its own gate row has frozen.
`setState` with `text` writes a `kind=text` cell (exactly one of `value`/
`valueSeconds`/`fromState`/`text`); because `lookAssignment.rows` and creation
palettes bind to text cells, and a state write re-resolves every bound value
(re-running the look resolve when `lookAssignment` is touched), this is how a
rule restyles a body — `elements.world.json`'s `look-*` rules drive one
`lookOf.<body>` cell per body. A text row also takes a `fromState` copy from
another text cell. Two indirections make "the body my `target` cell names"
addressable: a key spelled `$cell:<row>:<key>` resolves to that cell's integer
value at every read/firing (effect `key`/`fromKey`, `compareState`
`key`/`comparandKey`), and a body-reference token `cell:<row>:<key>` does the
same inside `$distance:`/`$los:`/`$nearest:`. `$nearest:<bodyRef>:<row>` is the
nearest other active body whose cell in keyed `<row>` is nonzero (−1 for none,
ties to the lowest index) — `elements.world.json`'s `auto-target` rule is
`setState target.0 fromState $nearest:body:0:enemy`. `save` admits on DIFFERENT
terms again: like `pose`, it has no `WorldMutation` ordinal — it writes a
session snapshot to the world's own loaded file
(`WorldDefinitionSource.SourcePath`, the SAME target the console's no-argument
`world.save` resolves; no authored path, and no homeless-world refusal exists
because every boot shape is file-backed), composing no candidate and journaling
nothing, so the sim state after a tick that fires it is bit-identical to a tick
that does not — a replay hash cannot see it. It rides `WorldServer.
FireWorldRuleEffect` directly through a NEW `WorldServer.SaveEffectTap` (mirroring
`EchoTap`) that the composition root wires to the identical `WorldSessionCapture.
Capture` fold `world.save` itself runs, since `Puck.World.Server` cannot reach the
render/screen/audio/pacing state that fold needs. No throttle beyond the ordinary
`Level`/`Edge` vocabulary — a `Level` gate fires it every tick held, the same
footgun a level-triggered `addState` already carries (see `WorldRule.Mode`'s own
remarks); a write failure is caught at the tap and narrated on stderr by name,
never fatal to the tick. Effects and
predicates address a (row, KEY) PAIR — an omitted key means the row's slot cell,
and `WorldStateRow.IsKeyed` is the discriminator (deliberately NOT `!IsSlot`: a
row with no cells at all is slot-addressable, since the first write mints its
slot cell — `IsSlot` asks whether a value exists to READ, `IsKeyed` whether an
omitted key can ADDRESS one). `CompareState` may instead name
a reserved channel: `$tick`, `$population`, `$region:<placementId>`,
`$machine:<screen>:<address>` (one live byte off a declared screen's booted
machine — the same `IWorldMachineMemoryPeek.TryPeek` primitive
`WorldAddonMemoryWatch` rides, called directly). A `compareState`'s comparand is
EITHER an authored `value` OR a second `(comparandState, comparandKey)` pair
resolved through the SAME operand walk (reserved channels included) — never both,
never neither, and the two sides must resolve to the same cell kind. That one
widening is the periodicity/cooldown/round-boundary vocabulary (gate `$tick`
against a schedule row your effects advance for "every N ticks"; a request-gated
cooldown is a `NonNegative` countdown row decremented while `>0`, gated `<=0`,
NOT a `$tick` threshold — see `WorldRules.cs` remarks). `mode` is `Level` (fires
every tick the gate holds) or `Edge` (fires once per crossing, re-arming when the
gate closes) — a rule that writes a row almost always wants `Edge`. A rule's `name`
is a `WorldCellName`, the SAME validated-identifier type a state row and a cell
key ride (dot-free, free of the reserved character set, refused by name at the
JSON converter and at `world.row.remove rules`), and `WorldRuleCompiler` additionally
refuses the reserved `$` prefix — `$` marks what the engine mints, and nothing
mints a rule. Read back with `world.rules`, whose `latch=held|open` column is the
gate-held latch (`held` = the gate held at the last evaluation, so an edge rule
will not fire again until it lets go). Authored with `world.row.set rules`/
`world.row.remove rules` (ordinals 52/53) under `Mutate`/`section:rules` — a hold
UNTRUSTED principals are refused outright (see [authority.md](authority.md)).
Rules evaluate in DOCUMENT ORDER and their effects apply IMMEDIATELY, so a later
rule's gate — AND a later rule's live `fromState` copy operand, which reads
through the same walk — sees an earlier rule's SAME-TICK write; a rule ADDED by
this tick's effects starts on the next tick. Declaration order is therefore the
whole answer to "does the copy see the pre-write or post-write value", and it is
the same answer on every run. A rule's EFFECTS are a different question: they
act as `WorldPrincipal.World` (see [authority.md](authority.md)).

A `setState`/`addState` effect is submitted only when it could MOVE the
destination (`WorldServer.FireWorldRuleEffect`): the resolved value already
matching the cell has always skipped, and so does a value the destination row's
declared envelope (`nonNegative`/`min`/`max`) pins where the cell already sits —
`WorldStateRow.ClampToEnvelope` answers both. That is what keeps a `Level` rule
pointed at a floored row from composing a candidate the whole-document validator
refuses once per TICK for the life of the session (a `nonNegative` row draining
by `-5` reached its floor and then emitted 2679 `[world.mutation rejected: …]`
lines over the remaining 2679 ticks of a 12-second boot). It never changes what
is submitted: a write that genuinely tries to CROSS a bound (a cell at 3 taking
`-5`) is still submitted and still refused BY NAME, so the envelope duality is
unchanged — this removes the inert case from the write side, it does not add
saturate-on-write (ruled out).

`setState`/`addState` carry the SAME value/comparand duality on the WRITE side:
EITHER a literal `value` OR a live copy `(fromState, fromKey)` — another row or
reserved channel, read fresh on every firing through the identical
`ResolveOperand`/`ReadWorldFact` path the comparand uses — never both, never
neither, kinds must match (refused `EffectSourceAmbiguous`/
`EffectSourceKindMismatch`, the effect-side siblings of `ComparandAmbiguous`/
`ComparandKindMismatch`). A READ operand — gate subject, comparand, or
`fromState` — must address a cell its row DECLARES; an undeclared cell would
read 0 forever with no refusal, so it refuses at compile
(`StateCellUndeclared`, owner ruling 2026-08-06). Write destinations mint
their cells and stay exempt, and because rules recompile under whole-document
revalidation, removing a cell a rule reads refuses the removal naming the
rule. This is what closes the round-reset gap a moving
comparand alone cannot: a rule REACTING to a counter someone else advances
(`compareState round != roundReflect`, a rule that does not itself own the
advance) resets a SET of other rows to authored literals AND resyncs its own
shadow row to `round`'s CURRENT value in the same firing
(`setState roundReflect fromState=round`) — a standing `addState roundReflect
+= 1` only tracks a disciplined `+1` counter and desyncs silently (gate stuck
open, latch held, no further resets, no refusal anywhere) the instant the
counter advances by anything else or is set outright. When the rule that
ADVANCES the round is itself authored as a rule, the resets can just be more
effects in that SAME rule's `effects` list instead — a rule is not limited to
one row write; the copy operand exists for the DECOUPLED case, where the rule
doing the resetting is not the thing that changed the counter.

A `$region:<placementId>`/`$machine:<screen>:<address>` gate resolves against
the document at EVERY compile (boot, and every subsequent mutation — the
whole-document revalidation `RecompileRules` runs on every `Install`). A rule
sensing a placement's region can therefore only be authored once that placement
already exists, and that placement's region can never be removed while ANY
rule (including the one being fired) still names it — retire the referencing
rule first, in an earlier mutation of the same or a prior tick, then remove the
placement. Sense a PERMANENT placement (boot-declared, never removed) rather
than a token placement a rule itself spawns/removes, for exactly this reason.
`src/Puck.World/Assets/worlds/elements.world.json` is the worked example: two
boot-declared region placements, `Region` interactions that set countdown
cells, and `Level` rules that drain/countdown them.

`state` (`WorldStateSection`, `Puck.World.Schema/WorldState.cs`) is the
document's abstract state inventory. It has three ownership lanes:
`world` holds mutation-addressable document cells; `body` holds ephemeral
per-body counters and timers; `identity` holds the same compact slot vocabulary
but synchronizes it through the durable identity-document seam. Body and
identity names share one world-wide namespace and are compiled once into each
body's bounded ordinal arrays — declarations never live under individual
actions, and the runtime never performs document lookups on the action hot path.
The lane is the lifetime declaration; there is no second `lifetime` field to
contradict it.

`WorldStateCatalog` (`WorldStateCompilation.cs`) is the typed compiled view of
that inventory. It assigns catalog-bound `WorldStateHandle` values in
world → body → identity document order and records each declaration's ownership
lane, slot/keyed/lattice storage shape, deterministic value kind, and lane-local
ordinal. Runtime processors resolve `(lane, name)` once, retain the handle while
that catalog is current, and index the immutable descriptor catalog thereafter;
a replacement declaration shape rejects old handles, and the catalog neither
owns values nor changes their lane-specific storage. `WorldDefinition.StateCatalog`
is non-serialized; definitions sharing `StateRaw` share its compiled view, and
`WithWorldState` preserves that view and its handles across value-only updates.

`state.world` (`WorldStateRow`) is genre-neutral game state — score, rounds,
inventory, flags. **A slot is a table with one
key, and there is ONE authored spelling for both.** A row names itself,
declares its `kind`, and carries EITHER a bare `value` — sugar for the one
cell keyed `WorldStateRow.SlotKey` (`"$value"`) — OR a `cells` array of
author-keyed `{"key","value"}` objects. Two optional fields, never two
discriminators: a row carrying both, or a `value` beside a `capacity`
(declaring a capacity is declaring keyed-row intent), refuses by name.
Omitting both is a declared-but-empty row.

```json
"state": {
  "world": [{"name":"score","kind":"int","value":0,"min":0,"max":1000}],
  "body": [{"name":"jumpUses","kind":"Counter","initial":0,"resetFact":"Grounded"}],
  "identity": [{"name":"stance","kind":"Counter","initial":0,"playerWritable":true,
    "envelope":{"$type":"set","values":[0,1,2]}}]
}
```

### `dynamics` — the personality table

`WorldDynamicsRow` (`WorldDynamics.cs`): named rows of `{name, f, zeta, r}` —
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
followers), a camera program's `dynamics` op (the boom ease), a grounded/swim
kit's `motion.dynamics` (planar velocity shaping — exactly one of `dynamics`
or the engage/release `response` table, never both, never neither), and a
`state` row/cell's `dynamics` trait (the eased read, above).

### `state.lattices` + the `lattice` row trait — the lattice (scalar rows, reactions, lattice-derived geometry)

`WorldFields.cs` (the compiled composite) + `WorldState.cs` (the document
spelling). The document declares ONE `state.lattices` topology (name, origin,
`cellSize`, `width` × `depth` × `layers`, `stepEveryTicks`, `reactions`) and
lattice-shaped state rows: `{"name": …, "kind": "fixed", "lattice":
{"topology": …, "initial"/"min"/"max", optional "heightScale"/"color",
"paint": […]}}`. `WorldFieldsSection.Compile` assembles the runtime composite
the engine consumes (`WorldDefinition.Fields` is that compiled view — never
an authored section; there is no top-level `fields` member any more). A cell
write against a lattice row (`world.state.cell.set`) refuses through
whole-document revalidation — the lattice's cells are simulation state, not
authored cells. Rows are seeded by their trait's `paint` rectangles and
evolved by the topology's `reactions` in document order each step: `diffuse`, `decay`, `transform`
(`when` conditions on the cell → `then` set/add writes), `emit` (bodies tagged
nonzero in a keyed row deposit into the cell they stand in), `expose` (writes
1/0 into a keyed row per body by a field test at the body's cell — the bridge
to body-level chemistry). A row with `heightScale` IS geometry: its value
raises a solid column above the origin that bodies stand on
(`WorldFieldLatticeSolid`, unioned with the authored solids for contact) and
the renderer shows (`WorldFieldEmitter`: one CPU-baked distance brick per
height field, coloured by `color`, uploaded through the engine's brick pool).
`WorldFieldProgram.Compile` is the typed reaction compiler view over that same
authored topology and reaction list: stable field/node handles, its canonical
state catalog, fixed-point scalar inputs, typed state dependencies, immutable
canonical read/write sets, the dependency DAG they imply, and separate
cell-node/full-cell/body work counts. It is deliberately not a second serialized
graph language; editors and schedulers consume it beside `WorldDefinition.Fields`,
which remains the complete topology/paint/display composite and the document
remains the one authoring home. `WorldDefinition.FieldProgram` is the cached,
non-serialized door. It retains compatible handles across unrelated definition
edits and value-only state updates, and replaces them when field or reaction
program inputs change.
`layers: 1` is a ground lattice; more layers is a voxel volume and costs
proportionally. A lattice carries at most 262,144 cells so a full eight-field
primer (eight lattice rows) remains inside the federation frame; when any row has `heightScale`,
the XZ footprint is at most 126 × 126 cells and the sum across layers may raise
at most 126 cells, fitting the padded 128³ render brick without truncation.
Cell values are sim state beside the population — stepped
after the rules, checkpointed (`Fields` block), delivered as `FieldCells`
deltas on the snapshot (`FieldsFull` on a primer) — never document rows, so
nothing journals them. Read back with `world.fields`. `elements.world.json`
paints a fuel forest beside an ice glacier; a burning body emits heat, heat
ignites fuel, fire emits heat and consumes fuel, heat melts ice into water,
water quenches fire — no interaction names the boundary.

### Authored randomness — SOURCE x SITE x MOMENT

One primitive, three separable parts. A **source** is a shape, a **site** is a
place that draws, a **moment** is when.

**Source** (`WorldGenerator`) is the document's whole randomness vocabulary.
`source` selects the shape and each shape reads a DISJOINT field set — a foreign
field refuses BY NAME, including `bound`/`mode`, which are non-nullable and are
refused against their declared defaults:

- `markov` — `start`, `bound`, `mode`, `contexts` (weighted alternatives, each
  naming the context it moves INTO). Writes TEXT; the only shape that DEALS. One
  emission is one walk from `start` to a TERMINAL context (one declaring no
  alternatives), refusing by name at `bound` rather than truncating. `mode` is
  `withReplacement` (default), `withoutReplacement` (dealt out → refuse by name)
  or `reshuffleOnExhaustion`.
- `uniformRange` — `rangeMin`/`rangeMax`, both or neither. One numeric draw.
- `weightedNumeric` — `weighted` (`{value, weight}` rows). One numeric draw.
- `streamDraw` — no fields. One raw 32-bit draw.

`WorldGeneratorCapacity`: 32 contexts, 64 alternatives per context (one deck-mask
bit each), bound ≤ 64, token ≤ 64 UTF-16 units, 64 weighted outcomes, 64 declared
sources, uniform bounds inside int32.

**A source holds NO position.** Declare it once in the optional `generators`
section (`{"name": …, "generator": {…}}`) and reference it from any number of
sites, or inline it at one site — the two spellings compile to the identical
record.

**Site** (`WorldDraw`) declares a value is drawn: exactly one of `source` (a
declared row's name) or `generator` (inline), plus `timing`. Three sites:

```json
{"name":"bark","kind":"text","draw":{"source":"barkTable","timing":"event"}}
"population": { "capacityDraw": {"generator":{"source":"uniformRange","rangeMin":128,"rangeMax":128},"timing":"boot"} }
"host": { "backendDraw": {"source":"backendTable","timing":"boot"} }
```

The CURSOR and dealt DECKS live on the SITE (`drawCursor`/`drawDecks`, engine-
minted row fields — never cells), so **two sites referencing one source draw
INDEPENDENT sequences**. That is what makes a reference safe.

**Moment** (`timing`): `boot` (drawn once at first fill; a later `generate`
refuses by name), `tickPeriod`, `event`. The latter two redraw through the SAME
`WorldMutation.Generate` (ordinal 51) / `world.generate <row>` — ONE argument,
because a site owns its whole draw. Cadence is an ordinary `$tick`-scheduled or
event-gated rule, so timing costs no mutation ordinal.

**The seed ladder is four rungs**, each LENGTH-DELIMITED before its bytes:
engine constant → `generation.worldSeed` → running INSTANCE identity → SITE
DESCRIPTOR (`state.<row>`, `population.capacity`, `host.backend`). The descriptor
is an IDENTITY, never a positional ordinal: the live site set moves under
ordinary operation (a settled facet clears, `world.row.remove state` retires a row,
`UpsertStateRow` adds one), and a positional stream would silently re-point a
live site while its cursor kept counting.

**The engine SEEKS, never replays.** Fixed advance cost per sample (which is why
`uniformRange` is a multiply-high map, uniform to within `n/2^32`, not a
rejection-sampled bounded draw), so resuming at cursor `n` is one `Advance` —
O(1). There is NO per-tick cadence ceiling.

**Boot-only sites SETTLE AND CLEAR** into their ordinary literal field and
NARRATE on stderr (`[world.draw: settled <site> instance=<name> -> <value>]`) —
settling erases the only evidence the value was random. State sites keep facet +
cursor and RESUME on reload; they fill only while the row carries no cell, so an
authored `value` is a deliberate override. `host.backendDraw` draws its backend
BY NAME from a weighted TEXT source over the backend tokens (never an unnamed
ordinal) and is XOR-by-presence against `host.backend`;
`population.capacityDraw` cannot be (its record is a STRUCT, so an authored
`capacity: 128` is indistinguishable from the default) — there the draw wins.

**Domains narrow STATICALLY** against the site's own envelope, the census
coherence sum, and every reachable backend token — so a roll can never decide
whether the world boots. `population.capacityDraw` is TEMPORARILY floored at
`WorldBodiesLimits.CapacityCeiling` (128) because `world.population` crashes
below it; that collapses its domain to a single value until the population lane
lifts the floor.

**Reserved `$` names are ENGINE-MINTED ONLY.** The
rule lives in `WorldStateReservedCells.TryValidateReservedCell`
(`Puck.World.Schema/WorldState.cs`), called from `WorldDefinitionValidator`'s state
walk — which runs at boot, at every live mutation and on every undo-replay entry
— AND from the `UpsertStateCell` compose arm, so a hand-authored file and a
console verb refuse by the same code, with the verb naming it at the verb. A
`$`-prefixed ROW name is refused outright (nothing mints a row; this is also what
keeps `$tick`/`$population`/`$region:` from being shadowed), and so is a
`$`-prefixed RULE name. A `$`-prefixed CELL key is refused unless it is exactly
the key that row's shape mints — `$value` on a slot-addressable row, and nothing
else. (The rule used to police VALUES too, because a generator's draw position
and dealt decks were CELLS an author could hand-write; draw bookkeeping now lives
in typed row FIELDS at the site (`drawCursor`/`drawDecks`), refused by the
field's own range check instead of by a carve-out in the cell namespace.)

There is **no `$type`** and no `rows` member — both are retired spellings of
the pre-collapse shape and refuse as unmapped members like any other stale
field. `kind` is `int`|`fixed`|`bool`|`text`; never float, the determinism
contract. A `fixed` value (`value`, `min`, `max`, or a cell's own `value`) is
a **DECIMAL STRING** through `FixedQ4816.TryParse`/`ToString`, never the raw
Q48.16 bit pattern — only the per-cell mutation wire (`UpsertStateCell`) and
the addon ABI channel stay raw. `min`/`max` are BOTH-OR-NEITHER on a numeric
row (a half-declared range refuses); when both are present every cell must
fall inside — the range a HUD gauge bound to `state.<row>` or
`state.<row>.<key>` reads (see [hud.md](hud.md)). The row `name` and every
cell `key` are `WorldCellName` (`Puck.World.Schema/WorldSafeName.cs`) — a
validated type that cannot hold an empty, unsafe, or DOTTED value, refused at
JSON parse naming the character; the dot-free rule is what makes
`state.<row>.<key>` parse unambiguously (the engine-minted `"$value"` slot
key is the one reserved exception). `nonNegative` is a per-row floor ANY numeric row may
declare, enforced regardless of `min`; `int` + `nonNegative` IS a timer, never
a fifth kind, and the cross-document write-back channel
(`Server.WorldOwnedWorlds.Decide`) reads that same row trait rather than
assuming a floor of its own. Capped at `WorldStateCapacity.MaxRows` (128)
rows, `MaxCellsPerRow` (128) cells per row (which an authored `capacity` may
only NARROW, never widen), and
`MaxTextValueLength` (256) text UTF-16 code units, refused by name past any.

A keyed row may set `evicts: true` to trade its ordinary refuse-on-overflow
capacity ceiling for FIFO drop-oldest: an `UpsertStateCell` write that mints a
brand-new key past `capacity` succeeds and evicts the row's OLDEST surviving
cell instead of refusing (in-place rewrites of an existing key never grow the
row, so they can never trigger it, and never move that key's age — true
insertion-order FIFO, not LRU). Requires a declared `capacity` — refused by
name without one, which also covers a slot row, since a slot never declares
one. The composition itself (`WorldStateCellWriter.ApplyEviction`, `Puck.World.Schema`,
2026-08-06) is a SHARED pure function: `WorldServer.TryCompose`'s
`UpsertStateCell` arm calls it for the running world's own document (so a live
write and every `world.undo` journal re-composition reproduce the identical
victim; the dropped key is named on that write's `[world.mutation: …]` echo,
`"(evicted '<key>')"` — never a silent drop), and `WorldIdentity.TryAppendEvictingText`
calls the SAME function for an owned-identity document write outside the
ordered mutation domain (a self-authored `chat.log`, or a cross-document
`chat.whisper` landing in a bounded inbox — see `authority.md`'s C-CHAT entry)
— one composition, never two readings of the eviction rule.

A row may instead declare `advance` (`WorldStateAdvance`, `rateNumerator`/
`rateDenominator`/`epochTick`) — a CONTINUOUS accumulation trait, complementary
to `rules`' periodicity/cooldown vocabulary above rather than a duplicate of
it. The stored slot cell is a BASE; the read value is `base +
rate*(currentTick-epochTick)`, computed LAZILY (no per-tick write, no journal
entry) via `Puck.Maths.DiscreteMeasure`'s exact rational allocation. The rate is
in the row's own DISPLAYED unit (a `fixed` row's `1/1` is `1.0` per tick, so
`1/240` reads `0.17498779296875` at 42 ticks elapsed, exact); a NEGATIVE rate
mirrors its positive twin rather than flooring the signed quantity (`-1/3` over
43 ticks subtracts 14, not 15). Legitimate only on an int/fixed SCALAR
(slot-eligible) row, never beside `draw`/`capacity`/a non-empty `cells`
array. An explicit write RE-BASES (base=written value, epoch=this tick,
unconditionally — `Server.WorldServer.RebaseCellTraits`, which also runs
inside `world.undo`'s per-entry replay, keyed off each journal entry's own
tick, so undo restores `(base, epoch)` bit-exactly). A declared `min`/`max`/
`nonNegative` CLAMPS the computed value every read without rewriting the stored
base — the read side of the envelope duality. The application lives at exactly
ONE site, `WorldStateReader.TryRead` (below), so `world.state`, a rule's
`compareState`, a HUD gauge and the `UpsertStateCell` Add compose arm all see
the same number: an `add` composes against the LIVE value and then re-bases
(live 41, `add -10` → base 31, still advancing). A row declared with no value
carries no slot cell, so a rule READING it refuses `StateCellUndeclared` until
the first write. Read back on `world.state`'s row line as
`advance=<num>/<den>@epoch<n>`. Keyed-cell advance (a per-cell rate inside a
table) is CHARTERED to the combat wave, not absent by oversight.

`epochTick` is SESSION-relative (a server tick count from process start), so
`world.save` writing it verbatim would leave a reloaded document reading
FROZEN at its stored base until the new session's own tick counter climbed
back past the old epoch — owner ruling 2026-08-06: **settle at save, in the
serialized PROJECTION only.** `WorldSessionCapture.Capture` (the `world.save`
fold, `src/Puck.World/WorldSessionCapture.cs`) writes every advancing row's
slot cell AND every advancing keyed cell's own base as its LIVE value
(`WorldStateAdvance.ComputeCurrentValue`) at the server's completed tick, and
projects `epochTick: 0` — never touching the live in-memory document, exactly
like the render-lever/population/screens folds this same class already does.
Tick 0 of the reloaded session therefore already reads what the save
observed and keeps advancing immediately with no freeze.

Authority is TWO holds, both decided by the one admission predicate
(`WorldServer.TryAdmitMutation`): `Mutate`/`section:state` gates the four
State kinds like any other section, PLUS a second, row-scoped `Edit` over the
CONCRETE `state:<name>` subject (`GrantSubjectKind.State`) or the `all`
wildcard — the SAME subject for the whole-row pair (`UpsertStateRow`/
`RemoveStateRow`) and the per-cell pair (`UpsertStateCell`/`RemoveStateCell`),
narrower authority than any other section (see [mutations.md](mutations.md)
and [authority.md](authority.md)).

A row or a keyed cell may instead declare `dynamics` (`WorldStateDynamics`,
`{row, y0, v0, epochTick}`) — a LIVING trait, mutually exclusive with
`advance`/`draw`/a slot-row's own bare `value` shape the same way `advance`
already is. `row` names a `dynamics` section row (below); `y0`/`v0` are the
follower's initial position/velocity (velocity per second), riding the SAME
per-kind encoding an ordinary cell value takes — raw `FixedQ4816` bits for a
`fixed` row, a whole number for `int` — authored the same spelling too (a
decimal string for `fixed`, a plain number for `int`). On an int row this
rounds the eased value and velocity to whole units at every rebase. The
stored `Value`/cell value remains the TRUTH —
rules, grants, and `world.state`'s `value=` column read it unchanged. A write
REBASES the trait: the live eased sample at the applying tick becomes the new
`(y0, v0)`, `v0` additionally taking a `Retarget` velocity kick sized by the
truth's own jump (so the follower keeps chasing continuously through a
mid-flight rewrite rather than snapping), and `epochTick` moves to that tick
— the closed-form counterpart of `advance`'s own rebase, applied at the same
compose site. The eased value is read LAZILY, on demand
(`WorldStateReader.TryReadEased`), through
`Puck.Maths.SecondOrderDynamics.Evaluate` — no per-tick write, no journal
entry, so a `dynamics` cell costs nothing between reads. `world.state`'s row
and cell lines report the authored trait and its live `eased=` value beside
`value=`; the HUD's `state.<row>[.<key>]` binding reads the SAME eased value,
while an explicit trailing `.$target` facet reads truth (see
[hud.md](hud.md)). `world.save` settles a `dynamics` trait the identical way
it settles `advance`: `y0`/`v0` become the live eased sample at the saved
tick and `epochTick` projects to `0`, so a reloaded session keeps easing with
no freeze.

World/owned-world ids (`Server/WorldOwnedWorlds.cs`) and `world.instance.start`
names are `WorldSafeName` (the same `WorldSafeName.cs`) — the reserved-character
kernel `WorldCellName` shares, plus a bare `"."`/`".."` refusal instead of the
dot-free rule; `WorldOwnedWorldFileName.For` takes a `WorldSafeName` and escapes
nothing, so the id→file-name mapping is injective into file-name STRINGS — but
not into storage LOCATIONS, since the catalog directory resolves names
case-insensitively. One id names one location only under the separate
**case-insensitive uniqueness** rule the two admitting doors hold (the seed-list
validator and `WorldOwnedWorlds`).

Worlds have no in-code definition. A boot with no `--world` override loads
`src/Puck.World/Assets/worlds/nexus.world.json`; an explicit path or the shipped default that cannot be loaded
refuses the boot by name. Four shipped GAME worlds — the charter's whole roster: `nexus` (the hub — a floating
island above a field of planetoids, and the boot default; carries the `references` section naming the other three
plus `studio` by document path, and one `portal-arch` placement per named world), `dive` (the underwater
arena scaffold — the one that also authors `water`), `kart` (the racing arena), `jump` (the platformer arena). A
fifth document, `studio`, ships beside them as a non-game DEV CANVAS for character/creation work — neutral floor,
no scenery or crowd, four anchored camera eyes and a `sheet` layout composing four angles at once — reached with
`--world` or through the nexus's mapped archway. Five quilt documents (`quilt-nw`, `quilt-ne`, `quilt-se`,
`quilt-sw`, `quilt-island`) ship beside them as non-game adjacency/federation stress content — each a `basis`
delta over the `quilt-base` template (see "Document composition" below). Every shipped document layers over
`standard.world.json`, so a change that adds a required top-level section is authored once, there. The loader is
`src/Puck.World/WorldDefinitionLoader.cs`.

## Document composition (`basis`)

`WorldDefinition.Basis` is the document-composition member: a file naming a `basis` (a file path resolved against
its own directory) is a DELTA over that document — templates/prefabs for similar worlds. The mechanism is
`WorldDocumentBasis` (`Puck.World.Schema/WorldDocumentBasis.cs`), invoked from `WorldDefinitionFileSource` on EVERY
file load (boot, `world.load`/`world.reload`, the replay re-drive's apply-boundary re-read, and both neighbour
resolvers), composing on the raw JSON trees BEFORE the strict parse — a partial template cannot model-parse
(required members), so the model only ever sees the finished composition, and the consumed `basis` member is
stripped: a LIVE document always carries `Basis == null`, the validator refuses anything else, and every wire
egress (replica, replay embed) is self-contained by construction. Merge rules: objects merge member-wise
(recursive), omitted inherits, authored `null` clears, a `$type`-changed union object replaces wholesale, and a
row list whose rows all carry the settled identity vocabulary (first of `id`/`name`/`index` on every row of BOTH
sides) merges BY KEY in basis order — new keys append, `{"<key>": …, "$drop": true}` tombstones remove (a stale
tombstone refuses by name), a leading `{"$replace": true}` row replaces wholesale. `$drop`/`$replace` are
compose-time vocabulary only; chains are depth-capped (`WorldDocumentBasis.MaxChainDepth`, 8) and cycles refuse by
name. The content pin folds the WHOLE chain's raw bytes (`ComputeChainContentHash`, length-delimited,
derived-first), so editing a template moves every derived document's pin — flat documents keep the undelimited
single-file pin unchanged. `world.save` preserves the derivation of the file it OVERWRITES
(`SavePreservingBasis`): it peeks the target's `basis` at save time (the file is the one truth — nothing caches
derivation between load and save), computes the merge-inverse diff (`WorldDocumentBasis.Diff`), PROVES it by
re-merging before writing, and degrades to a flat save with a named note when it cannot (basis unreadable,
deleted, or the delta cannot reproduce the document). Read-backs: `world.status` echoes `basis <path|none>`;
`world.save`'s echo names the preserved basis or the flat-save note. Storage composes a synced delta too:
`IWorldDocumentSource`/`WorldDefinitionFileSource.TryComposeChain` generalize the chain walk onto any byte source,
and `Puck.World.Server`'s `WorldStorageDocumentSource` resolves basis members against a flat cloud
`puck/worlds/basis/{name}` namespace (`WorldOwnedWorldSync.BasisAddressFor`) — the storage neighbour resolver and
`storage.pull` both compose before parsing, exactly like a directory load. `storage.push` pushes the whole chain
(each link its own blob) via `WorldDefinitionFileSource.TryResolveChainFiles`, deduplicated per push call when two
owned worlds share a basis. Law suite: `tests/Puck.World.Tests/DocumentBasisLawTests.cs`,
`StorageCompositionLawTests.cs`.

**`standard.world.json` — the standard library, not a world.** The engine ships
no content default: the standard bindings, movement channels, chase rig,
icon/badge table, theme, seat modes and markers are AUTHORED, in
`standard.world.json`, beside the kits, body-motion programs and state rows it
carries. Every shipped world names it as its `basis` (directly, or through
`quilt-base`). Absent now means
absent: `channels` resolves to NONE (a kit whose motion program claims
`MoveAdvance`/`MoveStrafe`/`Turn` refuses by name when nothing declares them),
and `views` resolves to `WorldViewDefaults.Absent`, a placeholder holding the
property non-null between parse and validation which the validator refuses for
any document whose `population.capacity` is nonzero — the same derived refusal
`kits` carries, so a seatless document may still author neither. A world takes
the standard set by naming `standard.world.json` as its `basis`;
`null.world.json` does, authoring only its own `layouts` and
other prototype-specific sections over it.

**Kit motion model (`WorldKit.Motion`, a `WorldMotionModel` row).** A kit
declares WHICH motion model it advances on, alongside `BodyMotionProgram`
(which operations run each tick) — a `$type`-discriminated union
(`WorldDefinition.cs`, the same pattern as `WorldScreenSource`), three arms:
`"grounded"` (`WorldMotionModel.Grounded`, authored under `motion` —
e.g. `jump.world.json`'s `vaulter` kit), `"vehicle"`
(`WorldMotionModel.Vehicle` — anisotropic body-frame drive for the
`ResolveVehicleFrame`/`ShapeVehicleVelocity` ops: longitudinal
accel/brake/coast, lateral grip with a held `DriftChannel`, speed-scaled
steering, a held `BoostChannel` riding the sprint ordinal seam, and
`PitchRate` selecting the flying variant; `kart.world.json`'s kits are the
worked example — its contact-pinned variants pair the arm with a program
keeping `ApplyVerticalGravity`, its flyer with `ApplyVerticalDecay`, which
is what decides vertical contact ownership per the seam's rule), and
`"swim"` (`WorldMotionModel.Swim` — `dive.world.json`'s `diver` kit is the
worked example: thrust/turn rates, buoyancy, the surface band, its own
response table, `moveFrame: "World"` + `facingSnap: true` (camera-relative
swim, facing snapped to the swim direction), and a pinned
`thrustSpeedEnvelope` `{ min: 3.2, max: 3.2 }` — `ThrustSpeed`/
`ThrustSpeedEnvelope` compile STRAIGHT into the SAME shared
`FixedMotionTuning.MoveSpeed`/`MoveSpeedEnvelope` slots grounded's do, so the
grounded-shaped speed resolve is arm-correct for swim with NO fork; only the
swim-specific half — buoyancy, the rise/sink clamp, `FloatDepth`,
`SurfaceSettleRate` — compiles into its own `FixedSwimTuning` record, read
only by the swim-only ops). Grounded carries the jump-kit constants
(rise/fall gravity, the velocity-response table); Vehicle carries its own
drive constants above; Swim carries thrust/turn speed, buoyancy, the
rise/sink clamp, and its own response table (gated on `AtSurface`, not
`Grounded`). Grounded and Swim additionally shape their planar (Swim: thrust-plane)
velocity through exactly ONE of two mechanisms — the engage/release
`Response` table (`MotionResponse` rows, gated on movement regime) or a named
`Dynamics` row (a pole-matched second-order follower — see the `dynamics`
section above); a kit authoring both, or neither, refuses by name
(`WorldDefinitionValidator.ValidatePlanarShaping`). `Dynamics` compiles once
per kit (`WorldKit.Compile`) against the world's own `simulation.rateHz` —
a world authoring no simulation rate cannot compile one and refuses by name.
The follower's state lives in `WorldBody` as ordinary sim state, included in
whatever the body snapshot/checkpoint covers; changing which mechanism a kit
uses, or retuning a live `dynamics` row, is expected to change replay hashes.
All three arms share `SprintMultiplier`/`SprintChannel` — a HELD
(not edge-triggered) channel that scales the commanded planar (or, for Swim,
thrust) speed while it reads held, default `1`/`null` (no sprint) —
Vehicle's `BoostChannel` rides the SAME held-multiplier ordinal seam under a
different name; resolved to `FixedWorldKit.SprintChannelOrdinal` the same way
`WanderFlavor.PressChannel` resolves its own ordinal, since a channel name
needs the world's compiled channel table and a body's own compile step has
none — each arm's `DeclaredSprintChannel`/`DeclaredMoveFrame` helper reads
its own row (Vehicle contributes `BoostChannel` to `DeclaredSprintChannel`
but has no `MoveFrame` of its own, so `DeclaredMoveFrame` defaults to
`Heading` for it). `MoveFrame` (`MotionMoveFrame.Heading` default / `.World`) and
`FacingSnap` — `Heading` is tank controls (the historical default, every
kit that never sets this field); `World` treats `MoveAdvance`/`MoveStrafe`
as ALREADY-WORLD-FRAME axes (the seat's client resolves its camera yaw into
the submitted intent BEFORE the wire — determinism: the sim never reads a
camera pose) and, with `FacingSnap` on, snaps the body's facing to
`Atan2` of the commanded direction every tick carrying input, no ramp — the
camera-frame 3D-platformer feel `jump.world.json`'s `vaulter` kit authors
(Grounded), and `dive.world.json`'s `diver` kit authors (Swim — under
`World`, the aim's elevation also splits the commanded forward into planar
and vertical channels client-side; the explicit `MoveUp` channel is
orthogonal and stays live regardless of `MoveFrame`).

**Response-row ORDER shadows regimes — author air rows first.** The
velocity-response table evaluates in order, first open gate wins, and a
`recently Grounded` window (`0.09s` ≈ 21 ticks at 240 Hz) stays open through
the RISE of every jump — so a recently-Grounded row above a `now Rising` row
governs the first ~21 airborne ticks with GROUND rates (a stick released at
takeoff bleeds momentum at the ground `releaseRate`; air steering briefly
runs the ground `engageRate`). Author `now Rising` / `now Falling` rows
ABOVE any recently-Grounded row; a plain always-row then covers grounded
ticks. `jump.world.json`'s `vaulter` kit is the worked example of the
corrected order, with the measured arc numbers in its motion row.

`WorldDefinitionValidator` cross-checks the kit's `BodyMotionProgram`
against its declared model: an operation the program selects that reads a
tuning facet (`MotionTuningFacet`) the declared model doesn't supply refuses
BY NAME. `grounded` supplies every facet the `grounded` and `free` programs
read (the `free` program's facets are a strict subset), so the world's
`free`-program kits also author a `grounded` motion row. `vehicle` supplies
its own gravity trio (`GravityArc`/`GravityBleed`) plus `VehicleDrive`, and
deliberately none of grounded's planar-shaping facets — a vehicle kit never
authors a `grounded`/`free` row. `swim` supplies its own two facets
(`SwimThrust`, `SwimBuoyancy`) and deliberately none of grounded's gravity
facets — a swim kit never authors a `grounded` row, and a world declaring a
`swim` model with no `water` section refuses at boot (a swim kit implies a
medium to swim in). A further model is another record arm, a new
`CompiledBodyMotionProgram` capability where one is needed (see
`CompiledBodyMotionProgram.OwnsVerticalContactState` — the swim program sets
this `false`, so the contact resolve never writes its vertical channel), and
a new `SuppliedMotionTuningFacets`/`WorldBody.SetTuning` case — never a hunt.

A seated player's live profile overrides the kit's `MoveSpeed`/`ThrustSpeed`
(feel stays real-time under `profile.set`/`identity.motion`);
`Grounded.MoveSpeedEnvelope` / `Swim.ThrustSpeedEnvelope` (owner ruling,
2026-08-06, Swim folded in the same wave `dive.world.json`'s `diver` kit
pins its authored `3.2` through) is the world's own counter-pin — an
authored `MotionScalarEnvelope { min, max }` that clamps the RESOLVED
speed at the seat-time read (`WorldBody.ResolveMoveSpeed`, before the
program ever sees it), regardless of whether it came from the profile or
the profileless fallback. Absent (the default) is wide-open, today's
behavior exactly; `min == max` pins the effective speed outright; the
validator refuses `min > max` and refuses a kit whose OWN `MoveSpeed`/
`ThrustSpeed` falls outside its own envelope, by name. `identity.show`'s
`moveEffective=` echoes what the sim actually applied beside `move=` (the
profile's raw request) — the two diverge only when an envelope is
narrower than what the profile asked for. `MotionScalarEnvelope` is the
reusable shape every arm's own overridable scalar adopts, never a bespoke
bound — both arms compile into the SAME shared `FixedMotionTuning.MoveSpeedEnvelope`
slot (`WorldMotionTuningFactory.Compile(WorldMotionModel.Swim)` passes
`ThrustSpeedEnvelope` into it), so `WorldBody.ResolveMoveSpeed`/
`EffectiveMoveSpeed` are arm-correct by construction with no per-arm resolve.

`ResolveMoveSpeed` is a per-arm dispatch (`WorldBody`'s private
`CompiledMotionArm` — `Grounded`/`Vehicle`/`Swim`, set by `SetTuning` alongside
the compiled tuning) — one resolve shared by the sim and every read-back so
the two can never disagree; a new arm adds a member, a `SetTuning` case, and a
`ResolveMoveSpeed` case, never a hunt. `Swim`'s case rides `Grounded`'s
verbatim (a shared `case` fallthrough) since its speed compiles into the SAME
`m_tuning`/`MoveSpeedEnvelope` slots grounded reads — no separate resolve
needed; only `Vehicle` forks, into its own `m_vehicleTuning`.
`Vehicle.TopSpeedEnvelope`
(owner ruling, 2026-08-06) is the vehicle arm's OWN counter-pin over the SAME
shape, clamping `m_vehicleTuning.TopSpeed` instead of a seated profile — the
vehicle arm deliberately never reads a profile's speed (a kart's speed is the
kit's). `BoostMultiplier` multiplies AFTER the clamp, on the resolved value,
never the raw `TopSpeed`, mirroring how a held sprint scales grounded's
already-clamped rate. `ResolveVehicleFrame`'s steering-authority falloff
anchor and `ShapeVehicleVelocity`'s commanded speed both read the SAME
resolved value (`scratch.MoveSpeed`, filled once before phase 0) rather than
a second `TopSpeed` read, so a clamped kit's falloff still reaches its anchor.
The validator (`ValidateVehicleMotion`) checks `TopSpeedEnvelope`'s SHAPE only
(finite, `min <= max`) — deliberately NOT that the kit's own `TopSpeed`
already sits inside it, unlike grounded's `MoveSpeed`: grounded's own-value
check protects a profileless FALLBACK a separate, unvalidated profile read
can legitimately diverge from, but vehicle's `TopSpeed` IS the live-clamped
read (`world.row.set kits …` retunes it in place), so requiring it to already
conform would refuse the exact past-the-cap retune the envelope exists to
catch.

`views.seatRig` is a `WorldCameraProgram` — an ordered op list, not a kind
union (see views.md for the op table). Its `dynamics` op names a `dynamics`
row (above) whose second-order response the CALLER applies as a
presentation-only ease on the boom; a program with no `dynamics` op passes
the boom through with no ease — a different mechanism from
`WorldAnchor.Group.SmoothRate`'s exponential ease, which is unrelated and
still authored separately for a group-centroid establishing shot.

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
  mutated so a rejected candidate cannot alter the live value holder.
- **State-backed identifiers** use `DocumentIdentifier`: an ordinary string
  remains literal, while a `state.<row>[.<key>]` string reads its identifier
  from a Text state cell. This includes binding-group identifiers on chord,
  context, and wheel rows; creation row/document/shape names; look names and
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
- Embedded Forge documents (creations/tunes/patches) bridge through
  `Puck.Assets.Documents.DocumentJsonOptions.Shared` so the inline embed
  carries exactly the vocabulary its canonicalizer hashes.

**Adding a schema field — the sweep direction.** Adding a top-level SECTION
refuses at boot until every shipped world carries it (through its own
`Absent`/empty placeholder's derived validator rule — see "The validator"
above). Adding a NESTED member does not refuse at parse — it silently defaults, and
usually (not always) refuses at validation — so sweep the shipped worlds
either way. Adding a JSON key with no model member always refuses. Renaming a
member is doubly fatal. One `world.save` re-canonicalizes a file to the
current model.

## Identity conventions

- Screens are POSITION-ADDRESSED by `WorldScreen.Index` (an engine
  screen-surface index); the derived `WorldMachineCableGroup.Screens` and
  `WorldSpeakerSource.Machine` key off the same int — screen index IS machine
  identity for screen-hosted machines. Cable linking itself is authored
  per-machine: a `Machine` source's `cable` port (`WorldMachineCable` — name +
  position), never a row of its own; `WorldDefinition.MachineCableGroups()`
  derives the groups.
- Everything else is string-addressed: stable ids (`WorldSceneRow`,
  `WorldCreation`, `WorldPlacement`, `WorldSpawnPoint`,
  `WorldBindingOverlay`, HUD panels/elements, profiles) or names
  (`WorldCamera`, `WorldKit`, `WorldLook`, `WorldChannel`, `WorldSpeaker`,
  `WorldAddonRow`, `WorldViewLayout`, `WorldStateRow`).
- Spawn points carry both modes deliberately: `Id` is the mutation address,
  but LIST ORDER is seat identity (seat n spawns at `SpawnPoints[n]`).
- Grant rows are keyed by their `(principal, capability, subject)` triple —
  a grant IS that triple (`Exclusive` and the co-drive fields are row data,
  not key). `GrantSubject` and `WorldPrincipal` serialize as the console
  grammar tokens through their own converters (`all`, `body:<n>`,
  `screen:<n>`, `section:<name>`, `state:<name>`,
  `region:<name>`, `seat:<n>`, `creation:<id>`, `placement:<id>`;
  `seat1..seat4`, `console`, `addon:<name>`,
  `peer:<index>:<generation>`, `document:<id>`). A member-wise serialization would permit denormalized
  "phantom grant" keys no table lookup could match. Two asymmetries to know:
  `composition` is a write-only subject token (echoed by `world.grants`,
  rejected on read — only the boot seed constructs it). A peer identity is
  always generation-bearing; reusing an index after disconnect mints a new
  token and cannot inherit the previous generation's grant key. `region:`/
  `seat:` are the world-events feed's subjects (Observe-only, untrusted
  principals only — see references/addons.md's "World events" section);
  `WorldPlacement.Region` (`WorldPlacementRegion`) is the document-side
  facet a region name addresses — a sphere on the placement's own position,
  keyed by the placement's own `Id`. `WorldPlacement.Attach`
  (`WorldPlacementAttach`, new) is a placement's BODY-ATTACHMENT facet — a
  0-based `body:<n>`-indexed target plus a local offset rotated into the
  body's own frame. It derives TWICE off the one authored facet: the
  authoritative fixed-point resolve
  (`Server/WorldPlacementAttachment.TryResolve`, called on demand by
  `world.attachments`, its only caller), and the rendered pose —
  presentation float over the client's INTERPOLATED body pose, packed every
  frame by `Client/WorldStampPool.cs`. Riding the interpolated pose is what
  keeps an attached row as smooth as its body; reading the authoritative
  resolve in the renderer would judder at the tick rate. An attached row
  draws through the reserved stamp pool, never as a static stamp
  (`Client/WorldPlacementStamper.IsStaticStamp` is the one fork), so it
  charges `WorldPlacementPolicy.MaxStampRegistrations` alongside animated
  rows and its authored `Position`/`YawDegrees` are inert. `Region`, `Solid`
  (under the analytic contact provider), and `Emission` no longer refuse
  alongside `Attach` — each now reads the resolved DYNAMIC pose instead of
  the row's static transform (`Server/WorldEventFeed.CollectRegions`,
  `Server/WorldColliderSet.RefreshAttached`,
  `Client/WorldStampPool.TryShapePosition`/`RootPose`), so an equipped item's
  aura/hitbox/voice tracks its carrier; an inactive carrier makes the facet
  contribute/sense/sound nothing, the same verdict the render stamp already
  had. `Distribution`/`Mirror` (static-stamp-only) and `Inhabit` (a row
  cannot both spawn its own bodies and ride another's) still refuse by name
  rather than blend, and `Solid` still refuses under the FIELD contact
  provider (it compiles every solid row's geometry once into one SDF
  program, never rebuilt per tick — `collision.requirements` non-empty). An
  out-of-range `BodyIndex` refuses at author time; a valid but
  inactive/despawned target body makes the row contribute nothing at
  RUNTIME — no refusal, `world.attachments` names the reason and the stamp
  parks below the floor. `WorldPlacement.Contribution`
  (`WorldPlacementContribution`) is the CONTRIBUTION SLOT facet — a host world
  authors the frame and a federation partner fills it. Its two halves never
  mix: `tenure` (`Presence`/`Endowed`), `slotCreationId`, `link` (an
  `adjacencies` row name, required for `Presence` and refused for `Endowed`)
  and `graceSeconds` are AUTHORED; `contributor` and `retractDeadlineTick` are
  SERVER-STAMPED and a submission naming either is refused BY NAME
  (`Server/WorldServer.Contributions.cs`'s `TryComposeUpsertPlacement` reads the
  contributor off the acting principal — accepting an authored one would be the
  laundering the acting-principal rule forbids). An UNFILLED slot shows its own
  `slotCreationId`, so no creationless placement has to be representable, and
  the validator pins the pair. Retraction — the per-tick
  `SweepContributionTenure` pass, once the watched link has read dropped past
  `graceSeconds` — re-points `creationId` back and clears the stamp through
  ordinary journalled mutations, so the host's FRAME stands, only the piece
  goes, and `world.undo` puts it back. It defers (never orphans) while the
  slot's inhabitant is drive-possessed. Read back with `world.contributions`.
  `WorldPlacement.Respond` (`WorldPlacementResponse` rows) is the RESPONSE
  facet — a state-driven prototype swap: an ordered `{when, prototypeId}`
  list, each `when` the SAME `WorldFieldCondition` grammar a
  `fields.reactions` Transform/Expose condition uses, tested at the
  placement's own coupled lattice cell
  (`Server/WorldFieldLattice.TryBodyCellOf`) by the per-tick
  `SweepPlacementResponses` pass — run right after the field lattice steps,
  so it reads THIS tick's own writes. Entries try in authored order; the
  FIRST whose condition holds wins, through an ordinary `UpsertPlacement`
  under `WorldPrincipal.World`; when none holds the row is left exactly as
  it reads — the facet only ever SELECTS on a match, it never reverts a
  prior swap. Refused alongside `Attach`/`Inhabit`/`FaceSources`; every
  candidate prototype (the row's own base and every entry's) must resolve
  to a declared, non-animated creation, and the analytic solid-collider
  ceiling counts the WORST CASE across every variant the row could show.
  Read back with `world.responses`.

## Owned-world identities

An identity is an ordinary owned `WorldDefinition` document, not a catalog
row: `WorldOwnedWorlds` (`Server/WorldOwnedWorlds.cs`) is the CATALOG (seats
select identities from it; a seat's profile IS a `WorldIdentity` wrapping one
owned document), one file per identity under the local state directory,
named `WorldOwnedWorldFileName.For(id)` (`"<id>.world.json"`). Every id is a
`WorldSafeName`, so the mapping escapes nothing and is injective into file-name
STRINGS — but a string is not a storage location, and the catalog directory
resolves names case-insensitively, so **ids are unique IGNORING CASE**. That is
the rule the seed-list validator holds (a case-variant pair refuses at
validation) and the rule every id comparison in the catalog holds
(`FindById`, `Create`'s collision guard, `ReplaceFromSync`'s match, and the
file-name check). A loaded file whose name does not match
`WorldOwnedWorldFileName.For` of its OWN declared identity `id` — ignoring case,
so a case-only rename of a catalog file is ADMITTED and keeps the name it
carries — is refused by name (`[identity] owned world refused: …`,
distinguishing "the name another file in this directory carries" from "a name
no file in this directory carries") rather than silently renamed or merged —
that document parses, so it stays where it is and the refusal names the remedy.

A document the loader refuses is handled by the CLASS of the refusal, and no
refusal is ever a hard boot failure. Only a verdict on the BYTES — the
`{path} is not a valid puck.world.def.v1 document: …` and `cannot decode …`
classes, which include a document with no `identity` section — is DISCARDED:
the file moves into the `unloadable/` subdirectory (outside the catalog's
`*.world.json` top-directory glob, like `basis/`), once, so the next boot has
nothing left to refuse. Every other class can answer differently on the next
boot — `cannot read …` (locked or half-written), `no file at …`, `… basis
composition refused: …` (a chain link not placed yet), `… document validation
refused: …` (which may rest on an adjacency neighbour the sweep is itself
moving) — so those files STAY where they are and are only named. Quarantining
them would cascade: the neighbour resolver reads the same directory the sweep
empties, and the seeding pass would write defaults over every freed name.

Each half narrates as ONE stderr line — `[identity] discarded N unloadable
owned world(s) into '…'` and `[identity] refused N owned world(s) this boot
could not read …` — grouping file names by their shared reason, with the path
itself stripped out of the reason so one fault across a directory reads as one
group and a lone corrupt file stands in a group of its own. There is no
migration and no read-side tolerance for a retired shape: an emptied catalog
re-seeds from `playerDefaults.identities`, and a catalog that still holds
documents simply lacks the discarded ids.

Retention is exact on both sides. A quarantine destination that is already
taken (deterministic file names, and the catalog re-seeds a freed name) takes
an ordinal suffix rather than overwriting the earlier copy, and the seeding
pass SKIPS any seed id whose catalog path still holds a file or directory — so
a disposal whose move failed, or a document left in place, keeps its authored
bytes for the next boot instead of being replaced by a fresh default, and a
directory occupying a deterministic file name cannot crash startup.
`identity.create` refuses an id whose catalog path is occupied for the same
reason, reading the DIRECTORY rather than the identity list — a boot that
admitted nothing leaves that list empty while the bytes are still on disk. Read
back with `WorldOwnedWorlds.Discarded` + `identity.list`'s `discarded=` column
(disposals — the moved bytes stay readable under `unloadable/`) and
`WorldOwnedWorlds.Refused` + `identity.list`'s `refused=` column (everything
left in place, whatever the class).

**Seeding.** When the identity directory holds zero admitted documents,
`WorldOwnedWorlds` seeds one owned world per `playerDefaults.identities` row
(`WorldIdentitySeed(Id, Name, Color)`, validated non-empty, ids and names both
unique ignoring case, hex color — `ValidatePlayerDefaults` in
`WorldDefinitionValidator.cs`) and persists each immediately.

**`WorldIdentity`** (`Puck.World.Schema/WorldIdentity.cs`) is the runtime
handle over one owned document's `identity` section
(`WorldIdentityDefinition(Id, Name, Color, MoveSpeedState, TurnSpeedState,
Controllers)`): `MoveSpeed`/`TurnSpeed` read
and write the owned document's OWN `state` rows named by those state-row
references; `Bindings` is the owned document's own first `bindingOverlays`
row's document (the seat's profile binding layer — see "Binding composition"
below); `Hud` is the owned document's first `Hud` panel (the identity's
PRIVATE seat-scope HUD panel, see `hud.md`).

**Seating.** `SessionRequest.SetIdentity` (gated on `WorldCapability.Drive`
over the targeted slot's body — the same grant `Join`/`Leave` use) sets a
slot's participant to a named owned identity.

**Cross-document durable state.** A body's authored durable-state writes
(`WorldPopulation.DurableStateOutputs`, drained every tick in
`WorldServer.Step`) submit against the OWNER identity's own `state` rows
through `WorldOwnedWorlds.Submit`/`Decide` — gated by a `Mutate` grant the
owner's OWN document declares for the writing document's principal
(`WorldPrincipal.Document(sourceDocumentId)`) over `state:<slot>`; refusals
name a missing source id, an unknown owner, a missing/unknown slot, the
absent grant, the wrong storage kind, an out-of-envelope or negative value,
or overflow. `Save()` re-serializes the owner through
`WorldDefinitionSerialization.Save` to its own file on every accepted write;
`ReplaceFromSync` adopts a pulled cloud copy the same way.

## Binding composition (`WorldBindingComposer.cs`)

The composer is N-ary (`Compose(params ReadOnlySpan<BindingProfileDocument?>)`,
base-first, null layers skipped, mismatched `Version` throws). The four layer
CLASSES are assembled by `src/Puck.World/WorldSeatBindings.cs`: engine
default → every world `bindingOverlays` row in order → the seat profile's
`bindings` → live session rebinds (freshest wins). A row's members are two
lists: `held` (a SET — down in any order) and `chord` (a SEQUENCE — pressed in
that order, tested with the held members removed from the press order); a
member is a modifier id, or a raw source id (`"held": ["mouse.button1",
"mouse.button2"]`) which becomes an implicit default-threshold modifier;
`modifiers` remains for thresholds and named multi-source groups. A page row
applies when its members are satisfied (deepest wins), a command row fires
when the down set is exactly its members; a row with neither list is the
group's resting page. A command row targeting a channel may author
`mode: Toggle`: each chord completion flips the input-side channel latch, and
breaking the physical chord leaves that latch untouched. This is the first-class
authoring model for auto-actions: auto-X toggles channel X without inventing a
bespoke command or simulation state. The standard profile uses held `look` (LT)
plus `gamepad.leftStickPress` to toggle `forward` for autorun, and held `look`
plus `gamepad.rightStickPress` to toggle `up` for auto-jetpack. Each command
chord consumes its stick press before the resting page's bare stick binding can
see it. Toggle contributions are owned by the compiled command destination,
not by the button that flipped them; synthesized chord edges carry that stable
logical source through press, reassertion, and release. Parallel auto-actions
therefore coexist, while several bindings for the same destination operate one
latch. Merge rules: the row key is
(group, sorted `held`,
ordered `chord`); a later layer's row for the same key overrides
WHOLESALE when the meaning differs (a `Command`, or a page under a different
id) and ENTRY-BY-SOURCE when both name the same page: a row's `sources` list
(a control can activate a destination from several physical sources, e.g. a
gamepad button AND a keyboard key) replaces the earlier layer's entries AT
EACH of its listed sources independently — an entry surviving at all of its
sources stays combined, one narrowed to fewer sources by a later layer keeps
only the ones still its own — first-touch-per-layer so a hold/release pair in
one layer accumulates.
The merged document compiles once per change through
`BindingProfile.Compile` in `Puck.Commands` — deliberately shared, never
copied. `WorldSeatBindings` compares the filtered composed document plus the
ordered channel-name map before compiling: a route that presents a new document
instance with identical effective content is a true no-op, preserving held
commands, chord/page state, and release latches. Live surface: `player.bind`,
`player.bindings`.

A page may name `inherits`, the profile-unique id of another page in the same
group. Compilation flattens the inherited page first, then replaces its entries
at every source or activator identity the child declares; untouched bindings
remain active with no runtime fallback lookup. Missing pages, cross-group
inheritance, empty ids, and cycles refuse by page name. The standard
`actionWheel` page inherits `base`, so its right-stick selector override does
not suspend left-stick or keyboard movement while the radial is open.

A chord row's, context row's, and wheel row's `group` may be a literal or a
`state.<row>[.<key>]` reference to a Text cell. All references to one cell
resolve together before the profile is composed, so changing that single cell
renames the relationship consistently instead of requiring a document-wide
search/replace. `standard.world.json` is the worked example — its
`state.world.bindingGroups` row holds `defaultActionGroup`, and every chord and
wheel row names it through the reference.

Each `WorldBindingOverlay` may also carry `bindingBar`: the presentation policy
for the on-screen mapping bar. Absence anywhere in the resolved chain (no
identity row, no world row) resolves to `WorldBindingBarAuthoring.Absent` — NO
bar draws; there is no baked-in C# look any more (the defaults-to-document
conversion: `standard.world.json` now AUTHORS the values that used to be the
`WorldBindingBarLayout.Default` static). The first world row supplies the world
floor; the selected identity's own first row may replace it for that seat,
matching the existing first-row binding-layer consumer in `WorldIdentity`.
`world.binding-bar [on|off|auto] [player]` reads the resolved policy and controls
its live visibility override.

`bindingBar.slotSet` (required, non-empty) names the physical controls the bar
shows by INPUT SOURCE ID (`gamepad.buttonSouth`, `gamepad.leftTrigger`,
`mouse.button1`, …) — the same vocabulary a binding entry's `sources` speak,
validated against `Puck.Input.InputSourceVocabulary` through
`InputSourceVocabularyHook` (the `Puck.Input`-vocabulary seam Schema reaches the
same way it reaches command/channel vocabulary), refusing an unknown id by it, a
duplicate by index, and the whole list past `WorldBindingBarCapacity.MaxSlots`
(32 — a declared document ceiling now that no device enum bounds the vocabulary).
The classic twelve (`BindingBarLayout.SlotSources`) render in their fixed
compass-diamond positions regardless of authored order; `gamepad.back`/
`gamepad.guide`/`gamepad.start` (`CenterSources`) render as a fixed three-slot
row above the anchor, left to right in that real-controller order regardless of
authored order; every other id (touchpad, mute, the grips, a mouse button, …)
renders in a row further above, left to right in AUTHORED order —
`BindingBarLayout.Categorize`/`Place`'s documented placement rule.

`bindingBar.banks` (required, 1..`WorldBindingBarCapacity.MaxBanks` = 5 — the
WoW-addon original's five chord states: resting/LT/RT/LT>RT/RT>LT) is a keyed
list of `(id, pageId, order, alpha, activeAlpha?, offsetX?, offsetY?)` rows: each
bank renders the WHOLE authored `slotSet` against its OWN named page (a
`BindingPageDefinition.Id` — validated to exist somewhere in the COMPOSED
binding profile, checked after `BindingProfile.Compile` succeeds, since only the
whole overlay stack's result can answer that), displaced from the bar's shared
anchor by an arrangement the ENGINE derives from `order` alone (unique per row;
`BindingBarLayout.BankOffset` uses a fixed nested-cross table in button pitches:
order 1 nests up and inward, 2 down and inward, and 3/4 sit straight above/below;
later orders alternate farther above and below) — `offsetX`/
`offsetY` are optional per-axis overrides for a world that wants one bank placed
by hand. Each draws at its authored `alpha` — or `activeAlpha`
(default 1.0) when that bank's page is the seat's CURRENTLY active one. A
player's own `BindingProfileDocument.BindingBar` (stored in the identity
document's `bindingOverlays` section)
(`Puck.Commands.BindingBarPreferences`: `hideUnbound`/`stacked`/`scale`, all
nullable, LOOK only — never a binding) overrides the world's `hideUnbound` and
adds a `stacked` toggle (render every bank vs. only the seat's active one — falling
back to every bank when none of them actually names the active page, rather than
drawing nothing) and a `scale` override, resolved in `WorldBindingBarControl.Status`.

`bindingBar.text` (default `true`) is the bar's ATLAS-TEXT switch: `false` drops
every text run the bar writes — every badge whose authored icon row carries a
`label` (`LB`/`RB`, `LT`/`RT`, `LS`/`RS`, the menu trio, the exotics), the
active page's name under the modifier
indicators, and the chord-hint lines above them — leaving a purely pictographic
bar: the plates, the PROCEDURAL badge glyphs (the d-pad arrows and the
face-position diamonds), the bound actions' icons, and the indicators all still
draw. The policy resolves ONCE
per seat in `WorldOverlayFeed.Tick` and shapes what it publishes (a suppressed
badge is `OverlayResolvedGlyph.None`, a suppressed label the empty string, suppressed
hints an empty span — each already a case `BindingBarWriter` draws nothing for),
so the writer carries no text policy of its own. `standard.world.json` authors
`"text": false`; `null.world.json` keeps the text (its showcase banks name their
page).

Every rendered slot's `Pressed` state reflects the PHYSICAL control's live carry
(`BindingBarSeatComposer.IsPhysicallyPressed`, resolved once from the seat's
ACTIVE page view by input source id and reused across every bank showing that
control — a control's momentary press state does not depend on which bank/page is
drawing it). Badge content comes from `icons.badges`, keyed by the SAME input
source id: `WorldIconTable.ResolveBadge` is the one door a slot, a modifier
indicator, and a chord hint all go through, checking the row's per-family
override (`Puck.Input.Devices.GamepadType` member name) before its default icon.
A source with no badge row simply draws no badge, so badging a control the
gamepad vocabulary never named (`mouse.button1`) is an authoring act, not a code
change.

**Overlay visibility (`visible`).** Every overlay element — a `hud.panels` row, a
seat's player-scope panel, `hud.defaults` (the gate over every world panel),
`hud.defaults.cursor`, and `bindingBar` — takes an optional `visible` predicate
over per-seat presentation facts (`OverlayPredicate` / `OverlayFact`,
`WorldOverlayVisibility.cs`; evaluated by `WorldOverlayFacts`): `now {fact}`,
`recently {fact, windowSeconds}`, `all`, `any`, `not`. Facts: `SeatInput` (a
routed signal this tick), `PointerMotion`, `WheelOpen`, `ConsoleOpen`,
`SeatCameraApplication` (the seat's camera control application is active —
`WorldSeatBindings.IsCameraModeActive`).
Absent = always visible. `{ "$type": "recently", "fact":
"SeatInput", "windowSeconds": 3 }` hides an element three seconds after the
seat's last input and shows it on the next; a world-scope panel reads a fact as
true when it holds for any joined local seat. Windows compile through the world's
simulation rate; nothing here enters the simulation.

**Context rows.** `puck.bindings.v1` carries an optional `contexts` section:
`{family, state, group}` rows (`BindingContextDefinition`), merged across
layers on `(family, state)` — a later layer overrides a re-declared key IN
PLACE, new keys append, so precedence order is authored primarily by the
layer that ships the vocabulary. The seat's ACTIVE group derives as: first
matching context row's group (document order) → the seat's requested group
(`WorldSeatBindings.SetActiveGroup`, the mode pointer) → the profile default.
A family is one of three kinds: a BUILT-IN engine family
(`WorldContextFamilies` — the output of one per-seat single-valued state
machine): `roster` publishes `unjoined|claimed|pending|active`, `engagement`
publishes `engaged|none` (a loopback read of whether the grant table's
control-application set names anything beyond the seat's own body, synced once
at post-build wiring and every tick post-step via
`WorldSeatContextSync.Publish`), and `layout` publishes the window composer's
active layout selection (an authored `views.layouts` name, or `builtin`) — an
OPEN-states family (`WorldContextFamilies.IsOpenStates`): any state token is
admitted, and a token matching no authored layout simply never matches; a
`state:<row>` family (`WorldStateBindingContext`) reading the routed world's
scalar/keyed state; or an AUTHORED `seatModes` family (`WorldSeatModeFamily`,
document top-level `seatModes`) — a world-declared name plus its admitted
states, flipped by `player.mode <family> <state> [seat]` and validated
strictly (unknown states refused, the name may not collide with a built-in
family or the `state:` prefix). A state whose `target` is `"camera"` composes the camera control application:
the seat possesses its authored `camera-seat-<slot>` inhabited placement
through the ordinary Engage door, its own body intent diverting to
`body.control`'s idle contract, and its view resolving through
`views.cameraRig` (see views.md). `player.camera [seat]` is the bindable
no-token toggle onto the same state. Compile refuses a malformed/duplicate row or
an undeclared group; the vocabulary gate refuses an unadmitted family/state —
all by row. `player.bindings` leads with the derivation echo:
`group=<active> (<step>)`, per-family `<family>=<state>→<group>
(wins)|(shadowed)|(no row)`, and `requested=<group>` marked `(shadowed)` when
a row overrides it — a matching row that lost to an earlier row is reported,
never silent. `null.world.json` authors the shipped example: a
`{layout, rts, rts}` row flips the seat onto a wheel-only `rts` group while
its RTS layout override is active. Every group needs a resting (empty-chord)
page — a blank-slate group authors one with an empty `entries` list.

**Wheel rows.** `puck.bindings.v1` also carries an optional `wheels` section
(`BindingWheelDefinition`: `{id, group, holdPages, rings, style}`). `id` is
profile-unique and is the merge/runtime-continuity identity: several radials
may share one binding GROUP, and a later layer replaces a re-declared radial
WHOLESALE by id. `holdPages` is a non-empty list of distinct chord-row page
ids from that same group. Any one of those pages presents the radial, so an
author may bind several physical openers to one wheel (Tab and LT, for
example), while other hold pages in the group present different wheels (LT
for wheel A and RT for wheel B). Releasing one opener defers commit while
another hold page still presents the same radial.

Rings are ordinary `BindingPageDefinition`s worn as concentric shells
(`BindingProfile.Compile` bounds: 1–3 rings per wheel, 2–8 sectors per ring,
ring page ids sharing the document-wide page-id namespace). A SECTOR row
narrows the page-entry shape to a command destination plus label/icon and an
optional constant `value`/`activateOn`; `source`, `activator`, `channel`,
`scale`, and a non-default `mode` refuse by name. The compiler mints an opaque
`BindingActivation` for each sector, and commit returns it through
`InputRouter.Activate` in the originating seat's deterministic lane. The
vocabulary gate therefore requires every sector command to exist, be
Bindable, and accept the authored value kind — sectors are not console
lines.

`style` is optional authoring policy. `pointerSelection` is `Angle` (direction
alone beyond the dead zone), `HitTarget` (the pointer must remain in the
authored annulus; reusable by a future touch adapter), or `Disabled`;
`placement` is `Pointer` (opening pointer position, with viewport-center
fallback) or `ViewportCenter`. Authors also control dead-zone/ring/grace
fractions, rotation, clockwise ordering, and the initial ring. `axisDeadZone`
is the normalized explicit-ring Axis2D neutral threshold, independent of the
visual/spatial `deadZoneFraction`; excursion-controlled wheels instead use
`excursion.deadZone`. `selectionGraceSeconds` is the neutral dwell before an
empty commit becomes a cancel: a quick throw remains selected during that
window, while holding the selector centered beyond it clears the command. Each
return to neutral completes one selector excursion, so another flick in the
same direction begins a fresh excursion even when its peak is weaker than the
last. Direction remains live throughout an active excursion, including a
constant-radius rotation whose magnitude never sets another peak. On return to
neutral, the wheel retains the last direction at or above `switchFraction`, or
the excursion's peak when a short throw never reached it. `switchFraction` is
also the magnitude an opposite-side excursion must reach and the magnitude a
different sector must reach while grace holds the prior sector; raise it to
reject stronger spring rebound, or lower it to admit lighter direction changes.

`ringSelection` is `Explicit` (the default: `player.wheel.ring` bindings and
pointer-wheel notches step the active ring) or `Excursion` (neutral-relative
selector magnitude chooses it). Excursion requires an `excursion` object:
`deadZone` is the inclusive magnitude that selects no ring; `thresholds`
contains exactly N-1 ascending boundaries for N rings; `hysteresis` supplies
the retained band on both sides of each boundary; and
`spatialTravelFraction` says what fraction of the seat viewport's smaller
extent equals pointer/touch magnitude 1. Axis2D magnitude is already
normalized. The final ring has no outer bound. Each spatial gesture captures
the first available device position as neutral—even when that position arrives
after the opening frame—and never moves that origin until close. Placement is
therefore visual only. With `HitTarget + Excursion`, ring choice uses distance
from captured device neutral while sector eligibility/direction remains
relative to the displayed hub; this deliberately permits direct mouse/touch
targeting and gamepad excursion on the same authored radial.

Selection, ring navigation, commit, and cancel sources are ordinary entries
on each hold page. The engine default uses Tab and authors right-stick
selection; the four shipped worlds currently replace the `play-primary`
radial with one six-sector action ring. `WorldWheelFeed` owns presentation,
and `world.view.wheel` reports the live wheel, hover, effective selector dead
zone, and neutral-grace duration.

## Capacity constants

- `WorldBodiesLimits` (`Puck.World.Schema`): `CapacityCeiling = 128`,
  `LocalSeatCount = 4` (indices 0–3) — single-sourced against
  `WorldClient.EntityCapacity` (the F3 reconciliation, 2026-08-06; see
  [SKILL.md](../SKILL.md)'s "Boundaries" section). There is no
  `MaxPopulation`/`MaxPopulationSimulated` constant; shipped worlds author
  `networkPlayers: 124` (128 minus the 4 local seats) as ordinary document
  data, not an engine ceiling.
- `WorldHudCapacity` (`WorldHud.cs`): see [hud.md](hud.md).
- `WorldStateCapacity` (`WorldState.cs`): `MaxRows = 128`,
  `MaxCellsPerRow = 128` (an authored `capacity` may only narrow it),
  `MaxTextValueLength = 256` (UTF-16 units, a text cell's value), and
  `MaxBodySlots = 128` across the `body` and `identity` lanes (the fixed
  per-body register/checkpoint width).
- `WorldDynamicGeometryCeilings.MaxContributedDynamicInstances = 16000`,
  the document-global CPU/instance-grid admission ceiling. The recorded
  GPU-bound measurement is 0 but does not govern admission.
- `WorldPlacementPolicy`: `MaxShapesPerStamp = 48`,
  `MaxStampRegistrations = 8`, `TimelineSecondsPerFrame = 8f/60f`, and the
  reserved derived-face screen band.
- `WorldRenderEnvelope.cs` — the render-capacity oracle: `Configure` at boot
  from the probe, `TryFit(candidate)` at every apply; unconfigured reads as
  "fits".

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
- `WorldState.cs` — the `state` section: `WorldStateSection` (world/body/identity ownership lanes),
  `WorldStateRow` (the document-cell substrate — `kind` int/fixed/bool/text,
  `value` sugar or `cells`), `WorldStateCell`, and `WorldStateCapacity`.
- `WorldStateCompilation.cs` — the immutable typed descriptor catalog over the
  state section: ownership lane, storage shape, value kind, stable handle and
  lane-local ordinals, plus one-time `(lane, name)` resolution for runtime
  processors.
- `WorldDefinitionRows.cs` — the one row-find per section
  (`FindCreation`/`FindPlacement`/`FindKit`/`FindSpawnPoint`/`FindStateRow`),
  ordinal and allocation-free.
- `WorldStateReader.cs` — the ONE `(definition, rowName, key, tick)` →
  `(row, rawValue, text)` read over the `state` section. Every live read
  routes through it: a rule's gate comparand and live copy operand
  (`WorldServer.ReadStateCell`), a rule effect's read-modify-write
  (`FireWorldRuleEffect`), the `world.state` read-backs, the HUD
  `state.<row>[.<key>]` binding (`WorldHudBindingResolver.ResolveState`), and
  the `UpsertStateCell` Add compose arm. A null `key` means the slot cell; an
  unknown ROW returns false, a known row missing the cell returns true with a
  null `rawValue`. **The `tick` parameter is what an `advance` row's value is
  computed at** (see the `state` section above) — it returns a RAW value
  rather than a `WorldStateCell` precisely because a computed value has no
  stored cell to hand back, and minting one per read would allocate on the
  per-frame HUD path. The whole-document validator and the durable
  identity-document reads (`WorldIdentity`, `Server/WorldOwnedWorlds`)
  deliberately stay OFF this seam — the validator owns a name-keyed map
  (routing would make it quadratic), and an identity document has no server
  tick to pass, so a row it carries reads FROZEN at its stored base.
- `WorldSpeaker.cs` — speaker rows, feeds, emission facets, tune/patch asset
  rows, audio defaults.
- `WorldColor.cs` — golden-ratio index palette for simulated avatars.
- `WorldHostTokens.cs` — the one spelling for backend/surface-format tokens
  (JSON converters + the `world.row.set host` payload grammar).
- `WorldAdjacencies.cs` — the authored boundary rows, fixed frame compilation,
  crossing test, derived overlap, reciprocal hysteresis, and corner topology;
  see [adjacency-and-federation.md](adjacency-and-federation.md).
- `ShadowTier.cs` — the tier↔scale map `world.save` folds live shadow reach
  through.
- `BindingVocabularyHook.cs` — the static injection seam the composition
  root wires with a `[ModuleInitializer]` (`WorldDataHookInstaller` in
  `Puck.World`) so validators reach the input vocabulary without this
  project referencing `Puck.Input`.

## Verifying a change here

No engine gate. Build (`dotnet build Puck.slnx -c Release` — architecture
lanes + XML-doc diagnostics) and RUN `Puck.World`, round-tripping the
affected document over stdin (`world.status`, `world.save`, `world.load`).
Proven in-process by `tests/Puck.World.Tests/StrictParseLawTests.cs`.
Validate HUD document changes by running the app — see [hud.md](hud.md)'s
"Verifying" section for the recipe.
