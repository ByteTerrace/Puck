# Debug world game station

The debug world set's game station is the standard's
([`../standard/game-station.puck`](../standard/game-station.puck)): a table in
the display stations' family whose top is one glass display that swaps games
through its menu, as a display station's turntable swaps shapes. Its table has
a cream cap bevelled at the rim, the console glass set in it, a glowing line
under the cap's lip, an ochre apron with a teal seam, a brass rail along each
chair's edge, and a cream column on a teal foot. The glass stands 0.95 m over
the floor and is 1.72 m across by 1.6 m deep.

## The owner's rulings

- **Every station is a digital game table with its own games.** A station swaps
  games through its menu (chess, Klondike, FreeCell) and keeps its own state for
  each game it can show, so swapping games or playing at one station never
  touches another. The gallery is planned to hold twelve, as it holds twelve
  display stations.
- **Two seats for chess.** A player in the far chair plays the other side; with
  no one there the computer plays it. Anyone else watches. Patience stays one
  player's.
- **NEW GAME works for every game**, chess included.
- **A station move waits while a physical table's player holds a piece.** Where
  a station shows a physical table's game, the game's display door takes no
  move while the table's seated player has a piece up.
- **Every operation is bounded in memory and CPU**, and twelve stations at once
  stay cheap: an idle station does no rule work, a station's rules wake only on
  its own input or its game's state, the computer's search runs bounded and
  only for the station whose turn it is, and shared data (piece and card
  meshes, the catalog, the status lines) exists once.
- **The layout is centred on the glass**, sized to read from the chair, and the
  menu and status line read clearly. The washed-out glass and streaked
  reflections in gallery daylight are a renderer defect owned elsewhere; the
  station's own contrast and sizing are its own.

## How a station is built

[`game-stations.puck`](game-stations.puck) is the set's library: the catalog,
the module instance a station holds its games in, and what the world holds for
every station.

- **The world's part**, expanded once over the list of stations: each station's
  table prototype (its paints read the world's rows by the station's id), its
  placement and pick grid, the rows its paints and the console read
  (`gameStationGame`, `gameStationPick`, `gameStationStatus`,
  `gameStationRowInk`, and the chess board's inks), the constant tables every
  station's rules copy from, the console commands, and the decks' faces and
  kit.
- **The station's instance**, `use gameStationGames as <id>(station: …, index: <i>)`,
  with the station's id as the alias and `i` its place in the world's list of
  stations: its chairs and presses, its chess game (the chess library,
  [`../games/chess.puck`](../games/chess.puck), in its digital mode), its
  chess display ([`../games/chess-station.puck`](../games/chess-station.puck)),
  its Klondike ([`klondike.puck`](../games/klondike.puck)) and the patience
  layout ([`../games/patience-station.puck`](../games/patience-station.puck)).
  Every row and rule the instance declares is the instance's own, `<id>$<name>`
  (`world.state south3$solitaireKlondike`).
- **The rules one set plays for every station**, `gameStationSetRules()` at the
  world's top level: FreeCell ([`freecell.puck`](../games/freecell.puck)). Each
  rule sweeps the stations' tables (`forEach: solitaireTable`, keyed by `i`);
  a table's mailbox is a cell under its key (`freecellRequest[i]`) and its deck
  and piles are members of pile families (`freecellPile8Of<i>`), so a change at
  one station wakes and costs that station's binding alone
  ([analysis](../../docs/reference/state/analysis.md)).
- **The shared-chess instance**, `gameStationSharedChess`, is the same station
  but for its chess, which is the world's own game: the debug world's chess
  table ([`chess.md`](chess.md)), whose own rules judge, hold and reset it, so a
  move at the glass moves the table's pieces too.

Three worlds stand stations:

- [`game-station-root.puck`](game-station-root.puck) stands one station with its
  own chess (id `station`) on a small floor. Boot it with
  `--world worlds/debug/game-station-root.puck`: seat 1 starts at the near chair
  (spawn `hero`) and seat 2 at the far chair (spawn `rival`).
- The gallery's game floor on level 3 ([`gallery/games.puck`](gallery/games.puck))
  plans twelve stations, six on each gallery
  ([`gallery/layout.puck`](gallery/layout.puck)'s `galleryGameStations`, clear of
  the corner stairs, the terrace, the lift's landing, the wonder keys' stand
  and the tree's north branch). It stands the first, `south3`, in its
  shared-chess form; the rest wait on the work sheet ([Cost](#cost)).
  `debug.go cards` stands the seat at its near chair.
- The test worlds ([`klondike-tests.puck`](klondike-tests.puck),
  [`freecell-tests.puck`](freecell-tests.puck)) stand stations of their own on
  `gameStationRoom`, the root's small room.

## The glass

From the near chair the glass reads, nearest first:

- the menu, a row of buttons across the whole width: GAMES at the left, a
  button a game (the game shown lit amber, the others teal) and NEW GAME;
- the status line above it, lettered in amber on the dark glass;
- the board area, the glass's whole width, which every game centres on;
- along the far edge, the status line again, lettered to read from the far
  chair.

The menu's lettering is embossed in the table and the status lines are text
paints; each line of capitals is centred on its box by the text's `anchor`
(`capMiddle`), never by hand offsets.

- **Chess** draws 15 cm squares centred on the board area, the pieces standing
  on them at 1.5 times the standard set's size. The near chair plays White, the
  far chair Black. The near chair's cursor ring is pale cyan, the far chair's
  pink, a lifted piece's amber and the last legal move's two squares
  terracotta.
- **Klondike and FreeCell** lay their cards (16 by 23 cm) centred on the board
  area: the top row along the far side and the columns fanning toward the near
  chair.

## Playing at a station

Walk up to a station and press the context button (E, or the pad's RB) within
2.5 m of its centre: the seat sits down in the chair at the edge it faces most
squarely and the camera eases to the glass from there. The same button stands
it up.

| Device | Move the cursor | Lift, play or tap | Put back | Stand up |
|---|---|---|---|---|
| Mouse | Point at a square, a card or a button | Click | Right-click, or click what you lifted | E |
| Keyboard | W A S D or the arrows | Space or Enter | Escape or Backspace | E |
| Pad | Left stick or d-pad | South (A) | East (B) | RB |

A tap on a game's button shows that game; NEW GAME starts the game shown
afresh (chess sets every piece back, patience deals again). In chess, on your
side's turn a pick on your piece lifts it and a pick on another square asks the
game's display door (`chessPut`) for the move, which the game judges as it
judges any move. The computer plays the side whose chair stands empty (Black
when no one sits at all, neither when both chairs are taken). In patience, the
pointer reaches any card of a fan and the keys stand on a column's last card;
a pick on a face-up card lifts it and every card after it, and a pick on
another pile plays the run there.

## From the console

| To | Console line |
|---|---|
| Show a game | `station.game <station> chess`, `klondike` or `freecell` |
| Read every station's game, status and menu | `world.state gameStationGame`, `gameStationStatus`, `gameStationRowInk` |
| Read who sits where and their presses | `world.state <station>$gameStationChair`, `<station>$gameStationEdges`, `<station>$gameStationPointer` |
| Read chess at the glass | `world.state <station>$chessStationCursor`, `<station>$chessStationPicked`, `<station>$chessStationRingCode` |
| Move in a station's chess | `station.chess <station> 11 27` (e2 to e4) |
| Start a station's chess afresh | `world.state.cell.set <station>$chessFresh request 1` |
| Deal or move in patience | `klondike.deal <station> 1`, `klondike.draw <station>`, `klondike.move <station> <from> <to> <card>`, `freecell.deal <i>`, `freecell.move <i> <from> <to> <card>` (FreeCell takes the station's number) |
| Read a station's FreeCell | `world.state freecellRequest`, `freecellStatus`, `freecellDeals` (one cell a station), `world.state freecellPile8Of<i>` |
| Read where each card lies | `world.state <station>$patienceSlotOf`, `world.state <station>$patienceSeat` |

## Cost

Counts read from the compiled documents with the rule work sheet the World
admits (`world.budget.rules` reads the same sheet live):

| | Rows | Rules | Sheet (work units a tick, at rest plus one change) |
|---|---|---|---|
| One station with its own chess | 177 | 325 | 555,397 |
| Two | 282 | 616 | 1,068,789 |
| Twelve | 1,387 | 3,537 | 6,106,017 |

FreeCell's rules are one set for every station and price about 80 thousand
units whatever the count; chess, Klondike, the patience layout and the chairs
are still copied into each station's instance and price about half a million a
station. The tick admits 4.2 million units (`RuleCapacity.MaxWorkUnitsPerTick`),
so twelve stations wait on moving those to the same shape. What an idle station spends is a
different number from what the sheet admits: a station's rules are judged by
change, so at rest they do no work; `world.budget.rules --spent` reads what
they spent. Bringing the sheet down to admit twelve is the open item
"Twelve game stations on the work sheet" in
[the open items](../../docs/plans/open-items.md#card-tables-in-the-board-game-room).

## Tests

[`klondike-tests.puck`](klondike-tests.puck) and
[`freecell-tests.puck`](freecell-tests.puck) hold the patience scenarios on
stations of their own, reading only what every seeded deal shares;
`freecell-tests.puck` also stands two stations and claims a game at one never
touches the other. [`chess-tests.puck`](chess-tests.puck) holds the chess
table's. Run them with `puck test <file> --reproduce --jobs 1`.
