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

[`game-stations.puck`](game-stations.puck) is the set's library: the catalog
and what a world holds for every station, expanded once over the world's list
of stations by `gameStationSet`. Every station plays one set of rules: each
rule sweeps the stations, and a station is its place `i` in the list (`$member`
in a rule, `0` for the first in the console), so a change at one station wakes
and costs that station's bindings alone
([analysis](../../docs/reference/state/analysis.md)), and twelve stations price
as one set of rules plus each station's own change ([Cost](#cost)).

- **Each station's own things**, named by its id: its table prototype (its
  paints read the world's rows at the station's place), its placement and pick
  grid (`gameStationCells<i>`, over its own topology of that name), its chess
  board's placement (`<id>-chess-board`, anchoring the grid `chessBoard<i>`),
  its piece bodies (`<id>-piece<n>`), its card bodies (`<id>-card<k>`) and its
  piles' outlines.
- **Every station's state**, a cell under its place or its member of a family of
  rows: the rows its paints and the console read (`gameStationGame`,
  `gameStationStatus`, `gameStationPickOf<i>`, `gameStationRowInkOf<i>`, the
  chess board's `chessStationBoardInkOf<i>` and `chessStationRingInkOf<i>`), its
  chairs (`gameStationChairOf<i>` and the presses beside it), its chess game
  (`chessTurn<i>`, `chessBoard<i>`, `chessPieceCellOf<i>` and the rest of
  [`../games/chess.puck`](../games/chess.puck)'s `chessGameState`), its play at
  the glass ([`../games/chess-station.puck`](../games/chess-station.puck)'s
  `chessStationState`) and its patience tables.
- **The rules**, one set at the world's top level: the patience collection
  ([`solitaire.puck`](../games/solitaire.puck)), Klondike
  ([`klondike.puck`](../games/klondike.puck)), FreeCell
  ([`freecell.puck`](../games/freecell.puck)) and the patience layout
  (`patienceSetRules`), which sweep the stations' tables
  (`forEach: solitaireTable`) or a family whose member `i` is station `i`'s
  (`forEach: patienceCardNoOf`); then the chairs
  (`gameStationRules`), chess's judge, computer and doors and the station's
  play, holodeck and glass, which sweep the stations (`forEach: gameStationOf`,
  one cell a station) or every station's pieces (`forEach: chessPieceCodeOf`,
  the piece bound to `$each` and the station to `$member`); and the patience
  rules that read the chairs (`patienceStationRules`).
- **Each station's computer** is a search job of its own, `chessAI-<id>`, over
  its own game's rows: its judge evaluates every rule for its own station's
  bindings alone (the job's `member`), and a row only another station's bindings
  read is no input of the job, so a move at one station never restarts
  another's search.
- **The holodeck** ([`../standard/holodeck.puck`](../standard/holodeck.puck)) is
  the pool of bodies a game's tokens stand as: each body an inhabited placement
  admitted by its pool's cell (one write through `holodeckShow` when the
  station's game changes) and counted by its own (through `holodeckSet`, for a
  captured piece or a queened pawn's new look), posed once it exists. Chess's
  pieces live in rows of their own (`chessShown`, `chessSince`, `chessLive`,
  `chessAt`), the cards in the default ones (`holodeckShown` and the rest), and
  each station's pool of each is keyed by its place in the list. A materialized
  body takes the population's highest free slot, so a world sizes its bodies
  for the largest token game each station hosts (`gameStationBodies`: the
  outlines and 52 cards).

Worlds that stand stations:

- [`game-station-root.puck`](game-station-root.puck) stands one station
  (id `station`, place 0) on a small floor. Boot it with
  `--world worlds/debug/game-station-root.puck`: seat 1 starts at the near chair
  (spawn `hero`) and seat 2 at the far chair (spawn `rival`).
- The gallery's board game room on level 2 ([`gallery/games.puck`](gallery/games.puck))
  plans twelve stations in two rows of six, `a1` to `a6` on the south gallery
  and `b1` to `b6` out in the projecting wing
  ([`gallery/layout.puck`](gallery/layout.puck)'s `galleryGameStations`). The
  gallery booted alone (`gallery-root.puck`) stands all twelve, places 0 to 11
  in that order, each holding its own games and its cards as a holodeck pool.
  The debug world stands only the first, `a1`
  ([`game-station-deck.puck`](game-station-deck.puck)): it numbers its bodies
  below the deck from it, so its cards stand always rather than as a pool
  (`holodeck: false`), and its chess pieces take the free slots
  `galleryPopulation` keeps. `debug.go cards` and `debug.go chess` stand the
  seat at `a1`'s near chair.
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
| Show a game | `station.game <i> chess`, `klondike` or `freecell` (the station's number, 0 for the first, as every station command takes) |
| Read every station's game and status | `world.state gameStationGame`, `gameStationStatus` (one cell a station), `gameStationRowInkOf<i>` (its menu) |
| Read who sits where and their presses | `world.state gameStationChairOf<i>`, `gameStationEdgesOf<i>`, `gameStationPointerOf<i>` |
| Read chess at the glass | `world.state chessStationCursorOf<i>`, `chessStationPickedOf<i>`, `chessStationRingCodeOf<i>` |
| Read the holodeck's pieces | `world.state chessShown` (1 while a station's pieces stand), `chessLive` (1 a piece the game wants), `chessAt` (the mark of the square each was last stood on), `chessPieceCellOf<i>` (the game's squares) |
| Read a station's turns played | `world.state chessStationPlies<i>`, `chessStationGames<i>` (games a self-playing station set up afresh) |
| Move in a station's chess | `station.chess <i> 11 27` (e2 to e4) |
| Start a station's chess afresh | `world.state.cell.set chessFreshOf<i> request 1` |
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
| One station (game-station-root, the board games lab) | 417,552 |
| Twelve stations playing themselves (the stress lab) | 1,404,013 |
| The debug world with its one station | 4,292,342 |
| The gallery booted alone with twelve stations | 3,201,686 |

The tick admits 4.4 million units (`RuleCapacity.MaxWorkUnitsPerTick`). Every
station plays one set of rules, so a station past the first adds what every
line's checks cost for its bindings and nothing for its firings: the sheet
prices a line at rest plus one change, and one change is one station's. A
line whose firing does not provably close its own gate (a Level gate that holds
fires again, such as `chess-put` waiting for the board to settle) is charged a
firing for every station, which makes those lines the set's largest. A piece
rule reads the piece's own cells (`chessPieceCellOf[$member][$each]`), which a
move of one piece reaches for that piece's binding alone. Every cell a firing
writes is priced (about 512 units), so a game switch is one firing of the
pool's door (`holodeckShow`, two cells) however many tokens the pool stands,
and a body at rest costs nothing: each lifecycle and pose rule closes its own
gate. A piece's square is read when its own body comes to rest
(`fact(placement, $each, Resting)`, a fact the host versions per placement), so
a settle reads only the pieces that moved; the en-passant check reads the
sliders' rays over an occupancy mask (`boardSlide`) rather than copying the
board. A put-back or a fresh game stands every body again by flipping one
parity the pose rule folds into each body's last pose, rather than marking each
body; the fresh-game door writes only what something reads before the judge's
first settled pass; the search's own lines (`search(ply) > 0`) are priced with
the search, not the tick; and a pose rule's arrival deadline wakes only the
bodies it is due for. What an idle station spends is a different number: its
rules are judged by change, so at rest they do no work.

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
