# Test worlds

Authored worlds whose own documents carry the whole test: a `schedule` section
says what to submit and when, and `verdict`-marked `state` rows say what the
rules must conclude. `puck test` boots each one through the real `Puck.World`
executable, headless, and reads the verdicts back out of the state export the
world writes at its declared export tick.

Beside them, `sources/` holds the `.puck` half: a world whose behaviour is
written as a `test` block rather than as a hand-authored schedule and verdict
row, which is how a world's own behaviour is normally stated. `puck test`
compiles the source and boots the world its test generated.

```sh
puck test tests/Puck.World.Verdicts/phase-advance.world.json --keep out
puck test tests/Puck.World.Verdicts/sources/seat-writes-a-cell.puck
```

Both worlds are a basis delta over the shipped `games/hearts.world.json`, so the
rules under test are the shipped game's own; each adds only its verdict rows,
the rules that decide them, its schedule, and the `grants` its scheduled seat
needs. Hearts is a whole bootable document with no seats and no bodies, which is
why a delta over it needs nothing else.

Each scheduled row acts as `seat1`, never as `console`: the console principal is
trusted at every gate, so a step acting as it would prove nothing about
authority. What `seat1` may do is the two authored grants — `Mutate` over
`section:state` and `Edit` over `all` — and removing either turns both worlds'
scheduled commands into recorded refusals.

| World | Proves |
|---|---|
| `phase-advance.world.json` | A scheduled command submits a guarded transform (`world.state.act`) that advances `heartsPassPhase`'s generation — the claim neither Stratego's nor Hearts' baseline sequence could make, because the baseline runner issues raw `UpsertStateCell` and nothing else. Its second verdict is deliberately unproven, so the failure path is exercised on every run: nothing is scheduled against `heartsTrickPhase`, so no firing ever writes its status and the verdict reads "never evaluated" against that gate while still reporting the generation it saw. |
| `sources/seat-writes-a-cell.puck` | A `test` block generating its own world: a `given` cell, a seat step, and an expectation the run passes. The green half of the construct's pair; `proofs/wrong-expectation.puck` is the red half. |
| `sources/every-kind-is-given.puck` | A `given` line writes a Fixed, a Bool and a Text row as an authored cell of that kind would, and an expectation over a Fixed or a Bool row records what its gate read in a witness row of that kind, which the report lists beside the ints (`saw=[speed=2.25]`, `saw=[open=false]`). |
| `refused-command.world.json` | A guarded transform naming the wrong generation is refused, and a transform naming a row that does not exist is refused, and the phase never advances. The verdict asserts the refusal's consequence; the refusals themselves are recorded in the run's `schedule.json` manifest under `echoes`, which `puck test` prints. |

A verdict row's status cell is authored at `0` and only a rule effect writes `1`
or `2`. The effect door stamps the firing's tick into the row's reserved
`$firedTick` cell, and `puck test` judges a status with no stamp as "never
evaluated" whatever it reads — so a status a cell trait accrued into, or a
scheduled command wrote, cannot pass. Every other door refuses the write by
name.

A failing verdict is **sticky**: once a rule writes `2`, later firings are
absorbed, so the export carries the first failing tick and the values the gate
saw then. That decides the authoring shape — write the expectation at the tick
it becomes decidable, never a pre-emptive `else` that fails before the schedule
has run, or the verdict settles on that first tick and can never pass. A gate
that never becomes decidable leaves its verdict at "never evaluated", which
fails.

`puck test` over this whole directory exits **1**, by construction:
`phase-advance.world.json` carries a verdict that is meant to fail so the
failure path is exercised on every run. The green form is
`puck test tests/Puck.World.Verdicts/refused-command.world.json`, which exits 0.
`TestCommandLawTests` pins both, which is what makes the failing half an
assertion rather than a nuisance.

`phase-advance.world.json` is what a later `.puck` `test { given when expect }`
block lowers to, member for member: `when` becomes `schedule.rows`, and each
`expect` becomes one verdict row plus the rule that decides it.
