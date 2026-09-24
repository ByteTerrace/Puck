# Puck Familiars

A creature-taming game, written entirely in Puck DSL. Wild familiars roam the
world. The player catches, tames or befriends them, keeps up to six in a stable,
and fights beside one at a time in real time. The game has one player and no
computer opponent; wild familiars only fight back.

The rules are written once and carry no setting of their own, so another game
can take them and supply its own creatures, names and balance. The package is
this folder and its [manifest](manifest.json).

- [rules.puck](rules.puck) is the rules module. Every name a player reads and
  every number that balances the game arrive as its arguments: elements, the
  effectiveness chart, seasons, statuses, moves, species and tuning. Its header
  lists the orders the player gives and the answers they get back.
- [seasons.puck](seasons.puck) is the default ruleset, `module seasons()`. It
  documents every tuning value.
- [familiars.puck](familiars.puck) is the playable world. It uses the default
  ruleset and holds the package's tests.

## The default ruleset

There are twelve elements. Each one is a season and a form:

| | Stone | Stream | Spark |
|---|---|---|---|
| **Spring** | Root | Rain | Swarm |
| **Summer** | Dune | Tide | Blaze |
| **Autumn** | Husk | Mire | Gale |
| **Winter** | Ice | Snow | Aurora |

Two rules decide almost every matchup:

- Within a season, the forms beat each other in a circle: Stone blocks Stream,
  Stream douses Spark, and Spark shatters Stone.
- Within a form, each season overcomes the season before it. Opposite seasons
  are rivals and hit each other hard both ways.

An element resists itself, and every other pairing is neutral. Four immunities
are the exceptions: Tide takes nothing from Blaze, Aurora nothing from Gale,
Root nothing from Ice, and Mire nothing from Tide. Every element hits three
elements hard, resists three, and is hit hard by three. A familiar can have two
elements, and a hit on it multiplies both.

The season clock turns every three minutes. A move whose element is in season
hits a quarter harder, and foraging mostly finds in-season species. Each of the
eighteen species joins the player in only some ways: by catching, taming or
befriending.

## Play

The world has no window yet: `Puck.World --world familiars.puck` runs it
headless and reads console lines from standard input. The player gives orders
through the console: set a verb and a target on `famOrder`, then add one to its
request. This forages for a wild familiar:

```text
world.state.cell.set famOrder verb 12
world.state.cell.set famOrder request 1 add
world.state famRole
```

The order is judged on the next tick, and `famOrder[result]` holds the answer.
The verb list and the answer codes are in the header of [rules.puck](rules.puck).

## Use the rules in another game

Import [rules.puck](rules.puck) and stamp the module with your own data:

```puck
import "rules.puck"

use rules(elements: myElements, chart: myChart, seasons: mySeasons, statuses: myStatuses, moves: myMoves, species: mySpecies, tuning: myTuning)
```

The arguments are described at the top of [rules.puck](rules.puck). To keep
the default creatures and change only some balance, import
[seasons.puck](seasons.puck) instead and pass its constants, such as
`species` and `chart`, alongside your own. Constants are shared across the
files a source imports, so name yours so they don't clash with the ones those
files declare.

## Run and verify

```text
puck format --check .
puck lint familiars.puck --strict
puck test familiars.puck --reproduce
```

`puck test` boots one headless world per test. The tests cover the chart,
exact damage for strong, immune and in-season hits, statuses, the player's
strike and cooldown, catching, taming and what breaks it, befriending, loyalty
and disobedience, fainting and revival, defeats, the season clock and foraging.
`--reproduce` runs each world twice and requires identical exports.
