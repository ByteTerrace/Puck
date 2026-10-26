# Debug world Reversi table

The debug world stands a Reversi (Othello) table in the gallery hall's board
game room, the east end of level 2 ([`shapes.md`](shapes.md#the-board-game-room)),
east of the chess table ([`chess.md`](chess.md)). The board is 8x8, its centre at
(65, 9.905, 22.6) on a table 3.5 m by 1.7 m whose top is 0.8 m over the floor.
Black sits on the table's -Z side and White on its +Z side, and each has a tray
of 32 discs at the end of the table on their right: Black's at -X, White's at
+X. Every disc is black on one face and white on the other. [`reversi.puck`](reversi.puck)
holds it and `debug.puck` imports it whole (`import "reversi"`). The game follows
the [World Othello Federation's official rules](https://www.worldothello.org/about/about-othello/othello-rules/official-rules/english).

The game is two layers. The rules are a move door over the logical board
`reversiBoard`: a request moves the token `reversiMove.play` to a cell, and the
door plays it when the cell is empty and brackets at least one unbroken run of
the opponent's discs along one of the eight compass rays (one `$match` read of
the `reversiBlackLine` or `reversiWhiteLine` pattern); every bracketed run flips
by `setRay`. A side with no legal move passes by itself (its moves along each
direction are 64-bit cell masks shifted over the opponent's runs,
`reversiMoves`); two passes in a row end the game, as a full board does, and the
side holding more discs wins.

Over the rules lies the table. Each disc is a rigid body, and whenever the
physics has rested for 0.3 s (`reversiSettleTicks`), the rules read the cell
each disc lies on (`reversiCell`) and the face it shows (`reversiFace`: black
when its black face is up), so the table shows a board of its own
(`reversiSeen`). Discs are alike, so the reading compares the two boards as cell
masks: a table that differs from the accepted position by one disc of the side
to move on an empty cell is a request through the door; a disc on a cell the
position leaves empty is counted illegal (`reversiIllegal`). Either way the
table is then put back to the position: a disc showing the wrong face is turned
over where it lies (posed 6 cm up, rolled half a turn, and dropped), which is
how a move's flips reach the table; a refused or stray disc goes back to its
tray; and a cell the position holds that the table lacks is filled from a tray,
one cell a reading, which is how the opening's four discs reach the board.

## Playing at the table

Walk within 2.5 m of the board and press the context button (E, or the pad's
RB): the Moth sits down and the camera eases to the board from her side.
`debug.go reversi` stands her at Black's edge, facing the board and close enough
to sit. The controls switch to the board page chess declares while she sits.

| Device | Move the cursor | Play a disc | Stand up |
|---|---|---|---|
| Mouse | Point at a cell | Click | E |
| Keyboard | W A S D or the arrows | Space or Enter | E |
| Pad | Left stick or d-pad | South (A) | RB |

A blue outline marks the cursor's cell; it starts on 19, one of Black's opening
moves. A play drops a disc 6 cm onto the cell with the side to move's face up,
the lowest-numbered disc of that side's tray first; it acts only on the seat's
own turn, once the table has rested and matches the position. A disc played on
a cell that brackets nothing is refused (`reversiRejected`) and goes back to its
tray. Seat 1 plays every side the computer does not (`reversiAiSide`).

## Cells, discs and the console

A cell's ordinal is 8 × row + column, the column along +X and the row along +Z,
so cell 0 is the -X, -Z corner, at Black's right hand. The opening holds white
on 27 and 36 and black on 28 and 35; Black's first legal moves are 19, 26, 37
and 44. Cell (column, row) is centred at x = 64.475 + 0.15 × column,
z = 22.075 + 0.15 × row. The discs are `reversiDisc0` to `reversiDisc63`, the
first 32 in Black's tray; disc n is body 108 - n, below the cursor (body 109),
and seat 1 drives each of them.

| To | Console line |
|---|---|
| Play a disc of the side to move on a cell | `world.state.cell.set reversiPut $value <cell>` |
| Start a new game | `reversi.reset` |
| Give the computer a side (2 White, 1 Black, 0 none) | `reversi.opponent <side>` |
| Read the position and the table's board | `world.state reversiBoard`, `world.state reversiSeen` |
| Whose turn (1 Black, 2 White), the moves, flips and refusals | `world.state reversiTurn`, `world.state reversiMoveCount`, `world.state reversiFlips`, `world.state reversiRejected` |
| The last move's bracket reach in each direction | `world.state reversiRay` |
| The disc counts and the winner (1 Black, 2 White, 3 a draw) | `world.state reversiBlack`, `world.state reversiWhite`, `world.state reversiWinner` |
| The opponent's search | `world.search` |
| Frame the table from Black's side | `view.override camera look 65 11.2 20.6 65 9.9 22.6`, then `view.override camera auto` |

## The opponent

The computer plays White by default (`reversiAiSide` 2). On its turn, once the
table has rested and matches the position, the `reversiAI` search job looks
three plies ahead by negamax, scoring the disc difference with a corner worth
ten discs, and its disc is put down on the table as a player's is. The search
spends the tick's whole search allowance and takes turns with the other tables'
jobs ([search](../../docs/reference/state/search.md)).

## Tests

[`reversi-tests.puck`](reversi-tests.puck) holds the table's scenarios, each
booted in the whole debug world: the opening reaching the board, a seated
player's move turning the bracketed disc over on the table, the computer's
answer, a refused disc going back to its tray, one- and two-direction flips for
each colour, and two passes ending the game. Run them with
`puck test worlds/debug/reversi-tests.puck`.
