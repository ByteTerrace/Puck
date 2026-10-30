# Debug world card table

The debug world stands a card table in the gallery hall's board game room, the
east end of level 2 ([`shapes.md`](shapes.md#the-board-game-room)), north of the
Go table. It plays Klondike patience with 52 physical cards on a felt board
centred at (60.8, 9.86, 35.9), on a table 1.6 m by 1.5 m whose top is 0.8 m over
the floor. [`cardtable.puck`](cardtable.puck) holds the table, its cards and
its seat; [`klondike.puck`](klondike.puck) holds the rules and
[`solitaire.puck`](solitaire.puck) the collection they belong to (`solitaire.table`
names the game in play); `debug.puck` imports the table whole (`import
"cardtable"`). The deck is the standard package's ([`cards.puck`](../standard/cards.puck)):
52 faces and a back as signed-distance creations that any author imports.

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
2.5 m of the board's centre: the Moth sits down and the camera eases to the
board from her side. `debug.go cards` stands her at the +Z edge, facing -Z
across the board and close enough to sit. Sit on the +Z side: the cards lie
upright for that seat, and the keys assume it. From there the top row runs
left to right as the stock, the waste, a gap and the four foundations
(spades, hearts, clubs, diamonds), and under it the seven tableau columns fan
toward her.

| Device | Move the cursor | Lift, play or draw | Put the run back | Stand up |
|---|---|---|---|---|
| Mouse | Point at a card | Click | Right-click, or click the pile it came from | E |
| Keyboard | A D or the arrows across; W up the fan and to the top row, S down | Space or Enter | Escape or Backspace | E |
| Pad | Left stick or d-pad | South (A) | East (B) | RB |

A blue outline marks the card under the cursor. A pick on the stock draws
(`draw` cards, one or three by `option`), or turns the waste over once the stock
is empty. A pick on a face-up card lifts it and every card after it in its fan,
and an orange outline marks it; a pick on another pile plays the run there. The
door answers an illegal move with `result` -1 and the cards stay where they were.
A won game deals again on the next pick.

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
| Frame the table from the seat | `view.override camera look 60.8 11.2 37.6 60.8 9.86 35.9`, then `view.override camera auto` |

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
