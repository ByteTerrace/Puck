# Puck Parlor

Puck's first official asset package contains physical chess, Chinese checkers,
Hearts and Lineup, each with rules and AI authored in Puck DSL. The package is this
folder and its [manifest](manifest.json); it needs no installer or NuGet package.

All four games inherit [parlor.basis.puck](parlor.basis.puck). It owns the
window, 60 Hz simulation, controls, seat rig, motion program, collision defaults,
ground, and presentation theme. Each game owns its board, pieces, rules and AI,
its local seat, and refines its camera, body capacity and piece-specific physics.
The basis declares no bodies, so it is itself a valid world and lints clean alone. The basis has
no dependency on the repository's standard world or another package.

## Run and copy

Puck composes each source with the shared source basis directly. From this
directory, run any entry point without generating an intermediate document:

```text
Puck.World --world chess.puck
Puck.World --world chinese-checkers.puck
Puck.World --world hearts.puck
Puck.World --world lineup.puck
```

Copy this folder as-is. `manifest.json` lists the shared basis, the four source
entry points, and every supporting file. Every path is relative to this folder.
The manifest is an inventory, not an executable world; pass a game's `.puck`
source to Puck.

Use `puck compile <game>.puck --validate` only when a JSON document is needed
for a consumer that cannot load Puck DSL. Generated `.world.json` files are
ignored local output and are not part of this package.

## Play and verify

```text
puck format --check .
puck lint chess.puck --strict
puck lint chinese-checkers.puck --strict
puck lint hearts.puck --strict
puck lint lineup.puck --strict
puck compile chess.puck --validate
puck compile chinese-checkers.puck --validate
puck compile hearts.puck --validate
puck compile lineup.puck --validate
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

## Lineup

Two seats each secretly hold one of twenty-four porcelain busts and take turns
asking yes-or-no questions about the other's: hair colour, eye colour, or whether
the bust wears glasses, a hat, a beard or earrings, has long hair or a large nose.
The rules answer from the other seat's hidden bust, and the asker's own gallery
lays down every bust the answer rules out. Naming a bust instead ends the game:
the right bust wins and any other loses. Seat 1 plays at the keys (arrows choose a
question and a bust, Enter asks, G names, N deals a new game once one is over);
seat 2 is the computer. Set `lineupOptions[cpuMask]` to `0` for two seats at the
keys, `2` for the computer on seat 2 (the default) or `3` for two computers.

Each bust is one attribute mask, and a question is one bit of it. The answer set
of a question is the twenty-four-bit set of busts carrying that bit, so
eliminating is one AND of a seat's standing set with the answer set or its
complement. Every pair of busts differs in at least one bit, and
[LineupLawTests](../../tests/Puck.World.Tests/LineupLawTests.cs) holds the roster
to that and holds every answer to a brute-force filter of the roster. The
computer asks the question whose yes/no split of its own standing set is closest
to half, the lowest question on a tie, and names the bust once one remains. It
names every bust within six turns.

Each seat's secret, standing set and gallery rows are readable by that seat alone:
another seat's console read of them is refused by name, and the test blocks prove
it through the real World. The rules read both sides; a seat never supplies an
answer. The galleries are drawn from shared part prototypes (a face, eight hair
styles, three eye colours, four beards, glasses, a hat, earrings and two noses),
each dealt per bust from the seat's own gallery rows, so a bust the standing set
no longer holds lies down in grey. The
[games reference](../../src/Puck.World/Assets/worlds/games/README.md#lineuphidden-busts-yes-or-no-questions-and-a-splitting-computer)
documents the console door and the proofs.

The Inter font ships with its [license](fonts/Inter-LICENSE.txt).

## Manifest fields

`schema` identifies this inventory as `puck.asset-package.v1`; `id`, `name` and
`version` identify the package. `basis` names the common Puck DSL source.
`worlds` lists playable source entry points. `files` is the complete payload;
all dependencies are contained within this package folder.
