# Puck Parlor

Puck's first official asset package contains physical chess, Chinese checkers,
and Hearts, each with rules and AI authored in Puck DSL. The package is this
folder and its [manifest](manifest.json); it needs no installer or NuGet package.

All three games inherit [parlor.basis.puck](parlor.basis.puck). It owns the
window, 60 Hz simulation, controls, seat rig, motion program, collision defaults,
ground, and presentation theme. Each game owns its board, pieces, rules and AI,
and refines its camera, body capacity and piece-specific physics. The basis has
no dependency on the repository's standard world or another package.

## Run and copy

Puck composes each source with the shared source basis directly. From this
directory, run any entry point without generating an intermediate document:

```text
Puck.World --world chess.puck
Puck.World --world chinese-checkers.puck
Puck.World --world hearts.puck
```

Copy this folder as-is. `manifest.json` lists the shared basis, the three source
entry points, and every supporting file. Every path is relative to this folder.
The manifest is an inventory, not an executable world; pass a game's `.puck`
source to Puck.

Use `puck compile <game>.puck --validate` only when a JSON document is needed
for a consumer that cannot load Puck DSL. Generated `.world.json` files are
ignored local output and are not part of this package.

## Play and verify

```text
puck fmt --check .
puck lint chess.puck --strict
puck lint chinese-checkers.puck --strict
puck lint hearts.puck --strict
puck compile chess.puck --validate
puck compile chinese-checkers.puck --validate
puck compile hearts.puck --validate
puck test .
```

`puck test` runs the worlds on their fixed tick grid without wall-clock sleeps,
concurrently by default, while printing results in source order. Use `--jobs 1`
to serialize a diagnostic run or `--jobs <n>` to set the worker limit. Add
`--reproduce` when qualifying determinism; it reruns each world and requires
byte-identical state and schedule output.

The CLI test suite also runs every entry point listed in `manifest.json` through
`puck test`. A package edit therefore runs its authored scenarios during normal
`dotnet test` verification, including physical settling and AI play. These
scenarios are examples, not an exhaustive independent oracle for every legal
game position.

The independent [Hearts](../../tests/Puck.World.Tests/HeartsLawTests.cs) and
[Chinese Checkers](../../tests/Puck.World.Tests/ChineseCheckersLawTests.cs)
law suites also load these package sources directly. They check legality
against separate oracles and cover physical correction, complete AI play,
and Hearts deck conservation across multiple hands.

Move pieces on the table. A settled physical arrangement is checked against the
last accepted game position; an illegal arrangement does not advance the game.
The authored tests exercise physical observations and AI through the real World.

Chess tracks captures, promotion, castling, en passant, check and game outcomes.
Chinese checkers supports adjacent moves and connected jump chains. Hearts has
four seats, deterministic deals, three-card passing, legal trick play, scoring,
shooting the moon and a match to 100; its default is one human and three AI seats.
Hearts is an open tabletop, and each AI reads only its own hand and public play.

For chess, set the `aiSide` state slot to `0` for White, `1` for Black or `-1`
for two humans. Chinese checkers uses the same slot with zero-based seat numbers
and `-1` to disable AI. In Hearts, `heartsOptions[aiMask]` selects AI seats:
`0` gives four humans, `14` the default opponents and `15` four AI seats.

The Inter font ships with its [license](fonts/Inter-LICENSE.txt).

## Manifest fields

`schema` identifies this inventory as `puck.asset-package.v1`; `id`, `name` and
`version` identify the package. `basis` names the common Puck DSL source.
`worlds` lists playable source entry points. `files` is the complete payload;
all dependencies are contained within this package folder.
