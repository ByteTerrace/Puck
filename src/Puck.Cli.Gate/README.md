# Puck.Cli.Gate

Puck.Cli.Gate holds the verbs that choose and run verification: `affected`, `gate`, `host`, `baselines` and `locks`. They reference none of the verbs composed beside them and no World runtime.

## Documentation

- [`puck affected`](../../docs/reference/cli.md#puck-affectedthe-checks-a-change-needs)
- [`puck gate`](../../docs/reference/cli.md#puck-gatethe-change-scoped-gate)
- [`puck host load`](../../docs/reference/cli.md#puck-host-loadadmission-lines-for-the-machine)
- [`puck baselines`](../../docs/reference/cli.md#puck-baselinestest-baselines)
- [`puck locks`](../../docs/reference/cli.md#puck-locksrestore-lock-files)
- [Puck.Cli.Gate.Tests](../../tests/Puck.Cli.Gate.Tests/README.md) — the laws that hold this assembly's verbs.
- [Puck.Cli](../Puck.Cli/README.md) — the `puck` root that composes every verb assembly.
