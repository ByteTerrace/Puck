# Puck games

Game rules a table imports, as libraries: each emits no document of its own,
and a world expands its templates where the rows belong. The table, its pieces,
looks, seats and presentation stay in the world that stands it.

[`chess.puck`](chess.puck) is the rules of chess: the board's game state, the
settled-board judge (captures, promotion, castling, en passant, check, the
75-move rule and repetition), the `chessInput` move door and a one-ply computer
opponent. Its header lists what a table expands and what it owns. A game
station's holodeck chess ([`chess-station.puck`](chess-station.puck), notes in
[`game-station.md`](../debug/game-station.md)) is the worked example: it stands
the pieces as bodies and lets the library's physical judge read where they
settle.

The library also judges a drawn board (`chessJudge(digital: true)`), whose
pieces are their cells alone, for a world that draws its pieces rather than
standing them. [`klondike.puck`](klondike.puck) and [`freecell.puck`](freecell.puck) are
patience as rules over piles, each one template a world expands where it wants
a game, which [`solitaire.puck`](solitaire.puck) collects and switches between.

Two libraries draw these games on the standard's game station
([`../standard/game-station.puck`](../standard/game-station.puck)) from their
own state: [`chess-station.puck`](chess-station.puck) (the board as paints on
the glass, the pieces as rigid bodies of the station's holodeck, and the chairs'
play over them) and [`patience-station.puck`](patience-station.puck) (the
deck's cards, a holodeck pool posed into the slots the piles name). The
holodeck itself is the standard's
([`../standard/holodeck.puck`](../standard/holodeck.puck)). The debug world's
station ([`../debug/game-station.md`](../debug/game-station.md)) is the worked
example.

Four hidden-information games, each the debug world's table and a fixture of
the [disclosure plan](../../docs/plans/disclosure.md)'s noninterference
harness. Each header names what a table expands, who reads which rows, and the
fixture's principals and hidden cells; each seat acts through its own sealed
inbox ([`../standard/sealed.puck`](../standard/sealed.puck)).

| Library | Game | The debug world's table |
|---|---|---|
| [`auction.puck`](auction.puck) | A sealed-bid auction of lots, the bids revealed at a fixed close | [`../debug/auction.puck`](../debug/auction.puck) |
| [`shedding.puck`](shedding.puck) | A shedding card game: a draw pile under `Count`, hands under `Placeholder`, a public discard pile | [`../debug/shedding.puck`](../debug/shedding.puck) |
| [`deduction.puck`](deduction.puck) | Social deduction: hidden roles, table talk, whispers, the moles' den and a sealed vote | [`../debug/deduction.puck`](../debug/deduction.puck) |
| [`skirmish.puck`](skirmish.puck) | A fog-of-war skirmish: hidden positions, line of sight, `knowledge` rows and two searching computer players | [`../debug/skirmish.puck`](../debug/skirmish.puck) |
