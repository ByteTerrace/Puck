# Boot-once hand-back

Branch: `codex/boot-once`, based on `727ecc0c9`. No push, merge, stash operation,
other worktree, whole-solution build, full-suite run or windowed World.

## Needs you

The lead owns integration and the required adversarial cross-family review.
Review the in-place authority/observer reset and restored session allocator in
particular. No product decision is waiting.

## Changed

Scenario workers keep one ordinary native host. Compatible scenarios share the
boot configuration and replace state, rules and schedule over its checkpoint.
The reset includes the boot authority cohort, observations, resolver generations,
client input producers and submission guards, router activation sequences,
transfers, schedules and persisted files. Each scenario keeps its own transcript,
exports and archived state. Configuration keys are computed from input bytes;
no derived identity is stored in source control. Unchanged companion checkpoint
admissions are reused only while their source bytes match.

`--reproduce` observes the existing tape capture independently of operator
recording, seals every authority at the completed host-step boundary, restores
each starting checkpoint in place, and replays recorded input. It checks every
tick's authority hash, final canonical export and machine cores. There is no
second native host boot or second execution of the authored schedule.

The checkpoint defects were:

- Recording captured before committing the addon restart, although restarting
  retires guest input controllers and releases their held body channels. Capture
  now preflights before commit and records the actual state after commit.
- Rule recompilation discarded scoped replay images restored before it. Those
  images now restore after their final bindings exist.
- Attached solids lost their last sampled carrier transforms, waking sleeping
  bodies on the first restored tick. Checkpoints retain that collision sample.
- In-place restoration retained future session epochs, old awake/sleeping work
  sets and pending mutations. They now replace the discarded timeline exactly.

Checkpoint envelope version is 22; tape version is 8. The latter records an
ordered addon restart when operator recording arms inside an independent
capture. No simulation hash algorithm changed. Fresh authored trajectories are
compared with the baseline; corrections affect continuation after restoration,
not the original live trajectory.

### Measured scenarios

All commands used the lane's Release CLI and the explicit built World artifact:

```text
dotnet .claude/scratch/boot-once-cli/Puck.Cli.dll test worlds/debug/<file>.puck --jobs 1 --world-artifact src/Puck.World/bin/Release/net11.0/Puck.World.dll --keep <evidence-directory>
```

The replay row also passes `--reproduce` before and after. Times include source
preparation and execution, exclude building, and are single wall-clock samples
on the shared machine. They are evidence, not a maintained performance baseline.

| File | Scenarios | Before seconds | After seconds | Native boots before → after |
|---|---:|---:|---:|---:|
| `gallery-inspect-tests.puck` | 3 | 36.86 | 46.08 | 3 → 1 |
| `input-continuity-tests.puck --reproduce` | 5 | 9.84 | 6.85 | 10 → 1 |
| `wonderland-tests.puck` | 5 | 60.20 | 64.36 | 5 → 1 |

Every before/after verdict passes. Final comparisons cover the full per-tick
authority hash traces, rather than only verdict rows. Reproduction evidence
contains `divergence: -1` for every observed authority. The physics file includes
the cross-world garden round trip and its foreign verdict.

Evidence is under this worktree's `.claude/scratch/boot-once-*`: `before-state`,
`before-replay`, `before-wonderland`, the final `accepted-state`, `accepted-replay` and `accepted-physics` directories.
The scratch directory is ignored; this report preserves the commands and results.

### Driven mid-session recording

The original idle reproduction failed at tick 0 in PopulationPose, Arena,
RuleLatch, BodyContinuation, Input and ScopedReplays. After the fixes, the driven
headless exercise below arms at tick 557 and records through tick 619:

```text
dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll --world worlds/debug --devices none --headless --unpaced --exit-after-seconds 180 --state-dir .claude/scratch/boot-once-driven-final-state
```

Standard input:

```text
world.wait 100
body.press jump 1 0.2 0
player.signal keyboard.w press
world.wait 80
player.signal keyboard.w release
body.pose 12.5 0.05 30.5 90 0 0 0
world.wait 15
body.fly 0 -1 0 0 0 0 1.5 0
world.wait 90
body.where 0
body.pose 8.8 0.05 30.5 -90 0 0 0
world.wait 15
body.fly 0 1 0 0 0 0 1.5 0
world.wait 90
body.where 0
body.pose 56.5 9.25 21.1 0 0 0 0
world.wait 150
body.press interact 1 0.2 0
world.wait 10
body.where 0
replay.record boot-once-driven
player.signal keyboard.w press
world.wait 10
player.signal keyboard.w release
body.press interact 1 0.2 0
world.wait 50
replay.stop
quit
```

Observed output:

```text
[world.transfer: transfer=0 'boot' seat 1 crossed wonderland-door/glass -> wonderland-mouse-hole/glass in place, at (9.38, 0.06, 30.49) moving (-1.25, 0.00, 0.00) m/s, scale 0.25]
[world.transfer: transfer=1 'boot' seat 1 crossed wonderland-mouse-hole/glass -> wonderland-door/glass in place, at (10.40, 0.13, 30.50) moving (5.00, 0.00, 0.00) m/s, scale 1.00]
[world.engage: seat1 auto-engaged placement:chessTable — context button]
[world.engage: seat1 left placement:chessTable — context button]
[replay.stop: wrote Replays/boot-once-driven.puckreplay | MATCH | 62 ticks | hash=0x63EDF86A7225E12D | machine cores MATCH (6) — faithful capture]
```

### Focused verification

Builds name only the changed World/CLI projects or their focused test projects,
using Release, `-nodeReuse:false -m:2`. Final builds have zero warnings/errors.
Focused classes: `TestCommandLawTests`, `SessionPrincipalLawTests`,
`ReplayAddonRestartLawTests`, `BodySleepLawTests`,
`WorldRigidSleepContinuationLawTests`, `ReplayArmingLawTests`,
`WorldScopedReplayTests`, `WorldAuthorityCheckpointCodecLawTests`.
106 server tests and 5 CLI tests passed (111 total). The CLI checks include fresh-process equivalence, pristine/changed/pristine isolation, sibling verdicts and checkpoint reproduction.

The explicit changed-file formatter and changed-document link checks were run.
Main process IDs are recorded in `.claude/scratch/boot-once-pids.txt`; worker
artifacts also include `process-id`. No unrelated process was stopped.

## Found

- The original `noclip-tests.puck` run failed the existing “in suit, noclip
  switched off in air leaves her hovering” case: `bodyPosition(body, 0, y) > 3`
  failed at tick 96. Wonderland supplies the passing physics baseline instead.
- Final wall time improved for replay (9.84 → 6.85 s), but state (36.86 → 46.08 s) and physics (60.20 → 64.36 s) regressed. Boot counts are the firm workload reduction; these samples do not establish a speedup for every file or for the full sweep. Earlier intermediate runs varied substantially on this shared machine.
- Diagnostics were removed after isolating the checkpoint faults. A shallow-definition admission shortcut passed verdicts but changed store hashes; it was rejected, and the normal admission path restored all 13 complete baseline traces. Missing NuGet
  assets were resolved with one restored build; a temporary console diagnostic
  was rejected by the Server architecture gate and removed. Neither is an
  unresolved build failure.
- The three named files are the measured scope; the full debug scenario sweep
  and windowed behavior were not run, as instructed.
