# Puck.Cli.Harness

Puck.Cli.Harness describes and stages a World run for the verbs that boot one: `WorldLaunch`, the one door every `Puck.World` process the CLI starts goes through (build resolution, the shared arguments, the GPU lease for a device World, the host admission, and the job that ends the World with the CLI), the stored World artifact and its key and closure, the offscreen leg, debug-layer output, the canary manifests, their model and assertions, the refusal census (`puck refusals`), the counters batch manifest and the shared `--gpu-jobs` option. It references no World runtime, so `puck affected` and `puck gate` read canary manifests without reaching the engine.

## Documentation

- [`puck canary`](../../docs/reference/cli.md#puck-canaryreal-world-behavioral-proofs) — the manifests and the World artifact.
- [Puck.Cli.Harness.Tests](../../tests/Puck.Cli.Harness.Tests/README.md) — the laws that hold this assembly's verbs.
- [Puck.Cli](../Puck.Cli/README.md) — the `puck` root that composes every verb assembly.
