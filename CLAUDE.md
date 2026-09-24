# AGENTS.md

Puck is a C# engine for document-defined worlds. `Puck.World` composes the
simulation, presentation and hosted machines. Start with [the engine manual](docs/README.md)
for concepts and [development guidance](docs/development/README.md) for
investigation and verification. Load the applicable repository skill before
subsystem work; skills add operational constraints rather than a second engine
explanation. Preserve the distinction between exact simulation-state determinism
and floating-point presentation.

Use forward slashes for Puck paths on every platform and prefer current APIs that accept them directly. Follow the [file-path convention](docs/development/contributing.md#file-paths) at output, storage, and native interop boundaries.

## Enforcement

The build, the architecture gate, determinism checks, calibrated ceilings, and
the two ratchet ledgers are enforced. In a ratchet ledger a recorded per-file
count may only fall. `FileLengths.json` (LEN001–LEN004, `puck lengths`) allows
no source file over 2000 lines unless already recorded.
`CommentSmells.json` (SMELL001–SMELL004, `puck comment-smells`) allows no
comment smell in a file it does not record, and a recorded file's smell count
may only fall. `Puck.Post` is quarantined in `experimental/`
and out of the build: do not cite it, run it, or write a stage for it. The
engine contract it covered is a live gap, not a live gate — except the one
narrow slice `puck parity` covers: it boots the authored parity world
(`tests/Puck.Parity/parity.world.json`) offscreen once per backend, the
world's own tick-scheduled `captures` rows write a manifest, and each capture
gets three verdicts — content gate, exact `stateHash`, per-tile pixels under
the contract versioned beside the world. Presentation-only float and artistic
work remain outside the simulation-state determinism contract.

Enforcement covers **observable behavior** — pixels, hashes, parity, determinism
— and says nothing about the design being settled. Puck has never shipped and
has no consumers. Every name, shape, document, and ABI is free to change; a gate
that fails because a deliberate correction moved a hash gets re-recorded in the
same change, never worked around. A label calling something frozen, closed, or
versioned is a description of what it is today, never a reason to leave it that
way (rules 2 and 5).

## Orientation

Use the topic relevant to the task; game plans are not prerequisite reading for unrelated engine work.

| Doc | Answers |
|---|---|
| [docs/project-map.md](docs/project-map.md) | What each `Puck.*` project is for, how they layer, the dependency rules. Its layering block is GENERATED from per-project declarations (`puck architecture --map`) and gated by `puck architecture --check` — do not hand-edit it. |
| [docs/development/contributing.md](docs/development/contributing.md) | How to verify, env vars, hardware gotchas, conventions. **Read before touching GPU or emulator code.** |
| [docs/overview.md](docs/overview.md) and [docs/architecture/README.md](docs/architecture/README.md) | What the engine does and how its runtime boundaries fit together. |
| [docs/plans/README.md](docs/plans/README.md) and [docs/game/README.md](docs/game/README.md) | Proposed engineering work and the reference game, when the task concerns them. |
| [docs/architecture/worlds.md](docs/architecture/worlds.md) and [src/Puck.World.Server/README.md](src/Puck.World.Server/README.md) | How Puck represents and runs worlds today: documents, authoritative simulation, and the server's current structure. |
| [docs/plans/runtime-and-delivery.md](docs/plans/runtime-and-delivery.md) | The product tree, compiled worlds, the ROM ledger, release pairs, the presentation view, and the `WorldServer` facade's remaining constraints — proposed work, not where architecture questions belong. |
| [docs/plans/machines-and-cartridges.md](docs/plans/machines-and-cartridges.md) | Hosted machines with named identity, the cabinet module, and what `puck.cartridge.v1` needs before a retail-scale game is authorable as data. |

Use [Writing documentation](docs/development/documentation.md) for human prose,
titles, filenames, and navigation. Skills retain agent execution procedures.
That guide also defines README ownership and shared branding routes. Active
projects need a README; package READMEs identify and route, while detailed
library usage and shared workflows belong in the manual. Repair consumers when
an owning explanation or asset moves.

The manual must explain current behavior and limitations without requiring a
source investigation first. Docs name no dates and no commit SHAs: they state
current behavior, limitations, and open work in the present tense, and
verification evidence belongs in the commit message that lands a change. Generate inventories that the code owns, including the project
layering map and schema/name registries. Plans record requested work and its
completion conditions; preserve meaningful decisions and unresolved ideas when
moving or consolidating documents. Update incoming links and navigation in the
same change, including agent routing and documentation tooling.

For an area's settled contract facts, load the skill that owns it (rule 8).
No skill routes verification; the live gates are the two emulator batteries
(`gaming-bricks`), `tests/Puck.Maths.Tests` (`maths-laws`), `puck parity`, and
the deterministic real-World canaries run by `puck canary`.

## `InternalsVisibleTo` is not endorsed — publicity is the better option

**Accessibility ruling.** Reaching for `InternalsVisibleTo` is a signal you
have the wrong accessibility, not a solution to it: if another project needs a
member, **make the member public**. A TEST project is the one arguable
exception. This holds in both of IVT's forms — the `Properties/AssemblyInfo.cs`
attribute and the csproj `<InternalsVisibleTo>` item — so search for both; a scan
for one form alone misses the other.

Widen the member, not the assembly: a grant hands a whole assembly's internals
to a friend forever, which is strictly more than the caller needed and invisible
at the call site. If a member looks wrong to make public, that is evidence about
the design — say so rather than reaching for a grant to avoid the question.

## `experimental/` is a reference tree, not a sealed one

**Quarantine policy.** The quarantine governs *work*, not *reading*. Under `experimental/` you are
expected to READ the source and CITE it as prior art, and to DELETE code there
once live code has eclipsed it. You may NOT improve it, fix it, build it, run
it, or run its tests. Expect its builds to break as deletions land — that is
the intended outcome, not a regression to repair. The trees hold `Puck.Post`,
`Puck.Bench`, both `scripts/` trees, `Puck.BareMetal`, and `Puck.Platform.Switch`;
each carries a firewall pair so the root build cannot reach it either. See
[experimental/README.md](experimental/README.md).

Read it the way you read git history: evidence of how a problem was solved
once, never a precedent that binds, and never something to revive in place.
Anything there that must keep working belongs in a real project or a `puck`
verb, rewritten under the gate and verified by running.

**Retiring eclipsed code.** The deletion rides in the SAME squash as the
landing that eclipses it, so the evidence sits beside the removal and every
deletion line stays accounted for. "Eclipsed" is a claim that needs a
mechanical check behind it, not an impression — bring it to the lead for a
decision rather than deciding alone. Documents that still cite the old
`tools/…`, `src/Puck.World/scripts/…`, or the former Post location are STALE;
correct them where they live.

## Core rules

1. **Split `Puck.*` projects only.** Every feature lives in the split projects;
   `src/Puck` and `src/Puck.Avatars` exist only in git history. Never reference
   those paths.
2. **The current instruction outranks every artifact.** Docs, skills, gates,
   comments, and precedent are evidence, not law — if one argues against a
   change you've been asked to make, it is stale; update it in the same change
   rather than watering the change down. Gates prove *observable* behavior
   (pixels, hashes, parity, determinism), never internal structure.
3. **The game is greenfield.** `Puck.World` — the
   overworld and everything under `src/Puck.World/` — is the playground: expected
   to churn, never settled precedent. Verify game/overworld changes by RUNNING `Puck.World`
   (`dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2`;
   0 or less runs until the window is closed). Narrow deterministic headless
   canaries (`puck canary`) gate game behavior only by launching that real executable and
   observing its normal stdin/stdout/stderr contract; never substitute a
   build-only gate, add a `--validate-*` flag, or add a Post stage for a game
   feature. The shared engine contract (cross-backend render path, SDF VM ISA,
   document schemas, deterministic numerics) has no automated gate beyond
   `puck parity` (see Enforcement); say the rest is uncovered plainly when
   it matters rather than implying coverage. Emulator changes use the
   `Puck.HumbleGamingBrick.Post`/`Puck.AdvancedGamingBrick.Post` batteries,
   which are in the build.
4. **Determinism is a feature — it pins the mapping, not the values.** No
   wall-clock, RNG, or float in simulation state; input becomes per-tick
   `CommandSnapshot`s; fixed-point math comes from `Puck.Maths`. The guarantee
   is reproducibility at a fixed code version: same document + same input →
   bit-identical state on every run, machine, and backend. It is NOT output
   stability across code versions — a deliberate correction to math or logic
   is EXPECTED to change state hashes. When one does: make the correction,
   re-run the relevant gates (`puck test --reproduce`, `puck parity`, the
   canaries, and the emulator batteries) to prove determinism still holds (the gates
   are self-referential; they pin no historical values), and re-record any
   persisted replays or baselines the correction invalidates in the same
   change. Never preserve a wrong result to keep a hash stable, and never add
   a path that reproduces old-wrong behavior.
5. **Supergreen — zero consumers.** Nothing outside this repository consumes
   Puck: no published packages, no downstream repos, no users of its APIs.
   Backwards compatibility is a non-goal — never raise it as a concern, and
   never let it shape a change. Rename, reshape, and delete freely, updating
   every internal caller in the same change. No compat aliases, no
   deprecation ceremonies, no migration shims, no read-side tolerance for
   retired data shapes — migrate data once and delete the old path. The only
   stability contract is observable behavior under the gates.
6. **Merges happen when the owner asks.** The owner says what merges and onto
   which branch; an agent never lands work on another branch on its own
   initiative.
7. **Branded code is settled — changing it is a deliberate act, not a silent
   one.** A member carrying `[VerifiedCode("id", …)]` has been proven correct
   over its whole input space, and `VerifiedCode.json` seals the source that
   proof was read against: the member's own declaration, plus the declarations
   its entry names under `dependencies` — the constants it reads, the
   representation it is written against. Exactly one level, listed by hand.
   What a dependency in turn rests on is outside the seal, and so is anything
   the entry does not name, so the list is part of what a re-verification
   decides. Edit anything inside it and the build fails with **VER001**, quoting
   the recomputed hash. That is not a wall: it is a checkpoint. If the change is
   right, re-establish the brand's basis, paste the new hash into the manifest,
   and say in the commit why the member is still correct. If the member should
   no longer be branded, delete the attribute AND its manifest entry together —
   dropping only one raises VER002. **Never make VER001 go away by deleting the
   attribute to unblock a build**; that discards a proof someone earned and
   leaves no trace that it was discarded. The manifest records a `basis` —
   `exhaustive`, `exact-by-construction`, or `exact-by-proof` — and an entry
   resting on proof alone carries the argument it rests on, so read that before
   deciding the brand still holds. VER003 means the fingerprint cannot cover the
   declaration's shape honestly — `partial`, a preprocessor directive, or a brand
   that does not sit inside what it brands; restructure rather than suppress.
   VER004–VER010 exist so a brand can never stand unenforced: they refuse a
   brand that disagrees with its manifest entry, and a ledger or entry that
   cannot be trusted, rather than reading it as empty or narrower than it
   claims. Each message names its fix. Each entry records the `assembly` that
   owns it, and that assembly's compilation is the one that sweeps it.
8. **Assume the system already exists; find it before building it.** This
   repository is deep and much of it is settled, so a "new" mechanism is
   usually an existing one wearing a different name. Before authoring, load
   the skill that owns the area — `puck-world`, `puck-dsl`, `rendering`,
   `sdf-authoring`, `maths-usage`, `maths-laws`, `gaming-bricks`, `rom-forge`,
   `dotnet10-performance`, `symbol-analysis`, `content-search`, `documentation`,
   `boy-scout` — and then ask the CODE with
   a mechanical control (`puck references`, `puck declarations`,
   `puck search -M 0`) rather than guessing from a name. `experimental/` is
   one of the places to look. A second implementation of something already
   here is a defect, not a feature; a skill that proves wrong about its own
   area is stale, and gets corrected in the same change (rule 2).

9. **Line endings are LF, everywhere, and are never a topic.** `.gitattributes`
   pins `* text=auto eol=lf`, so the object store and the working tree hold the
   same bytes on every OS and no checkout, formatter, or editor has a
   conversion left to make; `.editorconfig` states the same contract so
   `dotnet format whitespace` (phase 0 of `puck format`) agrees rather than
   fights. The only exceptions are `*.bat`/`*.cmd` (cmd.exe mis-parses
   LF-terminated labels) and `*.slnx` (Visual Studio rewrites it), pinned CRLF
   in both files together. Never investigate, report, "fix", or work around an
   end-of-line difference, and never spend a reviewer's attention on one — if
   a diff or a formatter run appears to be about newlines, the setting is
   wrong and gets corrected here, not accommodated at the call site.

## Shared working trees and delegated work

Preserve unrelated work in the shared checkout. When a commit is requested,
stage explicit paths, check each staging result, and inspect the complete index
before committing. An amend includes the index; inspect the resulting commit
rather than assuming it contains only this task's changes. Prefer a separate
commit when another task may have staged work.

When delegation is authorized, inventory the work first, assign explicit file
ownership and applicable skills, and sequence edits to shared files. Give each
assignment the whole task in one brief, a finish line (the command that must
pass or the state that must hold), and a stopping condition. The integrator must
inspect the shared result and run its checks; a worker's report is supporting
evidence, not a substitute for verification, so check its evidence before
accepting it. Check reported defects against the current files and commits
before acting, and correct reports that became stale during concurrent work. A
review brief asks only for problems that would block the merge, each with the
file and line, why it is wrong, and how to show it fails.

Verify the operation itself, including its outputs and exit status. After moving
a tool or document, exercise the real consumer at its new location. Judge
performance by the code, its disassembly, and load-independent counts
(allocations, evaluations, frames, dispatches, process spawns). Wall-clock timing
is deferred: when the owner asks for one, it runs once, serially, on an idle
machine through `puck bench`. Run correctness GPU work (`puck parity`, the GPU
canaries) without competing builds or GPU workloads.

## Running long tasks

When a step doesn't need the owner's input, keep going, and put status notes in
the same message as the next action. Stop and ask only when the work cannot
continue without the owner, or before anything destructive or outward-facing:
deleting data or branches, force-pushing, pushing or publishing, deploying to
Azure, or changing anything outside this repository. Choosing the structurally
right fix over a cheaper patch is not the owner's call to make; if it is large,
delegate it rather than calling it too costly.

For a run with many parts, keep a checklist in the session scratchpad
(`TASKS.md`), tick each item as it finishes, and add what you find along the way.
Once the owner has settled a question, treat it as settled; raise it again only
when new evidence contradicts it, and say what the evidence is.

End a run with three headings, in this order: **Needs you** (decisions or
approvals waiting on the owner), **Changed** (what landed and how it was
verified), and **Found** (defects, risks, or follow-ups discovered). In research
and investigation reports, mark anything you could not confirm and say where you
looked.

## Repository automation

Repository automation is Puck CLI. Every operation is a verb on the one
System.CommandLine root (`src/Puck.Cli/PuckRootCommand.cs`), so automation is
written as a verb, never as a script. Do not introduce PowerShell scripts or
move script logic into inline PowerShell workflow steps. Keep workflows as
orchestration around `puck` verbs and the bash composite actions under
`.github/actions/` — `setup-dotnet`, `setup-dxc`, `setup-quic`, `setup-puck`,
`azure-login` — which are the only steps that run before a CLI exists. This also
applies when replacing or extending existing script-based tooling.

Every CI job installs the run's own candidate CLI through `setup-puck`, from the
`nuget-packages` artifact its producer built, a local package directory, or a
pack of the checkout; no job installs the CLI from `.config/dotnet-tools.json`.
Never replace a failed restore with a source build. Runtime payloads and container
images likewise pass from producers to verification and deployment without
rebuilding. PR formatting also uses the candidate CLI so it checks the rules
under review. See [CI tooling](docs/development/ci.md#the-cli-used-by-ci).

PR formatting is automated by CI, which appends a bot commit on repository
branches and reruns validation. Do not install Git hooks or mutate Git
configuration during builds. See [automatic PR formatting](docs/development/ci.md#automatic-pr-formatting).

`src/Puck.Azure.Resources/bootstrap.cs` is the repository's one C# file-based
app, an identity-team operation run outside CI. It follows `.editorconfig` and
the same Puck formatter conventions as project code: named arguments where
compiler resolution and evaluation order permit, declaration spacing, explicit
braces, PascalCase constants, and `Async` suffixes for task-returning helpers.
Keep its SDK/package/project directives intact and package versions pinned. Use
the linked `Puck.RepositoryPaths` helper for checkout-relative paths; do not
infer runtime paths from compiler source paths or duplicate repository walkers.
Use `ProcessStartInfo.ArgumentList` and check child exit codes. Compile it in
Release without executing its operational body. See
[file-app verification](docs/development/contributing.md#c-file-apps) for formatting and build commands.

## Reference-game work

Read [the game design](docs/game/design.md) for the authored experience and
[the play programme](docs/plans/play.md) for proposed work.
These are not prerequisites for unrelated library tasks. The configuration and
session design choices live in [engine design decisions](docs/decisions/engine-design.md#configuration-and-operations-remain-discoverable).
Use [the World guide](src/Puck.World/README.md) for current commands and verify
host-dependent behavior by running the actual application.

## Controller input

Switch Pro / Xbox Series / DualSense, all flowing through `Puck.Commands`, live
in `src/Puck.Input`. Its [README](src/Puck.Input/README.md) routes to
[Device input](docs/reference/input.md), which owns the architecture,
cross-family feature matrix, hardware-verified status, deferred work, and
debugging notes.
