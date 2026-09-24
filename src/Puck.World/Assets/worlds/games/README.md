# Solitaire collection

Nexus imports [solitaire.puck](solitaire.puck), which composes the
three patience games bundled with Windows XP. They run as state rows, patterns,
and rules; the console is their playing surface.

Every game in this directory is authored as `.puck` source, and the directory
holds sources only. The game's build compiles each one into its own output
(`build/WorldAssets.targets`), where
[WorldDocumentOutputLawTests](../../../../../tests/Puck.Cli.Tests/WorldDocumentOutputLawTests.cs)
holds every shipped document to exactly what its source compiles to. A document
names a game by its document name (`games/poker`), never a file.

| Table | Module | Options |
|---|---|---|
| 1 | [Klondike](klondike.puck) | Draw one (`option=1`) or three (`option=3`); unlimited stock passes |
| 2 | [Spider](spider.puck) | One, two, or four suits (`option=1`, `2`, or `4`) |
| 3 | [FreeCell](freecell.puck) | Four free cells and eight columns (`option=1`) |

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
| 1 | Waste |—|—|
| 2–8 | Seven tableau columns | First seven tableau columns | First seven tableau columns |
| 9 |—| Eighth column | Eighth column |
| 10–11 | Spade/heart foundations | Ninth/tenth columns | Spade/heart foundations |
| 12–13 | Club/diamond foundations | Completed runs in 12; no pile 13 | Club/diamond foundations |
| 14–17 |—|—| Four free cells |

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
[`$zone:` spelling](../../../../../docs/reference/state/rules.md#resolve-a-cell-key), so a top-card lookup
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
The shared dynamic keys and a firing's rollback are exercised by
[PointerKeyLawTests](../../../../../tests/Puck.State.Rules.Tests/PointerKeyLawTests.cs)
and
[AtomicFiringLawTests](../../../../../tests/Puck.State.Rules.Tests/AtomicFiringLawTests.cs).
For console changes, also run the real `Puck.World` executable and record a
`replay.record` / `replay.stop` / `replay.verify` trajectory.

## Billiards: the cue gesture

[billiards.puck](billiards.puck)'s cue ball takes a strike through the shared `attack`
channel (declared by the arena district; a physical control means "attack" or "strike" depending
which table a seat stands at, never a second input surface). Holding it charges the keyed Fixed
`cue` row's cell `0` monotonically toward its authored ceiling (`cue-charge`, a level rule gated on
`$channel:1:attack`); releasing fires `cue-release`, an edge rule whose `applyRigidImpulse` effect
strikes `placement:cueBall`'s live inhabitant along seat body `0`'s own forward facing, scaled by
the charged cell, then resets the cell to zero. `applyRigidImpulse` is the world-rule effect family's
one honest path to a rigid impulse—it calls the same `WorldBody.TryApplyRigidImpulse` `body.impulse`
does, never a second impulse mechanism—addressed by the same `body:<n>`/`argmax:<row>`/
`placement:<id>` body-reference grammar a spatial rule channel reads, so a struck body and its heading
body are two independently live-resolved references. A struck body carrying no `rigid` kit facet
refuses by name (`world.rule.hazards`/`world.rule.trace` read the refusal back); every rack ball
shares the cue ball's own `billiardBall` kit, so only the placement `applyRigidImpulse` actually
names—never an arbitrary nearby ball—ever takes the strike.

Background: [Microsoft Solitaire](https://en.wikipedia.org/wiki/Microsoft_Solitaire)
and [Microsoft Spider Solitaire](https://en.wikipedia.org/wiki/Microsoft_Spider_Solitaire)
identify the bundled games; the
[FreeCell FAQ](https://www.solitairelaboratory.com/fcfaq.html) distinguishes legal
single-card play from limitations in older automatic multi-card shortcuts.

## Backgammon—the chance-node probe

[backgammon.puck](backgammon.puck) is a standalone, self-contained
document (its own `documentId`, headless `host.presentation: "none"`) rather
than an importable module: the market's probe for the `search` section's
`chance` node, run on its own rather than composed into the island. A `points`
ring topology of 24 cells carries four checkers (`checkerPoint`, keyed `w0`/`w1`
for white, `b0`/`b1` for black; off the ring at `-1` means on the bar), a
two-cell `dice` row draws `uniformRange 1..6` on each cell (`world.generate
dice` rerolls it), and `cube` is a plain, inert row standing in for the
doubling cube. One `ai` search job supplies both seats: `relocate`
(`displace: false`—checkers share a point, no hitting) and `drop` (bar entry)
are its shapes, `depth: 2` with a `chance` node at ply 1 averages the position
over the 36 dice pairs the search's own next roll might be, and `score` reads
each side's own pip progress. Four per-checker judge rules (`backgammon-legal-*`) compute legality against a
shadow `origPoint` row: an ordinary rule, `backgammon-sync-orig`, keeps it
mirrored to `checkerPoint` on every real tick (a hypothetical candidate's own
scratch copy never persists past its own judge pass, so `origPoint` still
reads the real pre-move position inside one). The classic backgammon check—
no other move while a checker of the same side sits on the bar—reads as
`origPoint[<other checker>] != -1` in each of the four expressions.

Two deliberate reductions, stated plainly rather than left to be discovered:
hitting is not modelled (`displace: false`, no blot/point-ownership check), and
bearing checkers off the board is not a distinct removal step—a side's
checkers reaching their own endpoint (23 for white, 0 for black) stands in for
it. Both are honest simplifications of a probe proving the chance-node
primitive, not the licensed rules of backgammon. Setting a starting position
must author it into the document rather than poke a live `checkerPoint`/`dice`
cell post-boot: those cells are exactly what the four judge rules above watch,
so an external write crosses the same rules the search's own hypothetical walk
does and flips `turn` for real before anything else runs.

[ArenaSearchChanceLawTests](../../../../../tests/Puck.State.Search.Tests/ArenaSearchChanceLawTests.cs)
proves the chance mechanism itself—an expectiminimax value over a two-outcome
chance equalling the hand-computed average, and a `Method: Tree` job's playout
drawing from the job's own stream so two independently built runtimes with the
same seed make the same choice.
[BackgammonLawTests](../../../../../tests/Puck.World.Tests/BackgammonLawTests.cs)
proves the document: the bar-occupied refusal (with its control—a checker's
own bar entry stays legal) and the same position with a clear bar.
## Chinese Checkers—physical marbles and jump chains

[chinese-checkers.puck](../../../../../worlds/parlor/chinese-checkers.puck), now
part of the [Parlor package](../../../../../worlds/parlor/README.md), boots on its own with a wooden
board, 121 recessed holes, and ten physical marbles per player. The default
three players occupy alternating camps. Change the source's `playerCamps` to
`[0, 3]` for two players, `[0, 1, 3, 4]` for four, or `[0, 1, 2, 3, 4, 5]` for
six, then recompile. The graph, placements, ownership and opposite goals all
come from the same compile-time collections. It is not imported into Nexus.

A turn moves exactly one marble to an empty adjacent hole, or through a chain
of jumps over adjacent marbles of either color. Every jump lands in the empty
hole immediately beyond its blocker; chains can turn, stop early, and have no
fixed hop limit. Jumps do not capture. A step cannot be combined with jumps.
The first player to occupy all ten holes of the opposite camp wins, and play
then stops. This version allows movement through and into any camp, including
leaving a goal camp; target-locking, long-jump and anti-blocking house rules
are not enabled.

After one second of physical rest, the rules compare every marble with its
last accepted position. Wrong-seat moves, missing marbles, collisions and
multiple moves are rejected without changing the turn or legal snapshot.
The board records an illegal arrangement and leaves it visible for correction;
restoring the position consumes no turn. `turn`, `verdict`, `move`, `home`,
`winner`, `moveCount` and `illegalCount` expose the result.

The optional opponent is disabled initially. Enable seat 1 (the second player)
with `world.state.cell.set aiSide $value 1`; use `-1` to disable it. Its bounded
one-ply max-n search scores each player's goal occupancy and remaining distance.
It judges candidates with the same rules as physical play, moves the chosen
body once, and waits for it to settle. Position revisions prevent a completed
answer from being applied to a different board. This is a simple positional
opponent, without deeper tactical search.

The generic `$board:jumpDistance` query visits each reachable cell once rather
than enumerating every possible hop sequence. The AI considers at most
`pieceCount * 121` endpoints per search, under the engine's per-tick work budget.
Run `puck test worlds/parlor/chinese-checkers.puck` from the repository root
for physical settling and AI scenarios through the real executable. The CLI
suite runs these package scenarios automatically.
[ChineseCheckersLawTests](../../../../../tests/Puck.World.Tests/ChineseCheckersLawTests.cs)
loads the package source directly and compares its judge with an independent
endpoint oracle, including physical correction, AI turns and stale answers.

## Reversi—rays, brackets, and an inverse board

[reversi.puck](reversi.puck) is 8x8 Reversi under the
[World Othello Federation's official rules](https://www.worldothello.org/about/about-othello/othello-rules/official-rules/english),
a standalone, self-bootable document (its own `documentId`, headless
`host.presentation: "None"`) like backgammon. `reversiBoard`
is a `cellsOf` row over an 8x8 `Grid` whose eight default compass directions are
the eight rays a move brackets along: 0 empty, 1 black, 2 white, cell ordinal
`row * 8 + file`, black to move first from the four opening discs on 27, 28, 35
and 36.

Play is one door: write `reversiMoveCell`, then increment `reversiMoveRequest`.

```text
world.state.cell.set reversiMoveCell $value 19
world.state.cell.set reversiMoveRequest $value 1 add
world.wait 30
world.state reversiBoard
world.state reversiRay
```

`reversiMoveApplied` catching up to `reversiMoveRequest` is the acknowledgement;
`reversiRejected` counts the requests the rules refused and `reversiFlips` counts
the discs the last accepted move turned. `reversiRay` holds that move's eight
bracket probes, each the step distance to the first cell an unbroken run of the
opponent's discs does not cover.

Three mechanisms carry the game. **Legality** is one `$match` read: the patterns
`reversiBlackLine`/`reversiWhiteLine` accept a whole ray that opens with a run of
the opponent's discs and closes on one of the mover's, and the `any` direction's
`count` facet answers how many of the eight rays accept — one move door
conjunct rather than eight. **Flips** are `setRay`, which rewrites the longest run
its pattern accepts walking outward from the played cell (a dynamic `from` key):
exactly a bracket. A ray that brackets nothing accepts no prefix, and `setRay`
refuses an empty prefix; because one refused effect rewinds the whole firing,
each of a colour's eight writes sits in an `if` on that direction's own `prefix`
facet of the same bracket pattern. A move's flips are one firing per colour
(`reversi-flip-black`, `reversi-flip-white`) that always commits. **Mobility**
is the `reversiMobility` INVERSE board: the `reversiCandidate` tokens hold each
legal cell's own ordinal (and -1 elsewhere), `reversiCellIndex` supplies the codes, and the board they derive is the side to
move's legal-move map, counted into `reversiMobilityCount` through
`$board:mask`. A side whose mobility is zero passes; two consecutive passes end
the game, as does a full board, and the larger disc count wins.

Two things this document does not do. There is no `search` job, and so no CPU
opponent: none has been authored. A job's judge cost is the sum of every
ordinary rule's work units with none of the mutual-exclusion folding the tick
budget applies; with the flips as two rules that sum no longer carries a per-cell
table, but no job has been planned against it. And the board carries no
placements or pieces — it is a state-and-rules document, played through the
console.

The source's `test` blocks prove a one-direction flip for each colour and a
two-direction flip: `puck test src/Puck.World/Assets/worlds/games/reversi.puck`.

[ShippedWorldStateBaselines](../../../../../tests/Puck.World.Tests/ShippedWorldStateBaselines/README.md)
holds the recorded trajectory: five accepted moves, one refused, ending on a
two-direction flip whose `reversiRay` shows `W` and `SW` at 2.

## Go—liberties, ko, and a tree search on a full board

[go.puck](go.puck) is Go on the full 19x19 board, a standalone headless
document like Reversi. `board` is a `cellsOf` row over the `goban` grid, whose
four authored directions (N, E, S, W) are the only adjacency a liberty or a
group follows: 0 empty, 1 black, 2 white, point ordinal `row * 19 + column`.
Black moves first.

A move is one write: move the stone token to the point.

```text
world.state.cell.set stone play 180
world.wait 2
world.state board
world.state captured
world.state refusal
```

`placed` catching up to `stone[play]` is the acknowledgement. A request the
rules refuse sends the token back to `placed`, counts in `rejected`, and names
its reason in `refusal`: 1 an occupied point, 2 a suicide, 3 a ko. A pass is
`world.state.cell.set passRequest $value 1 add`; two in a row end the game.

The rules are a handful of board queries and transforms:

- **Capture.** A stone placed where it leaves an opposing group with no liberty
  takes that group. `$board:enclosedAt` counts the stones a placement would
  take before it lands, and the `clearEnclosed` transform sweeps them after.
- **Suicide.** A stone that takes nothing must keep a liberty:
  `$board:boundaryAt` counts the empty points its joined group would border.
- **Ko.** Every move and pass pushes the board's exact `$board:fingerprint` onto
  the `positions` history ring. Only a move taking exactly one stone can remake
  the position the mover left a full turn ago, so that move is first tried on
  the `probe` board and refused when its fingerprint is the one the ring holds
  a turn back. The canonical fingerprint would not do: it folds a board's
  mirror images together.
- **Report.** After each move, `group` and `liberties` read the placed group
  through `$board:component` and `$board:boundary`, and `atari` says whether an
  opposing group beside it has one liberty left.
- **Scoring.** Area scoring: stones plus the empty points that reach only one
  colour. Once the game is over, a `stabilize` group floods each side's reach
  through empty points with `boardCombine` shifts, one step a pass, until a pass
  reaches nothing new. `writeSet` then paints the declared sets `blackArea` and
  `whiteArea` onto `owner`, `territory` counts them, and `winner` compares the
  areas with 6.5 points of komi to white.

Set `aiSide` to 1 or 2 and the CPU plays that colour. The `goAI` search job is
a UCT tree over the stone token: `drop` places the first stone and `relocate`
every later one, and the judge is the move doors themselves. The root walk
judges every point once; each of the tree's 256 iterations grows at most one
node and plays three plies out. On the empty board one CPU move judges about
2,350 candidates at a judge price of about 90,000 work units against a
per-tick allowance of about 3.7 million, and lands after about 90 ticks, three
seconds at 30 Hz. Its move enters through the same door as a player's.

The source's `test` blocks cover a lone stone's liberties, an atari, a single
and a group capture, a suicide refusal, a ko refusal and the exchange that
lifts it, the area count, and one CPU move: `puck test
src/Puck.World/Assets/worlds/games/go.puck --reproduce`.
[GoMoveLawTests](../../../../../tests/Puck.World.Tests/GoMoveLawTests.cs) pins a
move's tick, the flood's passes and the CPU's work by the rule and search
counters, and the
[`go-capture`](../../../../../tests/Puck.World.Canaries/go-capture/canary.json)
canary plays into a capture and the CPU's reply through the real `Puck.World`.

Three limits stand. The CPU's score is material alone (stones on the board),
so it plays legal moves but not good ones; a territory estimate would make it
stronger at a higher judge price. Ko is simple ko, one turn back, not
superko. Scoring counts every stone as alive: dead stones are not removed.

## Tetromino—a staged piece life, a bag, and a full-row pattern

[tetromino.puck](tetromino.puck) is the falling-block game played with the
seven tetrominoes, I, O, T, S, Z, J and L, in a well ten columns wide and twenty
rows tall, with two hidden rows above where pieces appear. It is a standalone
document with one seat and no room: the seat's body floats, and only its
channels matter.

| Control | Keyboard | Gamepad |
|---|---|---|
| Move left or right | arrow keys | d-pad left or right |
| Soft drop, a row a tick while held | down arrow | d-pad down |
| Hard drop | space | d-pad up |
| Turn clockwise or counter-clockwise | X or Z | east or south face button |
| Hold | C | west face button |
| Restart after a game over | Enter | Start |

`body.press <channel> 1 0.1` presses one from the console, and
`world.state.cell.set bag $value <kind>` sets the next piece (0 to 6 in the
order I, O, T, S, Z, J, L).

`well` holds one line mask a row, row 0 at the top: bit `c + 3` is column `c`,
and the walls are always set, so an empty playing row reads 57351 and a full one
65535. Rows 0, 1 and 24 are the ceiling and the floor. A piece is `kind`, `turn`
and the top-left corner of its four-by-four box, `px` and `py`; its shape is four
row masks read from the static `shape` row.

How each rule of the game is written:

- **A piece's life** is the `piece` workflow: `spawn`, then `fall`, which waits
  until the piece rests and its lock deadline has passed, then `lock`, which
  writes it into the well, then `clear`. The cursor evaluates one step a tick,
  so a piece is never spawned while another falls, and `stage` mirrors the
  cursor for the rules outside the workflow.
- **Gravity** is a `cycle` row with one cell a level, each reading zero once
  every period of that level; the `gravity` rule moves the piece down on the
  zero. Level 1 drops a row a second, level 10 fifteen a second.
- **Lock delay** is a deadline written with `schedule lockAt in 0.5s` whenever
  the piece appears, falls, or moves or turns (fifteen resets until it reaches a
  lower row). A hard drop sets it to the current tick.
- **Row clears** read the `fullRow` pattern over the well's own word:
  occurrence `k` is the `k`-th full row from the top, and the clear step shifts
  the rows above each one down, top first. One to four rows score 100, 300, 500
  or 800 times the level, and the level rises every ten rows.
- **The bag** is a draw site over a weighted source of the seven pieces drawn
  without replacement until all seven are out, then restarted, so each run of
  seven pieces holds each once. The site's own value is the next piece.
- **Turns** try the five wall-kick offsets the Super Rotation System publishes
  for the piece family, starting rotation and direction, from the static
  `kickX` and `kickY` rows, and take the first that fits.
- **Score** saturates at 999999: `score` is declared with
  `bounds(0..999999, overflow: Saturate)`.
- **Game over** is a piece with no room to appear, or one that locks wholly in
  the hidden rows.

The source's `test` blocks lock three pieces and clear one row, clear four rows
with an upright I, deal a bag of seven, lock a resting piece when its deadline
passes, saturate the score, end the game on a blocked spawn, kick a turn off the
wall, hold once per piece, and soft-drop: `puck test
src/Puck.World/Assets/worlds/games/tetromino.puck --reproduce`.
[TetrominoTickLawTests](../../../../../tests/Puck.World.Tests/TetrominoTickLawTests.cs)
pins a piece's life by the rule counters: a quiet tick fires nothing, a hard
drop to the next piece is five firings over four ticks, and the worst-case tick
is priced at about 109,000 of the 4,000,000 work units a tick admits. The
[`tetromino-clear`](../../../../../tests/Puck.World.Canaries/tetromino-clear/canary.json)
canary plays three pieces into a clear through the real `Puck.World`.

Limits: the well is simulated but not drawn, so it is read through the console.
A held move does not repeat, there is one next piece and no longer preview, there
is no T-spin or back-to-back bonus, and a turn cannot kick a piece above the top
of the well.

## Hidden ranks—concealed armies, and what each side has learned

[hiddenranks.puck](hiddenranks.puck) is a module fragment, not a bootable world: the
board, the two armies and the rules, over a 10×10 `grid` topology
(`hiddenRanksField`, cell ordinal = row × 10 + column counting from red's back
rank). `minimal-hiddenranks-host.world.json` under
[`tests/Puck.World.Tests/Fixtures`](../../../../../tests/Puck.World.Tests/Fixtures)
supplies the two local seats and the `attack` channel the module's rules read.

The rule set: ranks 1 Marshal through 10 Spy with a lower
number winning, 11 Bomb and 12 Flag which never move, one 40-piece army a side,
two 2×2 lakes, the Spy beating a Marshal it attacks, the Miner defusing a Bomb,
equal ranks removing each other, and the Flag ending the game.

`hiddenRanksOwner` is the one public board—0 empty, 1 red, 2 blue, 3 lake. Authoring
the lakes as owner 3 is what makes a ray over that board stop at water exactly as
it stops at a piece, so the Scout's reach is one `$match:…:distance` read rather
than a second terrain row. Each side's ranks live on its own board
(`hiddenRanksRedRank`, `hiddenRanksBlueRank`) whose `visibility` admits that side's
seat alone, and whose `readersFrom` names `hiddenRanksReveal`, the keyed text row a
flag capture writes both seats into: the armies are revealed at the end by
widening the live audience, not by copying the boards.

What a side has learned is a `knowledge` board (`hiddenRanksRedKnows`,
`hiddenRanksBlueKnows`) over the same topology, each naming the opposing rank board
as its source and a Bool mask (`hiddenRanksRedSeen`, `hiddenRanksBlueSeen`) as the
squares it currently sees. A strike raises the one square each side learns about,
fires `observe` on that side's knowledge board—which folds the source rank in
with its own observation tick—and lowers the mask again in the same rule, so a
later strike elsewhere cannot overwrite the memory. A remembered piece keeps its
value and reads `visible: false` on the next refresh. Knowledge is keyed by the
stable piece identity and resolves visibility through `hiddenRanksPosition`, so a
revealed piece carries its remembered rank when it moves.

Captured pieces are removed with `removeStateCell` rather than zeroed, so a
side's rank board's cell count is its surviving piece count.

Moves arrive through `hiddenRanksFrom`, `hiddenRanksTo` and an incremented
`hiddenRanksRequest`. `hiddenranks-judge` states the legality of the pending request
once—ownership, a moveable rank, a target that is neither own nor lake, an
orthogonal line, and the Scout's own reach—and the six rules after it read that
verdict. `hiddenRanksResult` is 1 for an accepted move and -1 for a refused one;
`hiddenRanksApplied` catches up to `hiddenRanksRequest` either way, and a refused move
does not end the turn. `hiddenRanksOwner` carries `phaseOf: "hiddenRanksTurnGuard"`:
an external gameplay transform writing the board must present that row's
generation. Setup ends when both seats hold their own `attack` control, which is
what `hiddenRanksReady` records.

Deliberate reductions, stated rather than left to be discovered: the two-squares
and more-squares repetition restrictions are not modelled; a Scout moves any
distance along an empty line but may only attack an adjacent square, which is the
original rule rather than the later variant; losing by having no legal move is not
detected, only the flag capture ends the game; and both armies' opening setups are
authored in the document rather than chosen by the players, so the setup phase is
a readiness handshake rather than a placement interface.

`hiddenranks.state.json` under
[`ShippedWorldStateBaselines`](../../../../../tests/Puck.World.Tests/ShippedWorldStateBaselines)
records the export after the committed sequence: twelve requests, nine of them
accepted, three of those nine strikes—one won, one lost, one mutual—and three
refused outright, a Bomb asked to move, a move into a lake, and a move onto one's
own piece.
## Hearts—physical cards, passing and AI

[hearts.puck](../../../../../worlds/parlor/hearts.puck), in the
[Parlor package](../../../../../worlds/parlor/README.md), boots a felt table with 52 movable, rigid cards and
four thirteen-card hands. It deals automatically. Seats two through four are
AI opponents by default; seat one is human. This is an open table for local
play and spectating, without private network hands. The AI reads only its own
hand and public trick information, regardless of what the camera shows.

The rules follow [American Hearts](https://www.pagat.com/reverse/hearts.html).
The two of clubs opens; players follow suit when possible; hearts cannot be
led before being broken unless only hearts remain. Highest in the led suit
wins. Hearts score one each, the queen of spades thirteen. The chosen variants
forbid first-trick points unless only penalty cards remain, give every opponent
26 for shooting the moon, and share a match win among tied lowest scores.
The match ends when any score reaches 100 at hand end.

Passing cycles left, right, across and hold. All four players select before
any pass arrives. Move one card into its owner's gold pass tray, or into that
seat's central trick space during play. After half a second of rest, the
observer checks the whole layout and submits the card to the rules. An illegal
move preserves the legal hand and turn; return the card or correct its target.
Accepted actions arrange the cards into their hand, pass, trick and archive
spaces. Finished tricks go to the archive at the far end of the table.

The console also accepts actions. Write `seat` and `card`, then increment
`request` last. `result` is 1 accepted or -1 refused:

```text
world.state.cell.set heartsAct seat 1
world.state.cell.set heartsAct card 0
world.state.cell.set heartsAct request 1 add
world.wait 8
world.state heartsAct result
```

This example requires seat one to hold card zero and the stage to allow it.
Card id is `suit * 13 + rank - 2`, with clubs, diamonds, hearts and spades
numbered 0–3 and ranks 2–14. Placements are `card0` through `card51`; their
faces label rank and suit. `heartsExpected` maps cards to `heartsPlaces` slots:
hands 0–51, pass trays 52–63, trick spaces 64–67, archive 68–119.

`heartsTable` reports stage (1 passing, 3 playing, 4 finished hand), turn,
trick and scoring. At hand end, set its `deal` cell to 1 to gather and shuffle
again, keeping match scores. The deterministic deal stream continues.
`matchOver` latches the result; `winnerMask` has bits 0–3 for seats 1–4.

`heartsOptions[aiMask]` is 0 for four humans, 14 for the default opponents,
and 15 for a full AI table. `pace` controls quiet ticks between decisions;
`autoDeal = 1` continues through hands until match end. The AI scans its own
hand once per action, preferring safe discards, low winning risk, and dangerous
cards to pass. It is a deterministic heuristic, without hidden-hand inspection
or game-tree search. It moves a physical card and waits for normal admission.

Compile-time collections generate cards, slots and repeated seat rules;
`derive` shares legality and scoped `rules` share stage gates. Piles retain
sorting, canonical arrangement, trick history and phase tags.
Run `puck test worlds/parlor/hearts.puck` from the repository root for real-host
dealing, physical AI play and scoring scenarios. The CLI suite runs these
package scenarios automatically.
[HeartsLawTests](../../../../../tests/Puck.World.Tests/HeartsLawTests.cs) loads
the package source directly and checks every card against an independent
legality oracle across sampled hands, five-hand conservation, private AI
decisions, physical correction, and completion of a full AI hand.


## Lineup—hidden busts, yes-or-no questions and a splitting computer

[lineup.puck](../../../../../worlds/parlor/lineup.puck), in the
[Parlor package](../../../../../worlds/parlor/README.md#lineup), stands two
galleries of twenty-four porcelain busts on a table, one per seat. Each seat
secretly holds one bust; on its turn it asks a yes-or-no question about the
other seat's, or names a bust and ends the game. Seat 2 is the computer by
default.

A bust is one attribute mask: bits 0–3 hair colour (black, brown, red, silver),
4–6 eye colour (brown, blue, green), then glasses, hat, beard, earrings, long
hair and a large nose. Question `q` asks about bit `q`, and `lineupYes[q]` is its
answer set, the twenty-four-bit set of busts carrying that bit. An ask keeps
`lineupYes[q]` on a yes and its complement on a no, ANDed into the asker's
`lineupStanding` set; the answer comes from the other seat's `lineupSecret`,
which the asker never supplies.

The console acts through the same door as the keys and the computer. Write
`seat`, `kind` (1 ask, 2 name) and `value` (question 0–12 or bust 0–23), then
increment `request` last. `result` is 1 accepted or -1 refused:

```text
world.state.cell.set lineupAct seat 1
world.state.cell.set lineupAct kind 1
world.state.cell.set lineupAct value 9
world.state.cell.set lineupAct request 1 add
world.wait 4
world.state lineupTable
```

`lineupTable` reports stage (1 playing, 2 over), turn, the last question and
answer, and the winner. `lineupOptions[cpuMask]` picks the computer's seats
(bit 0 seat 1, bit 1 seat 2) and `pace` the ticks it waits before acting. The
computer walks the thirteen answer sets once per turn and asks the one whose
split of its standing set is closest to half, the lowest on a tie, and names the
bust once one remains.

`lineupSecret1`, `lineupStanding1` and seat 1's gallery rows admit `seat1`
alone, and seat 2's rows `seat2`: a seat's console read of the other seat's is
refused by name. The galleries are dealt from the gallery rows, one deal per
part (face, hair, eyes, glasses, hat, beard, earrings, nose), so every bust is
composed from shared part prototypes and a ruled-out bust lies down in grey.

Run `puck test worlds/parlor/lineup.puck` for the real-host answers, eliminations,
wins and losses, the disclosure refusals, the keys and the computer.
[LineupLawTests](../../../../../tests/Puck.World.Tests/LineupLawTests.cs) holds the
roster to one hair and one eye colour a bust and no two busts alike, every answer
to a brute-force filter of the roster, and the computer to naming every possible
bust within six turns. The
[`lineup`](../../../../../tests/Puck.World.Canaries/lineup/canary.json) canary
plays to a win through the real `Puck.World` and proves a wrong name loses, and
[lineup.sequence.json](../../../../../tests/Puck.World.Tests/ShippedWorldStateBaselines/lineup.sequence.json)
pins the export after a three-question game.
## Snake—a ring body on a tick-indexed beat

[snake.puck](snake.puck) is a module, not a bootable document: it declares the
state and rules and leaves the seat, the input channels and the population to a
host. [minimal-snake-host.world.json](../../../../../tests/Puck.World.Tests/Fixtures/minimal-snake-host.world.json)
is the smallest host that completes it—one local seat on the shared `walk` kit,
plus the `forward`/`strafe` bipolar channels the four turn presses arrive on.

The rules are the arcade set [Blockade (1976) fixed and the 1997 Nokia handheld
made universal](https://en.wikipedia.org/wiki/Snake_(video_game_genre)): one
snake crawls a cell per beat, a turn that reverses straight into the neck is
refused, eating the food grows the snake and places another, and running into
the snake's own body ends the run. Two reductions are deliberate: the 8x8 field
wraps rather than carrying walls, and the cell the tail is about to vacate
counts as solid.

The body is a `ring`-domain row (`snakeTrail`) that each step pushes the new
head cell onto; `snakeBoard` is the occupancy the gates read, and the departing
tail is cleared through the trail age `snakeLength - 1`. The snake is capped at
six segments because the move rule reads four fixed trail ages and clears the
tail through one branch per length. `history(row, age)` accepts an expression
age, so the cap is no longer forced; lifting it is an
[open item](../../../../../docs/plans/open-items.md). `snakeHeadingLog` is a
second ring, four deep, written by a `push` statement with the literal heading
each accepted turn took; `snakeIllegalTurns` counts the reversals the
same four rules refused. `snakeMeals` is a two-deep `evicts` table keyed by the
cell each meal was eaten on, so a third meal drops the first.

The beat is two `cycle` cells of `snakeTempo` over the same thirty-step lattice
rotation: `slow` turns one step a tick and reads zero once every thirty ticks,
`fast` turns two and reads zero once every fifteen. `snakePace` accumulates one
a second through `advance` from the last spawn, and the step rule reads `fast`
instead of `slow` once it passes eight—the beat's period halves as a run goes
on. A death rebases it and schedules `snakeRespawn` one second out; the spawn
rule re-seeds the field once `$tick` reaches it.

Turns reach the game two ways, both landing on the same
`snakeTurnRequest`/`snakeTurnSerial` pair: a `body.press` on the seat's
`strafe`/`forward` channels, or an ordered console submission—

```text
world.state.cell.set snakeTurnRequest $value 1
world.state.cell.set snakeTurnSerial $value 1 add
world.wait 30
world.state snakeHead
world.state snakeHeadingLog
```

—where the request is the heading (0 east, 1 south, 2 west, 3 north) and the
serial is what makes it a new submission. The recorded trajectory is
[snake.sequence.json](../../../../../tests/Puck.World.Tests/ShippedWorldStateBaselines/snake.sequence.json):
it joins the seat, is refused one reversal, eats four times (the third and fourth
each evicting a meal), dies on its own body, waits out the deadline, plays a
second run, and dies again — ending before the second deadline passes, so
`snakeRespawn` carries an armed deadline tick rather than the boot-time zero.
## Paddleball—a physical court and three interaction latches

[paddleball.puck](paddleball.puck) is a module, not a bootable document: it declares a court,
three bodies that play on it, and the rules that judge them, and leaves the seat,
the `attack` channel and the population to a host.
[minimal-paddleball-host.world.json](../../../../../tests/Puck.World.Tests/Fixtures/minimal-paddleball-host.world.json)
is the smallest host that completes it—one local seat, the `walk` program, the
three looks and a population of six.

The rules: two paddles face each other across a court, the side that was last scored upon serves, a paddle that meets the ball
returns it, a ball that passes a paddle and reaches the goal behind it scores one
point for the other side, and the first side to eleven wins.

Where the other games in this directory judge a board, this one judges geometry.
`paddleballBall` is a `rigid` kit on the court's solid floor; a serve is an
`applyRigidImpulse` down the court, the same door `body.impulse` opens. The three
questions the rules ask about that geometry are the `interactions` table's three
rows, one per latch shape it carries:

| Interaction | Shape | Asks |
|---|---|---|
| `paddleball-paddle-strike` | Distance × Edge | did a paddle just meet the ball? (one firing per approach) |
| `paddleball-paddle-pressure` | Distance × Level | is the ball still inside a paddle's reach? (one firing per tick) |
| `paddleball-goal-west` / `paddleball-goal-east` | Region × Edge | has the ball arrived in a goal? |

An interaction row carries no gate, so a goal is an event rather than a point:
`paddleball-point-west`/`paddleball-point-east` turn one into a point only while the ball is
in play, and `paddleball-dead-ball-*` count the arrivals that were not. The serve
door is one pair—write `paddleballServeSerial` or press the seat's `attack` control—and
`paddleball-serve-refuse` turns a serve away while the ball is live, while the
between-points deadline has not yet passed, or after the game has been won.
`paddleballRallyClock` accumulates a second a second through `advance` from the serve
that started the rally; `paddleballSpin` is a `Fixed` row whose `dynamics` trait makes
every read of it an eased follower chasing the stored value a strike writes; and
`paddleball-let` reads `$physics:quiescent`—the ball is the only rigid body on the
court—to call a rally whose ball has stopped short of either goal.

Two facts the document works inside, both engine behavior rather than style:

- **A property row's keys are body indices, and an inhabited placement claims the
  highest free slot first.** With the host's population of six, the ball is body
  5, the paddles 4 and 3 and the two serve posts 2 and 1. The keys and
  `bodies.capacity` are one fact.
- **A placed body's facing is reset by the placing.** A baseline sequence's
  `pose` step writes a zero yaw, so a paddle that has been moved can no longer
  supply a heading. The serve therefore aims along a pair of fixed posts standing
  in the court's far corners, which nothing ever moves.

[paddleball.sequence.json](../../../../../tests/Puck.World.Tests/ShippedWorldStateBaselines/paddleball.sequence.json)
plays a whole game through those doors: a rally the physics plays by itself (the
ball is served, returned by the east paddle and turned again by the west one,
`paddleballLongestRally` 2) ending in a let, a dead ball, three refused serves, and
twenty-one points, each one a real serve down an open lane into the goal behind
the paddle that stepped out of it. Since an unopposed serve always scores and the
conceding side serves, the points alternate and the recorded game ends 11-10 with
`paddleballWinner` reading 1.

## Word spy—the word grid, the hidden key, and a spymaster that aims

[wordspy.puck](wordspy.puck) is a module fragment, not a bootable world: a
5x5 word grid, a key card the two spymasters alone may read, and the rules for
touching cards.
[minimal-wordspy-host.world.json](../../../../../tests/Puck.World.Tests/Fixtures/minimal-wordspy-host.world.json)
is the smallest host that completes it—four local seats and nothing else, since
this module reads no channel and owns no placement. The seats are the four
roles: 1 red spymaster, 2 red guesser, 3 blue spymaster, 4 blue guesser.

The rules: twenty-five word cards; a key card giving nine to the starting team,
eight to the other, seven neutral and one trap; a one-word clue and a number;
the number plus one guesses; an own word lets the team guess again, anything
else ends the turn, the trap loses the game outright, and the first team to find
all its own words wins. The board words are Puck's own list.

A cell is its own key: grid ordinal `"0"`..`"24"`, row-major. The `sql { }`
block authors the public half of the board in one statement—`wordSpyBoard`
decomposes into `wordSpyBoardShown` (0 face down, else the identity the touch
turned up) and `wordSpyBoardLive` (the keyed Bool mask `nearest` and `mean`
filter candidates on)—plus the six scalar `DECLARE`s the game keeps score in.
`wordSpyWords` is a `Text` table declared `embeds(wordSpyWordVectors)`, so
one declaration mints the word at each ordinal and its vector under the same
key; `wordSpyLexicon` does the same for twelve clue words that are **not** on
the board, which is what makes a clue a clue.

The key card, the four masks derived from it (`wordSpyRedLive`,
`wordSpyBlueLive`, `wordSpyRedAvoid`, `wordSpyBlueAvoid`), and everything the
spymaster computes from them declare `visibility` admitting `seat1` and `seat3`
alone: the poles `wordSpyAim`, the clue `wordSpyClue`, its log
`wordSpyClueLog`, the chosen word `wordSpyClueChoice`, and the shortlist
`wordSpyHint`. A transform may not write a row a wider audience reads than the
rows it reads ([What a transform may write](../../../../../docs/reference/state/transforms.md#what-a-transform-may-write)),
so a mean over a hidden mask lands in a hidden row. Everything else is public.

The spymaster is five rules, one stage a tick, because every vector transform
takes the cross-row path and lands at the tick boundary rather than in the rule
frame: the masks refresh, two `mean`s reduce the team's own live words and the
cards it must steer away from (the other team's plus the trap) to two
poles, `mix` aims three parts toward the first and one part away from the second,
then one `nearest` over the lexicon picks the clue word into the hidden `Text`
slot `wordSpyClueChoice`, one `nearest` over the board words—filtered by
`wordSpyBoardLive`—is the private shortlist, and `remember` files the aim under
the clue's own number unless a clue already on file is within 0.9 cosine of it.
The clue **number** is `$reduce:count:wordSpyHint:where:<team>Live`: how many
of the three cards the clue pulled are the clueing team's own live words, which
is exactly what a clue's number states. The rule that publishes the number also
copies the chosen word into the public `wordSpyClueWord`, the one hidden value
the game shows every seat.

A player names a seat and a cell in `wordSpyAct` and increments `request`;
`result` answers `1` accepted or `-1` refused:

```text
world.state.cell.set wordSpyAct seat 2
world.state.cell.set wordSpyAct word 0
world.state.cell.set wordSpyAct request 1 add
world.wait 12
world.state wordSpyBoardShown
world.state wordSpyClueWord
```

Two authoring constraints this document works inside, both engine behavior
rather than style:

- **A `Bool` cell refuses a numeric expression as its source, and an expression
  refuses to carry a `Bool` cell into an `Int` one.** A `mean`/`nearest`
  `where` filter must be a keyed `Bool` row, so `wordspy-live-refresh`
  classifies each cell with an `if`/`else if`/`else` chain writing literals
  rather than with four boolean expressions, and `wordspy-resolve` reads the
  reveal state through a branch.
- **`nearest` into a keyed `Int` table replaces that row's whole cell set**
  with the matched keys and their integer dot products. `wordSpyHint` is
  therefore the shortlist itself, not a row anything else writes.

Four deliberate reductions, stated rather than left to be discovered. The
embedding model is `puck-fixture`, the offline SHA-256 generator `puck embed`
locks into [wordspy.embeddings.json](wordspy.embeddings.json): its vectors
are deterministic but carry no real semantics, so the clue word a game picks is
reproducible rather than apt. Both spymasters are the same rule pair, so there
is no human spymaster interface. A clue's word and number are published but the
guess is the guesser's own choice through the door above, not taken from the
shortlist. And the game plays one board rather than a match.

[wordspy.sequence.json](../../../../../tests/Puck.World.Tests/ShippedWorldStateBaselines/wordspy.sequence.json)
drives a real game through that door: four seats join, three clues are given,
four guesses land—an own word, a neutral card that ends red's turn, blue's own
word, and the trap, which ends the game with `wordSpyWinner` at 2—and
five requests are refused, a spymaster guessing instead of the guesser, a card
already face up, a guess before the clue lands, a guess on the other team's
turn, and a guess after the game is over. `wordSpyClueLog` holds two of the
three clues: the third was within 0.9 cosine of one already on file and
`remember` declined it.

## Arena deathmatch—pickups, respawns, and a contended meter

[arena.puck](arena.puck) is a module, not a bootable document: two fighters,
two health pickups and the rules that score them, left to a host for its seats,
its `attack` channel and its population.
[minimal-arena-host.world.json](../../../../../tests/Puck.World.Tests/Fixtures/minimal-arena-host.world.json)
is the smallest one that completes it—two local seats on one floating kit, the
`attack` channel the fighters shoot on, a four-body population (seats 0 and 1,
the two items) and the `arenaItem` look the item placements wear.

The rule set is the free-for-all deathmatch id Software's Quake III Arena ships
as its default game type
([Quake III Arena](https://en.wikipedia.org/wiki/Quake_III_Arena),
[Deathmatch](https://en.wikipedia.org/wiki/Deathmatch_(video_games))): a kill
scores its killer a frag and its victim a death, a killed fighter waits out a
respawn deadline and fires nothing until it passes, health regenerates between
hits, health pickups restore more of it at once and return on their own item
respawn deadline, a participant may enter a match already under way, and the
first fighter to the frag limit wins.

Four mechanisms carry it. **Regeneration** is the `advance` trait on the
`ArenaFighter.health` pool field: the field's stored value is the last explicit
write's base and its ceiling clamps every read, so the exported pool snapshot
carries the base and clock the live health resolves from.
**Respawn and item cooldowns** are `schedule`, which writes the tick a wait
ends rather than a remaining duration, so a rule reads it back by comparing
`$tick` against the cell.
**Pickups** are the `interactions` section's one `Distance` row: the
`arenaFighters` pool against `arenaPickups`, both resolved through enum-valued
carrier fields in the `properties` registry, firing once per crossing inside an
8-unit radius with the fighter bound `$left` and the item `$right`. A dead
fighter's carrier becomes `Detached` and a consumed item's does too, so the same
table that grants the heal enforces who may take one and how often.
**Contention** is `arenaMomentum`, a Fixed cell carrying a
second-order `dynamics` follower that both fire rules write in opposite
directions: two presses landing on the same tick both write it, document order
decides the target the cell carries out of the tick, and the cell's clock keeps
the follower's position and velocity at that write—which is what a contended
tick looks like in the export.

Entering the match is its own submission rather than a gesture on the attack
control:

```text
world.state.cell.set arenaEnter $value 1
world.wait 30
world.state arenaRoster
world.state arenaFighters
```

`arena-join` sits after the four shot rules in document order on purpose. A
rule's writes land on the tick's own frame and every rule after it reads
through that frame, so an `arena-join` placed first would hand `arena-fire-2` a
fighter that had joined and was alive in the same tick.

Carrier bindings map the fighter and pickup enums to seats and named
placements. The logical pool values therefore remain stable if population body
indices or placement reconciliation order changes.

Deliberate reductions, stated rather than left to be discovered: a shot always
hits, since there is no aim, no weapon inventory and no projectile—a fighter's
body is the marker the pickup radius is measured against, not a shooter; the
two fighters are a fixed pair rather than an open lobby; armour, powerups and
self-damage are not modelled; the frag limit ends the match rather than a time
limit; and health regenerates on the trait's own clock even while a fighter is
waiting to respawn, which never matters because the respawn write rebases it to
full.

[arena.sequence.json](../../../../../tests/Puck.World.Tests/ShippedWorldStateBaselines/arena.sequence.json)
drives a whole match through those doors: seat 1 joins, seat 2 takes its seat
and then enters the roster, the two trade fire on the same tick twice, each
kills the other at least once, each is refused a shot it tries to take while
dead, the medkit is carried onto the plaza while one fighter is out and taken
by the other, and the match ends 2–1 on the frag limit. The mega health is
never taken: it stands 40 units out for the whole match, which is the control
on the distance the interaction measures.

This collection is part of [Puck.World](../../../README.md).

## Documentation

📚 [Worlds and federation](../../../../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../../../../docs/development/contributing.md)
