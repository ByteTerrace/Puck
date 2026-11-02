# Debug world card table

The debug world stands a card table in the gallery hall's board game room, the
east end of level 2 ([`shapes.md`](shapes.md#the-board-game-room)), north of the
Go table. It plays Klondike or FreeCell patience with 52 physical cards on a felt
board centred at (60.8, 9.86, 35.9), on a table 1.6 m by 1.5 m whose top is 0.8 m
over the floor. The pick grid is 48 by 54 cells of 2 cm: a 10.6 by 14.9 cm card,
columns 12 cm apart (FreeCell's eight fill the grid's width, Klondike's seven stand
centred in it) and a fan step of 4 cm, so the deepest column (six face-down cards
and a king down to an ace) ends on the grid's last row. The felt (1.08 m by
0.78 m) is cut to a game in play: its far edge is 6 cm past the top row, a deal's
fans end 13 cm short of its near edge and a fan of ten cards reaches it; deeper
fans run on over the table's wood and the pointer still picks them.
[`cardtable.puck`](cardtable.puck) holds the table, its cards and
its seat; [`klondike.puck`](klondike.puck) and [`freecell.puck`](freecell.puck)
hold the rules and [`solitaire.puck`](solitaire.puck) the collection they belong
to (`solitaire.table` names the game in play, 1 Klondike and 2 FreeCell, set by
`solitaire.game 1|2`; a game keeps its piles while the other is played);
`debug.puck` imports the table whole (`import "cardtable"`). The deck is the standard package's
([`cards.puck`](../standard/cards.puck)): 52 faces and a back as signed-distance
creations that any author imports, in two variants an author chooses: `light`
(an ivory ground, red hearts and diamonds, black spades and clubs, the gold rim
and court figures kept) and `dark` (a near-black ground, bright pips). This
table wears `cardDeckPrototypes(variant: "light")`; the pips and the rank and
suit index are large, matte and share the suit's colour so they read from the
seat. A face's relief stays within 2 mm and a court figure is a stack of cut-out
layers on the court field (`courtRelief`, `courtStep` in `cards.puck`), so no
card pokes through the one laid over it; a layer of the stack (`cardLayer`,
3.2 mm) is the card's thickness and both faces' raised rims.

A card's mesh bakes at a cell of several millimetres, so a mark must be a solid
column from the card's surface, not a thin slab floating over it: pips and index
glyphs stand on the face (their rim and core overlap the body), and a court
figure's pieces stand on the court field's plate, deeper pieces first. Squashing
a modelled 3D bust to a millimetre lost its features, and a pip core a hair
clear of the face baked to rim fragments.

The game is two layers. The rules are state: the stock, the waste, seven
tableau columns and four foundations are ordered piles of card keys
(`solitaireKlondikePile0` to `Pile13`, no pile 9), and a request through one
mailbox, `solitaireKlondike`, deals, moves a run or draws. The table follows the
rules: whenever the piles change (`kSync`), every card is read for its pile and
its depth in it (`kSlotOf`), and posed at that slot of the `kSlots` graph, face up
or face down. A card is not a rigid body, because a fan of cards a few
millimetres apart is not an arrangement a solver could hold; it is a body of the
`cardSlab` kit, posed exactly, so a move is judged by the door over the piles,
and no settled arrangement of the table is read. The table deals at boot from the
seeded stream; a deal gathers every card a tick, shuffles the stock, and deals the
tableau a card a tick, so it takes about 100 ticks.

## Playing at the table

Walk up to the table and press the context button (E, or the pad's RB) within
2.5 m of the board's centre: the Moth sits down in the table's one chair, at its
-Z edge facing +Z (the engage block's `seats`), and the camera eases to the board
from that chair wherever she stood. `debug.go cards` stands her at that edge,
close enough to sit. Both games are laid out for the chair, so the cards lie
upright and the keys move the cursor the way they point: the top row runs along
the far side, left to right, as the stock, the waste, a gap and the four
foundations (spades, hearts, clubs, diamonds), and nearer her the seven tableau
columns fan toward her.

| Device | Move the cursor | Lift, play or draw | Put the run back | Stand up |
|---|---|---|---|---|
| Mouse | Point at a card | Click | Right-click, or click the pile it came from | E |
| Keyboard | A D or the arrows across; W to the top row, S back to a column's last card | Space or Enter | Escape or Backspace | E |
| Pad | Left stick or d-pad | South (A) | East (B) | RB |

Only the pointer reaches a card inside a column's fan; the keys and the pad stand
on a column's last card. A blue outline marks the card under the cursor. A pick on the stock draws
(`draw` cards, one or three by `option`), or turns the waste over once the stock
is empty. A pick on a face-up card lifts it and every card after it in its fan,
and an orange outline marks it; a pick on another pile plays the run there. The
door answers an illegal move with `result` -1 and the cards stay where they were.
A won game deals again on the next pick.

## FreeCell

`solitaire.game 2` puts FreeCell on the table (it deals itself the first time).
Seated, the top row is the four free cells and the four foundations (spades,
hearts, clubs, diamonds) over eight columns of face-up cards, and the same keys
and pointer play: lift a card or a run from a column, a card from a free cell,
and play it on a column, a free cell or a foundation. The door moves a run only as
far as the free cells and empty columns could carry it a card at a time: at most
(empty cells + 1) times two to the power of the empty columns, not counting a
target column that is itself empty. Piles are 0 to 3 the cells, 4 to 7 the
foundations and 8 to 15 the columns, and the mailbox is `solitaireFreecell`:

| To | Console line |
|---|---|
| Deal again | `freecell.deal` |
| Move the run led by a card | `freecell.move <from> <to> <card>`, as `freecell.move 15 6 26` (the ace of clubs to its foundation) |
| Read the game | `world.state solitaireFreecell` (`status` 1 playing, 2 won; `result`, `moves`) |

Its scenarios are [`freecell-tests.puck`](freecell-tests.puck).

## From the console

The table, the seat and the console play the same mailbox. A move names the pile
it leaves, the pile it lands on and the card that leads the run; piles are 1 the
waste, 2 to 8 the tableau, 10 to 13 the foundations, and a card is
`suit * 13 + rank - 1` with suits spades, hearts, clubs and diamonds (the ace of
diamonds is 39).

| To | Console line |
|---|---|
| Deal again, drawing one or three | `klondike.deal 1`, `klondike.deal 3` |
| Draw from the stock, or turn the waste over | `klondike.draw` |
| Move the run led by a card | `klondike.move <from> <to> <card>`, as `klondike.move 8 13 39` |
| Read the game | `world.state solitaireKlondike` (`status` 1 playing, 2 won; `result`, `moves`, `score`, `passes`) |
| Read a pile | `world.state solitaireKlondikePile2` (bottom to top) |
| Read where the table put each card | `world.state kSlotOf` |
| Read the seat's cursor and lifted run | `world.state kSeat` |
| Frame the table from the seat | `view.override camera look 60.8 11.2 34.2 60.8 9.86 36.0`, then `view.override camera auto` |

## Looking at the table

A windowed check of the deck's look, close and wide, runs as one `puck_exec`
script on a windowed World booted with `puck_world_start` (the deal takes about
100 ticks, so the script waits it out):

```text
debug.go cards
world.wait bakes 180
world.wait 160
view.override camera look 60.8 11.0 33.9 60.8 9.86 36.05
world.wait captures 30
world.screenshot <abs-path>/cards-wide.png
world.wait captures 30
view.override camera look 60.8 10.35 34.7 60.8 9.86 36.2
world.wait 10
world.screenshot <abs-path>/cards-close.png
world.wait captures 30
view.override camera look 60.8 10.15 35.5 60.8 9.86 36.25
world.wait 10
world.screenshot <abs-path>/cards-fan.png
world.wait captures 30
view.override camera auto
```

The wide shot shows the whole felt with the layout centred on it, the close one
the top row and the first columns' fans (a face-down stack is a plain red back:
no figure's head stands through it) and the third a face-up fan from just above.

A test or a script plays by setting the mailbox cells and bumping `request`
(`world.state.cell.set solitaireKlondike from 8`, and `to`, `card`, `action 2`,
`request 1 add`), which the commands above do.

## Bodies

The table's 54 bodies are the population's top slots (the `cardBodies` of
[`shapes.puck`](shapes.puck), imported ahead of the other tables so their bodies
keep their numbers): the cursor outline is the highest, card `k` is the body
`1 + k` below it, and the held-run outline is the lowest. A card keeps the pitch it is posed at, face up -90 degrees and face down
+90, because its kit runs the drive-frame program (`cardSlab`) in place of the
walk program, with `turn.maxPitch` just short of a right angle. If the faces
read upside down or mirrored in a capture, `cardFaceUp` and `cardFaceDown` at
the top of `cardtable.puck` are the two numbers to swap.

## Tests

[`cardtable-tests.puck`](cardtable-tests.puck) holds the scenarios, each booted
in the whole debug world with its machines stopped: the seeded deal, a legal move
(the ace of diamonds to its foundation, which turns the next card up), an illegal
move refused, a draw, and a seated player playing the same ace with the keys.
Run them with `puck test worlds/debug/cardtable-tests.puck --reproduce --jobs 1`.
