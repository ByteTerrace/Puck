# Puck.Cli.Gate

Puck.Cli.Gate holds the verbs that choose and run verification: `affected`, `gate`, `host` and `baselines`. What they read from verbs composed beside them (the source types behind each generated schema, and the grammar of each verb whose own arguments decide whether a run is GPU work) arrives as a `GateComposition` from the root, so this assembly references none of those verbs and no World runtime.

## Documentation

- [`puck affected`](../../docs/reference/cli.md#puck-affectedthe-checks-a-change-needs)
- [`puck gate`](../../docs/reference/cli.md#puck-gatethe-change-scoped-gate)
- [`puck host load`](../../docs/reference/cli.md#puck-host-loadadmission-lines-for-the-machine)
- [`puck baselines`](../../docs/reference/cli.md#puck-baselinestest-baselines)
- [Puck.Cli.Gate.Tests](../../tests/Puck.Cli.Gate.Tests/README.md) — the laws that hold this assembly's verbs.
- [Puck.Cli](../Puck.Cli/README.md) — the `puck` root that composes every verb assembly.
