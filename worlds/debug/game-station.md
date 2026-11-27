# Debug world game station

The debug world set's game station is the standard's
([`../standard/game-station.puck`](../standard/game-station.puck)): a table in
the display stations' family whose top is one glass display that plays a
catalog of games, one at a time, as a display station's turntable shows a
catalog of shapes. Its table has a cream cap bevelled at the rim, the
console glass set in it, a glowing line under the cap's lip, an ochre apron
with a teal seam, a brass rail along the player's edge, and a cream column on
a teal foot. The glass stands 0.95 m over the floor and is 1.72 m across by
1.6 m deep.

Two worlds stand one:

- [`game-station-root.puck`](game-station-root.puck) stands a station alone
  on a small floor under a clear day, for iterating on it. Boot it with
  `--world worlds/debug/game-station-root.puck`: the seat starts at the
  station's chair (spawn `hero`), and the station's id is `station`.
- The gallery's board game room ([`gallery/games.puck`](gallery/games.puck))
  stands one where the card table stood, at the west end of the row of tables
  toward the balustrade, so it appears in the debug world and in
  [`gallery-root.puck`](gallery-root.puck) alike. `debug.go cards` stands the
  seat at its chair (spawn `cards`), and the station's id is `boardroom`.

The set's catalog ([`game-stations.puck`](game-stations.puck)) lists chess,
Klondike and FreeCell. Each game keeps one implementation of its rules:

- Chess is the chess library ([`../games/chess.puck`](../games/chess.puck)),
  drawn by [`../games/chess-station.puck`](../games/chess-station.puck). The
  debug world plays the chess table's own game ([`chess.md`](chess.md)), so a
  move at the glass moves the physical pieces too. `gallery-root.puck` and
  `game-station-root.puck` compose chess as a drawn board
  ([`../games/chess-game.puck`](../games/chess-game.puck)).
- Klondike and FreeCell are the patience collection
  ([`../games/solitaire.puck`](../games/solitaire.puck) over
  [`klondike.puck`](../games/klondike.puck) and
  [`freecell.puck`](../games/freecell.puck)), laid out by
  [`../games/patience-station.puck`](../games/patience-station.puck) with the
  standard deck. The deck's 54 bodies come from
  [`game-station-deck.puck`](game-station-deck.puck), which `debug.puck`
  imports ahead of every other population so every body keeps its index.

## Playing at the station

Walk up to the station and press the context button (E, or the pad's RB)
within 2.5 m of its centre: the seat sits down at the station's -Z edge facing
+Z, wherever it stood, and the camera eases to the glass from there. The same
button stands it up.

At the player's left the glass lists the catalog under GAMES. A tap on a row
shows that game, and NEW GAME at the list's foot starts the game shown afresh
(a patience game deals again). The status line along the near edge says whose
move it is in chess, or which patience game lies on the glass, a deal under
way, or a solved game.

| Device | Move the cursor | Lift, play or tap | Put back | Stand up |
|---|---|---|---|---|
| Mouse | Point at a square, a card or a row | Click | Right-click, or click what you lifted | E |
| Keyboard | W A S D or the arrows | Space or Enter | Escape or Backspace | E |
| Pad | Left stick or d-pad | South (A) | East (B) | RB |

In chess, a pick on a piece of the side to move lifts it (its square's ring
lights amber), and a pick on another square asks the chess game's display door
(`chessPut`) for the move. The door judges it as it judges any move, and an
illegal one is put back. The cursor's ring is pale cyan, and the last legal
move's two squares terracotta. The seat plays every side the computer does not
(`aiSide`, Black by default).

In patience, the pointer reaches any card of a fan and the keys stand on a
column's last card. A pick on a face-up card lifts it and every card after it
(an amber outline marks it), and a pick on another pile plays the run there; a
pick on Klondike's stock draws, or turns the waste over once the stock is empty.

## From the console

| To | Console line |
|---|---|
| Show a game | `station.game <station> chess`, `klondike` or `freecell` (`station`, or `boardroom` in the gallery) |
| Read the game shown, the seat's presses and its pointer | `world.state gameStationGame`, `gameStationEdges`, `gameStationPointer` |
| Read the status line | `world.state gameStationStatus` |
| Read chess at the glass: the cursor, the lifted square, each ring's code (2 last move, 3 lifted, 4 cursor, 5 at rest) | `world.state chessStationCursor`, `chessStationPicked`, `chessStationRingCode` |
| Ask for a chess move as the glass does | `world.state.cell.set chessPut from 11`, `to 27`, then `request 1` |
| Deal or move in patience | `klondike.deal 1`, `klondike.draw`, `klondike.move <from> <to> <card>`, `freecell.deal`, `freecell.move <from> <to> <card>` |
| Read where each card lies and the seat's cursor | `world.state patienceSlotOf`, `world.state patienceSeat` |

## Tests

[`klondike-tests.puck`](klondike-tests.puck) and
[`freecell-tests.puck`](freecell-tests.puck) hold the patience scenarios, each
booted in the whole debug world with the board game room's station showing its
game; [`chess-tests.puck`](chess-tests.puck) holds the chess table's. Run them
with `puck test <file> --reproduce --jobs 1`.
