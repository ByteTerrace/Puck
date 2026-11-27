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

- **Every station is a game table with its own games.** A station swaps games
  through its menu (chess, Klondike, FreeCell) and keeps its own state for each
  game it can show, so swapping games or playing at one station never touches
  another. The gallery is planned to hold twelve, as it holds twelve display
  stations.
- **The board is a holodeck.** When a game that uses tokens is shown, the board
  materializes that game's tokens as real bodies (the standard chess set's
  pieces, the deck's cards) and releases them when the game changes or ends.
  Players pick pieces up, set them down and knock them over, and the game is
  judged from where they settle. The station is the one place chess is played;
  the old physical chess table is retired, and the single-purpose game displays
  retire as the owner migrates them
  ([the decision](../../docs/decisions/play.md#tabletop)).
- **Two seats for chess.** A player in the far chair plays the other side; with
  no one there the computer plays it. Anyone else watches. Patience stays one
  player's.
- **NEW GAME works for every game**, chess included.
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
  station's rules copy from, the console commands, the decks' faces and kit,
  and the chess pieces' looks and kit (`chessStationSet`).
- **The station's instance**, `use gameStationGames as <id>(station: …, index: <i>)`,
  with the station's id as the alias and `i` its place in the world's list of
  stations: its chairs and presses, its chess game (the chess library,
  [`../games/chess.puck`](../games/chess.puck), with its physical judge), its
  board and piece bodies and their play
  ([`../games/chess-station.puck`](../games/chess-station.puck)) and the
  patience rules that read its chairs (`patienceStationRules` in
  [`../games/patience-station.puck`](../games/patience-station.puck)).
  Every row and rule the instance declares is the instance's own, `<id>$<name>`
  (`world.state south3$gameStationChair`).
- **The rules one set plays for every station**, `gameStationSetRules()` at the
  world's top level: the patience collection
  ([`solitaire.puck`](../games/solitaire.puck)), Klondike
  ([`klondike.puck`](../games/klondike.puck)) and FreeCell
  ([`freecell.puck`](../games/freecell.puck)). Each rule sweeps the stations'
  tables (`forEach: solitaireTable`, keyed by `i`); a table's mailbox is a cell
  under its key (`klondikeRequest[i]`, `freecellRequest[i]`) and its decks and
  piles are members of pile families (`klondikePile2Of<i>`,
  `freecellPile8Of<i>`), so a change at
  one station wakes and costs that station's binding alone
  ([analysis](../../docs/reference/state/analysis.md)). The patience layout
  (`patienceSetRules`) is one set too: a rule about a station's cards sweeps a
  family whose member `i` is station `i`'s (`forEach: patienceCardNoOf`,
  `forEach: klondikePile2Of`), binding the card to `$each` and the station to
  `$member`, and poses each card about its own station
  (`frame: placement:$each`).
- **The holodeck** ([`../standard/holodeck.puck`](../standard/holodeck.puck)) is
  the pool of bodies a game's tokens stand as: each body an inhabited placement
  whose count is a cell (`holodeckLive`), asked for and released through one
  door (`holodeckSet`), posed once it exists. Chess's 32 pieces are the
  station's instance's pool (`piece0` to `piece31`, as the chess library keys
  them); the cards are a world pool keyed `<station>-card<k>`. A materialized
  body takes the population's highest free slot, so a world sizes its bodies
  for the largest token game each station hosts (`gameStationBodies`: the
  outlines and 52 cards).

Worlds that stand stations:

- [`game-station-root.puck`](game-station-root.puck) stands one station with its
  own chess (id `station`) on a small floor. Boot it with
  `--world worlds/debug/game-station-root.puck`: seat 1 starts at the near chair
  (spawn `hero`) and seat 2 at the far chair (spawn `rival`).
- The gallery's game floor on level 3 ([`gallery/games.puck`](gallery/games.puck))
  plans twelve stations, six on each gallery
  ([`gallery/layout.puck`](gallery/layout.puck)'s `galleryGameStations`, clear of
  the corner stairs, the terrace, the lift's landing, the wonder keys' stand
  and the tree's north branch). It stands the first, `south3`, holding its own
  chess; the rest wait on the work sheet ([Cost](#cost)). `debug.go cards` and
  `debug.go chess` stand the seat at its near chair. The debug world numbers
  its bodies below the deck from it, so its cards stand always rather than as a
  pool (`holodeck: false`), and its chess pieces take the free slots
  `galleryPopulation` keeps.
- The board games lab ([`labs/board-games.puck`](labs/board-games.puck)) stands
  one station, and its stress mode
  ([`labs/board-games-stress.puck`](labs/board-games-stress.puck)) twelve, each
  playing chess against itself (`autoplay`).
- The test worlds ([`chess-tests.puck`](chess-tests.puck),
  [`klondike-tests.puck`](klondike-tests.puck),
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

- **Chess** draws 15 cm squares centred on the board area, and while chess is
  shown its pieces stand on them as rigid bodies at 1.5 times the standard
  set's size. The near chair plays White, the far chair Black. The near chair's cursor ring is pale cyan, the far chair's
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
side's turn with the board at rest, a pick on your piece lifts its body, which
hovers over your cursor; a pick on another square sets it down there. The
game's own apply path relocates the move (a captured piece's body is released,
a castling rook stands on its square, a pawn on the last rank comes back as a
queen) and the settled board is judged. An arrangement judged illegal, a
set-down or a piece knocked over alike, is put back as the last legal one stood,
every body standing on its square again. The computer plays the side whose
chair stands empty (Black when no one sits at all, neither when both chairs are
taken), and its move stands its piece's body on the new square to settle and be
judged like yours. In patience, the
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
| Read the holodeck's pieces | `world.state <station>$holodeckLive` (1 a standing body), `<station>$holodeckAt` (the square each was last stood on), `<station>$pieceCell` (the game's squares) |
| Read a station's turns played | `world.state <station>$chessStationPlies`, `<station>$chessStationGames` (games a self-playing station set up afresh) |
| Move in a station's chess | `station.chess <station> 11 27` (e2 to e4) |
| Start a station's chess afresh | `world.state.cell.set <station>$chessFresh request 1` |
| Deal or move in patience | `klondike.deal <i> 1`, `klondike.draw <i>`, `klondike.move <i> <from> <to> <card>`, `freecell.deal <i>`, `freecell.move <i> <from> <to> <card>` (each takes the station's number, 0 for the first) |
| Read a station's Klondike | `world.state klondikeRequest`, `klondikeStatus`, `klondikeDeals` (one cell a station), `world.state klondikePile2Of<i>`, `klondikeDown2Of<i>` (a column's face-down part), `klondikeFaceDownOf<i>` (a card's face: 1 down) |
| Read a station's FreeCell | `world.state freecellRequest`, `freecellStatus`, `freecellDeals` (one cell a station), `world.state freecellPile8Of<i>` |
| Read where each card lies | `world.state patienceSlotOf<i>` (a card's slot), `patienceCardAtOf<i>` (a slot's card), `patienceSeatZone` and the other `patienceSeat…` rows (one cell a station) |

## Cost

The rule work sheet the World admits, read from the compiled documents with
`puck compile <world> --validate --budget <lines>` (`world.budget.rules` reads
the same sheet live, and `--spent` what it spent):

| | Sheet (work units a tick, at rest plus one change) |
|---|---|
| One station (game-station-root, the board games lab) | 496,713 |
| Twelve stations playing themselves (the stress lab) | 3,847,956 |
| The debug world with its one station | 3,640,191 |

The tick admits 4.2 million units (`RuleCapacity.MaxWorkUnitsPerTick`). Each
station past the first adds about 305 thousand: its instance's own lines
(about 263 thousand summed, chess most of them) and its members of the
patience set's family sweeps. Every cell a firing writes is priced (about 512
units), so the holodeck's door writes two cells a body and a body at rest costs
nothing: each lifecycle and pose rule closes its own gate. The costliest chess
lines are the ones a game switch or a put-back opens for all 32 pieces at once
(the lifecycle, the pose, the tidy and the settled read, each about 17 to 34
thousand). What an idle station spends is a different number: its rules are
judged by change, so at rest they do no work. Twelve stations in the debug
world itself still wait on the sheet: the open item "Twelve game stations on
the work sheet" in
[the open items](../../docs/plans/open-items.md#card-tables-in-the-board-game-room).

## Tests

[`klondike-tests.puck`](klondike-tests.puck) and
[`freecell-tests.puck`](freecell-tests.puck) hold the patience scenarios on
stations of their own, reading only what every seeded deal shares;
`freecell-tests.puck` also stands two stations and claims a game at one never
touches the other; `klondike-tests.puck` also claims the cards stand while
Klondike is shown and are released when chess is. [`chess-tests.puck`](chess-tests.puck)
holds holodeck chess: the pieces materialize and are read as the start, a
seated player's physical move is accepted and the computer's reply lands, an
illegal one is put back, and a game switch releases the pieces. The stress
lab claims every station plays its turns. Run them with
`puck test <file> --reproduce --jobs 1`.
