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
| Acquire and admit leased work | `LeasedAdmission.AcquireAsync` in `Puck.Cli.Core` | `LeaseCommand` and `WorldLaunch` share GPU preflight, unreserved before acquisition, then bounded reservation under the hold and release before retry. Heavy work admits with its lease held. The total caller deadline includes acquisition and admission. |
| Sample and judge host headroom | `HostAdmission`, `HostProbe`, `HostStartTracker` in `Puck.Cli.Harness/Host` | `WorldAdmission` installs real admission by default, including standalone `Puck.Cli.Worlds`. `Install` is a test override; no entry point needs to remember an install call. |
| Turn captured processes into gate outcomes | `GateStepResult.Captured` | Gate execution and lock restore preserve merged stream order, refusal exit on timeout, and one timeout annotation. |

The framework-facade MSBuild task runs before Puck projects build; a project
reference to the runtime CLI would create a bootstrap dependency and target the
wrong runtime. Shared source is intentional. Add a new shared build source to
the task's compiler inputs and its assembly-cache identity together.

Admission's guarantee stops at a lease share: releasing one `WorldGpuHold`
share does not release another live start's share. Cross-process growth
reservations also remain process-local; machine leases serialize device and
heavy starts. Do not describe those reservations as machine-wide exclusion.

This map earns its place by naming the cross-assembly homes and distinctions a
name search misses: declared versus resolved references, assembly versus build
edges, compiler dependencies versus Puck-only warmup, and bootstrap versus
installed CLI execution. Update it with a move or a new caller.
