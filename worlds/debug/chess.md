# Debug world chess table

The debug world stands a chess table in the gallery hall's board game room, the
east end of level 2 ([`shapes.md`](shapes.md#the-board-game-room)), beside the
Go and Reversi tables ([`go.md`](go.md), [`reversi.md`](reversi.md)). The
board's centre is at (56.5, 9.85, 22.6); the table is 2 m square, its top 0.8 m
over the floor. White's back rank is on the table's -Z side, toward the south
windows, and Black's on the +Z side, toward the room's doorway aisle.
[`chess.puck`](chess.puck) holds it and `debug.puck` imports it whole
(`import "chess"`).

The board reads as a real one from each side: from White's seat a1 is the dark
corner at her left, h1 the light one at her right, and the queen stands on her
colour, d1, left of the king; from Black's seat a8 is light at his right.

The 32 pieces are rigid bodies on a tabletop board (the
[tabletop primitive](../../src/Puck.World.Schema/README.md#discrete-boards-cards-and-turns)).
However a piece was moved, the judge reads no move from input: whenever the
physics has stayed at rest for 0.3 s (nine ticks, `chessSettleTicks`), the rules
read the settled arrangement off the board and judge it against the last legal
position. The hold only has to outlast a piece that is touched or dropped beside
another still settling: the rigid solver's own resting latch has already held
each piece still before the world reads as at rest, and a fall or a bump wakes
the latch and restarts the hold. The computer's and the seated player's own
moves are placed 8 cm above their square and drop onto it. From Black's turn
starting, the computer's reply is searched and placed within 16 ticks, rests by
tick 24 and is judged at tick 32 (about 1.1 s at 30 Hz). A legal arrangement becomes the new position
and passes the turn; any other is recorded as illegal (`illegalCount`) and the
game waits for the pieces to be put right. The rules cover captures, promotion
(the computer always queens), castling, en passant, check, the 75-move rule and
repetition. A knocked-over piece counts
as off the board.

The computer plays Black by default. It searches one ply, judging each candidate
with the game's own rules and scoring material, central squares and pawn
progress, then moves its piece itself and puts a piece it takes beside the board.

## Playing at the table

Walk up to the table and press the context button (E, or the pad's RB) within
2.5 m of the board's centre: the Moth sits down, stands still, and the camera
eases to a view of the whole board from the side she stands on (White's side is
-Z, the side nearer the south windows). `debug.go chess` stands her at White's edge,
facing the board and close enough to sit. The controls switch to the board page
while she sits; the same button stands her up again and the camera eases back.

A blue outline marks the cursor's square. Lift a piece of the side to move
from under it, and it hovers over the cursor wherever the cursor goes; put it
down on another square and the move is played. Up the board is away from the
side she sits on, and right is her own right: from White's side, toward the
h-file.

| Device | Move the cursor | Lift and put down | Put the piece back | Stand up |
|---|---|---|---|---|
| Mouse | Point at a square | Click a piece, then click a square; or press on a piece, drag and release over a square | Right-click, or click the square it came from | E |
| Keyboard | W A S D or the arrows, one square a press | Space or Enter | Escape or Backspace | E |
| Pad | Left stick or d-pad, one square a push | South (A) | East (B) | RB |

A put-down takes a captured piece beside the board, moves the rook of a castle,
lifts the pawn an en-passant capture takes and queens a pawn that reaches the
last rank, then the board rests and is judged as ever. An arrangement judged
illegal is put back as it stood before the put-down (`illegalCount` still
counts it). A piece lifts only once the board has rested, only on its side's
turn, and not while the computer is moving; a square holding a piece of the side
to move takes nothing, so a put-down there is refused and the piece stays up.
Seat 1 plays every side the computer does not (`aiSide`).

`world.state play` reads the seated seat's cursor, the piece it holds
(`held`, a piece ordinal or -1) and the square it left. Body 40 is the cursor
outline; it rests inside the tabletop while nobody sits at the table.

## Moving a piece from the console

A piece is a body: piece `n` (the `piece<n>` keys of `pieceCell`) is body
`39 - n`, so White's pieces are bodies 24 to 39 and Black's 8 to 23; piece `n`
starts on file `n % 8` (piece 4 is the white king, piece 12 the e2 pawn).
Square (file, rank), counting from a1 = (0, 0), is centred at
x = 57.2 - 0.2 × file, z = 21.9 + 0.2 × rank: the files run toward -X, so a1
lies on +X. Its cell in the board's rows (`pieceCell`, `board`, `move`) is
8 × rank + 7 - file, so e2 is cell 11 and e4 cell 27: the grid counts its
columns toward +X. Lift a piece onto a square by posing its body at y = 10.35,
0.5 m over the table's top, over the square's centre; it drops onto the board:

| Move | Console line |
|---|---|
| e2-e4 (the e2 pawn, body 27) | `body.pose 56.4 10.35 22.5 0 0 0 27` |
| Ng1-f3 (body 33) | `body.pose 56.2 10.35 22.3 0 0 0 33` |
| Take a piece off the board | pose its body onto the table beside the board past the h-file, as `body.pose 55.6 10.35 22.3 0 0 0 <body>` |

Seat 1 holds `Drive` over the 32 piece bodies, so it may pose them as well as
the console.

## Reading the game

| To | Console line |
|---|---|
| Read the board's frame, occupancy, turn, verdict and last move | `world.tabletop` |
| Whose turn (0 White, 1 Black) | `world.state turn` |
| The last judged move and whether it was legal | `world.state move`, `world.state verdict` |
| How many arrangements were refused | `world.state illegalCount` |
| Where each piece stands (`-1` off the board or fallen) | `world.state pieceCell` |
| Check, castling rights, the move clock and repetitions | `world.state inCheck`, `world.state castleRights`, `world.state halfmoveClock`, `world.state repetitionCount` |
| The opponent's search: running or done, and its answer | `world.search` |
| Frame the table from White's side | `view.override camera look 56.5 11.05 20.8 56.5 9.85 22.6`, then `view.override camera auto` |
| Frame the board game room from its doorway | `view.override camera look 52 11.6 26 60 9.6 26.5`, then `view.override camera auto` |

## The idle replay demonstration

After 60 seconds with no move and no player within 3 m of the table, the board
resets and shows **Deep Blue (White)–Garry Kasparov, New York, 11 May 1997,
game 6, 1–0**. It plays one ply every three seconds, holds the final position
for twelve seconds, then repeats. Approaching or engaging cancels the demo and
restores a fresh playable table. The faerie is free to watch; its proximity does
not count as a player approaching. The computer opponent pauses during the demo.

The score was checked against the [ChessBox public game score](https://www.chessbox.in/chessgame/deep-blue-kasparov-garry-5/):

```text
1.e4 c6 2.d4 d5 3.Nc3 dxe4 4.Nxe4 Nd7 5.Ng5 Ngf6
6.Bd3 e6 7.N1f3 h6 8.Nxe6 Qe7 9.O-O fxe6 10.Bg6+ Kd8
11.Bf4 b5 12.a4 Bb7 13.Re1 Nd5 14.Bg3 Kc8 15.axb5 cxb5
16.Qd3 Bc6 17.Bf5 exf5 18.Rxe7 Bxe7 19.c4 1-0
```

Kasparov resigned after 19.c4; the demo does not label the position checkmate.
There are 37 plies: nineteen White moves and eighteen Black moves.

The `replays` row in `chess.puck` authors only addressed inputs, their pacing,
and the table's scope. The first idle cycle derives an in-memory tape by
submitting those inputs through the grant-checked move mailbox. The table's
ordinary relocation rules and settled-board judge process every move, including
captures and castling. Its screen says **SCOPE TAPE RECORDED** on that cycle.
Every later cycle restores only the captured table rows and bodies and replays
the accepted inputs. It compares every resulting scope hash with the first
live recording and shows **SCOPE HASH MATCH** or **SCOPE HASH MISMATCH**, beside
the historical result. No board positions, expected hashes or binary tapes are
committed. The rest of the world's clock, bodies, routes and state keep running.

`replay.scope chess-demo` reads progress, the starting hash, the recorded tail
and the current scope hash. `world.state chessDemo` and `world.state chessTape`
read the idle deadline, loop and match counts, cancellation count and status.
The authored timing constants stand near the top of `chess.puck`.
`world.state.cell.set chessDemo enabled 0` cancels and disables the loop.
`chess.move 11 27` requests e2-e4 through the same apply and judging path;
`chessInput` reads the mailbox and its accepted-request count.

## The opponent

`world.state.cell.set aiSide $value 1` gives Black to the computer (the
default), `0` gives it White and `-1` leaves both sides to people. The computer
moves only once the board has rested, the last arrangement was legal (or nothing
moved), it is its turn and the game has not reached a draw limit. Its search tries only
the squares each of its pieces could move to (the `chessMoves` patterns in
`chess.puck`: pawn steps, double steps and captures, knight jumps, slides to
the first piece, king steps and castling), a few dozen candidates, and answers
within about half a second of its turn starting; the full rules judge each
candidate as before. It spends a bounded amount of work each tick and restarts
only when a row it reads changes, so the rest of the world's living state never
holds it up. `world.search` shows its progress (`judged`, `accepted`, `spent`). The
[state reference](../../docs/reference/state/worlds.md#the-chess-opponent) walks
through how it plays.

## Tests

[`chess-tests.puck`](chess-tests.puck) holds the table's scenarios, each booted
in the whole debug world: a seated player playing a move with the keys and one
with a pointer drag, a seated player cancelling a lift, having an illegal
put-down put back and standing up, legal and illegal moves, captures, castling through
and clear of attack, en passant, a move left in check, threefold repetition and
the opponent on either side. Run them with
`puck test worlds/debug/chess-tests.puck`.
