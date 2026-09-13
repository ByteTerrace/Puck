# Tabletop games and composed worlds

This plan extends the reference game into tabletop districts and worlds
composed from reusable parts. It describes the proposed board, placement,
search, and game rules work, while retaining the acceptance conditions,
implementation decisions, and dated evidence that constrain the sequence.

The tabletop primitive is an owner decision for Lane D. Physics-first extends to
board games: a chess set is 32 ordinary rigid bodies on a shared `piece` kit —
no second entity kind, no engine-level "piece" concept. A placement's `board`
facet (`WorldPlacementBoard`) anchors a discrete Grid topology (already
carrying its own world-space origin/cellSize — no second frame member) to the
placement, and a world rule derives an occupancy row from each piece's
resting cell (`$board:cellOf:<row>:body:<n>`, a new reserved channel, Grid-
only) on `$physics:quiescent`'s rising edge — never every tick, and gated by
the `$upright:<bodyRef>` reserved channel (a body's own up axis dotted
against the world up its gravity opposes) so a knocked-over piece reads as
displaced rather than occupying its last resting cell. Legality is
authorable, not engine-adjudicated, and the shipped garden's default set is
everything short of adjudication: movement geometry for all six piece kinds,
captures, check, castling, en passant, and promotion. The judge now constructs a candidate from the side-to-move's source and
destination, then exactly matches its expected board against the physical
observation. Four lossless bit planes distinguish every cell value, and their
mask differences detect every changed square; direct
comparisons check the candidate's at most four affected cells, while a mask
rejects every change outside them. Ordinary moves, en passant, and castling are three small board
patches; promotion chooses the ordinary patch's replacement. The king's own
mask identifies its pair during castling. Local bindings hold attack and
geometry intermediates, and relative ranks share pawn geometry between colours.

`lastLegal` anchors accepted state and supplies every pre-move read. An incomplete or refused observation cannot become the next
move's starting position. Only acceptance commits the board, turn, en passant
target, check state, four castling rights, and promotion completion. Rights
survive a physical displacement that the players repair; a completed legal king
move or home-rook departure/capture consumes them permanently. Promotion waits
for the replacement before advancing the turn. Duplicate occupants and unrelated
piece-code changes prevent acceptance. The canonical-history counter remains a
diagnostic, not a repetition adjudicator. Checkmate, stalemate, full draws, and a
CPU opponent remain beyond this module. The
[chess authoring notes](../../src/Puck.World/README.md#the-world-as-data) own the
matcher, diagnostics, and physical settle contracts. `plan` is an ordinary,
unrendered board row that the console can write for candidate highlights.
Boards are a primitive the catalog reuses (checkers, go, cards on a table), never a
chess-specific engine feature, and a topology is carried by at most one
placement. The shipped `body.carry` facet is a separate primitive: it picks
up a rigid body, never a placement or board. See
the [schema reference](../../src/Puck.World.Schema/README.md#discrete-boards-cards-and-turns)
and `world.tabletop`'s console read-back.

The board itself renders as 64 ordinary placements (`boardSquareLight`/
`boardSquareDark`, one per cell, colors from a `boardColors` text row) rather
than a bespoke board-rendering feature — the same placement/prototype and
`state.<row>.<key>` palette-binding vocabulary the pieces already use, so a
future board (checkers, go) needs no new client code either. Each top-level
effect of a rule is its own boundary — one piece leaving the frame (a
capture, a knock clear off the table) refuses only its own write and never
its neighbours' — so the per-piece derive rules could fold into one rule;
the explicit `transaction` effect is the only atomic group. A walker's own capsule
reach already exceeds a 0.2 m cell, so no body can stand on the 1.6 m board
itself without risking contact; the garden's proof keeps Wren at a safe
standoff beside the table and moves pieces by console verb, never by having
her body touch one.

**The hidden-hand poker table (owner decisions, Lane C; re-cut 2026-09-06 into
heads-up fixed-limit hold'em).** State only, no card bodies: a `cards` token
domain with `rank` AND `suit` attribute rows, a
`deck`/`hand1`/`hand2`/`community` zone family, and, since the re-cut, a whole
game: blinds, four betting streets with a raise cap, fold, a showdown that
awards the pot (a tie splits it, the odd chip to the button), the cards
collected back into the deck, and the next hand dealt on request or
automatically (`house.autoDeal`). Placeholder card backs read
through `rank`/`suit`'s own public, `Hidden: Placeholder` visibility rather
than the zone rows themselves: a row's own `visibility.readers` is
all-or-nothing (`WorldStateDisclosure.Compose` gates the whole row once
before ever walking cells), so a zone can show every one of its member
tokens to an admitted reader or none, never a placeholder for the rest,
while an attribute row keyed over that zone's domain resolves each cell
through its owning zone's OWN visibility (`Observer.CanRead`'s nested
zones-by-domain lookup, which requires the attribute row's own `keysOf`
domain: drop it to save budget and `rank`/`suit` both go fully public, an
opponent's hole cards included, a near-miss the first landing corrected).
The `keysOf` domain now also declares `capacity: 52`: a `keysOf` row that
authors no capacity is priced at the 4096-cell row ceiling inside every
transform's storage term, document-wide, so the two attribute rows alone were
taxing every `transfer`, `sort`, and `boardCombine` in the garden, chess's
included. `hand1`/`hand2` keep their own `readers`/`readersFrom` for each
seat's direct, full read of its own two cards, `poker-showdown` widens that
same `readersFrom` row at showdown, one more use of the tabletop primitive's
own reveal seam, not a second one, and the seat's hole mask and strength word
live in `private1`/`private2` under the same policy, so nothing derived from
a hidden hand is ever public.

Three decisions carry the re-cut. (1) Hand strength is derived by expressions
over one 64-bit suit-lane mask (`poker-see-*` fold the cards in with
`forEach`, `poker-evaluate-*` rank them in sixteen bindings), not by
sorted-word patterns: the first table needed a scratch copy of each seat's
ranks and a `sortKeyed` per seat before its adjacency patterns could read,
folded only `pairAny` live, and its pattern rows (`pairAtRank2..14`,
`hasTripAny`, `hasQuadAny`, `straightAny`, `suitAtLeast5_*`,
`raiseAfterTwoChecks`) are deleted with it; the evaluator ranks every
category with its kickers for a small fraction of what the two sorts alone
cost (consult `world.budget.rules`, never a figure quoted here). (2)
`phase.street` has ONE writer, `poker-transition`, and every other rule
requests a change through `phase.next`: `RuleWorkBudget`'s exclusion trie
admits one summed value per writer of the pinned cell plus one, so a street
cell five rules wrote would have priced all eight transforms as a sum, where
one writer prices the two costliest streets (the deal's two transfers, the
collect phase's three). The row is named `phase` because the trie orders
pinned cells by their `row.key` spelling and the street must lead every
rule's pin set for the nesting to hold. (3) One rule per action for both
seats: the acting seat's pending action is read through `table[bettor]` in a
`compareValue` gate and its own cells are written through the expression key
`$expr:table[bettor]`; the two `betAction` ingress rows stay separate only so
each seat's `Edit` grant covers its own, and `poker-discard`, declared last,
clears and counts whatever no handler accepted in the tick. The garden's
static work sheet reads lower after the re-cut than before it (`world.budget`)
with the table doing strictly more, and the one hand per boot the first table
was limited to is gone: the collect phase returns every card. The law suite
(`tests/Puck.World.Tests/PokerHandStrengthLawTests.cs`) runs the shipped rules
on a real server: every category's exact strength word with a near-miss
control, the ordering, a full hand to showdown conserving chips and cards
and revealing both hands, and a fold with an out-of-turn discard.

**Garden W3 integration (owner decision).** The tabletop-rules, rigid-fidelity,
and cards lanes were each authored and budget-checked in isolation, every one
landing comfortably under `WorldStateCapacity.MaxRows` and
`WorldRuleCapacity.MaxWorkUnitsPerTick` alone. Merged into one document the
three lanes' `state.world` rows and rule/transform costs sum past both
ceilings — a document-capacity collision the per-lane work could not see, not
a defect in any one lane's design. Both ceilings are structural (document size
and static per-tick work, never a fixed-size buffer or a per-world tunable),
so the fix is to widen them rather than cut a lane: `MaxRows` 128 → 256,
`MaxWorkUnitsPerTick` 1,000,000 → 2,000,000.

**Games are imported fragments of one composed world (landed).** Each game in
the garden — chess, poker, dominoes, billiards, bowling, and a 4x4x4 tic-tac-toe
cube (`Box`-topology Qubic, the schema's own discrete-boards worked example) —
lives in its own file under `src/Puck.World/Assets/worlds/games/`, imported by
`puck.world.json` in that order, rather than all six sharing one ever-growing
document. `WorldDefinition.Imports` is the fan-in half of composition beside
`basis`'s single-parent chain: an ordered list of `{"document": …, "as": …}`
entries, each fully resolved (its own `basis`/`imports` included), composed
under its alias when it carries one, and folded left to right, then layered
under the importing file's own body. The reasoning this decision rests
on: a single-parent basis chain cannot express "six independent slices of one
document" without artificial ordering between unrelated games; imports can,
because siblings are checked for collision rather than silently overridden —
a same-key row, object member, or list two games both declare refuses by name
unless `puck.world.json` itself restates the key, so an accidental collision
between two games' content is caught at load rather than silently resolved by
import order. `key` joined the row-identity vocabulary (`id`/`name`/`key`/
`index`) alongside this, so a state row's `cells` — the vocabulary a game's own
counters and tables lean on — refines by cell rather than replacing wholesale
under a basis delta. The shared substrate — channels, kits, bodies capacity,
the tabletop placement and its plan seam, spawn points, the island, the
population, hud/views, and everything no game's own content touches — stays in
`puck.world.json` itself; `chessBoard` (the `state.lattices` topology the
tabletop anchors) moved with chess, `pondBasin` stayed. `billiardsColors`
(ball/tray AND pin/pinBand cells) is genuinely shared between billiards and
bowling and stays substrate rather than forcing an arbitrary owner. `world.imports`
reads the resolved stack back.

The import fold keeps each game's new rows together and preserves their authored
order; the importing file's new rows follow the imports. `GardenSplitLawTests`
checks each current fragment's rules, state, patterns, and topology against the
composed garden. Behavioral laws check the games independently of their authored
representation; a frozen copy of the original monolith would prevent those
programs from adopting newer state primitives.

Chess addresses pieces by placement ID (`piece0` through `piece31`) through
`pieceCell`, `pieceCode`, and `placement:$each`. Every piece and board square
inherits the `tabletop` frame. Import order may change body indices without
changing those identities or the board's local coordinates. The placement and
physical-move laws verify both contracts against the live server.

Board programs should share scans within an event and use topology operations
for geometry. Chess uses `boardShift` for pawn, king, and knight attack masks,
and `$board:attacks` for sliders that stop at the first occupied square. Qubic
uses three successive intersections and shifts to find four marks along each
of its 13 undirected line directions. Both keep their expensive expressions in
gated effects: rule-local bindings evaluate before the gate, including idle
ticks. Changes to scratch rows and rule order change replay hashes; replay
verification must prove consistency under the new document rather than preserve
a historical hash.

**The operand/effect unions, the row-domain union, and the garden split land together
(`tower/unions`, integrator ruling).** Three lanes built independently against the same
`ca29ca5e` base — compiled operands and effects becoming closed unions, `WorldStateRow`'s five
facets collapsing into one `Domain` union, and the garden splitting into imported game
fragments — then a fourth (the `$pair` composite key indirection, `moveToken`'s retirement into
a live `$board:pathCost` target plus a transaction) landed inside the operand/effect lane after
the split lane had already forked. Combining all four moves the passive 300-tick garden replay
hash to `0xE65582BEA0A09549` (from `0x397968B8F541A2C4` at `ca29ca5e`) and the frozen world's
720-tick replay to `0xFD0790057330914F` (from `0x1B21350FE4B50E0B`): every one of the four
changes is independently a pure representation or a deliberate, already-recorded content change,
and their sum is not separately re-provable against the pre-integration number — the relevant
guarantee is that the replay stays self-consistent (rule-failure-free, MATCH) at the new mapping,
never that combining independently-correct changes leaves a historical hash standing. Two
integration-only fixes rode along: `games/chess.world.json` and `games/tictactoe.world.json`
authored their `state.lattices` entries against the pre-union `WorldStateLatticeTopology` shape
(a `kind` discriminator field) since the split fork predates the lattice-topology union landing
in the other lane — migrated to the union's own `$type` discriminator, the one shape the type
now parses; and the two lanes' independently-declared `UnionPolyfill.cs` (one `[AttributeUsage(Struct)]`
for the operand/effect carriers, one `[AttributeUsage(Class)]` for the row-domain union) collapsed
into the one file `docs/game/design.md`'s row-domain paragraph already named as the shared destination,
attributed for both shapes.

**Rule bindings, static tables, independent effects, and two derived limits.**
A rule may declare `bindings`, values computed once per evaluation before the
gate and read as `$bind:<name>`; they exist because a value like
`min(damage, hp)` cannot be recomputed after the first effect writes `hp`, so
this is expressive power, not shorthand — named, feed-forward, never stored.
Static lookup data is not simulation state: a `tables` row references a
hash-pinned `puck.table.v1` document read through `$table:`, the same
name/source/hash shape music rows use, so a registry of hundreds of entries
never touches the cell budget, the checkpoint, or the tick hash. Every
top-level effect is its own boundary and the `transaction` effect is the one
atomic group; the implicit contiguous-run atomicity was a second, invisible
mechanism for the same thing, and it was what silently rolled back the chess
classifier's sibling writes. The rule-count ceiling is deleted — the per-tick
work budget is the bound — and a row's cell bound is the one cell bound every
domain shares (4096), with an unauthored capacity getting 128 of room; a
registry-sized row authors its capacity. The work sheet also stops charging
mutually exclusive rules together: rules whose gates pin literal cells to
disjoint ranges are priced as a trie of the cells they pin — the costliest
values at each cell, one more value per rule that can write the cell in a tick
since effects apply immediately — which tightens the bound without loosening
the guarantee; the same interval pass refuses a gate that can never hold. What
the document order decides silently is a read-back, not a scheduler:
`world.rule.hazards` names each earlier read of a later write and each pair of
same-tick writes with a set among them. Refused: an SMT solver behind the
budget (interval intersection over literal cells is the whole of what the
sheet can honestly claim; an invariant like "these flags are exclusive" is the
author's, not the compiler's), and reordering rules for the author. Refused on
the same review: a fixed
C# "recipe executor" (game nouns in the engine), a `copyCells` transform
(derivable from `forEach`), and a per-effect `isolate` flag (a second
atomicity mechanism where making the boundary explicit was the fix).

**Infix expressions, the rule trace, and the budget breakdown.** An expression
is authored as an infix string as well as a token list: the string is syntax
over the same postfix tokens — one parser, one printer, no second evaluator, no
new cost — so the authoring surface stops being the reason a rule is hard to
read without the engine gaining a language; the shipped games author every
expression in it, and the canonical writer stops escaping `+ < > &` so the
file reads as the author wrote it. Debugging a rule is a read-back,
not a debugger: `world.rule.trace` captures a rule's next evaluations with
every binding value, every gate conjunct's compared values and verdict, and
every effect's computed value and outcome, as an observer that leaves the
state hash alone; replay reaches the tick, the trace explains it. The work
budget stays worst-case — that is what makes the tick a bound — but it is no
longer opaque: an over-budget refusal names the costliest lines with their
multipliers, and `world.budget.rules` lists every line, so the fix is a
capacity on the row that is actually multiplying, never a guess. Refused on
the same review: loosening the budget to an average-case estimate (a bound
that can be exceeded is not a bound), and a stepping debugger (there is no
call stack; a rule's evaluation is one line of facts).

**`Puck.State`: the state and rule engine as a standalone deterministic library.**
A package with no world, body, rendering, or presentation concept, on the
`Puck.Commands`/`Puck.Physics` precedent, which `Puck.World.Schema` consumes and
extends, so a card game, a turn-based resolver, or another engine's frontend
can run authoritative rules over `Puck.Maths` and `Puck.State` alone. The
library owns the state vocabulary (rows, cells, domains, draws, topologies,
patterns, tables), the rule record with its state-neutral predicate, effect,
and transaction-step arms, the compiler over a `RuleCompileContext`, the work
budget, the dataflow and hazard analyses, and the read side of every operand:
a fact reads itself through an `IRuleReader` the host implements, a virtual
call where a closed-union switch stood, and prices itself so the work sheet
stays derived. The world extends it through one `RuleVocabulary` of registered
families — an operand family owns its reserved spellings and compiles them, an
effect or predicate family owns its JSON discriminator and compiles its arm, a
key family answers a dynamic-key spelling — consulted before the library's own,
and the world's arms join the JSON union at serializer resolution through a
type-info modifier rather than an attribute list the library would have to
know. Nothing in the library carries a `World` name. The two enums the physics
kits share with rules (`ActionStateComparison`, `ActionTriggerMode`) live in
`Puck.State`, which references nothing of `Puck.Physics`. The evaluator is the
library's, behind one state-host interface `WorldServer` implements: a
`StateMutation` through the host's own mutation door, a preflight scope the
host opens and closes around a transaction's candidate, the effect arms only
the host can fire, and the rule kinds only the host can evaluate — which run
each evaluation back through the library's gate-and-fire, so the edge latch,
the trace, and the refusal ledger are one mechanism whether a rule, a decision,
or an interaction fired. The latch is simulation state the library hashes and
checkpoints. A domain fault is never silent: a binding, an effect, or a
`compareValue` conjunct whose expression overflows or leaves a function's
domain is a counted `Arithmetic` refusal the trace shows as `refused`, so a
rule that stopped firing is looked up in `world.rule.failures`, not guessed
at. Interactions and decisions stay host-evaluated until a pair domain over
rows is designed rather than assumed; composing a mutation against a row
(eviction, rebase, envelope) stays each host's door. Refused: a compatibility
shim between old and new spellings at any phase.

**The exotic Maths functions are expression calls, one family per prefix.**
An author reaches `Puck.Maths` from an expression by the name of the thing —
`pair(x, y)`/`pairX`/`pairY` and the Szudzik algebra, `morton`/`mortonX`/
`mortonY`, `hilbert(order, x, y)`/`hilbertX`/`hilbertY`, the hex family over
`HexagonalIndex` (`hex(q, r)`, `hexQ`, `hexR`, `hexRadius`, `hexEuclideanSquared`,
`hexDistance`, `hexNeighbor`, `hexRotate`, `hexMirror`, `hexSwap`,
`hexAdd`, `hexSubtract`, `hexMultiply`, `hexScale`, `hexTranslate`), the square
family over `SquareIndex` on the same spellings (Chebyshev as `squareRadius`/
`squareChebyshev`, Manhattan as `squareLength`/`squareDistance`), `gcd`/`lcm`,
the floored `mod` with `cycleForward`/`cycleDistance` over an m-cycle, `smallestMissing`
over a 64-bit option set, `isPrime` and the bounded `prime(i)` table (a
next-prime or n-th-prime search is unbounded on the tick path and stays a
generator draw source's concern), the layer
family over `LayerSequence` (`layer`, `layerOffset`, `layerStart`, `layerSize`
over `(index-or-layer, start, step, seed)`), and `sqrt`, `sin`, `cos` — never
by a matrix or an ISA spelling. The inverse of every encoding is a sibling
call (`pairX(pair(x, y))` is `x`), a domain fault fails the expression the way
an overflow does, and the compile-time kind proof admits each family only in
the kind it means (`sin`/`cos` fixed, `sqrt` both, the rest int).
`ModularTransform` stays a C# concern: a quasicrystal inflation would arrive as
a generator draw source, not as an author-facing matrix. The combinatorial and
factorial number systems over [Puck.Maths `Combinatorics`](../../src/Puck.Maths/README.md#combination-and-permutation-ranks)
are calls over one cell each: a subset is a bitmask (`subsetRank`/`subsetAt`/
`subsetMember`, colex, n ≤ 64 — a poker hand's identity is one rank below
`choose(52, 5)`) and a permutation is nibble-packed (`arrangementRank`/`arrangementAt`/
`arrangementMember`, Lehmer codes, n ≤ 16 — a turn order or a short deck in one
cell), with `choose` and `factorial` beside them; a permutation longer than
sixteen is row-shaped and waits on a reduction operand.

**Hex boards have grid parity, on one convention.** A hex topology is
`HexagonalIndex` made spatial: cell `i` is index `i` (rings outward from the
origin, consecutive indices adjacent), its six directions are
`HexagonalCoordinate.Direction(0..5)` — counterclockwise from +q in the
Eisenstein basis, which on the board reads `E, SE, SW, W, NW, NE` — and cell
`(q, r)` sits at origin + cellSize · (q − r/2, 0, r·√3/2), so +q is +X and +r
leans toward +Z. Position-to-cell rounds to the nearest lattice point, axial
offsets step in `(dq, dr)`, cell centres come from the topology itself, and a
placement's `board` facet anchors a hex topology exactly as it anchors a grid.
The hex line game's table (`games/hexlines.world.json`) is the first board on
that convention; its tiles are placed by the same formula the engine answers
with, and the law that proves them recomputes both sides from
`HexagonalIndex` rather than from a second copy of the positions.

**A board is a graph; the regular kinds are generators onto it.** The compiled
topology was always a flat adjacency table with centres — every board query
indexes it without knowing the shape — so the `graph` topology kind authors
that table directly: cells with ids and centres, directions each naming their
opposite, edges filling one slot per (cell, direction) and the reverse slot
unless one-way. A territory map, a star board, or a tiling a tool emits is a
document, not a new runtime; the constraints are the table's own — at most
4,096 cells, at most 64 directions (a `$match:` direction mask is one word),
nearest-centre resolution within half a cell size, no axial offset, and the
identity as its whole symmetry group. The `tiling` kind is such a generator:
the triangular, kagome, truncated-square, rhombitrihexagonal,
truncated-hexagonal, elongated-triangular, and truncated-trihexagonal uniform
tilings from their unit cells, and the Penrose P3 rhombs by Robinson-triangle
inflation from a sun, each cut to a radius in edge lengths and compiled through
the graph path with edge normals as its directions — boot-time geometry in
doubles whose vertex merge quantizes to a fine grid, so the graph is the same
on every machine. The two snub tilings wait on a vertex-configuration grower.

**A rule can ask what a board would be; the `search` section is that
question.** The one gap a rule cannot close alone is the hypothetical:
checkmate, stalemate, a legal-square plan, and a CPU opponent all need a
board that is not the document's. The shipped chess world fixes the shape:
it has no move interactions — bodies move, and the rules snapshot the
settled pieces into `board`, diff it against the accepted `lastLegal`,
construct and exactly match `move`, judge it into `verdict`, and flip
`turn`. A game is a *judge*, and a ply is a candidate state the judge
accepts and that changes the turn key; an interaction-shaped world fits the
same definition with its gates as the judge. What runs: a value-typed
*frame* (`Puck.State.StateFrame`) lays every integer cell of the section out
once from the rows, so a copy is one span copy and a judge's fifty scratch
scalars cost nothing, and a second `IRuleHost` (`FrameHost`) runs the
document's own rules over it unchanged, so no second rule language exists
for the bot. Which rules a frame runs derives from the dataflow: a rule
reading a world operand (a body's cell, its uprightness, quiescence) is
host-only and skipped (`RuleDataflow.ReadsHost`); the candidate is written
into the token row those rules would have written, and every other rule
judges it. Candidates are authored shapes walked in a fixed order ahead of
token and target: `relocate` (evicting or not what stood there), `drop` (a
token off the board into an empty cell), `jump` (two cells along a direction
over an occupied intermediate that leaves the board), and `pair` (two tokens
by the same lattice translation on any grid, ring, hex, or box — the castle's
minimal primitive), and `promote` (a relocation whose code changes to one of
an authored list). A job over piles names ordered `zones` instead of a board
and searches `transfer`: only an end token moves, and the frame carries the
piles themselves, so pile order is searched as the zones hold it. Outputs land as ordinary
rows through the mutation door: `legal` masks for boards to 64 cells, `reach` for the token a
`held` slot names on any board, per-token `counts`, and — with an
authored `score` — `best`, from an iterative-deepening negamax with
alpha-beta on an explicit frame stack that a tick boundary suspends anywhere
and the checkpoint carries. The search is a *job* across ticks, never a
query that must answer in its tick: its node quota derives from what the
work sheet leaves of the tick budget divided by the judge's own cost, it
restarts when any framed cell other than its outputs changes, and a bot body
then issues the same command a human would through the player command path,
so a dog may knock the table while it thinks. Checkmate is the job at depth
one with no accepted candidate and the check row set. Legality is written
once, as the judge; *enforcement* is the author's choice per table (the
board binding's `enforcement`): `record` lets the players fix the board, the
diegetic default; `return` poses the piece back onto its `from` cell on the
verdict's refuse edge. Tokens on a cell is the frame seen from the other
side: a `cellsOf` row declaring `inverse: {tokens, codes}` is derived from
its token row at every compose and install and refused a direct write, and
a frame recomputes only a moved token's two cells; a count is already a
value. A job whose `method` is `tree` searches the same score by UCB1 instead: a
bounded node pool, playouts drawn from a SplitMix64 stream the job's stamp
seeds, the score folded back with alternating sign; a negamax job keeps a
transposition table keyed by the frame hash. The shipped chess
world derives `board` through `inverse` and shifts with `boardShift`; its
laws seed positions as tokens. The judge-per-node cost is the strength ceiling —
depth one in ticks, depth two or three over seconds — and a plane-native
make/unmake for strength beyond that is a later decision. Refused: a mate
detector unrolled into per-piece rules, a privileged bot mutation, a search
that blocks a tick, a frame that materializes row objects.

**Placements compose, and a game addresses its bodies by placement.** A
placement may name a `parent`: its position and yaw become a local offset and
heading in the parent's resolved frame (rotation and translation only, resolved
once at compile into `PlacementFrames`, never per tick), and a Grid topology
named by a placement's `board` facet takes its origin from that placement's
frame, so a board's squares and the pieces on it are authored in the board's
own coordinates and the whole table can move. A rule names the body inhabiting
a placement as `placement:<id>`, or `placement:$each` over a forEach row whose
keys are placement ids, resolved to a body index through an ordinal table —
never a string on the tick path — and a `$cell:` indirection's inner key may
itself be `$each`. Chess is re-authored as a self-contained module on those
primitives: pieces keyed by placement id, one forEach rule where thirty-two
were; dominoes, billiards, and bowling anchor to marker placements of their
own. The boot settle is the document's, not a test's: every tabletop rule
gates on a held-quiescence counter and a one-time snapshot seeds the accepted
board before the candidate matcher reads it. The
garden's passive replay hash moves with the content; the frozen world's does
not.
