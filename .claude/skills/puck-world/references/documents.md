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
`Looks`, `LookAssignment`, `Dynamics` (see below), `Curves` (`WorldCurves.cs` — the named
curvature-first spline table; see `maths-usage` for `Puck.Maths.CurvatureSpline`, the
compiled primitive each row's `Compiled` property derives from), `Grants`, `Hud`, `State`,
`InputHold` (its own type, `WorldInputHoldAuthoring`, is the AUTHORED seconds
shape — `WorldDefinition.CompiledInputHold` is the compiled ticks form
runtime code consumes; see `WorldInputHoldSettings`'s remarks), `Rules` (see
below), `Identity`, `Groups`, `Properties`, `Interactions`, `Generation`,
`Generators`, `References`, `Portals`,
`Simulation`, `Destinations` (`WorldDestinations.cs`), `Admission`
(`Protocol/WorldAdmission.cs`, the one trust list every ingress crosses —
key-bearing rows for the QUIC identity door, keyless `federatedAuthority` rows
for travellers an authenticated authority hands over; deny-by-default, an
absent/empty section admits neither), `Adjacencies` (`WorldAdjacencies.cs` — invisible
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
Groups, Properties, Interactions, PlayerDefaults, Probes, Dynamics,
Curves`.
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
rule restyles a body — `puck.world.frozen.json`'s `look-*` rules drive one
`lookOf.<body>` cell per body. A text row also takes a `fromState` copy from
another text cell. Two indirections make "the body my `target` cell names"
addressable: a key spelled `$cell:<row>:<key>` resolves to that cell's integer
value at every read/firing (effect `key`/`fromKey`, `compareState`
`key`/`comparandKey`), and a body-reference token `cell:<row>:<key>` does the
same inside `$distance:`/`$los:`/`$nearest:`. `$bind:<name>` reads a value the
enclosing rule's `bindings` list computed for this evaluation (feed-forward,
declared order, never stored). Any `expression`/`left`/`right`/`score`/affinity
member accepts an infix string (`"min(damage, hp[$each]) * 2"`, C precedence,
named forms as calls, `row[key]` reads, backquoted names, `0x` literals) as
well as the postfix `{ "tokens": [...] }` object; the string parses to the same
tokens (`WorldExpressionSyntax`) and writes back as a string. `$table:<name>[:<column>]:<key>` reads a static
`tables` document (`puck.table.v1`, hash-pinned, outside simulation state) by an
integer literal, a `$cell:` indirection, `$each`, or an int `$bind:`; a missing
dynamic key is a `TableKeyMissing` refusal, never a value. Every top-level
state effect is its own boundary; only a `transaction` groups effects
atomically. `$symmetry:<function>[:<argument>]:<row>`
reads a cell holding a symmetry-lattice node (0..239) through `ring`, `antipode`,
`canonicalRay`, `cycle:<steps>`, `reflect:<node|cell:<row>[.<key>]>`,
`orthogonal:<node|cell:…>` (1/0), `innerProduct:<node|cell:…>` (−2..2; 1 is a
sixty-degree neighbour) or `projectionX`/`projectionY` — the row is the
last token, `key` addresses the cell as usual, no node reads −1 (0 for
orthogonal/innerProduct/projections); `world.symmetry <node> [other]` echoes the
same maps. A `cycle` trait's generator is `word` (one to eight mirror nodes) or
the lattice's own cycle, raised to `power` per step; the period is the word's
derived order (`world.symmetry.word <mirror>... [node:<n>]` prints it and a
node's orbit). A `symmetryOrbit` generator source draws a node uniformly over
`ring` or over `node`'s orbit under `word`, dealing the orbit under `mode`.
`$nearest:<bodyRef>:<row>` is the
nearest other active body whose cell in keyed `<row>` is nonzero (−1 for none,
ties to the lowest index) — `puck.world.frozen.json`'s `auto-target` rule is
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
and `WorldStateRow.IsKeyed` is the discriminator: one switch over the row's
declared `Domain` (`WorldStateDomain` — `Slot`/`Keys`/`KeysOf`/`CellsOf`/`Ring`;
an unauthored row infers `Slot` or `Keys` from `cells`/`capacity`/`phase` alone
(a `phase` row has no single value to read even before its first participant,
so it infers `Keys`), so a plain row spells nothing new), exhaustive with
`IsSlot` by construction. A row
with no cells at all still infers `Slot`, since the first write mints its slot
cell. `CompareState` may instead name
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
An optional `decision` facet requires Level mode and owns its own cadence,
commitment, and rising-edge interruption instead of the ordinary latch.
Options reuse predicates, typed expressions, and effects; common and selected
effects run only on entry, not every held tick. `world.decisions` is its runtime
read-back, and `WorldRuleWorkBudget` includes its conservative worst-case work.
An option's `neighbors` expands a forEach body observer into bounded nearby
individuals, binding left/each to the observer and right to the candidate only
inside that option. Inspect candidateBudget as well as maxCandidates: rejected
points and incumbent rechecks consume attention. Incarnation-addressed choices,
not merely option ordinals, own commitment and entry transitions. Positions freeze
before ordinary rules; state gates still read in normal document order.
See the Schema README's `decision-policies` section for the complete authoring
contract. Keep choice state, local random draws, and timers in checkpoint/hash
coverage; refresh compiled handles while retaining unchanged policy episodes.
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
`src/Puck.World/Assets/worlds/puck.world.frozen.json` is the worked example: two
boot-declared region placements (`firePit`/`icePool`), `Region` interactions
that set countdown cells, and `Level` rules that drain/countdown them.

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

Runtime rule operands compile world-row names to catalog-bound handles. Keyed
reductions and arg-extrema resolve the row once and evaluate each candidate once,
so aggregate cost is linear in cell count. Rule numeric literals are exact JSON
decimal values (not binary32); integer values beyond 2^24 retain their low bits,
and fixed literals lower through the invariant Q48.16 parser. Contiguous state
effects in one rule are preflighted as one candidate and apply atomically. A
value-only `UpsertStateCell` uses targeted cell validation/install; declaration
changes still use whole-document validation and rebuild the affected compiled
surfaces.

An advancing trait's compiled rational lives in a weak external cache, never in
record equality. Dynamics `y0`/`v0` are always raw Q48.16 continuous-state bits,
even for an integer target. Cycle save projections carry `substepTicks` in
`[0,ticksPerStep)` so reload preserves the next transition, not only the value
visible at the save tick.

`world.state.hash` defaults to the historical `capture` digest and accepts an
explicit `capture|pose|world|authoritative` scope. `capture` remains the manifest
digest (pose plus resolved `state.world` values); `pose` is the replay pose fold;
`world` includes stored state traits plus resolved values; `authoritative` adds
the tick, poses, rule/interaction edge latches, per-body body/identity action
registers, and live field-lattice cells. Use the named authoritative scope when
future-decision state, rather than manifest compatibility, is the assertion.

```json
"state": {
  "world": [{"name":"score","kind":"int","value":0,"min":0,"max":1000}],
  "body": [{"name":"jumpUses","kind":"Counter","initial":0,"resetFact":"Grounded"}],
  "identity": [{"name":"stance","kind":"Counter","initial":0,"playerWritable":true,
    "envelope":{"$type":"set","values":[0,1,2]}}]
}
```

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
followers), a camera program's `dynamics` op (the boom ease), a kit shaping
row's `dynamics` facet (planar velocity shaping — exactly one of `dynamics`
or `along` on that row, never both, never neither), and a
`state` row/cell's `dynamics` trait (the eased read, above).

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
`WorldDynamicsRow.Compiled` precedent) rather than duplicated in the
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
below. Missing expressions read one, results clamp
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

A kit's `rigid` facet (`mass`, `restitution`, `friction`, `rollingFriction`,
`linearDamping`, `angularDamping`) hands its bodies to the rigid solver
instead of a locomotion program — see
[the server reference](../../../src/Puck.World.Server/README.md#rigid-dynamics-worldbodyrigidcs-worldpopulationrigidcs).
`mass` is required and positive; the other four are non-negative per-second
decay rates, never per-tick fractions. Requires `collider` (sphere, capsule,
or box — never `fromCreation`) and `bodyContact: solid`.

### `navigation` — bounded surface, flight, and medium routes

`WorldNavigation.cs` owns named finite domains. `surface` samples SDF ground,
step/slope limits, a vertical capsule, and swept neighbour edges; `volume`
uses swept-sphere cells and edges in three dimensions; `medium` adds a named
`state.world` lattice row carrying `lattice.medium`, checked live at nodes and
half-cell-or-shorter swept boxes so field evolution can invalidate a cached edge.
Each piece checks every intersected voxel and its local free surface (at most 27),
not just corners or point samples that can miss dry pockets.
Volume connectivity is authored as 6/18/26 neighbours, with blocked-axis
corner cutting refused. A `BodyTargetSource.Navigated(domain, register)` keeps
the ordinary authority-checked designation as its goal and supplies bounded
fixed-point A* waypoints to `ProduceSteeringIntent`'s approach shape; volume and medium targets
also drive `MoveUp`. Stable ties are `(f, h, nodeOrdinal)`. Static edges bake
once, search arrays are reused, and a body's route array allocates on first
use. Domain/cell/search/path ceilings are representation bounds, reported by
`world.navigation`, `body.targets`, and `world.budget`; `$nav:<bodyRef>:<facet>`
is the rule operand. Routes are local runtime state: clear them on producer,
designation, transfer, or domain-rebuild discontinuities; checkpoint and hash
them wherever uninterrupted simulation continuity is promised.

Optional domain `shared: { goalCapacity, expandedNodesPerTick }` replaces per-body
A* searches with queued reverse-Dijkstra destination trees. Domain + goal cell
is the sharing key; never bind cache ownership to a leader or body generation.
Each body still owns its copied path and cursor, and uses its exact designated
point at the end. Domain profiles partition clearance/topology/medium compatibility;
shared volume/medium users must fit the domain's root-centered clearance sphere.
Searches take deterministic round-robin turns under the domain's aggregate
per-tick expansion budget, each visiting at most 26 predecessor edges. Pending
requests pin resident trees; otherwise eviction is LRU using unique, contiguous
recency ranks (never saturated counters). Full pinned capacity
reports `$nav:<bodyRef>:capacity`, queued work reports `pending`, and neither
means `unreachable` or permits an unbudgeted fallback. `maxPathNodes` still bounds
extraction; a shared tree may settle the whole domain rather than stopping at the
independent A* `maxExpandedNodes`. Hard totals bound cells × goals and per-tick work.
Checkpoint/hash discovered costs, successors, settled flags, pending starts,
ages, and scheduler cursor; derive heap layout and cached hashes. Node hashes
use canonical 64-cell blocks with dirty-block invalidation, while pending hashes
sort the bounded request list; never scan a settled domain every replay tick.
Referenced-medium writes reset
the affected trees (not writes to unrelated fields), and obsolete trees are
canonical empty state at checkpoint/hash time. Restore field values before
restoring navigation's derived invalidation stamps. This is not incremental
repair, hierarchical routing, crowd collision avoidance, or group membership;
see the server README's Navigation section for the current limits.

### `state.lattices` + the `lattice` row trait — the lattice (scalar rows, reactions, lattice-derived geometry)

`WorldFields.cs` (the compiled composite) + `WorldState.cs` (the document
spelling). `state.lattices` declares one or more topologies (name, origin,
`cellSize`, `width` × `depth` × `layers`, `stepEveryTicks`, `reactions`); at
most one is `Field`-kind and drives THIS trait (`WorldTopologyCompilation.
FindPhysical` — reactions, lattice-derived geometry) — the rest are discrete
`Grid`/`Ring`/`Hex` topologies, though only `Grid` carries the rectangular X/Z
frame the `board` facet resolves against: a placement's `board` facet
(`$board:cellOf`/`offset`, `world.tabletop`) anchors to a `Grid` topology
alone (`Ring`/`Hex` refuse it), and a `Grid`'s `cellSize` must quantize to a
positive Q48.16 value — it is the divisor `$board:cellOf` resolves world
positions against (the garden's `chessBoard` alongside its own `pondBasin`
water field — see `Puck.World.Schema/README.md`'s tabletop-primitive
section). A discrete topology's own `directions` (optional; each kind's
compass/space names are the unauthored default) replaces its whole direction
vocabulary — see the schema README's discrete-boards section for the
authoring shape and validation. Field-shaped
state rows: `{"name": …, "kind": "fixed", "domain": {"$type": "cellsOf",
"topology": …}, "field": {"initial"/"min"/"max", optional
"heightScale"/"color", "paint": […]}}` — `domain.topology` names the
`Field`-kind topology (the same `cellsOf` case a discrete board's own domain
uses; which storage a `cellsOf` row gets is an implementation choice keyed on
`kind`, never a second authored trait), and `lattice` (`WorldStateFieldTrait`)
carries only what is left once the topology moves to `domain`.
`WorldFieldsSection.Compile` assembles the runtime composite
the engine consumes (`WorldDefinition.Fields` is that compiled view — never
an authored section; there is no top-level `fields` member any more). A cell
write against a lattice row (`world.state.cell.set`) refuses through
whole-document revalidation — the lattice's cells are simulation state, not
authored cells. Rows are seeded by their trait's `paint` rectangles and
evolved by the topology's `reactions` in document order each step: `diffuse`, `decay`, `transform`
(`when` conditions on the cell → `then` set/add writes), `emit` (bodies tagged
nonzero in a keyed row deposit into the cell they stand in), `expose` (writes
1/0 into a keyed row per body by a field test at the body's cell — the bridge
to body-level chemistry), `flow` (moves a field downhill, mass-conserving,
over the combined surface height of itself plus its `over` terrain fields;
each cell donates an equal share of its previous-step value to each of the
lattice's active-axis directions; an optional `spillRow` catches an edge
cell's outward share — without one, edges are walls). A row with `heightScale` IS geometry: its value
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
The authoritative `WorldFieldLattice` executes this program directly beside
the complete companion composite; it never lowers the authored reaction rows
again. Compatible live reaction edits replace the program while preserving
cell values, deltas, revision, and checkpoint shape. A lattice presence,
topology, cadence, or field-envelope change is an allocation change and
refuses live with restart guidance. `world.fields` reports the installed node
order, dependency edges, and pass counts after its cell statistics.
`layers: 1` is a ground lattice; more layers is a voxel volume and costs
proportionally. A lattice carries at most 262,144 cells so a full eight-field
primer (eight lattice rows) remains inside the federation frame; when any row has `heightScale`,
the XZ footprint is at most 126 × 126 cells and the sum across layers may raise
at most 126 cells, fitting the padded 128³ render brick without truncation.
Cell values are sim state beside the population — stepped
after the rules, checkpointed (`Fields` block), delivered as `FieldCells`
deltas on the snapshot (`FieldsFull` on a primer) — never document rows, so
nothing journals them. Read back with `world.fields`. `puck.world.frozen.json`'s
island paints grass beside an ice glacier; a burning body emits heat, heat
ignites grass, fire emits heat and consumes grass, heat melts ice into water,
water quenches fire — no interaction names the boundary.

### Authored randomness — SOURCE x SITE x MOMENT

One primitive, three separable parts. A **source** is a shape, a **site** is a
place that draws, a **moment** is when.

**Source** (`WorldGenerator`) is the document's whole randomness vocabulary.
`source` selects the shape and each shape reads a DISJOINT field set — a foreign
field refuses BY NAME, including `bound`/`mode`, which are non-nullable and are
refused against their declared defaults:

- `markov` — `start`, `bound`, `mode`, `contexts` (weighted alternatives, each
  naming the context it moves INTO). Writes TEXT; exhausts per context. One
  emission is one walk from `start` to a TERMINAL context (one declaring no
  alternatives), refusing by name at `bound` rather than truncating. `mode` is
  `withReplacement` (default), `withoutReplacement` (drawn out → refuse by name)
  or `restartOnExhaustion`.
- `uniformRange` — `rangeMin`/`rangeMax`, both or neither. One numeric draw;
  refuses a `mode`.
- `weightedNumeric` — `weighted` (`{value, weight, multiplicity?}` rows) and `mode`.
  One numeric draw; under an exhausting `mode` the outcomes are drawn through the
  site's single `drawnMasks` mask — the numeric shuffle bag. `multiplicity` (also
  on a Markov alternative) is that many units per pass; a set's units total at
  most 256.
- `streamDraw` — no fields. One raw 32-bit draw; refuses a `mode`.

The alias table over a source's full entry set is compiled once per
`WorldGenerator` instance (`WorldGeneratorEngine`, a `ConditionalWeakTable`), so
per-tick draws do not rebuild it; a drawn-down pool is rebuilt allocation-free
in bounded stack storage per emission, with the identical alias mapping.

A lattice row's paint may carry one `draw` fill (`WorldLatticeFill.Draw`,
`{ "$type": "draw", "source" | "generator" }`, numeric sources only): the
per-cell lattice draw. It is one whole-field pass of the row's stream
(`WorldGeneratorEngine.TryFireBatch`; cell `k` = the sample at
`drawCursor + k`, mask threaded cell to cell), painted at boot by `WorldServer`
at the pass the row's `drawCursor`/`drawnMasks` name, and advanced one pass plus
repainted by `world.generate <row>` (`TryComposeGenerate`'s lattice arm, then
`RepaintLatticeDrawAfterGenerate`). Draw keeps its authored position in the
paint list: it overwrites earlier fills and later fills overwrite it. Whole-
document rebuild/load/reset repaint every draw row; undo repaints only a row
whose cursor/mask position rewound, preserving unrelated reaction-evolved
fields. Read law:
`tests/Puck.World.Schema.Tests/WorldLatticeDrawLawTests.cs`; live proof:
`tests/Puck.World.Canaries/lattice-draw-fill`.

`WorldGeneratorCapacity`: 32 contexts, 64 alternatives per context (one
drawn-mask bit each), bound ≤ 64, token ≤ 64 UTF-16 units, 64 weighted outcomes,
64 declared sources, uniform bounds inside int32.

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

The CURSOR and drawn MASKS live on the SITE (`drawCursor`/`drawnMasks`, engine-
minted row fields — never cells), so **two sites referencing one source draw
INDEPENDENT sequences**. That is what makes a reference safe.

**Moment** (`timing`): `boot` (drawn once at first fill; a later `generate`
refuses by name), `tickPeriod`, `event`. The latter two redraw through the SAME
`WorldMutation.Generate` (ordinal 51) / `world.generate <row> [key ...]` — the
site owns its whole draw; a keyed site (a dice tray) redraws every cell, or the
named cells alone with the rest held. Cadence is an ordinary `$tick`-scheduled or
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
an explicitly authored default-valued `capacity` is indistinguishable from the
record default) — there the draw wins.

**Domains narrow STATICALLY** against the site's own envelope, the census
coherence sum, and every reachable backend token — so a roll can never decide
whether the world boots. `population.capacityDraw` is TEMPORARILY floored at
`WorldBodiesLimits.CapacityCeiling` (4096) because `world.population` crashes
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
and drawn masks were CELLS an author could hand-write; draw bookkeeping now lives
in typed row FIELDS at the site (`drawCursor`/`drawnMasks`), refused by the
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
assuming a floor of its own. Capped at `WorldStateCapacity.MaxRows` (256)
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

A row or a keyed cell may instead declare `cycle` (`WorldStateCycle`,
`{word?, power, output, ticksPerStep, epochTick, substepTicks?}`) — the
tick-indexed rotation, mutually exclusive with `advance`/`dynamics`/`draw`/
`lattice` and scalar-only at the row level the same way they are. The value is
a pure function of the server tick through a generator of the lattice's
reflection group (`Puck.Maths.SymmetryWord`: `word` is one to eight mirror
nodes, or omitted for the lattice's own thirty-step cycle, `Puck.Maths.CyclicRotation`;
`power` is applications per step, nonzero and inside the order — with no word
1, 7, 11 and 13 are the four rotation planes; one step lasts `ticksPerStep`
ticks from `epochTick`). The period is the word's derived order (`Order` on
the record; `world.symmetry.word` prints it); an identity word or power is
refused. `output` is `Step`/`Node`/`Ring` on an `int` row,
`Turns`/`Cos`/`Sin`/`ProjectionX`/`ProjectionY` on a `fixed` row — the rotation
outputs read the order's root of unity (`CyclicRotation.Rotor(step, order)`),
the lattice outputs read `Puck.Maths.SymmetryLattice`, the stored value being
the node (0..239) carried `power` applications along its orbit per step. The
stored value is the phase in the row's displayed unit — nothing accumulates,
nothing rebases (`RebaseCellTraits` leaves it alone; `UpsertStateCell`
preserves it), a write sets the phase, `addState` turns it. `world.state`
echoes `cycle=<coxeter|[m,…]>^<power>:<output>/<ticksPerStep>@epoch<n>[+<substepTicks>] order=<n>`;
`world.save` settles the value to the current index/node at epoch `0`. Read
laws: `tests/Puck.World.Schema.Tests/StateCycleReadLawTests.cs` and
`StateCycleWordLawTests.cs`; live proof: `tests/Puck.World.Canaries/state-cycle-trait`.

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
`CreationAuthorFrameLawTests`, documented in `Puck.World.Authoring`'s README). A creation also
carries its own animation, in three composable parts: a creation-level `drivers` list (≤ 8 —
`{name, signal, cadence, when}`, where `signal` is `planarTravel`/`travel`/`time` (integrating) or
`speed`/`verticalSpeed`/`turnRate` (instantaneous) and `when` is one token or an array of ≤ 4 that
must all hold — a `Puck.Physics.Motion.BodyFacts` name, `always`, or the client-derived `moving`/
`still` (eased rendered speed against `WorldGaitDrivers.MovingSpeed`), so a walker gated
`["Grounded", "moving"]` returns its limbs to rest on a stop with no sim fact involved), and
per-shape `swings`/`slides` (≤ 4 each) naming a driver, an `axis` (plus a
`pivot` for a swing), an `amplitude`, a `phase`, and a `wave` (`sine`/`halfSine`/`linear`/`constant` —
`constant` is the POSE BLEND, `amplitude · w`, how a climbing posture comes in on the `HoldingUnwalkable` gate;
`curve:<row>` samples the world's `curves` row by arc fraction, Z as the value), plus a per-shape
`parent` naming an EARLIER shape whose motion carries it (pivots included — the joint chain of a
limb). A driver's `cadence` and a facet's `amplitude`/`phase` may reference a numeric state cell
(`DocumentScalar`, resolved by the same walk as every document reference — a numeric cell is offered
as its decimal spelling), and a driver's `signal` may be `state.<row>[.<key>]`, read at the frame's
tick (a `cycle` row is a shared clock). The world validator refuses an undeclared curve or a
non-numeric signal row (`ValidateCreationBindings`). One primitive covers a walker's limbs, a climber's, a wheel, a rotor, a
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
Presentation-only on the same terms, pinned by `CreationEffectorLawTests`. `body.rig [body]` is the read-back (Immediate, client-local — the values live only on the stamp pool): per driver its phase and eased weight, per effector its weight, whether its latch is holding, and the WORLD point its tip is being asked for (`target=(x, y, z)` or `none`), so a piped run fences twice and asserts a planted foot's target is unchanged while `body.where` moved. A body-rooted part anchor (`WorldStampPool.TryBodyPartAuthoredPose`) reports the COMPOSED pose — drivers, parent chain, effector — so an anchor consumer and the rendered geometry never disagree. Everything else — the
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
hold list over the same primitives: the spider (`spiderKit`) pulls any face in a `[0, 180]` cone,
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

The tabletop's `chessBoard` Grid topology (8x8, 0.2 m cells) carries 32 `piece`-kit rigid bodies and is
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
An explicit path or the shipped default that cannot be loaded refuses the boot by name.
`puck.world.frozen.json` ships beside them: the frozen floating-island diorama, reference-only,
reachable via `--world`, never extended, deleted on owner order — the worked examples this file cites
from it (rules, lattice chemistry, regions) live there, and it is the ONLY document layering over the
likewise-frozen `puck.basis.frozen.json`. The loader is
`src/Puck.World.Schema/WorldDefinitionLoader.cs`.

## Document composition (`basis` and `imports`)

`WorldDefinition.Basis` is the document-composition member: a file naming a `basis` (a file path resolved against
its own directory) is a DELTA over that document — templates/prefabs for similar worlds. `WorldDefinition.Imports`
is the fan-in half beside it: an ORDERED list of fragment paths (each resolved against the importing file's own
directory, exactly like `basis`), letting several documents each own one disjoint slice of a world — the garden's
own `src/Puck.World/Assets/worlds/games/{chess,poker,dominoes,billiards,bowling,tictactoe}.world.json`, each
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
in merge order, each file's path paired with the top-level keys its own JSON declares; `world.save`'s echo names
the preserved basis/import count or the flat-save note. Storage composes a synced delta too, over `basis` only —
`imports` is a local-directory-load feature today, not yet extended to the cloud sync path:
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

**Kit motion row (`WorldKit.Motion`, a `WorldMotion` row).** A kit declares
its motion tuning, alongside `BodyMotionProgram` (which operations run each
tick) — a flat record (`WorldMotionTuning.cs`), authored under `motion`.

The row carries the movement platform every kit reads — `Speed` (`WorldSpeed`:
`value`, an optional `envelope` clamp, and an optional `held` multiplier
channel — a kart's "boost") and `Turn` (`WorldTurn`: `rate`, an optional
speed-scaled authority curve — `referenceSpeed`/`falloff` — `pitchRate` for a
drive kit's flying variant, and `maxPitch` — the radian ceiling the flying
variant's climb/dive attitude is clamped to, unread while `pitchRate` is
zero, defaulting to the engine's old hardcoded clamp) — plus
`MoveFrame`/`FacingSnap`, and two rows beside them, each supplying its own
tuning facet and each read by its own operations: `Holds` (below; the hold
LIST is mandatory — the hold list is the only spelling of a vertical channel,
so a Motion-kind kit authoring none refuses by name — while
`ResolveHold`/`ApplyHold` are selected like any other op) and `Shaping`
(required only when the program selects `ShapeVelocity`). Two more optional
rows on `WorldMotion` itself carry feel the engine used to hardcode, each
defaulting to the old constant bit-for-bit when omitted: `upTurn`
(`WorldUpTurnRates`: `field`/`contact`, the half-angle-per-second ceilings on
how fast a solved gravity field, respectively a measured ground-contact
normal, may turn the body's up axis) and `obstruction`
(`WorldObstructionLatch`: `displacement`/`idleThreshold`/`graceSeconds`, the
non-walkable contact witness's persistence — how far the body must move to
count as moved on, the driven-input floor below which it counts as idle, and
how long an unrefreshed latch survives a solver pass reporting no push). The
positive fixed rates must survive Q48.16 compilation, `displacement` must do
so after squaring, and `graceSeconds` must be a positive exact whole engine-
tick duration. A third scalar, `groundStick`, is the inward speed (world
units/second) a
grounded body on a curving surface is held against; it is independent of
`Speed` — a kit's own resolved move speed measurably over-corrects a shallow
slope climb (the bias converts to downhill drift under depenetration faster
than to held contact) — and also defaults to the engine's old constant.
In-medium locomotion is a kit authoring a `bond: "Medium"` hold row; a kart
is a kit whose shaping table carries an `across` row.

**`shaping` (`WorldShaping[]`) — the unified velocity-shaping table.** One
row shape serves the whole-vector response law, the anisotropic drive
decomposition, and a named second-order follower: `{ "when": <predicate,
optional>, "along": { "engage"?, "reversalRate"?, "release"?, "backwardSpeed"? }, "across":
{ "lateral"? }, "dynamics": "<row>", "turnScale": 1 }`. Rows evaluate in order,
first open gate wins; `when` admits the shaping-gate predicate vocabulary —
body-fact kinds (`now`/`recently`/`all`/`any`/`not`) plus `held` (`{ "held":
"<channel>" }` — the named composition channel's own live read at or above
its declared threshold, resolved against the world's channel table at
kit-compile time; legitimate only here). Exactly one of `along` or `dynamics`
is authored per row; `across` is legitimate only beside `along`. An omitted
convergence rate means exact, immediate convergence. An explicit rate must be
positive; zero is never a hidden spelling of "instant" or "disabled". `reversalRate`
and any authored `backwardSpeed` are refused on a whole-vector row because that law
does not read drive-only facets.

A row without `across` shapes the whole vector through the engage/release
response law — `engage` while the stick is deflected, `release` while
centered, with the shared recency clocks its `when` gate's `recently`
predicates read. A row with `across` runs the anisotropic drive
decomposition instead, the same body-frame longitudinal/lateral/residual
lanes converging each at its own authored rate: `along.engage` while throttle commands
more speed, `along.reversalRate` while back-throttle opposes forward travel,
`along.release` toward rest with throttle centered (and the over-speed
bleed), `along.backwardSpeed` the backward target speed full back-throttle
converges on from rest, and `across.lateral` the lateral convergence rate
toward zero slip. `turnScale` multiplies the turn tuning's own authority
curve while this row governs — `1` (the default) for an ordinary row, and a
held drift row's own tightened arc. A `dynamics` row names a `dynamics`-
section row (a pole-matched second-order follower — see the `dynamics`
section above) shaping velocity instead of either mechanism; it compiles
once per kit (`WorldKit.Compile`) against the world's own
`simulation.rateHz` — a world authoring no simulation rate cannot compile
one and refuses by name. The follower's state lives in `WorldBody` as
ordinary sim state, included in whatever the body snapshot/checkpoint
covers; changing which mechanism a row uses, or retuning a live `dynamics`
row, is expected to change replay hashes. A drift/boost row is authored as
an ordinary row gated on `held` — never a bespoke mechanism — so it must sit
AHEAD of the row it overrides.

What an anisotropic shaping row does NOT carry is what the motion row and its holds already
name: the forward speed full throttle converges on is `speed.value` (bounded
by `speed.envelope`, scaled by `speed.held.multiplier` while its channel
reads held), the steering rate at full authority is `turn.rate`, and gravity
is the held row's own `gravity` arc. One shaping table serves the ground,
hover, and air variants: a contact-pinned variant pairs an `across` row with
a Surface `Gravity` hold row, a flying variant a Free `Lift` row (`lift: 1`)
and a positive `turn.pitchRate`, which is what decides vertical contact
ownership per the seam's rule. Validation
(`WorldDefinitionValidator.ValidateShaping`): every authored convergence rate
positive; `along.backwardSpeed`, when present, non-negative; `across` refused without `along`,
`turn.falloff` in `[0, 1]`, `turn.pitchRate` non-negative, and a `held` gate
naming a resolvable channel. `DriveLawTests` pins the drive family, and
`ShapingRowLawTests` the whole-vector and dynamics-row families, to
recorded 240-tick fixed-point traces whose discriminating controls perturb
one facet each.

A worked kart:

```json
{
  "name": "kart",
  "bodyMotionProgram": "kart-drive",
  "motion": {
    "speed": { "value": 16.0, "envelope": { "min": 16.0, "max": 16.0 }, "held": { "channel": "boost", "multiplier": 1.5 } },
    "turn": { "rate": 2.4, "referenceSpeed": 4.0, "falloff": 0.55 },
    "holds": [
      { "name": "ground", "bond": "Surface", "cone": [0, 60], "hold": "Gravity", "reach": 1.2, "gravity": { "rise": 14.0, "fall": 26.0 }, "envelope": { "sinkSpeed": 30.0 } },
      { "name": "air", "bond": "Free", "hold": "Gravity", "gravity": { "rise": 14.0, "fall": 26.0 }, "envelope": { "sinkSpeed": 30.0 } }
    ],
    "shaping": [
      { "when": { "$type": "held", "channel": "drift" }, "along": { "engage": 7.0, "reversalRate": 18.0, "release": 4.0, "backwardSpeed": 5.0 }, "across": { "lateral": 6.0 }, "turnScale": 1.4 },
      { "along": { "engage": 7.0, "reversalRate": 18.0, "release": 4.0, "backwardSpeed": 5.0 }, "across": { "lateral": 22.0 } }
    ]
  }
}
```

with the program `[ResolveDriveFrame, ResolveHold, ShapeVelocity,
RunActionTriggers, ApplyHold, IntegratePlanarAndVerticalVelocity, CommitPose]`
(op ORDER in the authored list is inert — `CompiledBodyMotionProgram` groups the
selected set into its intrinsic phases) — the same op list every grounded kit
runs, since the hold list is where a kart's gravity lives.

A kit with no shaping row (empty or absent) refuses validation by name when
its program selects `ShapeVelocity`, exactly as an empty/absent hold list
does for `ResolveHold`/`ApplyHold`; a kit whose program never selects it (a
free-flight kit that owns its whole velocity channel directly) may author
none. `Speed.Held` is a HELD (not edge-triggered) channel that scales the
resolved planar speed while it reads held, default `null` (no held
multiplier) — a shaping row's boost is this seam under that name, never a
second channel; resolved to `FixedSpeed.HeldOrdinal` the same way a producer's
`BodyProducerParameter.Press` channel argument resolves its own ordinal
(`CompiledBodyProducer.Channel`), since a channel name needs the world's
compiled channel table and a body's own compile step has none. `MoveFrame` (`MotionMoveFrame.Heading` / `.World` default) and
`FacingSnap` — `Heading` is tank controls; `World` (every kit that never
sets this field) treats `MoveAdvance`/`MoveStrafe`
as ALREADY-WORLD-FRAME axes (the seat's client resolves its camera yaw into
the submitted intent BEFORE the wire — determinism: the sim never reads a
camera pose) and, with `FacingSnap` on, snaps the body's facing to
`Atan2` of the commanded direction every tick carrying input, no ramp — the
camera-frame 3D-platformer feel a `FacingSnap` kit authors.
Under `World` a seat's aim elevation also splits the commanded
forward into planar and vertical channels client-side; the explicit `MoveUp`
channel is orthogonal and stays live regardless of `MoveFrame`.

**Shaping-row ORDER shadows regimes — author air rows first.** The shaping
table evaluates in order, first open gate wins, and a `recently Grounded`
window (`0.09s` ≈ 21 ticks at 240 Hz) stays open through the RISE of every
jump — so a recently-Grounded row above a `now Rising` row governs the first
~21 airborne ticks with GROUND rates (a stick released at takeoff bleeds
momentum at the ground `release` rate; air steering briefly runs the ground
`engage` rate). Author `now Rising` / `now Falling` rows ABOVE any
recently-Grounded row; a plain unconditional row then covers grounded ticks.
`ShapingRowLawTests`' walker kit is the worked example of the corrected
order, with the measured arc numbers in its motion row.

`WorldDefinitionValidator` cross-checks the kit's `BodyMotionProgram`
against its declared motion row: an operation the program selects that reads a
tuning facet (`MotionTuningFacet`) the row doesn't supply refuses
BY NAME. The row supplies `Speed` (Speed/Turn together) unconditionally;
`Holds` and `Shaping` are each supplied CONDITIONALLY — only when the kit's
`holds` list is non-empty, or its `shaping` list is non-empty — so a
program selecting `ResolveHold`/`ApplyHold` or `ShapeVelocity` against a kit
authoring none refuses by that facet's name. Separately, and unconditionally:
a Motion-kind kit authoring no holds at all refuses by name outright,
whatever operations its program selects — the hold list is the only
spelling of a vertical channel, so even a kit with no vertical law of its
own still authors one row of kind `None`. A world whose kit authors a
`Medium` hold row with no medium lattice row (`state.world[].lattice.medium`)
refuses at boot. A `BodyMotionOp` reading a further facet owes
`RequiredMotionTuningFacets` and `SuppliedMotionTuningFacets` an entry —
never a hunt.

A seated player's live profile overrides the kit's `Speed.Value`
(feel stays real-time under `profile.set`/`identity.motion`);
`WorldSpeed.Envelope` is the world's own counter-pin — an
authored `MotionScalarEnvelope { min, max }` that clamps the RESOLVED
speed at the seat-time read (`WorldBody.ResolveMoveSpeed`, before the
program ever sees it), regardless of whether it came from the profile or
the profileless fallback. Absent (the default) is wide-open, today's
behavior exactly; `min == max` pins the effective speed outright; the
validator refuses `min > max` and refuses a kit whose OWN `speed.value`
falls outside its own envelope, by name. `identity.show`'s
`moveEffective=` echoes what the sim actually applied beside `move=` (the
profile's raw request) — the two diverge only when an envelope is
narrower than what the profile asked for. `MotionScalarEnvelope` is the
reusable shape every overridable scalar adopts, never a bespoke
bound.

`ResolveMoveSpeed` is ONE law for every kit — the seated profile's claimed rate,
else the kit's own, clamped by `Speed.Envelope` — shared by the sim and every
read-back so the two can never disagree. A kit that means to pin its speed
against any profile authors `min == max` rather than opting out of the profile
read, and a held speed multiplier (a shaping row's boost) multiplies AFTER the
clamp, on the resolved value: the envelope pins the base rate, the boost
rides on top. `ResolveTurnAuthority`'s falloff anchor and every shaping
row's own commanded speed both read the SAME resolved value
(`scratch.MoveSpeed`, filled once before phase 0), so a clamped kit's falloff
still reaches its anchor. The validator's own-value check applies to every kit
too: a kit whose `speed.value` falls outside its own envelope refuses by name, so
a live `world.row.set kits …` retune past the cap refuses instead of clamping
silently.


**Holds (`WorldMotion.Holds`, a list of `WorldHold` rows →
`FixedBodyHold[]`) — what may hold a body, in preference order, and the only
spelling of a vertical channel.** A kit authors an ordered list; the
`ResolveHold` operation takes the first row the world offers and `ApplyHold`
applies that row's vertical law and its own `thrust`. A Motion-kind kit
authoring none refuses validation by name — even a kit with no vertical law of
its own still authors one row of kind `None`, since the hold list is the only
spelling of a vertical channel, never simply absent, whatever operations the
program selects.

```json
"holds": [
  { "name": "wall", "bond": "Surface", "cone": [60, 120], "hold": "Pull", "pull": 1.0,
    "reach": 0.8, "speed": 2.0, "upLean": 0.0, "forward": "Heading",
    "onDrive": true, "driveAlignment": 0.5, "release": "jump",
    "spend": { "state": "stamina", "ratePerSecond": 1.0 } },
  { "name": "ground", "bond": "Surface", "cone": [0, 60], "hold": "Gravity", "reach": 1.2,
    "gravity": { "rise": 28.0, "fall": 46.0 }, "envelope": { "sinkSpeed": 40.0 } },
  { "name": "air", "bond": "Free", "hold": "Gravity",
    "gravity": { "rise": 28.0, "fall": 46.0 }, "envelope": { "sinkSpeed": 40.0 } }
]
```

`bond` is `Surface` (a contact-field face whose normal makes an angle inside
`cone` degrees with GRAVITY-up — 0 a floor, 90 a wall, 180 a ceiling; the cone
is measured against gravity-up, never the body's own leaned up), `Free` (no
surface at all), or `Medium` (the world's own field-lattice column — the world
either offers a medium where the body is or it does not, so the bond carries no
cone and no reach, and takes a `medium` law instead:
`{ idleDrift, equilibriumOffset, settleRate }` — `settleRate` is the one gain
that turns the equilibrium error into a target velocity; the governing shaping
row's own `along`/`dynamics` facet then rate-limits the body's actual velocity
toward that target the same way it rate-limits every other channel). `hold` is
`Gravity` (gravity holds the body
against the face — the walkable case, integrating the row's own `gravity`
arc), `Pull` (a pull of `pull` u/s toward the face applied as a POSITIONAL
standoff, gravity suspended while it holds), `Lift` (a fraction `lift` of
gravity cancelled — 1 hovers, bleeding whatever the vertical channel carries
back to rest at the row's own `gravity.rise` rate rather than integrating the
arc), or `None`. `gravity` (`{ rise, fall }`, u/s², u/s²) is the row's own
vertical arc — required on a `Gravity` or `Lift` row, refused on a `Pull` row
and on a `Medium` bond (a medium displaces by its own law); the world's own
solved gravity field, where one is authored, overrides the MAGNITUDE but
keeps the row's `rise:fall` ratio as the arc's asymmetry. `envelope`
(`{ riseSpeed?, sinkSpeed }`, u/s each) is the vertical-channel bound a
`Gravity`/`Lift` row's own terminal fall speed and a `Medium` row's terminal
rise/sink speeds share — the SAME field family a document-wide speed ceiling
walks, rather than three separately-authored numbers a caller has to
`Math.Max` across. Required for a `Medium` bond (both directions) and for a
`Gravity`/`Lift` row short of full lift (sink only — that arc never clamps a
rise); refused otherwise, including on a full-lift row (`lift: 1`), whose
channel decays rather than clamps. `thrust` (a fraction of the kit's resolved
move speed the `MoveUp` role commands vertically, `[0, 1]`, default `0`)
applies in EVERY bond: a non-`Medium` row commanding thrust takes the
vertical channel outright for the tick, clearing the ballistic carry, while a
`Medium` row's own thrust folds into its displacement law's convergence
instead — the medium's drift and the body's own MoveUp thrust are summed
BEFORE that convergence runs, so nothing writes the vertical channel twice,
and it publishes `InMedium`/`AtMediumBand`. `speed` (u/s along the row's
tangent plane, absent rides the kit's own resolved move speed), `reach` (how
far a surface row's probes search; required positive), `onDrive` +
`driveAlignment` (take the row by driving into a face in its cone), `release`
(a declared channel whose HELD read drops the row — no latch, so holding it
down keeps the body off the face), and `spend` (drain a body-lane Counter
slot at a rate; the row becomes ineligible the tick the slot cannot pay, and
a world's own rules refill it or trade for it — the engine has no stamina
concept of its own).

A pull owns the whole tangent-plane velocity, rise included; the tick it ends
(released, spent out, or its face lost) that rise is split against gravity-up
and carried into the ballistic channel, so a body letting go mid-climb keeps
the climb's momentum instead of dropping from rest.

Frame rules per row: `upLean` in `[0, 1]` blends the body's up axis from
gravity-up toward the face normal (0 keeps a body upright on a wall, 1 lays it
on the face). A pull's drawn axis is TURNED into its lean, never snapped, at the
rate a body turns over its own span — the row's `speed` over the collider's
probe height plus standoff, rad/s, derived and not authored — so a face change
(floor to wall to ceiling) is a turn, and the axis returns to the contact axis
the same way when the row ends; a `gravity` hold's drawn axis stays with its
lean, whose contact axis is bounded already. **Whether that lean also carries the body's CONTACT axis is
decided by the hold's KIND, never by the lean.** A `gravity` hold is one the
world's own gravity presses onto its face, so the face IS the ground the solver
should stand the body on and the axis leans with it, bounded through the same
accumulator a measured contact normal is adopted by — a kart on a loop. A `pull`
hold holds the body instead, gravity is suspended, and leaning the contact axis
there would tell the solver that the floor under the body is a ceiling and that
falling is upward: the floor stops depenetrating and a released body flies off.
So a pull's lean is the body's FRAME — the plane it travels in and the attitude
it is drawn at (`scratch.AttitudeUp`, which every attitude writer including the
facing snap composes about) — while the contact axis stays with the ambient
resolve. `forward` (`Heading`/`Intent`/`Velocity`) chooses what that drawn
attitude tracks inside the row's own frame. Movement rides the face's own tangent plane: forward is gravity-up
projected onto the face ("up the face"), right completes it, so
`ComputePlanarTargetVelocity` needs no new operation. A face whose normal is
parallel to gravity-up leaves that tangent undefined, and there the ordinary
frame stands.

**Resolution order, per tick.** What the body is actively DRIVING into outranks
what it happens to be resting on, so the `onDrive` pass runs first over the
ordered list. Otherwise the list decides, first match wins, with the row
already held evaluated by whether its own face is still there (the directed
tracking probe along that face's inward normal) rather than by a fresh take —
so a row authored EARLIER still wins from where the body stands, which is why
a walker authors `wall` before `ground`. A held non-walkable row also ends when
the contact resolve has stood the body on something walkable and the body has
stopped pulling itself along the face. When a held face ENDS while the body is
still driving forward, one further pass reaches PAST the edge — up the last
tangent by a body span and in past the face by a body width — and the body
arrives one body span off whatever it finds there; that is the whole of the
ledge transition, with no mantle phase.

Every probe is DIRECTED, for the same reason a pull's always was: an undirected
nearest-surface query on a world whose floor, walls, ramps and overhangs are one
holdable placement answers with the floor a body is standing on.
`IContactField.TryHoldableSurfaceAlongDirection` is that query; both providers
answer it, the field by ray march (`RayHit.Normal` is documented ZERO — the
surface orientation is a separate `TryFieldGradient` read one step back along
the ray). Which surfaces admit a hold at all is the collision row's
`defaultHold` (world-level) composed with a placement's own
`grip: { holdable: … }` override — the placement's own surface-holdability
facet, a distinct concept from a hold row's `Pull` kind despite the shared
word: `WorldPlacementGrip` says whether a face can be held AT ALL, never how.

Validation (`WorldDefinitionValidator.ValidateHolds`): unique row names; a cone
required for a `Surface` row and refused for a `Free` one, finite, inside
`[0, 180]`, increasing; a positive `reach` on a `Surface` row; `upLean` and
`driveAlignment` in `[0, 1]`; a positive `pull` on a `Pull` row and `Pull`/
`onDrive` requiring `Surface`; `lift` in `[0, 1]`; `gravity` required with a
positive `rise`/`fall` on a `Gravity` or `Lift` row and refused elsewhere;
`envelope` required (sink speed positive; a `Medium` bond also requires a
positive rise speed) on a `Medium` bond or a `Gravity`/`Lift` row short of
full lift, and refused otherwise; `thrust` in `[0, 1]`; `release` naming a
declared composition channel; `spend.state` naming a declared body/identity
Counter slot and a positive `spend.ratePerSecond`. Both `Holds` and `Drive` are
CONDITIONALLY-supplied facets — a kit authoring an empty or absent `holds` list
refuses a Motion-kind program outright (a fact checked ahead of the facet
mechanism, since it holds whatever operations the program selects), and a
drive program against a kit authoring no `drive` row is what the
`MotionTuningFacet` gate refuses by name. A Motion-kind kit's `holds` list must
also author at least one UNCONDITIONAL row — a `Free` bond with no `release`
and no `spend` — so `ResolveHold` always has a row it can fall to once every
earlier candidate goes ineligible and `ApplyHold` is never left with no current
hold to read; and a program selecting `ApplyHold` without `ResolveHold`
refuses by name, since `ApplyHold` applies whatever row `ResolveHold`
selected. A `Medium` row does NOT count toward this: `ResolveHold` takes it
only where the world's own lattice offers a medium column at the body, so a
Medium-only list still leaves a body outside its medium with nothing to fall
to — every kit authoring a Medium row also authors a trailing `Free` row for
that case.

A `Medium` row is the ONLY spelling of the medium law — `ApplyHold` runs it
against the row `ResolveHold` took, and `WorldMediumLawTests` pins it to a
recorded 240-tick fixed-point trace. `puck.world.frozen.json`'s `fishKit` is
the worked example: a kit whose `fishMotion` program runs
`ResolveHold`/`ApplyHold` over a `water` row carrying the five medium facets,
and a trailing `air` row (`Free`, `Gravity`) for the water's own dry fallback.
That row authors no `thrust` because its wander producer never writes the
`MoveUp` role; `WorldMediumLawTests`' own fixture is the thrust-carrying
example.

Read back with `body.hold` (`[body.hold: body:<n> hold=<name|none>
normal=(x, y, z) spend=<left|n/a>]`). The current row index, its anchor and
normal, and the spend accumulator's remainder are simulation state: captured in
`IntegrationResidue`, carried through `WorldAuthorityCheckpointCodec`, and part
of the replay hash.

**A kit's `tether` facet (`WorldTether` → `FixedWorldTether`) — an aimed
distance-cap rope, beside `rigid`/`carry`.** Absent (`null`) refuses
`body.attach`/`body.detach`/`body.reel` by name for that kit's bodies, the
same presence-is-the-switch convention `rigid`/`carry` carry. A body's attach
state is `m_tether is not null` (`WorldBody.Tether.cs`), read directly off the
intent (never through a kit's action table) and echoed by `body.tether`; the
facet itself is echoed per kit by `world.kits`. Its fields are the aim ceiling
and cone (`maxAnchorDistance`/`aimHalfAngleDegrees`), the rope
(`lengthRate`/`minLength`), the release scale (`releaseVelocityScale`), the
three channel names, and an optional `modeState` counter-slot name the facet
writes `1`/`0` to while attached/not — resolved to an ordinal at kit compile
time, the camera program's `select` op keys off it like any `state.<row>`
value. Positive numeric values must survive their Q48.16 compilation as
nonzero (including the cone's degree-to-radian conversion). Surface holds are
not authored here.

**Body facts on the wire (`BodyFacts`, `Puck.Physics.Motion`).** The engine
publishes each body's per-tick fact set on `EntitySnapshot.Facts` — one bit
per body-state `ActionFact` (`grounded`, `airborne`, `rising`, `falling`,
`inmedium`, `atmediumband`, `holdingunwalkable` — holding a surface row whose face is
outside the world's own walkable cone — `unsupported` — holding a free row with
lift — and `resting`, written only by the rigid solver once a rigid body's
linear and angular velocity latch to zero; `AffectedBy` has no bit, being a relationship rather than a state). The mask is derived through the SAME
predicate the kit's action gates read, so the snapshot, the gates, and the
`body.where` echo cannot disagree; a decoder refuses an undeclared bit by
name. `WorldSessionMirror.Facts(int)` and `WorldClient.Facts(int)` front it
for presentation, which is how animation keys on regime without the client
deriving one. `body.where` echoes it as `facts=` (lower-case, `|`-joined in
bit order, `none` when empty), followed by `home=(x, y, z)` — the position the
body was ACTIVATED at (a seat's spawn point, an inhabitant's placement plus its
own distribution sample) — then `scale=` (`Server.WorldBody.Scale`'s read-back)
and, for a routed local seat, `anchor=body:<n>` (the seat's currently routed
entity index). A producer's inward pull steers against that home rather than the world origin,
so a population spread over several placements keeps to its own ground
instead of congregating; a teleport moves the body, never its home. Facts are
NOT mutually exclusive: a body can be
grounded and rising in one tick, and a body on a wall reads `airborne|holdingunwalkable`
because contact resolution keeps running under every hold.

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
  `world.attachments` and every active tick for attached gravity areas), and
  the rendered pose —
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
Controllers, Voice)`): `MoveSpeed`/`TurnSpeed` read
and write the owned document's OWN `state` rows named by those state-row
references; `Bindings` is the owned document's own first `bindingOverlays`
row's document (the seat's profile binding layer — see "Binding composition"
below); `Hud` is the owned document's first `Hud` panel (the identity's
PRIVATE seat-scope HUD panel, see `hud.md`). `Voice` (`WorldVoiceProfile?` —
a `PatchId` resolving against declared `patches` rows, plus a positive
`CadenceTicks`) selects the identity's synthesized voice-babble pitch/timbre
and cadence. `WorldAudioDirector.TriggerBabble` reads it, drives
`Puck.Audio.Simulation.VoiceBabbler` for the cadence-jittered per-syllable
trigger schedule, resolves the patch, and fires one seeded trigger per
syllable under the `voice.babble` cue token (`voice.state`/`voice.babble` are
its read-back/debug-trigger verbs). Two things stay open: no producer yet
estimates an utterance's syllable count from dialogue/caption text, and a
babbling identity has no live-body correlation, so every syllable voices
listener-placed rather than at a resolved world position.

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
CLASSES are assembled by `src/Puck.World.Client/WorldSeatBindings.cs`: engine
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
- `WorldStateCapacity` (`WorldState.cs`): `MaxRows = 256`,
  `MaxCellsPerRow = 128` (an authored `capacity` may only narrow it),
  `MaxTextValueLength = 256` (UTF-16 units, a text cell's value), and
  `MaxBodySlots = 128` across the `body` and `identity` lanes (the fixed
  per-body register/checkpoint width).
- `WorldDynamicGeometryCeilings.MaxContributedDynamicInstances = 16000`,
  the document-global CPU/instance-grid admission ceiling. The recorded
  GPU-bound measurement is 0 but does not govern admission.
- `WorldPlacementPolicy`: `MaxShapesPerStamp = 48`,
  `MaxStampRegistrations = WorldBodiesLimits.DetailedRenderBand` (128),
  `TimelineSecondsPerFrame = 8f/60f`, and the reserved derived-face screen
  band.
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
  `value` sugar or `cells`, a `Domain`), `WorldStateCell`, and `WorldStateCapacity`.
- `WorldStateDomain.cs` — the row's declared cell domain (`Slot`/`Keys`/
  `KeysOf`/`CellsOf`/`Ring`), a closed union over `UnionPolyfill.cs`'s shared
  `[Union]` marker.
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

Discrete state shares `state.lattices`: only `Field` creates physical storage;
`Grid`, `Ring`, and `Hex` compile bounded adjacency. Keep token identity domains,
ordered zone membership, position attributes, phase progression, and knowledge
stamps inside the canonical state row converter and authoritative hash. The
closed transform union is shared by mutations and rule transactions. Readers,
secret draws, and observation payloads have separate authority/presentation
semantics; see the owning contract in
[`Puck.World.Schema`](../../../../src/Puck.World.Schema/README.md#discrete-boards-cards-and-turns).
Do not flatten restricted state into a public document value. Replica access
remains full authority trust. Test socket observations using authenticated
submission stamps, and check exact topology and query work bounds at preflight.

A `patterns` section row is a regular language over cell values
(`WorldPatterns.cs`: symbols as value ranges, a closed node vocabulary with
complement and intersection, a derivative machine inside a state budget of at
most 256) compiled at validation;
rules read it through `$match:<pattern>:<row>[:<direction>|:any][:prefix|:mask|:count]` over a board
ray, a zone's attribute word or per-token `value` expression (`$token` keys),
a `history` ring (`push`/`pushState`, `$history:<row>:<age>`), or a keyed row;
`$board:mask`, the `boardShift`/`boardImage` expression ops, and the
`writeSet` transform carry the one cell-set vocabulary; `world.match` narrates
one word.
The `sort` transform supplies the canonical order. Read back with
`world.patterns` and `world.match`.

An impression is an ordinary keyed `state` row, not a bespoke policy section or
memory component — see "Keyed belief rows and evidence dedup" in the Schema
README. `compareValue` compares numeric expressions and closes on arithmetic
failure; it is the same primitive that gates a rule's freshness check (a packed
`(origin, sequence)` Int64 against a companion marker cell), the one thing an
ordinary rule effect cannot express on its own. There is no sensor or transfer
implementation to preserve: gating who witnesses an event is the author's own
rule, and an ordinary keyed row is local to its world like any other `state`
row — it does not travel with a body across a transfer. Keep an actual-world
read-back and replay proof.
