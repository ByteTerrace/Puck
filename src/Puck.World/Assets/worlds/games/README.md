# Solitaire collection

Nexus imports [solitaire.world.json](solitaire.world.json), which composes the
three patience games bundled with Windows XP. They run as state rows, patterns,
and rules; the console is their playing surface.

| Table | Module | Options |
|---|---|---|
| 1 | [Klondike](klondike.world.json) | Draw one (`option=1`) or three (`option=3`); unlimited stock passes |
| 2 | [Spider](spider.world.json) | One, two, or four suits (`option=1`, `2`, or `4`) |
| 3 | [FreeCell](freecell.world.json) | Four free cells and eight columns (`option=1`) |

## Start and inspect a game

Set `solitaire.table` to choose the active table. Zero pauses the collection;
switching tables preserves each deal and pauses its rules, including a deal in
progress. Set the chosen game's `option` before requesting a new deal:

```text
world.state.cell.set solitaire table 1
world.state.cell.set solitaireKlondike option 3
world.state.cell.set solitaireKlondike action 1
world.state.cell.set solitaireKlondike request 1 add
world.wait 240
world.state solitaireKlondike
world.state solitaireKlondikePile0
world.state solitaireKlondikePile2
```

Use `solitaireSpider` or `solitaireFreecell` in place of `solitaireKlondike` for those tables. Deals use the
engine's deterministic shuffle stream, whose cursor is saved and replayed with
the world. A new deal gathers every card before shuffling. The stream does not
reproduce Microsoft's numbered FreeCell deals. Changing `option` during play
only affects the next deal; `activeOption` reports the current choice.

Pile cells appear in bottom-to-top order. The last card is exposed; stock draws
consume the first card. Card keys identify physical cards: 0–51 in Klondike and
FreeCell, 0–103 in Spider. Here `<game>` means `solitaireKlondike`, `solitaireSpider`, or
`solitaireFreecell`. Read `<game>Rank`, `<game>Suit`, and `<game>Face` for
rank (ace=1 through king=13), suit, and face-up status (1 or 0). Suits 0–3 are
spades, hearts, clubs, and diamonds; even suits are black. Spider's suit count
changes the suit mapping while retaining distinct identities for every card.

| Pile ID | Klondike | Spider | FreeCell |
|---|---|---|---|
| 0 | Stock | Stock | Deal staging pile; empty during play |
| 1 | Waste | — | — |
| 2–8 | Seven tableau columns | First seven tableau columns | First seven tableau columns |
| 9 | — | Eighth column | Eighth column |
| 10–11 | Spade/heart foundations | Ninth/tenth columns | Spade/heart foundations |
| 12–13 | Club/diamond foundations | Completed runs in 12; no pile 13 | Club/diamond foundations |
| 14–17 | — | — | Four free cells |

## Move or draw

The control row has three actions: **1** starts a new deal, **2** moves the
selected card and its legal suffix, and **3** draws (or recycles Klondike's
waste when its stock is empty). FreeCell refuses action 3.

For a move, write `from`, `to`, and `card`, then increment `request` **last**.
For example, this requests a move beginning with card 4 from column 2 to 3;
its legality depends on the current layout:

```text
world.state.cell.set solitaireKlondike action 2
world.state.cell.set solitaireKlondike from 2
world.state.cell.set solitaireKlondike to 3
world.state.cell.set solitaireKlondike card 4
world.state.cell.set solitaireKlondike request 1 add
world.wait 240
world.state solitaireKlondike result
world.state solitaireKlondikePile2
world.state solitaireKlondikePile3
```

To draw, set `action` to 3 and increment `request`. The `from`, `to`, and `card`
fields are unused for drawing and new deals. Wait for `applied=request`,
`stage=0`, and `busy=0` before another request. The example's 240-tick fence
covers a complete deal and cleanup at the shipped simulation rate. `result=1`
means accepted, `-1` means illegal, and `0` means a new deal is being prepared.
An illegal request is acknowledged without moving a card. `status` is 0 before
or during a deal, 1 while playing, and 2 after a win.

## Rules

Klondike deals columns of one through seven cards and exposes each top card.
Build downward in alternating colours; only a king can begin an empty column.
Move any face-up, correctly ordered suffix. Only the waste's top card can move.
Foundations build ace through king in their own suits, one card at a time;
their top cards may return to the tableau. Draw-three exposes a short final
batch when fewer than three cards remain. Recycling turns the waste face down
without changing the next pass's draw order. Covering cards turn face up
automatically when exposed. All 52 cards on the foundations win.

Spider deals 54 cards across ten columns and leaves 50 in stock. A column may
build downward across suits, but a moved multi-card suffix must descend in one
suit. Any card or valid suffix can begin an empty column. Drawing requires all
ten columns to be occupied and deals one card to each. A face-up king-to-ace
run in one suit moves automatically to the completed pile; this can reveal
another run. Completing all eight runs wins.

FreeCell deals every card face up, with seven cards in the first four columns
and six in the other four. Build downward in alternating colours; any card can
begin an empty column. Each free cell holds one card. Foundations build upward
by suit and cannot return cards to play. A shortcut moving a suffix is admitted
only when it could be performed using single-card moves: capacity is
`(empty free cells + 1) × 2^(spare empty columns)`. An empty destination column
is excluded from those spare columns. All 52 cards on the foundations win.

`moves` counts accepted player actions, including draws and stock recycling.
The score counters are simple untimed tallies: Spider starts at 500, charges
one per move/deal, and awards 100 per completed run; Klondike awards 10 for a
foundation move and 5 for waste-to-tableau, and charges 15 for a foundation
return. They do not reproduce XP's timed bonuses, Vegas accounting, or statistics.
This collection implements the card rules through Puck's console; it does not
include XP's mouse interface, card artwork, hint solver, or dedicated undo UI.

## Authoring and verification

The umbrella owns the active-table selector. Import it when embedding these
modules in another world. Piles are ordered `keysOf` zones over stable card
identities. Patterns judge a selected suffix and the destination's top card;
accepted moves transfer the suffix in one transaction with their counters and
request acknowledgement. Endpoint keys use the generic
[`$zone:` spelling](../../../../Puck.State/README.md), so a top-card lookup
needs no per-card rule loop, and every rule that judges or moves a pile
selects it live through its own `zones` table (`$zones[<index>]`): `move`,
`source`, and `target` index by the request's `from`/`to` (pile id = table
index, a gap where the pile is not a legal end), `gather` and Spider's
`complete` by their step counter in table order, `deal` by the column a step
lands in, and `reveal` sweeps the table with `forEach: "$zones"`. A pile family
that judges by a different pattern (the waste, the foundations, the cells) is
its own rule over its own table, and a request naming a pile outside a rule's
table closes that rule's gate. Deals,
recycling, and Spider cleanup use bounded phases to share Nexus's existing
per-tick budget. A table switch suspends those
phases and resumes them on return.

The executable rules are exercised by
[SolitaireLawTests](../../../../../tests/Puck.World.Tests/SolitaireLawTests.cs).
The shared dynamic keys, transaction rollback, live frame reads, and pattern
accounting are exercised by
[ZoneEndKeyLawTests](../../../../../tests/Puck.State.Tests/ZoneEndKeyLawTests.cs).
For console changes, also run the real `Puck.World` executable and record a
`replay.record` / `replay.stop` / `replay.verify` trajectory.

Historical context: [Microsoft Solitaire](https://en.wikipedia.org/wiki/Microsoft_Solitaire)
and [Microsoft Spider Solitaire](https://en.wikipedia.org/wiki/Microsoft_Spider_Solitaire)
identify the bundled games; the
[FreeCell FAQ](https://www.solitairelaboratory.com/fcfaq.html) distinguishes legal
single-card play from limitations in older automatic multi-card shortcuts.

## Backgammon — the chance-node probe

[backgammon.world.json](backgammon.world.json) is a standalone, self-contained
document (its own `documentId`, headless `host.presentation: "none"`) rather
than an importable module: the market's probe for the `search` section's
`chance` node, run on its own rather than composed into the island. A `points`
ring topology of 24 cells carries four checkers (`checkerPoint`, keyed `w0`/`w1`
for white, `b0`/`b1` for black; off the ring at `-1` means on the bar), a
two-cell `dice` row draws `uniformRange 1..6` on each cell (`world.generate
dice` rerolls it), and `cube` is a plain, inert row standing in for the
doubling cube. One `ai` search job supplies both seats: `relocate`
(`displace: false` — checkers share a point, no hitting) and `drop` (bar entry)
are its shapes, `depth: 2` with a `chance` node at ply 1 averages the position
over the 36 dice pairs the search's own next roll might be, and `score` reads
each side's own pip progress. Four per-checker judge rules (`backgammon-legal-*`) compute legality against a
shadow `origPoint` row: an ordinary rule, `backgammon-sync-orig`, keeps it
mirrored to `checkerPoint` on every real tick (a hypothetical candidate's own
scratch copy never persists past its own judge pass, so `origPoint` still
reads the real pre-move position inside one). The classic backgammon check —
no other move while a checker of the same side sits on the bar — reads as
`origPoint[<other checker>] != -1` in each of the four expressions.

Two deliberate reductions, stated plainly rather than left to be discovered:
hitting is not modelled (`displace: false`, no blot/point-ownership check), and
bearing checkers off the board is not a distinct removal step — a side's
checkers reaching their own endpoint (23 for white, 0 for black) stands in for
it. Both are honest simplifications of a probe proving the chance-node
primitive, not the licensed rules of backgammon. Setting a starting position
must author it into the document rather than poke a live `checkerPoint`/`dice`
cell post-boot: those cells are exactly what the four judge rules above watch,
so an external write crosses the same rules the search's own hypothetical walk
does and flips `turn` for real before anything else runs.

[SearchChanceLawTests](../../../../../tests/Puck.State.Tests/SearchChanceLawTests.cs)
proves the chance mechanism itself — an expectiminimax value over a two-outcome
chance equalling the hand-computed average, and a `Method: Tree` job's playout
drawing from the job's own stream so two independently built runtimes with the
same seed make the same choice.
[BackgammonLawTests](../../../../../tests/Puck.World.Tests/BackgammonLawTests.cs)
proves the document: the bar-occupied refusal (with its control — a checker's
own bar entry stays legal) and the same position with a clear bar.
