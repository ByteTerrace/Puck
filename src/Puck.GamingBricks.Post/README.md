# Puck.GamingBricks.Post

The battery scaffold shared by `Puck.HumbleGamingBrick.Post` and
`Puck.AdvancedGamingBrick.Post`: the pieces of each core's power-on self-test
that carry no machine-specific state, plus the generic pieces (`PostBattery<TContext>`,
`IPostStage<TContext>`, `HashDivergenceReport`) closed by each battery over its
own `PostContext`/snapshot types.

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
| `IPostStage<TContext>` | One battery stage: a name, a tier, and `Run(TContext)`. |
| `PostBattery<TContext>` | Runs an ordered `IPostStage<TContext>` list, isolating each stage's exceptions as `Infra`, into a `PostReport`. |
| `PostReport` | The folded per-stage results, exit code, and rendered table for one battery run. |
| `HashDivergenceReport` | Snapshot-hashes two machines and, on a mismatch, prints the component/offset localization and hex windows — the loop and `DescribeDivergence` stay per-brick. |
| `CommandLineArguments` | `Value(args, name)` looks up a flag's following value. |

## Not shared

Each battery's `PostContext` (artifacts directory, corpus roots, BIOS image
or console model), `PostMachine`, and stage list stay in the owning project —
they diverge per machine and forcing one shape onto two genuinely different
run contexts would cost more than it saves. Each probe's `DescribeDivergence`
(and, for the Advanced brick, its bus-subregion annotation) stays per-brick for
the same reason and is handed to `HashDivergenceReport` as a delegate.
