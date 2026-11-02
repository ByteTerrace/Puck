# Puck games

Game rules a table imports, as libraries: each emits no document of its own,
and a world expands its templates where the rows belong. The table, its pieces,
looks, seats and presentation stay in the world that stands it.

[`chess.puck`](chess.puck) is the rules of chess: the board's game state, the
settled-board judge (captures, promotion, castling, en passant, check, the
75-move rule and repetition), the `chessInput` move door and a one-ply computer
opponent. Its header lists what a table expands and what it owns. The debug
world's table ([`../debug/chess.puck`](../debug/chess.puck), notes in
[`chess.md`](../debug/chess.md)) is the worked example.

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
