# Verify state changes

This article helps you choose and run the checks that cover a change to the
state system or to state-driven world content. It maps each kind of change to
the check that exercises it, explains what each check proves, and lists the
parts of the contract that no automated check covers today.

Every command in this article runs from the root of the repository.

## Choose a check

Start from what you changed. Most changes need more than one row.

| You changed | Run | What it proves |
|---|---|---|
| A type or behavior in a `Puck.State*` library | The project's [law suite](#run-the-library-law-suites) | The library's own contracts over a headless arena, with no world involved. |
| Anything that could change what an authored `.puck` source means | The [author-expression corpus](#check-that-authored-sources-still-mean-the-same-thing) | Every shipped source compiles, decompiles, and recompiles to the same document. |
| A world's state, rules, or schedule | That world's [`test` blocks](#test-a-world-with-its-own-verdicts) through `puck test` | The rules reach the conclusions the world states about itself. |
| Engine behavior that shipped games rely on | The [shipped-world state baselines](#compare-shipped-worlds-against-their-baselines) | What moved in each game's state after a fixed input sequence, as a reviewable diff. |
| Behavior you can observe from the running `Puck.World` executable | The [canaries](#run-the-canaries) | Real-executable behavior, with a second leg that shows the proof can fail. |
| Anything that reaches the state hash on a GPU path | [`puck parity`](#compare-state-hashes-across-backends) | Both GPU backends reach the same simulation-state hash at each capture. |
| The cost schedule or its reference kernels | [`puck bench state-evidence`](#replay-the-cost-evidence) | The pinned instruction evidence still matches. |

> [!IMPORTANT]
> Determinism in Puck means reproducibility at a fixed code version: the same
> document and the same input produce bit-identical state on every run, machine,
> and backend. It doesn't mean state hashes stay the same across code changes.
> When you deliberately correct a calculation or a rule, hashes are expected to
> move. Re-record every baseline, replay, and expectation the correction
> invalidates in the same change, and never keep a wrong result to hold a hash
> steady.

## Run the library law suites

Each state library has its own test project. The suites build a small arena in
memory, run the library against it, and compare the result with a value worked
out independently beside each case. None of them boots a world or needs a GPU.

```powershell
dotnet test tests/Puck.State.Tests/Puck.State.Tests.csproj -c Release
dotnet test tests/Puck.State.Rules.Tests/Puck.State.Rules.Tests.csproj -c Release
dotnet test tests/Puck.State.Topology.Tests/Puck.State.Topology.Tests.csproj -c Release
dotnet test tests/Puck.State.Generators.Tests/Puck.State.Generators.Tests.csproj -c Release
dotnet test tests/Puck.State.Vectors.Tests/Puck.State.Vectors.Tests.csproj -c Release
dotnet test tests/Puck.State.Search.Tests/Puck.State.Search.Tests.csproj -c Release
```

| Suite | Covers |
|---|---|
| [Puck.State.Tests](../../../tests/Puck.State.Tests/README.md) | The data model, the arena and its journal scopes, `CellValue` kind admission, records and pools, expressions and functions, reductions, topologies, live zones, scheduling, and the cost schedule's evidence manifest. |
| [Puck.State.Rules.Tests](../../../tests/Puck.State.Rules.Tests/README.md) | The rule compiler, atomic firing and savepoints, deferred arms, Level and Edge modes, rule groups, every compile-time refusal, and every transform kernel. |
| [Puck.State.Topology.Tests](../../../tests/Puck.State.Topology.Tests/README.md) | Board queries, rays, and pattern words over lattice arenas. |
| [Puck.State.Generators.Tests](../../../tests/Puck.State.Generators.Tests/README.md) | Generator families, draw-site cursors and masks, and Penrose patches. |
| [Puck.State.Vectors.Tests](../../../tests/Puck.State.Vectors.Tests/README.md) | The vector transforms, their refusal codes, and rollback of a refused transform. |
| [Puck.State.Search.Tests](../../../tests/Puck.State.Search.Tests/README.md) | Candidate scopes, suspension and checkpoints, chance nodes, tree search, max-n, and judge admission. |

Several suites also check that the tick path allocates nothing once it has
warmed up. If one of those cases fails, the change added an allocation to a hot
path, even when every value is still correct.

The world document's own converters and validators are covered by
`tests/Puck.World.Schema.Tests`, and game-level behavior that boots a server is
covered by the [World tests](../../../tests/Puck.World.Tests/README.md).

## Check that authored sources still mean the same thing

You're free to change any C# type and the shape of the compiled document. What
an author wrote must keep its meaning. The author-expression corpus checks that
every `.puck` source the repository ships compiles cleanly, decompiles, and
recompiles to the same document, and that every shipped world source compiles
to the document beside it:

```powershell
dotnet test tests/Puck.State.Rebuild.Corpus -c Release
```

The corpus drives the transpiler in process, so it needs no running world. Its
[README](../../../tests/Puck.State.Rebuild.Corpus/README.md) lists the sources
it reads and the one source it excludes. The same project generates
[the corpus inventory](../../../tests/Puck.State.Rebuild.Corpus/inventory.md), a count of every construct
those sources use, and fails when the committed page disagrees with the corpus or a game package's claimed
construct is missing from its own source.

## Test a world with its own verdicts

The usual way to state what a world's rules should do is a `test` block in the
world's `.puck` source. The block schedules commands at exact simulation ticks
and names `verdict` rows that the rules must fill in. `puck test` boots the real
`Puck.World` executable, headless and unpaced, and reads the verdicts back from
the world's state export:

```bash
puck test tests/Puck.World.Verdicts/sources/seat-writes-a-cell.puck
```

Add `--reproduce` to run each world twice and compare the two exports byte for
byte. [Test a world](../../authoring/testing-a-world.md) teaches the block
syntax, and the [`puck test` reference](../cli.md#puck-testtest-worlds) covers every option.

## Compare shipped worlds against their baselines

`tests/Puck.World.Tests/ShippedWorldStateBaselines` holds one canonical state
export per shipped game, taken after a fixed scripted input sequence, together
with the sequence and the game's per-tick work budget. The baselines are
evidence for review: after a change, you re-record them and read what moved.

```powershell
dotnet test tests/Puck.World.Tests -c Release --filter "FullyQualifiedName~ShippedWorldStateBaselineTests"
```

To re-record, run `puck baselines state`, and `puck baselines state --check`
to compare without writing. The verb runs the baseline tests twice and refuses
to write if the two runs differ, so a nondeterministic world fails the
recording. The [baseline README](../../../tests/Puck.World.Tests/ShippedWorldStateBaselines/README.md)
describes each file. The README also
explains which parts of the authoritative hash the export shows as values and
which it shows only as digests.

## Run the canaries

A canary launches the real `Puck.World` executable, drives it through its
console, and checks what it prints. Every canary has two legs: a positive leg
that must show the behavior and a discriminating leg that must not, which proves
the check can fail. These canaries exercise state directly:

| Canary | What it covers |
|---|---|
| `tabletop-state` | Atomic tabletop and tactics state: move costs along a path, ray flips, zone transfers, and phase-guarded actions, with a replay that must match. |
| `records-pools` | Claiming, releasing, and reclaiming a pool instance, with a replay. |
| `state-cycle-trait` | Cycle traits turning rows with the tick, reading back the same values at every fence. |
| `push-ray` | A pool-token push along a ray from a live origin. |
| `edit-state-backed-row` | A live row edit whose position names a state cell, with the world still running after the edit. |

Many canaries also record a replay and verify that it plays back exactly.

```bash
puck canary --list
puck canary records-pools tabletop-state
puck canary
puck canary --merge
```

With no selection, `puck canary` runs the automatic set, the canaries that run
headless with no special hardware. That is not every proof a merge needs: the
offscreen GPU canaries require the `gpu` capability and never run in it. Before
you merge, run `puck canary --merge`, which runs the automatic set and every
canary requiring `gpu` together, rather than a hand-picked few.
See the [`puck canary` reference](../cli.md#puck-canaryreal-world-behavioral-proofs) for selection options and exit
codes.

A canary's script states its timing in ticks with `world.wait`. The World runs
the lines before the first wait before its first tick, and the line after a
wait runs before the next tick, so a busy machine doesn't change what a canary
observes.
Canaries run several World processes at once. On a machine too busy to finish
a leg before its manifest's `timeoutSeconds`, the leg is killed and reported
as a failure. Use `puck canary --jobs 1` to run the legs one at a time.

## Compare state hashes across backends

`puck parity` boots the authored parity world offscreen, once on Vulkan and once
on Direct3D 12. At each scheduled capture, it checks that the frame has real
content, that the two backends reached the same simulation-state hash bit for bit,
and that the pixels agree within the per-tile thresholds in the world's
contract. The state-hash verdict is the part that concerns the state system:
simulation state must never depend on the GPU backend. The check needs both
backends. See the [`puck parity` reference](../cli.md#puck-paritycross-backend-parity-over-the-authored-parity-world) and the
[parity world](../../../tests/Puck.Parity/README.md).

## Replay the cost evidence

The rule work budget prices operations with a reference schedule whose evidence
lives in `src/Puck.State/ReferenceSchedule.json`. When you change a reference
kernel or the schedule, replay the pinned instruction evidence:

```bash
puck bench state-evidence
```

[Rule analysis, scheduling, and work budgets](analysis.md) explains what the
schedule prices, and the [`puck bench state-evidence` reference](../cli.md#puck-bench-state-evidence)
covers the capture procedure.

## Know what isn't covered

No single check covers the whole state contract, and a few areas have no
automated check at all:

- **Cross-version stability** isn't a goal, so nothing checks it. Baselines and
  replays prove reproducibility at one code version.
- **The complete engine contract** (every document schema, every operation on
  every host) has no end-to-end gate. The law suites cover each library's own
  contracts, the canaries and test worlds cover the behaviors they name, and
  `puck parity` covers the scenes its world authors.
- **Replay hashes** cover only the explicitly hashed state trajectory. Undo
  histories, checkpoint restore, and journals need their own assertions, which
  the law suites provide for the cases they name.
- **Heuristic work units** are exact and deterministic, but they aren't measured
  CPU time. No check establishes a wall-clock latency guarantee.

When your change touches one of these areas, say in your change description
what you verified and what remains unverified.

## Next steps

- [Contributing to Puck](../../development/contributing.md#verification): the
  verification expectations for every kind of engine change.
- [State in Puck.World](worlds.md): how a world hashes, checkpoints, and replays
  its state.
- [State and rules overview](../state.md): return to the map of the state
  manual.
