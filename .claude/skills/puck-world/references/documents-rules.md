# The `rules` section

Part of [`puck.world.def.v1`](documents.md). Field names, defaults, and the
`ActionPredicate`/`ActionEffect` `$type` union list are generated
(`puck schema`, or `Assets/worlds/schema/rules.schema.json`); this file is the
decision/derivation prose the schema cannot state.

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
`setState` with `text` writes a `kind=Text` cell (exactly one of `value`/
`valueSeconds`/`fromState`/`text`); because `lookAssignment.rows` and creation
palettes bind to text cells, and a state write re-resolves every bound value
(re-running the look resolve when `lookAssignment` is touched), this is how a
rule restyles a body — the arena module's (`modules/arena.world.json`) `look-*` rules drive one
`lookOf.<body>` cell per body. A text row also takes a `fromState` copy from
another text cell. Two indirections make "the body my `target` cell names"
addressable: a key spelled `$cell:<row>:<key>` resolves to that cell's integer
value at every read/firing (effect `key`/`fromKey`, `compareState`
`key`/`comparandKey`), its inner key may itself be `$each`, and a
body-reference token `cell:<row>:<key>` does the same inside
`$distance:`/`$los:`/`$nearest:`; `placement:<id>` and (over a forEach row
keyed by placement ids) `placement:$each` name the body inhabiting a
placement. `$zone:<ordered-zone>:first|last` resolves an endpoint's original
string key from the active store, including a scratch frame. An empty zone's
endpoint names no cell: a `compareState` over it never holds (not even
`NotEqual`), an expression over it refuses, and a write it addresses refuses by
name. A rule's `zones` table (ordered zones over one token domain, in index
order, `""` a gap) makes `$zones[<index>]` a row position anywhere in the rule —
`compareState` `state`, `$reduce:`/`$match:` rows, a `$zone:` endpoint's zone,
transfer ends, expression rows — with an infix key as the index (`game[from]`,
`$each`, `$bind:<name>`, an expression); an evaluation whose index selects no zone
reads its gate closed (`world.rule.trace` shows `zones [<spelling> -> <zone|none>]`);
`forEach: "$zones"` iterates the table. Rule-authored
transfers resolve their `key`, `from`, and `to` before each transaction step;
direct mutations use literal keys and zone names. Pattern costs use the declared source capacity and actual token-expression
cost, and pattern read sets include attribute and expression dependencies.
The [Solitaire guide](../../../../src/Puck.World/Assets/worlds/games/README.md)
owns the collection's table selector, pile IDs, and request protocol.
A placement's `parent` composes its frame over another's, and a
`board`-named Grid topology anchors its origin to that placement. `$bind:<name>` reads a value the
enclosing rule's `bindings` list computed for this evaluation (feed-forward,
declared order, never stored). Any `expression`/`left`/`right`/`score`/affinity
member accepts an infix string (`"min(damage, hp[$each]) * 2"`, C precedence,
named forms as calls, `row[key]` reads, `$table:t:col[key]` and nested
`buffs[minion[$each]]` (the `$cell:` indirection), backquoted names, `0x`
literals) as
well as the postfix `{ "tokens": [...] }` object; the string parses to the same
tokens (`ExpressionSpelling`) and writes back as a string. Beside arithmetic, comparison, bit ops, `select`,
and the board ops, the call vocabulary carries one Maths family per prefix: `pair(x, y)`/`pairX`/`pairY` and
the Szudzik algebra (`pairSwap`, `pairMax`, `pairMin`, `pairSum`, `pairDifference`, `pairTranslate`,
`pairScale`); `morton`/`mortonX`/`mortonY`; `hilbert(order, x, y)`/`hilbertX`/`hilbertY`; the hex family
over `HexagonalIndex` (`hex(q, r)`, `hexQ`, `hexR`, `hexRadius`, `hexEuclideanSquared`, `hexDistance`, `hexNeighbor`,
`hexRotate`, `hexMirror`, `hexSwap`, `hexAdd`, `hexSubtract`, `hexMultiply`, `hexScale`, `hexTranslate`); the
square family over `SquareIndex` on the same spellings (`square(x, y)`, `squareX`/`squareY`, `squareRadius`
(Chebyshev), `squareLength`/`squareDistance` (Manhattan), `squareChebyshev`, `squareNeighbor` E/N/W/S,
`squareRotate` quarter turns, …) — a shell-ordered index over Z², not a grid topology's row-major ordinal, so a
grid cell still bridges through `%`/`/` or `$board:offset`; `gcd`/`lcm` (`gcd(dx, dy) == 1` is a lattice line
with no interior point); the floored `mod(a, m)` with `cycleForward(a, b, m)`/`cycleDistance(a, b, m)` for
track and pit races (`%` stays C truncation); `smallestMissing(mask)`, the smallest value missing from a 64-bit set (the Sprague–Grundy value of a set of options); `isPrime(n)` (exact, bounded) and `prime(i)` for
i in 0..255 (a Gödel multiset in one cell: add is `* prime(k)`, presence is `% prime(k) == 0`) — no next-prime or
n-th-prime search, which is unbounded and belongs to a generator draw source; `choose(n, k)`, `factorial(n)`
(n ≤ 20); a subset as a bitmask — `subsetRank(n, mask)` (colex rank below `choose(n, popCount(mask))`, n ≤ 64),
`subsetAt(n, k, rank)`, `subsetMember(n, k, rank, i)` — so a poker hand or a drafted set is one cell; a
permutation of 0..n−1 packed as nibbles (position i in bits 4i..4i+3, n ≤ 16) — `arrangementRank(n, packed)`
(lexicographic Lehmer code below `factorial(n)`), `arrangementAt(n, rank)`, `arrangementMember(n, rank, i)` — so a
turn order or a shuffled short deck is one cell, read back with `bitField(packed, 4 * i, 4)`;
the layer family over `LayerSequence` (`layer`, `layerOffset`, `layerStart`, `layerSize`, each
`(index-or-layer, start, step, seed)` — `layer(i, 6, 6, 1)` is `hexRadius(i)`); and `sqrt` (both kinds),
a keyed read is `row[key]` (a bare name or number is the literal key), `row[other[k]]` (a `$cell:` indirection), or
`row[from + 1]` / `row[(from)]` (any other expression as the key — an implicit int binding evaluated before the
gate, traced as `$key<n>`, one of the rule's bindings; identical expression-key spellings share a binding within
the same binding scope, never across rules or pattern-local scopes; parenthesize a bare name to read its row's value);
`sin`, `cos` (fixed radians). Every other function is int-only, and a domain fault fails the expression the way
an overflow does — and is counted: a faulting binding, effect, or `compareValue` conjunct reports `Arithmetic`
in `world.rule.failures` (narrated once per category on stderr) and `world.rule.trace` shows the conjunct as
`refused`, so a rule that "stopped firing" is read there first. Sharp edges an author hits by instinct: `%` is C
truncation (`(pit - 1) % 14` reads -1), so a circular index is `mod(pit - 1, 14)` or `cycleForward`; `>>` is an
arithmetic shift that drags the sign bit through a mask with bit 63 set, so a bitboard shifts with `>>>`;
`pair(x, y)` admits components up to 3,037,000,498 (a hash or a Q48.16 raw does not pair); a Gödel multiset
holds the first 15 primes at count one (their product is 6.1e17) and overflows at the 16th (`* 53`), and every
count multiplies, so cap the row or the item set; `$board:mask`/`writeSet` stop at 64 cells while a topology
admits 4,096 — a wider board's set algebra is the `boardCombine` transform (and/or/xor/andNot/not/shift/image
over whole board rows, one journaled mutation each); a `transfer` of `count > 1` takes `first`/`last`/`random`,
and an interior run is `slice`: the keyed token and everything after it, in order; a pile's order is one integer
through `$reduce:arrangementRank:<zone>` (k ≤ 20) and the `arrange` transform puts it back. `$table:<name>[:<column>]:<key>` reads a static
`tables` document (`puck.table.v1`, hash-pinned, outside simulation state) by an
integer literal, a `$cell:` indirection, `$each`, or an int `$bind:`; a missing
dynamic key is a `TableKeyMissing` refusal, never a value. Every top-level
state effect is its own boundary; only a `transaction` groups effects
atomically, and it journals once (a `Batch` mutation whose replay composes its
members in order), so three `boardCombine`s in one transaction are one entry. Branches use ordinary `ActionEffect`
records; `EffectFamily.AllowsTransaction` opts registered arms in, while the compiler rejects nesting and `save`.
`BoardCombination` owns the compiler/frame/live board operation contract; `copy` preserves all source values,
including its empty value when the target's differs. `$symmetry:<function>[:<argument>]:<row>`
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
ties to the lowest index) — the arena module's `auto-target` rule is
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
declared `Domain` (`StateDomain` — `Slot`/`Keys`/`KeysOf`/`CellsOf`/`Ring`;
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
is a `CellName`, the SAME validated-identifier type a state row and a cell
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
The arena module (`src/Puck.World/Assets/worlds/modules/arena.world.json`) is the worked example: two
boot-declared region placements (`firePit`/`icePool`), `Region` interactions
that set countdown cells, and `Level` rules that drain/countdown them.

## Discrete state, patterns, and impressions

Discrete state shares `state.lattices`: only `Field` creates physical storage;
`Grid`, `Ring`, and `Hex` compile bounded adjacency. Keep token identity domains,
ordered zone membership, position attributes, phase progression, and knowledge
stamps inside the canonical state row converter and authoritative hash. A
`cellsOf` row's `inverse` trait (`Puck.State.StateInverse`) declares it derived
from a keyed token row and a codes row rather than authored — the board is
refused by name at the mutation door and the validator alike, and the engine
recomposes it from `tokens`/`codes` at compose and install
(`Puck.State.DerivedBoards.Compose`); a frame recomputes only the moved
token's two cells. The
closed transform union is shared by mutations and rule transactions. Readers,
secret draws, and observation payloads have separate authority/presentation
semantics; see the owning contract in
[`Puck.World.Schema`](../../../../src/Puck.World.Schema/README.md#discrete-boards-cards-and-turns).
Do not flatten restricted state into a public document value. Replica access
remains full authority trust. Test socket observations using authenticated
submission stamps, and check exact topology and query work bounds at preflight.

A `patterns` section row is a regular language over cell values
(`Puck.State/PatternRow.cs` + `CompiledPattern.cs`: symbols as value ranges, a closed node vocabulary with
complement and intersection, a derivative machine inside a state budget of at
most 256) compiled at validation;
rules read it through `$match:<pattern>:<row>[:<direction>|:any][:prefix|:mask|:count]` over a board
ray, a zone's attribute word or per-token `value` expression (`$token` and `$previous` keys; a word starts at the
operand's `key` token when one is given, so a cascade's legality from a card is one read),
a `history` ring (`push`/`pushState`, `$history:<row>:<age>`), or a keyed row;
`$board:mask`, the `boardShift`/`boardFill`/`boardImage` expression ops (a fill is the union of
repeated shifts to the edge: a file or a rank from one seed bit, never a hand-written wrap constant), and the
`writeSet` transform carry the one cell-set vocabulary up to 64 cells, and the
`boardCombine` transform carries it over boards of any size; a group and its
breathing room are `$board:component:<row>:<min>:<max>:<maxVisits>` and
`$board:boundary:<row>:<min>:<max>:<boundaryMin>:<boundaryMax>:<maxVisits>`
from a key cell (a flood under a settled-cell budget, -2 when it runs out); a placement's legality is
`$board:boundaryAt` (the placed value's own boundary, the key cell excluded) and `$board:enclosedAt` (the cells of
adjacent components whose only boundary cell is the key cell) keyed on the empty cell — admissible iff
`boundaryAt > 0 || enclosedAt > 0` — and the `clearEnclosed` transform sweeps those components once the value lands;
`world.match` narrates one word.
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
