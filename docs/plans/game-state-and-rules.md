# Game state and rules

This plan defines the proposed state and rules vocabulary that the reference
game needs for actions, phases, boards, cards, transfer, and social memory. It
records why each owner decision changes the shape, what has landed, and which
behavioral evidence remains limited to a named fixture or replay.

The `ActionEffect.Judge` and judge asset family collapse into a
`$clock:<music>:phaseError` operand as an owner decision. `ActionEffect.Judge`,
`WorldJudgeRow`/`JudgeDocument` (`puck.judge.v1`), and the rhythm mechanism
they carried (`Puck.Audio.Simulation.RhythmJudge`, wired through
`MusicDirectorFactory.cs`) were a hit-window judge — the mechanism
underneath is signed phase error between a firing tick and the world's
`MusicClock`, which the new operand exposes directly: `remainder =
ElapsedTicks mod ticksPerBeat`, signed to `remainder` (late) or `remainder −
ticksPerBeat` (early, past half a beat, tied toward "late"). A hit window is
now an authored `compareState` range over it (`ClockPhaseErrorLawTests`
proves an authored two-tolerance range grades a press exactly as the
retired windows list did), with no dedicated effect or section; `music.state`
carries the live value as its `phaseError` field, the read-back the
retired `judge.state` verb owned. This family had zero callers in every
shipped world — the only asset that existed (now deleted, under
`Assets/worlds/judges/`) was referenced by no world (only by the retired
`music-judge-press` canary,
itself referencing a `prototypes/` world that no longer exists) — so
deleting it touched no shipped rule: the schema, validator,
`Puck.Physics.Motion.BodyMotionOp`, the wire vocabulary
(`WorldQuery.JudgeState`, `SessionRequest.cs`/`WorldSubmissionCodec.cs`),
the checkpoint codec's `JudgeGrades` section, `MusicDirectorFactory.cs`, and
their law tests. `Puck.Audio.Simulation`'s vocabulary never grew a "pattern"
word of its own, so the collision the state `patterns` section might one day
share with it never materialized — nothing to rename today.

**Carry, as attachment (owner decision).** Picking up a rigid body is not a
second attachment primitive beside the surface-hold system — it is a
carrier-declared kit facet (`carry`: a body-local frame offset, a
mass-equivalent, and a reach) authored the same "presence is the whole
switch" way `rigid` is. While carried, the target's own rigid integration is
suspended entirely — never solved — but its pose is not an unconditional
follow either: tangible carry sweeps the target's own collider from its
previous pose to the carrier-derived one against static geometry every tick,
and separately resolves it against every other active solid body on the same
positional-split terms an ordinary contact pair already uses, so a carried
body pushes and is blocked rather than passing through walls or other
bodies — and whichever correction either sweep applies is handed back to the
carrier too, so holding something that cannot advance stops the carrier, not
only the object. `body.release` refuses by name, leaving the relationship
untouched, when the target's current pose still overlaps geometry or another
body — the backstop for the rare case the continuous sweep above did not
already prevent. It re-enters the solver with the carrier's own velocity on a
successful
release, never snapped to rest. A body may carry at most one other body at a
time; a candidate must sit within the carrier's own live-scaled reach and its
own live-scaled mass must not exceed the carrier's mass-equivalent times an
authored fraction — the same mass ∝ Scale³ law a rigid body's own mass scales
under, so a shrunk carrier's ceiling shrinks with it rather than staying a
free constant. `body.carry`/`body.release` are the console/wire surface (the
same shape `body.impulse` already established for a rigid-solver-facing
verb); a rule effect and an authored chord are follow-on work, not yet built.

**Handle completion is the union rewrite's settled precursor (owner decision).** Every
per-tick reader that used to resolve a state row by name — a symmetry operand's source and
`cell:` argument, a `$cell:` key indirection, `$argmax:`/`$argmin:` (both the row itself and
its `:where:` filter), `$nearest:`'s tag row, a body reference's `cell:<row>:<key>`
indirection, and the `$board:`/`$phase:` readers — now carries a `WorldStateHandle` compiled
once at `ResolveOperand`/`ResolveCellRef`/`ResolveBodyRefToken` time and reads through
`WorldStateReader.TryReadHandle` instead of a name scan, matching the handle-based path
`ReadReduction` already used. The vanished-row question is settled the same way for all of
them: a compiled handle is only ever minted against a row `WorldRuleCompiler.CompileAll`
already proved present, and every document install revalidates by recompiling every rule
against the same candidate document — so an installed document can never carry a rule whose
handle addresses a row that has vanished, and `TryReadHandle` throws rather than reading a
neutral value it should never need to. This is a pure representation change: the passive
300-tick garden replay and the frozen world's 720-tick replay both hash identically before and
after. The case-type/union rewrite below is unstarted; this only clears its stated first step.

**Compiled rule operands and effects are closed unions, built to the union pattern before the
compiler has it (owner decision). Both halves have landed.**
`CompiledWorldOperand` and `CompiledWorldEffect` were flattened structs carrying every fact
kind's parameters at once, copied by value into every predicate, expression token, and
reader — the shape was wrong, not merely large. `CompiledWorldOperand` is now a carrier
struct (`WorldOperandUnion.cs`) over one sealed class per `WorldRuleFactKind`
(`WorldOperandKinds.cs`, 22 cases — never records or structs, since a union boxes a value
case on store and nothing at runtime compares two operands for equality or identity), written
to the C# 15 basic union pattern by hand: a `[Union]` struct holding one `object?`, a
constructor per case, `Value`, `HasValue`, and a generic `TryGetValue<T>` — with the two
attribute and interface types (`UnionAttribute`/`IUnion`) polyfilled internally until .NET 11
supplies them. Dispatch is a type-pattern switch over the cases at `WorldServer.ReadWorldFact`
and `WorldRuleWorkBudget.OperandCost`; `WorldOperandUnionLawTests` enumerates every fact kind
against the case-type table until the compiler's exhaustiveness takes over. The day the
toolchain moves, the flip is deleting the polyfills and switching on the carrier instead of its
`Value`; nothing else moves. `Kind`/`ValueKind` are the only members every case carries
(`WorldOperandFact`'s base, set once by each case's own constructor); everything else lives on
the concrete case, reached by the type-pattern switch or, for the four cases that share a
(row, key-indirection) address (`StateCellOperand`/`BoardOperand`/`PatternOperand`/
`SymmetryOperand`), through the narrow `IStateAddressedOperand` interface. Row and key names
still leave the hot object for compiled handles, kept only in the refusal text. This is a pure
representation change — the passive 300-tick garden replay hashes identically before and after
(`0x397968B8F541A2C4`). `CompiledWorldEffect` followed the identical shape: a carrier struct
(`WorldEffectUnion.cs`) over one sealed class per `WorldRuleEffectKind` (`WorldEffectKinds.cs`),
firing/preflight/transaction switched on the case types via `effect.Value`, factories replacing
the compiler's `with` clones. Both carriers share one `UnionPolyfill.cs` (`UnionAttribute`/
`IUnion`, attributed for either a struct carrier or a class/record hierarchy) rather than each
declaring its own copy.

**Cellset-domain unification, the 64-cell half (landed).** The forked
vocabulary was never a type-system problem: `$board:mask` already reads a
board's occupancy as a plain `Int` 64-bit cell-set, and `ValueToken`'s
`bitAnd`/`bitOr`/`bitXor`/`bitNot`/`popCount`/`lowestSetBit` were already
generic ops over that same `Int`, so no new operand/effect value kind was
needed to unify it — only the genuinely duplicate spellings were. Deleted:
`WorldBoardQueryKind.Image` (a baked read-and-image query that duplicated
`$board:mask` piped through the `boardImage` expression op — the op already
existed and is the one kept spelling), `WorldBoardQueryKind.CanonicalMask`
(the least image-mask fold; `Canonical`, the FNV fold over a board's actual
values, is the one canonical form now — a caller wanting canonical-under-
membership materializes a 0/1 board with `writeSet` and folds that), and the
`setMask`/`combine`/`mapBoard` state transforms, replaced by one
`writeSet(row, set, value)` (`set` names the integer cell holding a mask —
exactly `setMask`'s own shape, renamed; `combine`'s and `mapBoard`'s row-vs-row
reads compose ahead of time into that mask cell via `$board:mask` and the bit
ops instead). None of the four deleted arms was authored in any shipped
world or canary — the tabletop's 106 rules, the poker table, and dominoes ran
on `$board:mask`/`neighbour`/`rayCell`/`offset`/`attacks` throughout, so the
passive 300-tick garden hash is unchanged by this change, and the frozen
world's own hash likewise. `writeSet` keeps `setMask`'s 64-cell ceiling —
a mask is one expression value and an expression value is one word. Past 64
cells the set algebra is the `boardCombine` transform: and/or/xor/andNot/not/
shift/image over whole board rows of one topology, membership being "not the
board's empty", one journaled mutation per operation at three walks of the
board — a 19×19 attack map is three transforms where an 8×8's is one
expression, and no multi-word cell-set type enters the expression language.
A solitaire cascade is a `slice` transfer: the keyed token and every token
after it, moved in order as one run. The
[Solitaire collection](../../src/Puck.World/Assets/worlds/games/README.md) uses that
primitive for the Windows XP games: Klondike, Spider, and FreeCell. Their card
rules belong in authored state and patterns, with bounded dealing phases and
the existing console as their control surface. A connected group is `$board:component` and its
boundary `$board:boundary`: a flood from the key cell along the topology's
directions under a settled-cell budget, priced by that budget like `pathCost`,
reading -2 when the budget runs out rather than ever running unbounded; what
a placement encloses is `$board:enclosedAt` before it lands and the
`clearEnclosed` transform after, so a group's removal is one effect. A
transaction journals once: the host commits the preflight scope as one `Batch`
mutation — one admission, validation, journal entry, and delivery — so the
three transforms of a large-board capture cost the journal what one write does.

**The row-domain facet collapse has landed (owner decision).** `WorldStateRow`
carries one `Domain` (`WorldStateDomain`: `Slot`, `Keys`, `KeysOf(row,
ordered)`, `CellsOf(topology, empty)`, `Ring(capacity, empty)`), built to the
same sealed-case-class-plus-`[Union]`-marker pattern the compiled-operand
rewrite above is landing (shared `UnionPolyfill.cs`). `IsKeyed`/`IsSlot`/
`CellCeiling` are one switch over it; an unauthored row still infers `Slot`
or `Keys` from `cells`/`capacity` alone, so a plain row spells nothing new.
`Tokens`/`Zone`/`KeysFrom`/`Board`/`History` are deleted outright; `Lattice`
survives only as `WorldStateFieldTrait` (the physical-field row's leftover
parameters — `initial`/`min`/`max`/`heightScale`/`color`/`paint`/`medium`),
its own `topology` member folded into `Domain.CellsOf` since a `Field`-kind
`CellsOf` row and a discrete board are now one case, split by `Kind` alone.
The document's own token-domain declaration is simply a `Keys` row whose
`capacity` is the domain size — no second facet — and other rows address its
keys through `KeysOf`; `ordered: true` is what a pile/zone needs, `ordered:
false` (the default) is a plain keyed attribute row. The "every token belongs
to exactly one zone" invariant is no longer engine law: it is an authored
rule in the garden (`cardsZoneAccounting`/`cardsZoneInvariant`, summing
`$reduce:count:` over `deck`/`hand1`/`hand2`/`community` against the `cards`
domain's capacity) — a card leaving the tracked total moves a flag, never a
validator refusal. Every shipped and canary world is migrated to the `domain`
member once; the `capture`-scope `world.state.hash` (what `world.state.hash`
reports by default, and the actual simulated trajectory) is unchanged for
both the garden and the frozen island. The `authoritative`/replay-tape scope
— what `replay.verify` checks — moves, unavoidably: it used to hash a
now-collapsed `Tokens.Capacity` slot distinct from a row's own generic
`Capacity`, and real documents already carry ordinary capacity-bounded rows
(`hound`/`spider`/`pieceCell`/…) representation-identical to the old token
declaration post-collapse, so the old byte, which one distinction ended up
in, cannot be reconstructed from the new shape without keeping the very
facet this change deletes.

**The generator's card nouns are renamed to its actual primitive: multiset sampling
(owner decision, Lane 2c).** `WorldGenerator` draws from a weighted entry set with
optional exhaustion — a mechanism the schema, server, console, and tests spelled with
card-game words that named no capability a neutral spelling couldn't: `WorldStateRow.DrawDecks`
is `DrawnMasks`, `WorldGeneratorCapacity.MaxCardsPerSet` is `MaxEntriesPerSet`,
`WorldGeneratorMode.ReshuffleOnExhaustion` is `RestartOnExhaustion`, and the per-entry repeat
field on `WorldGeneratorAlternative`/`WorldGeneratorWeightedNumeric` (JSON `count`) is
`Multiplicity` (JSON `multiplicity`). `WorldGeneratorEngine`'s own internal vocabulary
renamed with it — `Deals`/`DecksAfter` to `Exhausts`/`MasksAfter`, the private `Deal` method
to `DrawEntry`, and every `card`/`deck`/`dealt` local variable and doc comment to
`unit`/`mask`/`drawn` — since a second, unrenamed vocabulary living one layer under the
public one is the same duplication that the documentation policy calls out. No shipped world or canary document
authored the alternatives/weighted vocabulary (the garden's poker table uses the tabletop
primitive's own token/zone/transfer vocabulary instead, never `WorldGenerator`), so the
only document fixture touched is `tests/Puck.World.Canaries/lattice-draw-fill/fixture.world.json`
plus that canary's and `symmetry-orbit-source`'s asserted console text (`decks=` → `masks=`).
Pure rename, no behavior change: the passive 300-tick garden replay hashes identically
before and after (`0xCC2D4742992B05CC`).

**`WorldStatePhase` is reduced to the guard stamp it names (owner decision, Lane 2a).**
A phase row now carries nothing but its own generation (`WorldStatePhase(long Sequence)`);
`WorldPhaseMode`, `WorldPhaseDefinition`, and the `completePhase`/`turnOrder` transforms
are deleted. `WorldPhaseGuard(Row, Sequence, Participant)` is the whole primitive: a
guard's sequence must match its row's before the guarded transform composes, and that
transform's success advances the row's generation by one in the same mutation — the
guard both admits and completes a turn, so a world that wants several ungated moves
before one ends reserves `PhaseOf` for the single row that should end it. Whose turn it
is, rounds, ready/skipped bitsets, and deadlines are no longer engine knowledge; a world
authors them as ordinary rows (a counter, a bitset board, a keyed "active" row) and rules
that read and write those rows with the same generic effects every other row uses. The
`$phase:<row>:current|active|ready|sequence|round|deadline|direction|skipped` fact
collapses to `$phase:<row>`, reading the one thing left to read — a phase row's own cells
stay empty, so this is not redundant with `$cell:`.

**`setRay`, `shuffle`, and `sort` are re-cut to their real shapes (owner decision, Lane 2b).**
`setRay`'s `through`/`until` fields are replaced by a `pattern` reference: the transform
walks the ray from its origin and writes the longest run the named `patterns` row accepts
— the same compiled machine and prefix semantics `$match` already runs, landed back on
the board instead of read as a fact. A Reversi-style bracket capture is authored as
`plus(opponent) . symbol(own)`; writing the accepted run's own-color terminator back to
itself is idempotent, so no engine-side exception carves the terminator out of the write.
`shuffle` permutes any ordered zone or any other keyed row, not zones alone — the
Fisher-Yates pass never read zone structure to begin with. `sort` splits into `sortZone`
(attribute keys over a zone's token domain, each carrying its own direction) and
`sortKeyed` (a keyed numeric row's own values, one `descending` flag): one `$type` was
carrying two authoring surfaces that refused each other's fields only at validation time;
two `$type`s let the shape refuse at the type level instead. The garden's two `sort`
rules move to `sortKeyed` (pure rename: the passive 300-tick replay hash is unchanged);
no shipped world or canary declared `completePhase`, `turnOrder`, `setRay`, `moveToken`,
or `shuffle`, so no other document migrates.

**`moveToken` is retired; `$board:pathCost` gains a dynamic target (owner decision, closed
union follow-on).** The opaque transform (pathfind, allowance debit, and occupancy baked
into one C# compose arm) is deleted; `$board:pathCost:<terrain>:cell:<row>:<key>:<maxCost>:<maxVisits>`
reads its destination ordinal live from another declared row's cell — the same `cell:<row>:<key>`
indirection `$distance:`/`$los:` already spend their own body-reference grammar on — instead of
only the compile-time literal ordinal it took before. A world now authors "move a token" as
ordinary rules: an affordability gate comparing the live path cost against a live allowance row,
and a `transaction` that debits the allowance by that same live expression, clears the token's old
occupancy and terrain cells, writes the new position, and sets the new occupancy and terrain
cells — nothing atomic left for the engine to own on the token's behalf.
`tests/Puck.World.Tests/DiscreteStateLawTests.cs`'s
`APathCostTransactionMovesATokenUnderAnAllowanceAndRefusesWhenCostExceedsIt` proves the gate reads
the cost live rather than baking a stale one at compile time (the control: raising the allowance
alone flips an unaffordable request to affordable). `tests/Puck.World.Canaries/tabletop-state`
re-authors its guarded move the same way; the passive 300-tick garden replay hash and the frozen
720-tick replay hash are both unaffected (`moveToken` was never shipped in either world).

**The local auction house dissolved into an escrowed conditional transfer over ordinary keyed
rows, authored entirely as rules — no bespoke market mutation kinds, no market-only C# compose
arms, no market-only checkpoint finality barrier.** `WorldMarket.cs`, `WorldServer.Market.cs`,
`WorldEconomicSettlement.cs`, `WorldMarketCommandModule`, and the `market` document section are
gone; ordinals 65-70 are retired, never reassigned. A listing's escrow, an outbid's refund, a
deadline's settle-or-return, and a house fee are ordinary `AddState`/`SetState` (a literal, a live
copy via `FromState`, or a computed share via `Expression`) against keyed `state` rows, a
`ScheduleState` deadline compared against `$tick`, and a `PushState` history ring for the bid
order — the same closed effect vocabulary every other rule already authors with, proven for both
an English auction and a buyout by `tests/Puck.World.Tests/EscrowedTransferRuleLawTests.cs`. A
world that wants a market authors it; the engine no longer ships one. `world.undo`'s old
market-finality carve-out goes with it — a rule-authored settlement is an ordinary journal entry
like any other state write, undoable like any other.

**Social memory dissolved into ordinary keyed rows; evidence deduplication is the one thing an
ordinary rule cannot already express, and it needed no new engine mechanism either.**
`WorldSocialMemory` (and its checkpoint/federation-transfer machinery), `WorldSocialPolicy`,
`state.social`, the `observeSocial`/`forgetSocial` effects, and the `social`/`socialClock`/
`socialResult` facts are gone. An impression is a keyed `state` row (one cell per observer),
updated by an authored `AddState` expression that blends new evidence toward the row's own
bound — `(1 - value) * rate`. A Level-mode rule re-evaluates its gate every tick it holds, so the
one thing that needed guarding against was re-blending a standing, unchanged claim every tick
instead of once per event; the fix is an authored `CompareValue` gate comparing a packed
`(origin, sequence)` Int64 (`shiftLeft`/`bitOr`) against a companion marker cell the rule sets
once admitted — ordinary postfix arithmetic, not a new dedup primitive. An Edge-mode rule or a
Distance interaction needs no such gate: the engine's own edge/per-pair latch already refuses a
re-fire while the gate stays continuously true. Proven, with a control row that has no freshness
gate and keeps re-blending every tick, by
`tests/Puck.World.Tests/KeyedImpressionDedupLawTests.cs`. Because social memory no longer lives
in a bespoke parallel store, it never had a federation-transfer story to dissolve either: an
ordinary keyed row is local to its world, exactly like every other `state` row, so an individual's
beliefs simply do not travel with it across a transfer — a real behavior change from the old
system's frozen-observer export/import, and a deliberate one (nothing shipped exercised it). An
impression keyed by observer alone conflates every subject it has ever concerned — a hound's trust
in whoever holds the bone right now silently becomes its trust in whoever holds it next the moment
the bone changes hands. `$pair:<bodyRefA>:<bodyRefB>` (`WorldRuleFacts.PairKeyPrefix`) is the
composite-key indirection that fixes it: on the same terms as `$cell:`, it resolves to a directed
`"<a>_<b>"` cell key (`(a, b)` and `(b, a)` name different cells), so a keyed row holds one cell per
(observer, subject) pair instead of one per observer. The garden re-authors `witness-claim`/`rumor`/
`choose-companion` and the pack kit's `alignmentAffinity` onto `boneHolderTrust` keyed by
`$pair:<observer>:cell:boneHolder:0` — each hound's own trust in whichever specific body it has
witnessed holding the bone — moving its hash. `hounds-meet` and its `affection`
dimension are retired rather than re-keyed: it was a Distance interaction (`O(population²)`
worst-case reach) whose only effect was a delivery an ordinary `AddState` now prices at the
engine's real conservative per-write cost — at this population size that product alone exceeds the
declared work-unit ceiling, a genuine cost the old bespoke effect's flat pricing had been hiding
rather than a regression to work around.

