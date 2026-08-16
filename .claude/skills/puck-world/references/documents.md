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
`WorldProjection.ToDefinition` hydrates a received projection back into a
`WorldDefinition` (undisclosed sections take their neutral built-in defaults) so
no downstream consumer changed type; a hydrated document is never saved,
journaled, or an authority.

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

`WorldDefinition` is one aggregate record — 33 REQUIRED positional section
members in declaration (= canonical-write) order: `Motion`, `SpawnPoints`,
`Render`, `Screens`, `Cameras`, `Population`, `PlayerDefaults`, `Channels`,
`TargetRegisters`, `BodyMotionPrograms`, `Kits`, `DefaultSeatKit`,
`Assignment`, `Addons`, `BindingOverlays`, `Storage`, `Creations`,
`Placements`, `Authoring`, `Speakers`, `Tunes`, `Patches`, `Audio`,
`Collision`, `Host`, `Views`, `Looks`, `LookAssignment`, `Links`, `Grants`,
`Hud`, `State`, `InputHold` (its own type, `WorldInputHoldAuthoring`, is the
AUTHORED seconds shape — `WorldDefinition.CompiledInputHold` is the compiled
ticks form runtime code consumes; see `WorldInputHoldSettings`'s remarks) —
plus 17 trailing OPTIONAL members (each `[JsonIgnore(Condition =
WhenWritingNull)]` with a `= null` default, so an existing document declaring
none of them round-trips unchanged): `Rules` (see below), `Identity`,
`Groups`, `Properties`, `Interactions`, `Generation`, `Generators`, `Water`
(the standing-water medium — one waterline `level`; null IS the dry world;
`WorldWater.cs`), `References`, `Portals`, `Simulation`, `Destinations`
(`WorldDestinations.cs`), `Admission` (`Protocol/WorldAdmission.cs`, the one
trust list every ingress crosses — key-bearing rows for the TCP identity door,
keyless `federatedAuthority` rows for travellers an authenticated authority
hands over; deny-by-default, an absent/empty section admits neither), `Market` (`WorldMarketSection`, `WorldMarket.cs` — the local
auction house's config and live listing ledger; null IS today's no-market
behavior, falling back to `WorldMarketSection.Empty`), `Adjacencies`
(`WorldAdjacencies.cs` — invisible reciprocal authority boundaries; null
names no seamless neighbours), `Text` (`TextFontCatalogDefinition` — the
named, hash-pinned world-space font catalog; null declares no fonts), and
`Metadata` (`WorldMetadataSection`, `WorldMetadata.cs` — author-facing
`title`/`description`/`authors`/`tags` plus a free-form `custom` bag; nothing
in the engine reads or dispatches on it, and it is distinct from `Extensions`
below, which exists to catch a misspelled top-level section name rather than
to hold content) — plus `Schema`
and the `[JsonExtensionData]` `Extensions` bag. There is no `Wander`/`Scene`
member and no `WorldSceneRow` type any more — both retired; scenery is
authored through `Placements` now.

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
  portal facet's `destination` resolves against: Play's own `references`
  section names the three dungeons by document path.
- **`Portals`** (`WorldPortals.cs`) — `WorldPortalsSection(WorldPortalDefaults
  PortalDefaults)`, whose `travel` is `Party` (the traveling seat's whole
  active local-seat party) or `Body` (one seat). It is the world-scope default
  a placement face's own `WorldPlacementPortal` facet falls back to when it
  authors no `Travel`; a null section resolves every facet to `Body`.
- **`Simulation`** (`WorldSimulationDefaults`, `WorldDefinition.cs`) — one
  field, `RateHz`, the authoritative server's fixed step rate in Hz. Read
  through `WorldDefinition.SimulationRateHz` (`Simulation?.RateHz ??
  WorldSimulationDefaults.DefaultRateHz`, 240) — never `Simulation` directly,
  since every consumer wants the resolved value, not the presence/absence of
  the section. MUST be a positive divisor of
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

The `WorldSection` enum (`Protocol/WorldGrant.cs`, 31 members, declared
order): `Kits, Screens, Cameras, Spawns, Motion, Population, Render, Addons,
Bindings, Creations, Placements, Authoring, Speakers, Tunes, Patches, Audio,
Collision, Host, Views, Looks, Links, Grants, Hud, State, InputHold, Rules,
Groups, Properties, Interactions, PlayerDefaults, Market`. It is the grant subject vocabulary
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
`CancelMarketListing`/`SettleMarketListing` family).

`rules` (`WorldRule`, `Puck.World.Schema/WorldRules.cs`) is the OPTIONAL
world-scoped rule section — the SAME `ActionPredicate`/`ActionEffect`/
`ActionTriggerMode` primitive a kit's per-body actions use, one level up.
Optional deliberately: a new REQUIRED section would refuse every existing
document at boot for declaring nothing. Only `all`/`compareState` predicates and
`setState`/`addState`/`generate` effects are admissible at world scope — plus,
each admitting an EXISTING `WorldMutation` kind into the rule effect set (riding
the exact seam `generate` proved, never a new door), `upsertHudPanel`/
`removeHudPanel` (a world-scoped HUD row) and `upsertPlacement`/`removePlacement`
(a placement row); the rest read or write per-body state (velocity/impulse/
designate/timer) and are refused BY NAME by `WorldRuleCompiler`. `save` admits on
DIFFERENT terms again: it is the ONE effect with no `WorldMutation` ordinal at
all — it writes a session snapshot to the world's own loaded file
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
than a token placement a rule itself spawns/removes, for exactly this reason —
the four shipped worlds author no `rules` section today, so there is no
worked example to cite; the pattern still governs the first world that adds
one.

`state` (`WorldStateRow`, `Puck.World.Schema/WorldState.cs`) is genre-neutral
game state — score, rounds, inventory, flags. **A slot is a table with one
key, and there is ONE authored spelling for both.** A row names itself,
declares its `kind`, and carries EITHER a bare `value` — sugar for the one
cell keyed `WorldStateRow.SlotKey` (`"$value"`) — OR a `cells` array of
author-keyed `{"key","value"}` objects. Two optional fields, never two
discriminators: a row carrying both, or a `value` beside a `capacity`
(declaring a capacity is declaring keyed-row intent), refuses by name.
Omitting both is a declared-but-empty row.

```json
{"name":"score","kind":"int","value":0,"min":0,"max":1000}
{"name":"lives","kind":"int","value":3,"nonNegative":true}
{"name":"inventory","kind":"int","capacity":8,"cells":[{"key":"coin","value":2}]}
```

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
`WorldPopulationLimits.CapacityCeiling` (128) because `world.population` crashes
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
unconditionally — `Server.WorldServer.RebaseAdvanceEpoch`, which also runs
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


World/owned-world ids (`Server/WorldOwnedWorlds.cs`) and `world.instance.start`
names are `WorldSafeName` (the same `WorldSafeName.cs`) — the reserved-character
kernel `WorldCellName` shares, plus a bare `"."`/`".."` refusal instead of the
dot-free rule; `WorldOwnedWorldFileName.For` takes a `WorldSafeName` and no
longer escapes/collapses characters (the id→file-name mapping is injective by
construction), so the collision-with-another-id checks that used to run beside
the character check are gone — a proof, not a courtesy.

Worlds have no in-code definition. A boot with no `--world` override loads
`src/Puck.World/Assets/worlds/play.world.json`; an explicit path or the shipped default that cannot be loaded
refuses the boot by name. Four shipped GAME worlds — the charter's whole roster: `play` (the hub — the
overworld's first main city, local multiplayer, and the boot default; carries the `references` section naming the
other three by document path, and a wall-mounted picture-frame placement per named world), `dive` (the underwater
arena scaffold — the one that also authors `water`), `kart` (the racing arena), `jump` (the platformer arena). A
fifth document, `studio`, ships beside them as a non-game DEV CANVAS for character/creation work — neutral floor,
no scenery or crowd, four anchored camera eyes and a `sheet` layout composing four angles at once — reached only
with `--world` and never from Play. Five quilt documents (`quilt-nw`, `quilt-ne`, `quilt-se`, `quilt-sw`,
`quilt-island`) ship beside them as non-game adjacency/federation stress content — each a `basis` delta over the
eleventh document, the `quilt-base` template (see "Document composition" below). The six FLAT documents (the four
game worlds, `studio`, `quilt-base`) carry the full required top-level set, so a change that adds a required
top-level section sweeps those six; the deltas inherit it. The loader is
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
`Grounded`). All three arms share `SprintMultiplier`/`SprintChannel` — a HELD
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
kit that never sets this field); `World` treats `MoveForward`/`MoveStrafe`
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
slot (`FixedMotionTuning.Compile(WorldMotionModel.Swim)` passes
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

`views.seatRig` (`WorldCameraRig`) carries `SmoothRate` (default
`0` = the unsmoothed snap every world used before it existed) — a
presentation-only low-pass on the seat's resolved eye/target, the same
`WorldAnchor.Group.SmoothRate` shape reused for a seat's own chase framing.

## The validator — the one thick gate

`WorldDefinitionValidator.cs`: `TryValidate(definition, out reason)` /
`Validate(definition)` run over the ENTIRE composed candidate — at boot, on
every live mutation, on whole-document swap, and on every undo-replay entry —
so builders and appliers never repeat semantic checks. Refusals are an
aggregated STRING list (`"Invalid WorldDefinition: …"`); the one
enum-reasoned section is HUD (`HudValidationException` carrying `HudRefusal`,
folded in as `hud.<Reason>: …`). A null required section fails earlier with
`"Incomplete WorldDefinition: <name> is required."` (`RequireSections` checks
31 nullable REQUIRED members — every OPTIONAL trailing member above, `Metadata`
included, gets no `Require` call; the struct sections `motion`/`wander`/
`population` cannot be absent-null).

Notable validator constants: `MaxCameras = 64`,
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
- **`Vector3` as `[x, y, z]`** via `Vector3JsonConverter` (STJ would silently
  zero struct fields otherwise).
- **`$type`-discriminated unions:** `ActionPredicate`, `ActionEffect`,
  `WorldScreenSource`
  (`none`/`testPattern`/`machine`/`camera`/`view`/`capture`/`console`/`qr`),
  `WorldLookSource`, `WorldSpawnPolicy`, `WorldAnchor`
  (`entity`/`entityLeaf`/`placement`/`group`), `WorldCameraMotion`
  (`follow`/`orbit`/`static`/`track`) and `WorldCameraAim`
  (`anchor`/`forward`/`worldPoint`), `WorldSpeakerSource`
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
  `Puck.Forge.Authoring.DocumentJsonOptions.Shared` so the inline embed
  carries exactly the vocabulary its canonicalizer hashes.

**Adding a schema field — the sweep direction.** Adding a top-level SECTION
refuses at boot until every shipped world carries it (`RequireSections`).
Adding a NESTED member does not refuse at parse — it silently defaults, and
usually (not always) refuses at validation — so sweep the shipped worlds
either way. Adding a JSON key with no model member always refuses. Renaming a
member is doubly fatal. One `world.save` re-canonicalizes a file to the
current model.

## Identity conventions

- Screens are POSITION-ADDRESSED by `WorldScreen.Index` (an engine
  screen-surface index); `WorldScreenLink.Screens` and
  `WorldSpeakerSource.Machine` key off the same int — screen index IS machine
  identity for screen-hosted machines.
- Everything else is string-addressed: stable ids (`WorldSceneRow`,
  `WorldCreation`, `WorldPlacement`, `WorldSpawnPoint`,
  `WorldBindingOverlay`, HUD panels/elements, profiles) or names
  (`WorldCamera`, `WorldKit`, `WorldLook`, `WorldChannel`, `WorldSpeaker`,
  `WorldScreenLink`, `WorldAddonRow`, `WorldViewLayout`, `WorldStateRow`).
- Spawn points carry both modes deliberately: `Id` is the mutation address,
  but LIST ORDER is seat identity (seat n spawns at `SpawnPoints[n]`).
- Grant rows are keyed by their `(principal, capability, subject)` triple —
  a grant IS that triple (`Exclusive` and the co-drive fields are row data,
  not key). `GrantSubject` and `WorldPrincipal` serialize as the console
  grammar tokens through their own converters (`all`, `body:<n>`,
  `screen:<n>`, `section:<name>`, `state:<name>`,
  `region:<name>`, `seat:<n>`; `seat1..seat4`, `console`, `addon:<name>`,
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
  parks below the floor.

## Owned-world identities

An identity is an ordinary owned `WorldDefinition` document, not a catalog
row: `WorldOwnedWorlds` (`Server/WorldOwnedWorlds.cs`) is the CATALOG (seats
select identities from it; a seat's profile IS a `WorldIdentity` wrapping one
owned document), one file per identity under the local state directory,
named `WorldOwnedWorldFileName.For(id)` (`"<id>.world.json"`). Every id is a
`WorldSafeName`, which makes the id→file-name mapping INJECTIVE BY
CONSTRUCTION — two identities can no more share a file than they can share an
id. A loaded file whose name does not match `WorldOwnedWorldFileName.For` of
its OWN declared identity `id` is refused by name (`[identity] owned world
refused: …`, distinguishing "the file already holding that id" from "a name
it does not carry") rather than silently renamed or merged; a document that
fails to parse/validate is refused the same way and simply excluded from the
catalog — never a hard boot failure.

**Seeding.** When the identity directory holds zero admitted documents,
`WorldOwnedWorlds` seeds one owned world per `playerDefaults.identities` row
(`WorldIdentitySeed(Id, Name, Color)`, validated non-empty, ordinally unique
ids, case-insensitive unique names, hex color — `ValidatePlayerDefaults` in
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
`bindings` → live session rebinds (freshest wins). Merge rules: the row key
is group + ORDERED chord; a later layer's row for the same key overrides
WHOLESALE when the meaning differs (a `Command`, or a page under a different
id) and ENTRY-BY-SOURCE when both name the same page (per-source replace,
first-touch-per-layer so a hold/release pair in one layer accumulates).
The merged document compiles once per change through
`BindingProfile.Compile` in `Puck.Commands` — deliberately shared, never
copied. `WorldSeatBindings` compares the filtered composed document plus the
ordered channel-name map before compiling: a route that presents a new document
instance with identical effective content is a true no-op, preserving held
commands, chord/page state, and release latches. Live surface: `player.bind`,
`player.bindings`.

Each `WorldBindingOverlay` may also carry `bindingBar`: the presentation policy
for the on-screen mapping bar. Null resolves to `WorldBindingBarAuthoring.Default`
(enabled, no rest timeout, reference layout), preserving the behavior of a row
authored before the policy existed. The first world row supplies the world floor;
the selected identity's own first row may replace it for that seat, matching the
existing first-row binding-layer consumer in `WorldIdentity`. Durations are
authored as `hideAfterRestSeconds` and compiled through the running world's
simulation rate. `world.binding-bar [on|off|auto] [player]` reads the resolved
policy and controls its live visibility override.

**Context rows.** `puck.bindings.v1` carries an optional `contexts` section:
`{family, state, group}` rows (`BindingContextDefinition`), merged across
layers on `(family, state)` — a later layer overrides a re-declared key IN
PLACE, new keys append, so precedence order is authored primarily by the
layer that ships the vocabulary. The seat's ACTIVE group derives as: first
matching context row's group (document order) → the seat's requested group
(`WorldSeatBindings.SetActiveGroup`, the mode pointer) → the profile default.
Families are a closed engine registry (`WorldContextFamilies` — a family is
admitted only as the output of one per-seat single-valued state machine):
`roster` publishes `unjoined|claimed|pending|active`, `engagement` publishes
`engaged|none` (a loopback read over the grant table's Control route, synced
once at post-build wiring and every tick post-step via
`WorldSeatContextSync.Publish`). Compile refuses a malformed/duplicate row or
an undeclared group; the vocabulary gate refuses an unadmitted family/state —
all by row. `player.bindings` leads with the derivation echo:
`group=<active> (<step>)`, per-family `<family>=<state>→<group>
(wins)|(shadowed)|(no row)`, and `requested=<group>` marked `(shadowed)` when
a row overrides it — a matching row that lost to an earlier row is reported,
never silent. No shipped world authors a `contexts` row today (the demo row
that once lived in the retired `kart-remap` scaffold has no surviving
successor); the shape is `{family, state, group}`, e.g.
`{engagement, engaged, engaged}` onto an `engaged` group.

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
fractions, rotation, clockwise ordering, and the initial ring.

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
radial with one six-sector action ring. Presentation and the live verbs:
[views.md](views.md)'s radial-menu section.

## Capacity constants

- `WorldPopulationLimits` (`Puck.World.Schema`): `CapacityCeiling = 128`,
  `LocalSeatCount = 4` (indices 0–3) — single-sourced against
  `WorldClient.EntityCapacity` (the F3 reconciliation, 2026-08-06; see
  [SKILL.md](../SKILL.md)'s "Boundaries" section). There is no
  `MaxPopulation`/`MaxPopulationSimulated` constant; shipped worlds author
  `networkPlayers: 124` (128 minus the 4 local seats) as ordinary document
  data, not an engine ceiling.
- `WorldHudCapacity` (`WorldHud.cs`): see [hud.md](hud.md).
- `WorldStateCapacity` (`WorldState.cs`): `MaxRows = 128`,
  `MaxCellsPerRow = 128` (an authored `capacity` may only narrow it),
  `MaxTextValueLength = 256` (UTF-16 units, a text cell's value).
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
  speakers); `WorldCameraRig.cs` — HOW a camera frames (compiles to one engine
  `ISdfCameraRig`; see [views.md](views.md)).
- `WorldViews.cs` — the `views` section (slots, layouts, seat framing).
- `WorldState.cs` — the `state` section: `WorldStateRow` (the one cell
  substrate — `kind` int/fixed/bool/text, `value` sugar or `cells`),
  `WorldStateCell`, and `WorldStateCapacity`.
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
Committed runner: `docs/verification/strict-definition-parse/run.ps1`.
Validate HUD document changes by running the app — see [hud.md](hud.md)'s
"Verifying" section for the recipe.
