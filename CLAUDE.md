# Puck agent rules

Puck is a C# engine for document-defined worlds. `Puck.World` composes the
simulation, presentation and hosted machines. This file holds the rules every
agent task follows. [The engine manual](docs/README.md) explains concepts,
[development guidance](docs/development/README.md) explains procedures, and the
skills under `.claude/skills/` hold each area's settled contracts and agent
procedures: load the one that owns an area before working in it (rule 8).
Rule 2 applies to this file as well: when it argues against a change you were
asked to make, it is stale, and you correct it in the same change.

Use forward slashes for Puck paths on every platform, and follow the
[file-path convention](docs/development/contributing.md#file-paths) at output,
storage and native interop boundaries.

## Orientation

| Doc | Answers |
|---|---|
| [docs/project-map.md](docs/project-map.md) | What each `Puck.*` project is for and how they layer. Its layering block is generated (`puck architecture --map`) and checked (`--check`); never hand-edit it. |
| [docs/development/contributing.md](docs/development/contributing.md) | How to find code and verify a change, hardware and toolchain cautions, conventions. Read it before touching GPU or emulator code. |
| [docs/overview.md](docs/overview.md), [docs/architecture/README.md](docs/architecture/README.md) | What the engine does and how its runtime boundaries fit together. |
| [docs/architecture/worlds.md](docs/architecture/worlds.md), [src/Puck.World.Server/README.md](src/Puck.World.Server/README.md) | How worlds are represented and run: documents, authoritative simulation, the server. |
| [docs/plans/README.md](docs/plans/README.md) | Proposed engineering work, including [runtime and delivery](docs/plans/runtime-and-delivery.md) and [machines and cartridges](docs/plans/machines-and-cartridges.md). |
| [docs/game/README.md](docs/game/README.md), [src/Puck.World/README.md](src/Puck.World/README.md) | The reference game's design and play programme, and the World's current commands. Read them only for game work. |
| [docs/decisions/engine-design.md](docs/decisions/engine-design.md) | Settled engine design decisions, including [configuration and sessions](docs/decisions/engine-design.md#configuration-and-operations-remain-discoverable). |
| [docs/reference/input.md](docs/reference/input.md) | Controller input (`src/Puck.Input`): architecture, feature matrix, hardware status. |
| [docs/development/documentation.md](docs/development/documentation.md) | Prose, titles, filenames, README ownership and navigation. The `documentation` skill owns agent-side verification. |

Docs state current behavior, limitations and open work in the present tense.
They name no dates and no commit SHAs; verification evidence goes in the commit
message. Update incoming links and routing, agent routing included, in the
change that moves a document.

## Enforcement

The build, the architecture gate, `VerifiedCode` brands (rule 7), calibrated
ceilings, determinism checks and two ratchet ledgers are enforced. In a ratchet
ledger a recorded per-file count may only fall: `FileLengths.json` (LEN001 to
LEN004, `puck lengths`) allows no source file over 2000 lines unless already
recorded, and `CommentSmells.json` (SMELL001 to SMELL004, `puck comment-smells`)
allows no comment smell in a file it does not record. The
[CLI reference](docs/reference/cli.md#puck-lengths-and-puck-comment-smellsratchet-ledgers)
owns their rules.

`Puck.Post` is quarantined in `experimental/` and out of the build: do not cite
it as a gate, run it, or write a stage for it. The engine contract it covered is
a live gap, except the slice `puck parity` covers
([contributing](docs/development/contributing.md#engine-changes-and-verification-coverage)).
Presentation-only float and artistic work are outside the simulation-state
determinism contract.

Enforcement covers observable behavior (pixels, hashes, parity, determinism) and
says nothing about the design being settled. Every name, shape, document and ABI
is free to change. A gate that fails because a deliberate correction moved a
hash is re-recorded in the same change, never worked around. A label calling
something frozen, closed or versioned describes it today; it is never a reason to
leave it that way (rules 2 and 5).

## Core rules

1. **Split `Puck.*` projects only.** `src/Puck` and `src/Puck.Avatars` exist
   only in git history. Never reference those paths.
2. **The current instruction outranks every artifact.** Docs, skills, gates,
   comments and precedent are evidence, not law. If one argues against a change
   you were asked to make, it is stale: update it in the same change rather than
   watering the change down. Gates prove observable behavior, never internal
   structure.
3. **The game is greenfield.** `Puck.World` and everything under
   `src/Puck.World/` are expected to churn and are never settled precedent.
   Verify game changes by running `Puck.World`
   (`dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2`;
   0 or less runs until the window closes). `puck canary` gates game behavior
   only by launching that real executable and observing its stdin, stdout and
   stderr. Never substitute a build-only gate, add a `--validate-*` flag, or add
   a Post stage for a game feature. The shared engine contract (cross-backend
   render path, SDF VM ISA, document schemas, deterministic numerics) has no
   automated gate beyond `puck parity`; say plainly what stays uncovered.
4. **Determinism pins the mapping, not the values.** No wall clock, RNG or float
   in simulation state; input becomes per-tick `CommandSnapshot`s; fixed-point
   math comes from `Puck.Maths`. Same document and same input give bit-identical
   state on every run, machine and backend at a fixed code version. A deliberate
   correction to math or logic is expected to change state hashes: make it,
   re-run the gates it reaches (`puck test --reproduce`, `puck parity`, the
   canaries, the emulator batteries) to prove determinism still holds, and
   re-record the persisted replays and baselines it invalidates in the same
   change. The gates compare runs with each other and pin no historical values. Never
   preserve a wrong result to keep a hash, and never add a path that reproduces
   old-wrong behavior.
5. **Zero consumers, zero legacy.** Nothing outside this repository consumes
   Puck. Backwards compatibility is a non-goal; never raise it or let it shape a
   change. Rename, reshape and delete freely, updating every internal caller in
   the same change. No compat aliases, deprecation steps, migration shims or
   read-side tolerance for retired data shapes: migrate data once and delete the
   old path.
6. **Merges follow the branch's owner.** A lane merges into the integration
   branch its lead's brief names, and the lead merges there; a delegated agent
   merges only when its brief says to. `main` is the owner's: never merge,
   squash or push onto it unless the owner asks, because a push to `main`
   deploys to Azure.
7. **Branded code is settled; changing it is deliberate.** A member carrying
   `[VerifiedCode("id", …)]` has been proven correct over its whole input space.
   `VerifiedCode.json` seals the source that proof read: the member's
   declaration plus the declarations its entry lists under `dependencies`, one
   level deep and listed by hand, so the list is part of what a re-verification
   decides. An edit inside the seal fails the build with VER001, quoting the new
   hash. If the change is right, re-establish the brand's `basis` (`exhaustive`,
   `exact-by-construction`, or `exact-by-proof`, whose entry carries the
   argument to re-read first), paste the new hash, and say in the commit why the
   member is still correct. To unbrand, delete the attribute and its entry together (one alone
   is VER002). Never delete a brand to unblock a build. VER003 means the
   declaration's shape cannot be fingerprinted honestly (`partial`, a
   preprocessor directive, a misplaced brand): restructure, never suppress.
   VER004 to VER010 refuse a brand, entry or ledger that cannot be trusted; each
   message names its fix. An entry's `assembly` is the compilation that sweeps
   it.
8. **Find the system before building one.** A "new" mechanism here is usually an
   existing one under another name. Load the owning skill (`puck-world`,
   `puck-dsl`, `rendering`, `sdf-authoring`, `maths-usage`, `maths-laws`,
   `gaming-bricks`, `rom-forge`, `dotnet10-performance`, `symbol-analysis`,
   `content-search`, `documentation`, `boy-scout`, `verification`,
   `review-passes`), then ask the code with `puck references`,
   `puck declarations` or `puck search -M 0`. `experimental/` is one of the
   places to look. A second implementation is a defect, and a skill wrong about
   its own area is corrected in the same change.
9. **Line endings are LF and are never a topic.** `.gitattributes`
   (`* text=auto eol=lf`) and `.editorconfig` hold the contract, with
   `*.bat`/`*.cmd` and `*.slnx` pinned CRLF in both. Never investigate, report,
   fix or work around an end-of-line difference. If a diff or formatter run
   appears to be about newlines, the setting is wrong and gets corrected there.

## `InternalsVisibleTo` is not endorsed

If another project needs a member, make the member public. Reaching for
`InternalsVisibleTo` signals wrong accessibility; a test project is the one
arguable exception. Search for both forms, the `Properties/AssemblyInfo.cs`
attribute and the csproj `<InternalsVisibleTo>` item. A grant hands a whole
assembly's internals to a friend, invisibly at the call site; widen the member
instead. If a member looks wrong to make public, that is evidence about the
design: say so.

## `experimental/` is a reference tree

Read and cite `experimental/` as prior art, the way you read git history, and
delete code there once live code has eclipsed it. Never improve, fix, build or
run it, or run its tests; its builds are expected to break as deletions land.
It holds `Puck.Post`, `Puck.Bench`, both `scripts/` trees, `Puck.BareMetal` and
`Puck.Platform.Switch`, each firewalled from the root build
([experimental/README.md](experimental/README.md)). Anything there that must
keep working is rewritten as a real project or a `puck` verb, under the gate.
A deletion rides in the same squash as the landing that eclipses it, and
"eclipsed" needs a mechanical check: bring it to the lead to decide. Documents
citing the old `tools/…`, `src/Puck.World/scripts/…` or former Post locations
are stale; correct them where they live.

## Verification

Load the [`verification`](.claude/skills/verification/SKILL.md) skill before
calling any change verified. It routes through `puck gate` and
`puck laws prove` when the CLI lists them, and otherwise through the manual
steps it gives. Its rules in brief:

- Run every gate from a copy of the head's own built CLI in a directory only
  your lane uses, never the global `puck` tool.
- Run ratchet and generator verbs with `--check` while verifying; their bare
  form rewrites the file.
- Measure `puck affected` against the lane's merge base, and never test
  binaries a failed build left behind.
- Prove every new or changed law red by withholding the fix.
- GPU legs run one at a time per GPU, under a grant the lead issues. A failed
  leg is re-run once alone: a timeout or wait that passes alone is a flake to
  report, and a wrong value is a failure to fix.
- Judge performance by code, disassembly and load-independent counts. A
  wall-clock timing runs only when the owner asks, once, serially, on an idle
  machine, through `puck bench`.

## Branches, commits and pushes

- Preserve unrelated work in a shared checkout. Stage explicit paths, check each
  staging result, and inspect the whole index before committing; an amend takes
  the index too. Prefer a separate commit when another task may have staged
  work.
- Commit subjects are lowercase `area: sentence` (`vulkan: …`), with the
  verification evidence in the body. Commits carry no `Co-Authored-By` trailer.
- Push a working branch (a lane, review or partner branch) once its gates pass.
  Pushing `main`, force-pushing, and deleting a remote branch are the owner's:
  ask first.
- Delete a local branch only once `git cherry` shows its work landed and no
  worktree holds it.

## Delegated work

When you delegate, inventory the work first, assign each agent explicit file
ownership and its skills, and sequence edits to shared files. Each brief holds
the whole task, the integration branch, a finish line (the command that must
pass or the state that must hold), a stopping condition, and whether it grants
the GPU. The integrator inspects the combined result and runs its checks; a
worker's report is evidence to check, not a substitute. Check a reported defect
against the current files before acting on it.

When you are the delegate:

- Report through your hand-back report. A lead session may be offline: never
  depend on messaging it, and put blockers, GPU legs you need, and decisions
  you could not make in the report.
- Give every scratch file and directory you create a name carrying your lane
  (`<lane>-cli/`, `<lane>-files.json`, `<lane>-red/`). A shared scratchpad is
  written by parallel agents, and generic names are overwritten mid-run.
- Never write the lead's ledger or plan files.
- Before reporting, merge the integration branch's current tip into your branch
  and re-run your checks.

## Running long tasks

When a step needs no input from the owner, keep going, and put status notes in
the message that carries the next action. Stop and ask only when the work
cannot continue without the owner, or before anything destructive or
outward-facing beyond the branch rules above: deleting data, deploying to
Azure, publishing a package, or changing anything outside this repository.
Choosing the structurally right fix over a cheaper patch is not the owner's
call; if it is large, delegate it rather than calling it too costly.

For a run with many parts, keep a checklist in the session scratchpad, named
for the run, tick items as they finish, and add what you find. A question the
owner has settled stays settled unless new evidence contradicts it; then say
what the evidence is.

End a run with three headings, in order: **Needs you** (decisions or approvals
waiting on the owner), **Changed** (what landed and how it was verified), and
**Found** (defects, risks, follow-ups). In research reports, mark anything you
could not confirm and say where you looked.

## Repository automation

Every repository operation is a verb on the one `puck` root
(`src/Puck.Cli/PuckRootCommand.cs`). Write automation as a verb, never as a
script, and never as inline PowerShell in a workflow. Workflows orchestrate
`puck` verbs and the bash composite actions under `.github/actions/`, and every
CI job installs the run's own candidate CLI. Formatting of PR branches is
appended by CI; install no Git hooks and change no Git configuration during
builds. [CI and releases](docs/development/ci.md) owns these rules, and
[C# file apps](docs/development/contributing.md#c-file-apps) owns the
conventions for `src/Puck.Azure.Resources/bootstrap.cs`, the one file-based app.
