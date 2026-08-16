# Puck.GamingBricks.Post

The battery scaffold shared by `Puck.HumbleGamingBrick.Post` and
`Puck.AdvancedGamingBrick.Post`: the byte-identical pieces of each core's
power-on self-test that carry no machine-specific state.

The [project map](../../docs/project-map.md) shows where it sits in the wider
repository; the [generated API reference](../../docs/api) owns complete member
signatures, parameters, return values, and exceptions.

## 🧱 Core types

| Type | Purpose |
|---|---|
| `PostVerdict` | The outcome class of one stage: `Pass`, `Skip`, `Fail`, `Infra`. |
| `PostTier` | The ordered fast→slow tier (`A`/`B`/`C`) a stage belongs to. |
| `PostStageOutcome` | A verdict plus a one-line detail — what a stage returns. |
| `PostStageResult` | A stage's name, tier, and outcome — one report row. |
| `CommandLineArguments` | `Value(args, name)` looks up a flag's following value. |

## Not shared

Each battery's `PostContext` (artifacts directory, corpus roots, BIOS image
or console model), `IPostStage`, `PostBattery`, `PostReport`, `PostMachine`,
and stage list stay in the owning project. They diverge per machine and are
not duplicated here — extracting them would mean forcing one shape onto two
genuinely different run contexts.
