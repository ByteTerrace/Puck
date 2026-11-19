---
name: tooling
description: "Maps shared build/CLI architecture, compiler assembly identity, candidate installation, generated sections, and host admission."
---

# Build and CLI tooling

Load [verification](../verification/SKILL.md) for builds, private CLI copies and
leases, and [symbol-analysis](../symbol-analysis/SKILL.md) for finding callers.
[CI and releases](../../../docs/development/ci.md) owns repository automation;
the [CLI reference](../../../docs/reference/cli.md) owns verb behavior.
File publication, paths, Git inventories, project ownership and Markdown parsing route to
[tooling-files](../tooling-files/SKILL.md).

| Capability | Shared home | Callers and boundary |
|---|---|---|
| Parse project declarations and evaluate architecture policy | `ArchitectureModel` in `build/ProjectArchitecture.cs` | The build task and `Puck.Cli.Core` compile this source. MSBuild supplies evaluated policy and resolved references; `ArchitectureCommand` supplies declared facts. Keep resolved-route enforcement (`PUCKARCH006`) in `PuckArchitectureGate`. A declared report cannot prove resolved references. |
| Walk project dependencies | [ArchitectureGraph](../../../build/ProjectArchitecture.cs) | Architecture closures follow assembly edges; affected and shader selection follow all build references. Choose the edge set; share the walk. |
| Traverse assemblies for derivation identity | `AssemblyReach.Of` | The default follows `Puck.*`; `WorldCompileCache` includes all non-framework compiler dependencies and records missing trimmed modules. `WorldCodeWarmer` uses the same walk. Keep identity folds with their derivations. |
| Splice generated document sections | `CliGeneratedSection.TryReplace` | Packages and the architecture map preserve delimiters and surrounding text, then use `CliGeneratedFile.WriteOrCheck`, including index agreement. |
| Install an isolated CLI candidate | `CandidateCliInstall` in `build/CandidateCliInstall.cs` | Compiled into `Puck.Cli.Release` and run as a file-based bootstrap by `setup-puck`, before a CLI exists. One exclusive feed, private package cache, fresh tool path and version check. The CLI supplies `CliProcess`; bootstrap `Main` supplies process I/O only. |
| Acquire and admit leased work | `LeasedAdmission.AcquireAsync` and `CliLease` in `Puck.Cli.Core` | Heavy work and device Worlds park their ticket during unleased admission, then reserve with a zero wait under the grant guard. Lost headroom parks the same ticket without serving the lane. `WorldLaunch` owns live worker holds, shared through `WorldWorkHold`. The total caller deadline includes acquisition and admission. |
| Bound a measurement window | `OperationDeadline` in `Puck.Abstractions`, owned by `MeasureCommand` | One clock and cancellation lifetime covers acquisition, settling and draining. `MeasureCommand.DrainAsync` checks it before each request and sample, and incomplete requests remain pending. `CliProcess.RunCapturedAsync` stops and reaps the active child tree before the quiet hold is disposed. |
| Recover abandoned lease metadata | `CliLease.PruneReleased` in `Puck.Cli.Core` | Inspection and quiet-priority checks share cleanup under the resource's exclusive guard. Acquisition already replaces metadata once it owns that same guard; process identity and metadata age never override a free guard. |
| Pause a suite budget while waiting for another run | `SuiteBudget` in `Puck.Cli.Core` | Sweep, motion, frame benchmarks and offscreen legs share remaining-time calculation, cancellation and scoped pauses. Offscreen callers lend their existing suite stopwatch. Canary's concurrent leg context already uses `CliProcess.RemainingBudget`; it does not pause a shared clock while other legs run. |
| Enumerate the live console vocabulary across boot shapes | `WorldConsoleVerbs.TryEnumerate` in `Puck.Cli.Harness` | `verbs` and `docs citations` read the structured `world.affordances` manifest. The catalog excludes authored commands; citation checking includes them. |
| Sample and judge host headroom | `HostAdmission`, `HostProbe`, `HostStartTracker` in `Puck.Cli.Harness/Host` | `WorldAdmission` installs real admission by default, including standalone `Puck.Cli.Worlds`. `Install` is a test override; no entry point needs to remember an install call. `HostStartTracker.Expected` is the one expected-peak door: the high percentile of `HostPeakWindow`'s last ten observed processes of the kind, persisted per kind under `HostStartTracker.ObservationDirectory` in the lease root, where the file decides and a process adds only what it observed since its last write; one outlier session never ratchets it, and an estimate the machine could not hold even with every admitted start gone judges by the lesser of itself and the kind's fallback (`HostStartTracker.Lone`), so a refused kind can still run and teach the window a lower figure. Each observation carries private bytes (judged against commit), private working set (judged against free RAM; `ProcessInspection.PrivateResidentBytes` reads it for another process) and whole working set (reported only, since its clean mapped pages are reclaimable). A change to those lines renames the directory and raises `CliLease.Protocol`. `HostLoadThresholds.NeverFits` names a start the idle machine could not hold, and `HostAdmission.Wait` refuses it at once. |
| Turn captured processes into gate outcomes | `GateStepResult.Captured` | Gate execution and lock restore preserve merged stream order, refusal exit on timeout, and one timeout annotation. |
| Move every external dependency by one rule and report moves and holds | `DepsCommand` in `Puck.Cli.Gate/Deps`, one `*Dependencies` class per ecosystem over `DependencyContext` | NuGet reuses `LockFiles.Record` with `update`. `NuGetDependencies` restores file-app moves before accepting them and retries a failed batch one move at a time. `DependencyFiles` puts every touched file back for `--check` and a failed run. A new ecosystem is a class beside them and a name in `DependencyEcosystems`, never a second verb. `deps.yml` orchestrates `update` then `submit` ([CI](../../../docs/development/ci.md#dependency-updates)); GitHub REST calls go through `CliGitHub`. |
| Read published package versions | `NuGetFeed` and `DependencyVersion` in `Puck.Cli.Core` | The dependency updater and NuGet release preparation share one NuGet flat-container reader and retry policy. `DependencyVersion` orders versions for NuGet, npm and Git tags. |
| Re-record lock files without advancing pinned packages | `LockFiles.Record` and `LockPins.Hold` in `Puck.Cli.Gate/Locks` | The restore evaluates the graph afresh (current project closure, every floating range at the newest package); `Hold` takes each package back to the version its lock file pinned wherever the project asks for it the same way, and a locked restore judges the set. `--update` skips the hold. |

The framework-facade MSBuild task runs before Puck projects build; a project
reference to the runtime CLI would create a bootstrap dependency and target the
wrong runtime. Shared source is intentional. Add a new shared build source to
the task's compiler inputs and its assembly-cache identity together.

An admission beneath an executing lease (this process or an ancestor) is
nonwaiting: a failed reading refuses the new start without disturbing live
work. `WorldWorkHold` admits each joining start this way; one share cannot
release another live start's hold. Cross-process growth
reservations also remain process-local; machine leases serialize device and
heavy starts. Do not describe those reservations as machine-wide exclusion.

This map earns its place by naming the cross-assembly homes and distinctions a
name search misses: declared versus resolved references, assembly versus build
edges, compiler dependencies versus Puck-only warmup, and bootstrap versus
installed CLI execution. Update it with a move or a new caller.
