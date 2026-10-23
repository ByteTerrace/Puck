# Archived projects

This directory preserves retired implementations and their evidence. Start with
the [engine manual](../docs/README.md) for current behavior. The dated policy and
retirement records below explain how this reference material can be used.

**Owner ruling, 2026-08-08, superseding the 2026-08-02 blanket ban.** The
quarantine governs *work*, not *reading*. You are expected to READ the source
here and CITE it as prior art, and to DELETE code once live code has eclipsed
it. You may NOT improve it, fix it, build it, run it, or run its tests. Expect
its builds to break as deletions land — that is the intended outcome, not a
regression to repair.

## What is here, and why

| Tree | Was | Quarantined |
|---|---|---|
| [Puck.Post](Puck.Post/) | src/Puck.Post (former root path) | 2026-08-02 |
| `scripts/world` | `src/Puck.World/scripts/` | 2026-08-02 |
| `scripts/recording` | `src/Puck.Recording/scripts/` | 2026-08-02 |
| `Puck.BareMetal` | (never in the build) | — |
| `Puck.Platform.Switch` | `src/Puck.Platform/Switch/` | — |
| `src/`, `tests/`, `worlds/` | The same paths at the repository root | While the debug world is the one live world |

The `src/`, `tests/` and `worlds/` trees hold every world but the debug world,
each file at the repository path it had: the shipped worlds other than
`debug.puck` and its basis chain, the root `worlds/` asset packages, every
fixture world under `tests/`, and the laws, baselines and ledgers that existed
only to boot or test them. A test file split between live and quarantined laws
keeps its live half at its old path and its quarantined half here under the
same name. The
[decision](../docs/decisions/engine-design.md#one-live-world-while-the-campaign-runs)
records the ruling and the [open items](../docs/plans/open-items.md#quarantined-gates)
list the gates waiting on these fixtures.

None are in `Puck.slnx`; none are reached by the root build. Each carries a
**firewall pair** — `Directory.Build.props` AND `Directory.Build.targets`,
because MSBuild discovers the two separately and one half alone is a firewall
with a door in it. That is what makes the quarantine structural rather than a
path filter someone has to maintain: a filter that happens to exclude the right
directories reads identically to one that excludes the wrong ones.

## Retired: `Puck.Demo`

Deleted whole: every folder listed below had a live eclipsing home, so nothing
was left here to read. Each row names the symbol or document that carries the
capability today, so the claim is mechanical rather than an impression:

| Folder | Eclipsed by |
|---|---|
| `Overworld/` | The authoritative fold and its tape: `Puck.World.Server.WorldServer`, `Puck.World.Server.WorldReplayTape`. |
| `Forge/` | The shared cartridge/audio document model and the two brick compilers: `Puck.GamingBricks.Forge.CartridgeDocument`, `Puck.HumbleGamingBrick.Forge.HgbCartridgeCompiler`, `Puck.AdvancedGamingBrick.Forge.AgbCartridgeCompiler`. |
| `Town/` | The shipped world document itself, authored rather than built in code: `experimental/src/Puck.World/Assets/worlds/puck.world.json`. |
| `Camera/` | `Puck.Platform.Probes.ProbeKernel` (`src/Puck.Platform/Probes/ProbeKernel.cs`). |
| `Audio/` | `Puck.Audio` (`Puck.Audio.Mixing.AudioMixer` and the rest of `Mixing/`/`Simulation/`). |
| `BindingBar/`, `DevConsole/` | `Puck.Overlays` — `Puck.Overlays.BindingBarLayout` and `Puck.Overlays.ConsolePanelWriter`. |
| `Garden/` | The garden district's creature steering, `Puck.Physics.FixedFlockSteering`, proven by the `flock-local` canary against the shipped world. |
| `Rts/` | A placement dealt from a state row (`WorldPlacementDeal` on `Puck.World.Schema.WorldPlacement`) generalizes spawning many instances from an authored region — the scenario's one consumer. |
| `Gravity/` | `Puck.World.Schema.WorldGravity` / `Puck.World.Server.WorldGravityField` — authored gravity areas a body walks under, already live on every shipped world. |
| `Museum/` | The screen source kinds `Puck.World.Schema.WorldScreenSource.View`/`.Session` and the recursion bound `Puck.World.Server.WorldForwardingScope`. |

`AgbDebug/`, `Bench/`, `BindingBar/`'s glyph atlas, `Camera/`'s webcam sensor,
`Configuration/`, `Creator/`, `Editing/`, `SdfDebug/`, `Text/`, `Tracker/`,
`Ui/`, `World/`, and `scenarios/` named no capability with a live successor:
they retire with nothing eclipsing them — a capability that lived only there
is simply absent from `Puck.World`, with no plan bringing it over.

## Nothing here is deleted, and nothing here is alive

This is removal from the build, not from history. Read it the way you read
git history: evidence of how a problem was solved once, never a precedent that
binds, and never something to revive in place.

**Anything in these trees that must keep working belongs in a real project or a
`puck` verb.** Reviving something means rewriting it there, under the gate,
verified by running — never un-quarantining a tree.

**Retiring eclipsed code.** The deletion rides in the same squash as the
landing that eclipses it, so the evidence sits beside the removal and every
deletion line stays accounted for. Account for the deleted lines and show what live code eclipses them.

## The debt this created, deliberately not paid here

Documents and skills across the repository still cite paths that moved. Those
citations are stale and will be corrected where they live — in `docs/`,
`.claude/skills/`, and the project READMEs — never by editing anything in this
tree.

## Reference-tree rules

Read and cite `experimental/` as prior art, the way you read git history, and
delete code there once live code has eclipsed it. Never improve, fix, build or
run it, or run its tests; its builds are expected to break as deletions land.
It holds `Puck.Post`, `Puck.Bench`, both `scripts/` trees, `Puck.BareMetal` and
`Puck.Platform.Switch`, each firewalled from the root build
([experimental/README.md](../experimental/README.md)). The engine contract
`Puck.Post` covered is a live gap: `puck parity --world <file>` covers a slice of
it once a parity world exists, and none does while the campaign runs
([contributing](../docs/development/contributing.md#engine-changes-and-verification-coverage)). While the current campaign
runs, `experimental/src/`, `experimental/tests/` and `experimental/worlds/` also
hold every world but the debug world (the package `worlds/debug/`,
the one live world), each file at its old repository
path, with the fixtures, laws, baselines and ledgers that only booted or tested
them ([decision](../docs/decisions/engine-design.md#one-live-world-while-the-campaign-runs),
[quarantined gates](../docs/plans/open-items.md#quarantined-gates)). Anything there
that must keep working is rewritten as a real project or a `puck` verb. A
deletion rides in the same change as the landing that eclipses it. Documents
citing the old `tools/…`, `src/Puck.World/scripts/…` or former Post locations
are stale; correct them where they live.
