# Debug world Go table

The debug world stands a Go table in the gallery hall's board game room, the
east end of level 2 ([`shapes.md`](shapes.md#the-board-game-room)), north of the
chess table ([`chess.md`](chess.md)). The board is 9x9, its centre at
(56.5, 9.905, 30.6) on a table 3.5 m by 1.7 m whose top is 0.8 m over the floor.
Black sits on the table's -Z side and White on its +Z side, and each has a tray
of 41 stones at the end of the table on their right: Black's at -X, White's at
+X. [`go.puck`](go.puck) holds it and `debug.puck` imports it whole
(`import "go"`).

The game is two layers. The rules of Go are a move door over the logical board
`goBoard`: a request moves the token `goMove[play]` to a point, and the door
plays it when the point is empty and the stone either captures or keeps a
liberty, refusing a suicide and a move that remakes the position a full turn
back (simple ko). A capture is `clearEnclosed`, the placed stone's own liberty
flood. Two passes in a row end the game, scored by area (stones plus the empty
points that reach only one colour), with 7 points of komi to White.

Over the rules lies the table. Each stone is a rigid body, and whenever the
physics has rested for 0.3 s (`goSettleTicks`), the rules read where every
stone stands (`goCell`) and the board the table shows (`goSeen`, each stone's
colour on its point). Stones of a colour are alike, so the reading compares
boards, not stones: a table that differs from the accepted position by one
stone of the side to move on an empty point is a request through the door;
any other change is counted illegal (`goIllegal`). Either way the table is
then put back to the position: a refused or stray stone goes back to its tray,
as the stones a move captures do, and a point the position holds that the
table lacks is filled from its colour's tray, one point a reading. A stone
stacked on another is counted illegal and left for a hand to move.

## Playing at the table

Walk within 2.5 m of the board and press the context button (E, or the pad's
RB): the Moth sits down and the camera eases to the board from her side.
`debug.go go` stands her at Black's edge, facing the board and close enough to
sit. The controls switch to the board page chess declares while she sits.

| Device | Move the cursor | Play a stone | Pass | Stand up |
|---|---|---|---|---|
| Mouse | Point at a point | Click | Right-click | E |
| Keyboard | W A S D or the arrows | Space or Enter | Escape or Backspace | E |
| Pad | Left stick or d-pad | South (A) | East (B) | RB |

A blue outline marks the cursor's point. Up the board is away from the side
she sits on and right is her own right. A play drops the lowest-numbered stone
of the side to move from its tray 6 cm onto the point; it acts only on the
seat's own turn, once the table has rested and matches the position. Seat 1
plays every side the computer does not (`goAiSide`).

## Points, stones and the console

A point's ordinal is 9 × row + column, the column along +X and the row along
+Z, so point 0 is the -X, -Z corner, at Black's right hand, and the centre is
40. Point (column, row) stands at x = 55.9 + 0.15 × column,
z = 30 + 0.15 × row. Black's stones are `goStone0` to `goStone40` and White's
`goStone41` to `goStone81`; stone n is body 191 - n, below the cursor (body
192), and seat 1 drives each of them, so a stone set by hand is read like any
other: `body.pose 56.65 10 30.6 0 0 0 191` drops Black's first stone onto point
41. Off the board a stone's `goCell` is `-1 - n`.

| To | Console line |
|---|---|
| Play a stone of the side to move on a point | `world.state.cell.set goPut $value <point>` |
| Pass | `world.state.cell.set goPassRequest $value 1 add` |
| Start a new game | `go.reset` |
| Give the computer a side (2 White, 1 Black, 0 none) | `go.opponent <side>` |
| Read the position, the table's board and the last reading | `world.state goBoard`, `world.state goSeen`, `world.state goChangedCount` |
| Whose turn (1 Black, 2 White), the moves and refusals | `world.state goTurn`, `world.state goMoves`, `world.state goRejected`, `world.state goRefusal` |
| The last stone's group, liberties and atari | `world.state goGroup`, `world.state goLiberties`, `world.state goAtari` |
| The score once two passes end the game | `world.state goTerritory`, `world.state goStoneCount`, `world.state goWinner` |
| The opponent's search | `world.search` |
| Frame the table from Black's side | `view.override camera look 56.5 11.2 28.6 56.5 9.9 30.6`, then `view.override camera auto` |

`goRefusal` says why the last request was refused: 1 an occupied point, 2 a
suicide, 3 a ko.

## The opponent

The computer plays White by default (`goAiSide` 2). On its turn, once the table
has rested and matches the position, the `goAI` search job grows a Monte Carlo
tree over the door's answers, three plies deep, scored by the stones each side
holds, and its stone is put down on the table as a player's is; with no move
it passes. The search spends the tick's whole search allowance and takes turns
with the other tables' jobs ([search](../../docs/reference/state/search.md)).

## Tests

[`go-tests.puck`](go-tests.puck) holds the table's scenarios, each booted in the
whole debug world: the stones settling in their trays, a seated player playing
and the computer answering, the cursor's steps and a pass, a lone stone's
liberties, atari, a group capture, suicide and ko refusals and the exchange
that lifts a ko, a given position filled on the table and a capture taken off
it, an illegal arrangement put right, the computer's move and the area count.
Run them with `puck test worlds/debug/go-tests.puck`.
