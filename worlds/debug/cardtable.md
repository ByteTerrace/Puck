# Debug world game table

The debug world stands a game table in the gallery hall's board game room, the
south gallery of level 2 ([`shapes.md`](shapes.md#the-board-game-room)), at the
west end of the row of tables toward the balustrade. It is a display table
([`../standard/display-table.puck`](../standard/display-table.puck)): a slim
teal chassis with a lit amber line round its rim on one steel column, and a top
that is one dark glass display, 1.2 m by 1.32 m, which the games glow on as the
floor's display consoles do. The glass plays chess, Klondike or FreeCell, one at
a time. [`cardtable.puck`](cardtable.puck) holds the table, its glass, its cards
and its seat, and `debug.puck` imports it whole (`import "cardtable"`); its
look is `lookDisplayTable` in [`gallery/look.puck`](gallery/look.puck).

The table keeps no game of its own. Chess on the glass is the chess table's game
([`chess.md`](chess.md)): the glass draws that game's board, and a move made at
the glass goes through the same judge and moves the physical pieces too, so a
game played at either table is one game. The patience games are the rules of
[`klondike.puck`](klondike.puck) and [`freecell.puck`](freecell.puck), which
[`solitaire.puck`](solitaire.puck) collects; a game keeps its piles while
another is shown.

## Playing at the table

Walk up to the table and press the context button (E, or the pad's RB) within
2.5 m of its centre: the Moth sits down in the table's one chair, at its -Z edge
facing +Z (the engage block's `seats`), and the camera eases to the glass from
that chair wherever she stood. `debug.go cards` stands her at that edge, close
enough to sit. Every game is laid out for the chair, and the keys move the
cursor the way they point.

A bar of four buttons runs along the glass's near edge: CHESS, KLONDIKE and
FREECELL show their game, and NEW DEAL deals the patience game shown again.
Under the bar a status line says whose move it is in chess, or which patience
game lies on the glass and whether it is solved. A
pick on the bar presses its button and is never read as a move. `table.game
0|1|2` does the same from the console. The table boots showing chess.

| Device | Move the cursor | Lift, play or press | Put back | Stand up |
|---|---|---|---|---|
| Mouse | Point at a square or a card | Click | Right-click, or click what you lifted | E |
| Keyboard | W A S D or the arrows | Space or Enter | Escape or Backspace | E |
| Pad | Left stick or d-pad | South (A) | East (B) | RB |

## Chess

The glass shows a board of 10 cm squares, White's back rank nearest the chair
and the a-file at her left, and on each square the piece the chess game's
`board` row holds there, drawn from the standard's chess icons
([`../standard/chess-icons.puck`](../standard/chess-icons.puck)): ivory pieces
with a dark rim and charcoal pieces with a glowing teal rim. Each square is two
paints on the glass: a light or dark fill, which shows while chess is shown,
and over it a ring, which at rest draws the board's grid in the glass's own
dark and lights teal for the cursor, amber for the lifted piece and terracotta
for the last legal move's two squares. The rules write a ring only when what it
shows changes, at most two a tick.

A pick on a piece of the side to move lifts it; a pick on another square asks
for the move. The move goes through the chess game's display door
(`chessPut`, in [`chess.puck`](chess.puck)), which takes the path a seated
player's put-down takes at the chess table: the physical pieces move, the board
rests, and the judge rules. An illegal move is put back as it stood. A pick on
the lifted piece, or the cancel button, puts it down where it was. The seat
plays every side the computer does not (`aiSide`, Black by default), and while
it plays here the chess table's idle demonstration waits.

| To | Console line |
|---|---|
| Show chess | `table.game 0` |
| Read the seat's cursor and lifted square | `world.state tableChess` |
| Read what each square's ring shows (2 last move, 3 lifted, 4 cursor, 5 at rest) | `world.state tableRingCode` |
| Ask for a move from the console, as the glass does | `world.state.cell.set chessPut from 11`, `to 27`, then `request 1` |
| Read the door | `world.state chessPut` |

## Klondike

`table.game 1` lays Klondike on the glass (it deals itself the first time). The
pick grid is 48 by 58 cells of 2 cm, centred on the table: a 10.6 by 14.9 cm
card, columns 12 cm apart, a fan step of 4 cm, and the four rows nearest the
chair are the bar. The top row runs along the far side, left to right, as the
stock, the waste, a gap and the four foundations (spades, hearts, clubs,
diamonds), and nearer the chair the seven tableau columns fan toward her. A
faint outline on the glass marks each pile of the game shown.

Only the pointer reaches a card inside a column's fan; the keys and the pad
stand on a column's last card. A blue outline marks the card under the cursor.
A pick on the stock draws (`draw` cards, one or three by `option`), or turns
the waste over once the stock is empty. A pick on a face-up card lifts it and
every card after it in its fan, and an orange outline marks it; a pick on
another pile plays the run there. The door answers an illegal move with
`result` -1 and the cards stay where they were. A won game deals again on the
next pick.

The game is two layers. The rules are state: the stock, the waste, seven
tableau columns and four foundations are ordered piles of card keys
(`solitaireKlondikePile0` to `Pile13`, no pile 9), and a request through one
mailbox, `solitaireKlondike`, deals, moves a run or draws. The table follows the
rules: whenever the piles change (`kSync`), every card is read for its pile and
its depth in it (`kSlotOf`), and posed at that slot of the `kSlots` graph, face
up or face down. A card is a body of the `cardSlab` kit, posed exactly, so a
move is judged by the door over the piles. A deal gathers every card a tick,
shuffles the stock from the seeded stream, and deals the tableau a card a tick,
so it takes about 100 ticks. While chess is shown every card lies inside the
chassis, under the glass.

The deck is the standard package's ([`cards.puck`](../standard/cards.puck)):
52 faces and a back as signed-distance creations, in the `light` variant (an
ivory ground, red hearts and diamonds, black spades and clubs). The pips and the
rank and suit index are large, matte and share the suit's colour so they read
from the seat.

## FreeCell

`table.game 2` lays FreeCell on the glass (it deals itself the first time). The
top row is the four free cells and the four foundations (spades, hearts, clubs,
diamonds) over eight columns of face-up cards, and the same keys and pointer
play: lift a card or a run from a column, a card from a free cell, and play it
on a column, a free cell or a foundation. The door moves a run only as far as
the free cells and empty columns could carry it a card at a time: at most (empty
cells + 1) times two to the power of the empty columns, not counting a target
column that is itself empty. Piles are 0 to 3 the cells, 4 to 7 the foundations
and 8 to 15 the columns, and the mailbox is `solitaireFreecell`.

Its scenarios are [`freecell-tests.puck`](freecell-tests.puck).

## From the console

The table, the seat and the console play the same mailboxes. A patience move
names the pile it leaves, the pile it lands on and the card that leads the run;
a card is `suit * 13 + rank - 1` with suits spades, hearts, clubs and diamonds
(the ace of diamonds is 39). Klondike's piles are 1 the waste, 2 to 8 the
tableau and 10 to 13 the foundations.

| To | Console line |
|---|---|
| Show a game | `table.game 0` (chess), `table.game 1` (Klondike), `table.game 2` (FreeCell) |
| Deal Klondike again, drawing one or three | `klondike.deal 1`, `klondike.deal 3` |
| Draw from the stock, or turn the waste over | `klondike.draw` |
| Move a Klondike run led by a card | `klondike.move <from> <to> <card>`, as `klondike.move 8 13 39` |
| Deal FreeCell again | `freecell.deal` |
| Move a FreeCell run led by a card | `freecell.move <from> <to> <card>`, as `freecell.move 15 6 26` (the ace of clubs to its foundation) |
| Read a game | `world.state solitaireKlondike`, `world.state solitaireFreecell` (`status` 1 playing, 2 won; `result`, `moves`) |
| Read a pile | `world.state solitaireKlondikePile2` (bottom to top) |
| Read where the table put each card | `world.state kSlotOf` |
| Read the seat's cursor and lifted run | `world.state kSeat` |
| Read the game shown and the status line | `world.state tableGame`, `world.state tableStatus` |
| Frame the glass from the chair | `view.override camera look 38 11.19 15.5 38 9.85 17.3`, then `view.override camera auto` |

A test or a script plays a patience game by setting the mailbox cells and
bumping `request` (`world.state.cell.set solitaireKlondike from 8`, and `to`,
`card`, `action 2`, `request 1 add`), which the commands above do.

## Looking at the table

A windowed check of the glass, wide and close, runs as one `puck_exec` script
on a windowed World booted with `puck_world_start`:

```text
debug.go cards
world.wait bakes 180
view.override camera look 38 11.19 15.5 38 9.85 17.3
world.wait captures 30
world.screenshot <abs-path>/table-chess.png
table.game 1
world.wait 160
world.wait captures 30
world.screenshot <abs-path>/table-klondike.png
view.override camera look 38 10.4 16.4 38 9.85 17.6
world.wait 10
world.screenshot <abs-path>/table-fan.png
world.wait captures 30
table.game 0
view.override camera auto
```

The first shows the chess board with its pieces, the bar and the status line;
the second the Klondike layout centred on the glass, and the third a face-up fan
from just above.

## Bodies

The table's 54 bodies are the population's top slots (the `cardBodies` of
[`shapes.puck`](shapes.puck), imported ahead of the other tables so their bodies
keep their numbers): the cursor outline is the highest, card `k` is the body
`1 + k` below it, and the held-run outline is the lowest. The chess pieces on the
glass are no bodies: each square is a placement (`tableToken<cell>`) whose
response facet shows the piece the cell holds. A card keeps the pitch it is posed
at, face up -90 degrees and face down +90, because its kit runs the drive-frame
program (`cardSlab`) in place of the walk program, with `turn.maxPitch` just
short of a right angle. If the faces read upside down or mirrored in a capture,
`cardFaceUp` and `cardFaceDown` at the top of `cardtable.puck` are the two
numbers to swap.

## Tests

[`cardtable-tests.puck`](cardtable-tests.puck) holds the Klondike scenarios, each
booted in the whole debug world with its machines stopped and the table showing
Klondike: the seeded deal, a legal move (the ace of diamonds to its foundation,
which turns the next card up), an illegal move refused, a draw, and a seated
player playing the same ace with the keys. Run them with
`puck test worlds/debug/cardtable-tests.puck --reproduce --jobs 1`.
