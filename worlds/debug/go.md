# Debug world Go table

The gallery's board room holds a 19×19 Go table at (56.5, 9.905, 30.6), north
of [chess](chess.md). The 3.50 m by 1.84 m table keeps its original height;
the flat 1.60 m board has 0.08 m pitch, 19 lines each way and nine star points.
Its face and painted markings belong to one creation. The spot at (65, 30.6)
remains free. [go.puck](go.puck) authors the table, imported by debug.puck.

All 361 stones are rigid bodies: 181 black and 180 white. Each colour shares
one lens mesh and palette. Contact uses that same ellipsoid, with radii
0.036/0.010/0.036 m, through the generic convex support solver. The lens's
origin is its underside. Single-layer 10×19 trays sit at X offsets ±1.28 m,
derived from one 42×19 lattice at 0.08 m pitch. Empty tray slots stay empty;
there are no hidden replacement stones.

The logical board is `goBoard`. Its move door checks an empty point, takes
opposing groups without liberties, then refuses suicide. Simple ko compares
the resulting board exactly, colour by colour, with the predecessor board;
passes update that predecessor too. Two consecutive passes end the game.
Area is stones plus empty points reached by only one colour, with 7.5 komi
to White. Play out disputed dead groups before passing; shared regions are
neutral. Filling the board or using a tray's last stone does not end play.

The physical table is read after its own stones rest for nine ticks (0.3 s).
Other tables do not delay it, and a carried stone always prevents settlement.
`goCell` records stone locations and `goSeen` their colours at points.
`goDiff` and `goGap` mark changed and missing points; ordinal max reductions
select a point without ray scans. A single new stone of the side to move is
a move request. Other arrangements increment `goIllegal` and are reconciled.
Stacked stones are reported as collisions and require a hand to separate them.

Returns and resets use an ascending deterministic queue, at most eight stones
per tick. `goReseat` exposes that busy state; seated play and the opponent wait
for it. A reset also returns disturbed tray stones. Missing accepted stones
are filled from the lowest-numbered available stone, one point per reading.
`goExhausted` reports a put with no stone left in that colour's tray.

## Playing

`debug.go go` stands the Moth at Black's edge. Within 2.5 m, E (pad RB) sits
her down and frames the tabletop; the same button stands her up.

| Device | Cursor | Play | Pass |
|---|---|---|---|
| Mouse | Point | Click | Right-click |
| Keyboard | W A S D or arrows | Space or Enter | Escape or Backspace |
| Pad | Left stick or d-pad | South (A) | East (B) |

The blue outline marks the cursor. Up is away from the seated side and right
is the player's right. A play drops a stone 6 cm above its point and waits for
physics. Seat 1 plays every side the computer does not own.

Point ordinal = 19 × row + column, along +Z and +X respectively. Point 0 is
(55.78, 29.88), centre 180 is (56.5, 30.6), and point 360 is (57.22, 31.32).
Black stones are `goStone0` through `goStone180`; White's are `goStone181`
through `goStone360`. The cursor is body 471 and stone n is body 470 − n.
Off-board stone n reads `-1 - n`. Seat 1 may pose every stone; for example,
`body.pose 56.58 10 30.6 0 0 0 470` drops the first black stone at point 181.

| Action | Console |
|---|---|
| Play a point | `world.state.cell.set goPut $value <point>` |
| Pass | `world.state.cell.set goPassRequest $value 1 add` |
| Reset | `go.reset` |
| Computer side (2 White, 1 Black, 0 none) | `go.opponent <side>` |
| Position and physical board | `world.state goBoard`, `world.state goSeen` |
| Turn, moves and refusals | `world.state goTurn`, `world.state goMoves`, `world.state goRefusal` |
| Last group, liberties and atari | `world.state goGroup`, `world.state goLiberties`, `world.state goAtari` |
| Scoring phase, passes and reach | `world.state goScoring`, `world.state goScorePasses`, `world.state goReached` |
| Empty area, stones and winner | `world.state goTerritory`, `world.state goStoneCount`, `world.state goWinner` |
| Return queue and supply | `world.state goReseat`, `world.state goReturn`, `world.state goExhausted` |
| Opponent progress | `world.search` |

Refusal 1 means occupied, 2 suicide and 3 ko. Scoring phase 1 is flooding,
one pass per tick, with a board-derived ceiling of 363 passes; phase 2 is done.

## Opponent and scenarios

The beginner opponent plays White by default. Its one-ply Negamax job judges
at most eight candidates per scheduled step, bounded by the shared work
allowance. Captures dominate; self-atari and eye filling are penalized,
endangered enemy groups add pressure, and third/fourth-line positions get
small opening weights. It passes when no positive-scoring move remains.
Search jobs take turns when other tables are thinking. Their admission prices
their own dependency rows and reserves every job's input-stamp upkeep.

[go-tests.puck](go-tests.puck) covers all-stone settlement, seated play,
captures before suicide, group captures, ko and its release, illegal table
arrangements, the far corner, reset, passes and area scoring, including a
serpentine flood longer than 128 ticks. Run
`puck test worlds/debug/go-tests.puck --reproduce --jobs 1`.
