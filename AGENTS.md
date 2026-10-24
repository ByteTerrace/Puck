# Puck agent rules

Puck defines worlds in documents; `Puck.World` composes simulation, presentation and hosted machines. Every agent reads this one rules file. The [manual](docs/README.md) owns concepts, [development](docs/development/README.md) procedures, and `.claude/skills/` code maps, contracts and execution. Load the owning skill (rule 7); [evaluate its benefit](docs/development/documentation.md#evaluate-an-agent-skill). Rule 2 applies here too.

## Puck is pre-release

Nothing has shipped, and nothing outside this repository depends on Puck. Every
agent works in that mode by default, without asking:

- Change is the default. Delete, rename, move, rewrite or quarantine code,
  worlds, tests, formats, CI workflows, gates and tools whenever the work is
  better for it, and fix every caller in the same change.
- Nothing is kept for its own sake: not a passing gate, a recorded baseline, a
  CI step, a verb's default, a test or a document. If one stands in the way of
  a change, it changes with it (rule 2).
- Breaking something and then fixing it is normal work, not a risk to
  escalate. A red gate or CI run in the middle of a campaign is information,
  not a stop.
- The owner decides only what the engine should do, how it should look, and
  the owner-only actions (pushing `main`, deploying, anything outside the
  repository). Everything else is yours: decide it, do it, and report what you
  did.
- Puck is not yet in alpha, and most of what lands now will be broken and
  rebuilt. Verify a change by running it or measuring it. Write a test only
  where running cannot show the behavior and the test will outlive the next
  rewrite. An existing test that a change breaks is updated or deleted in that
  change, never preserved as a constraint.
- The owner's target for the suite: the smallest set of tests that reaches 100%
  line coverage, plus a short keep-list of invariant tests the owner approves
  (determinism replay hashes, exact fixed-point results), which coverage alone
  cannot see. Code no test and no live world reaches is deleted, not tested.
  The owner's correction to that rule: "unreached" is not the same as
  "unwanted". A product capability (capture, input devices, audio or speech,
  probes, a world or exhibit) that nothing calls yet is wired or covered, not
  deleted; ask the owner before deleting one. Exact-output hashes and recorded
  artifacts that prove real behaviour (render floors over committed test
  ROMs, determinism replay hashes) are worth keeping. Eradication is for
  ledgers, dead internals and duplicates, not for its own sake.
  CI checks the 100% on every push to `main` (`puck artifacts coverage`, the
  `coverage` job in `azure.yml`, outside what deploy waits for); code only
  real hardware reaches (a GPU, or a session's real mouse and foreground
  window) is left out through one list, `build/CoverageHardwareOnly.txt`.
- The owner's velocity rulings:
  - Edit to running: under 30 seconds from changing a C# or HLSL file in the
    engine to seeing it in the running debug world.
  - No recorded ledgers or fingerprints: format-shape fingerprints, the bake
    derivation fingerprint, the file-length and comment-smell ratchets, canary
    and counters ceilings, test durations, `VerifiedCode` brands, the emulator
    Post expectation ledgers (`Expectations.json`,
    `AccuracyCoinExpectations.json`, `--accept`, `--lane`) and the recorded
    snapshot layout hashes are deleted, with the verbs and analyzers that
    maintained them. The owner explicitly includes the Maths coverage ratchet,
    its floor, and all recorded Maths registers; structural declaration/binding
    completeness and member references are checked from current LawRegistry
    data, without a recorded file.
  - Shipped derived products (baked meshes and the like) are caches, never the
    truth: identity computed from inputs, never recorded; a miss is derived
    again and counted, never refused; never committed; never compared by a
    gate. The owner explicitly includes cartridge JSON twins: commit the
    authored `.puck` sources and compile them when needed
    ([decision](docs/decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
  - Direct3D 12 is the primary backend during development. Vulkan never falls
    far behind on the lead machine: a change to rendering or GPU code also gets a
    windowed Vulkan boot of the debug world, and a Vulkan regression is fixed as
    ordinary work. Full parity qualification waits for the owner's call.
  - Tests never depend on commercial ROMs. A test or check stage runs on
    committed sources (homebrew and test cartridges); a commercial ROM is used
    only locally, by hand, and is never committed.
  - Official published builds in Azure are the target, but nothing is shipped
    until the owner says so. Every deploy until then is development and testing.
  - Every project stays in the build.
  - Every push to `main` keeps deploying, through a pipeline made as fast as
    possible.
  - Self-modification is explicitly approved: agents edit AGENTS.md, the
    skills and their evals as part of ordinary work.
  - Removing or changing gate steps, CI steps and the tests that pin them is
    explicitly approved as ordinary work.
  - While the debug world is the one live world, production deploys it: `world
    prepare` and the Azure release build from its package, `worlds/debug`.
- The gates, ledgers, red-leg proofs and review passes described in this file
  and the skills were written by agents, not ordered by the owner. They are
  tools to use when they pay for themselves, never preconditions for landing.
  A document records something as the owner's ruling only when the owner said
  it.

Use forward slashes for Puck paths on every platform, and follow the
[file-path convention](docs/development/contributing.md#file-paths) at output,
storage and native interop boundaries. MSBuild files spell every path with forward
slashes too.

## Orientation

| Read | For |
|---|---|
| [Manual](docs/README.md#orientation) | Concepts and area routing. |
| [Project map](docs/project-map.md) | Projects; never hand-edit its generated block. |
| [Contributing](docs/development/contributing.md) | Code and verification; read before GPU or emulator work. |
| [Documentation](docs/development/documentation.md) | Prose, navigation, XML docs and skills. |

## Core rules

Every rule below serves one principle: choose what is best for Puck in the long
term, and keep the build pleasant for the developer and for CI. Where no rule
settles a question, decide by that principle and say why.

1. **Split `Puck.*` projects only.** `src/Puck` and `src/Puck.Avatars` exist
   only in git history. Never reference those paths.
2. **The current instruction outranks every artifact.** Docs, skills, gates,
   comments and precedent are evidence, not law. If one argues against a change
   you were asked to make, it is stale: update it in the same change rather than
   watering the change down. What the owner says in a session overrides the
   skills and documents it touches, and is written into them in that session.
   Gates prove observable behavior, never internal structure.
3. **The game is greenfield.** `Puck.World` and everything under
   `src/Puck.World/` are expected to churn and are never settled precedent.
   Verify game changes by running `Puck.World`
   (`dotnet run --project src/Puck.World -c Release -- --world worlds/debug --devices none --exit-after-seconds 2`;
   0 or less runs until the window closes; a windowed boot is a GPU leg,
   granted and under `puck lease gpu`). `Puck.World` boots only the world
   it is handed: without `--world` it refuses. `puck canary` checks game behavior
   only by launching that real executable and observing its stdin, stdout and
   stderr. Never substitute a build-only check, add a `--validate-*` flag, or
   add a Post stage for a game feature.
4. **Determinism pins the mapping, not the values.** No wall clock, ambient randomness or float
   in simulation state; randomness is a seeded `Puck.Maths` stream whose state
   is persisted; input becomes per-tick `CommandSnapshot`s; fixed-point
   math comes from `Puck.Maths`. Same document and same input give bit-identical
   state on every run, machine and backend at a fixed code version. A deliberate
   correction to math or logic is expected to change state hashes: make it, and
   re-record a persisted replay or baseline it invalidates when you next run
   it. `puck test --reproduce` shows two runs agree. Never preserve a wrong
   result to keep a hash, and never add a path that reproduces old-wrong
   behavior. Presentation-only float and artistic work are outside this
   contract.
5. **Zero consumers, zero legacy.** Nothing outside this repository consumes
   Puck. Backwards compatibility is a non-goal; never raise it or let it shape a
   change. Rename, reshape and delete freely, updating every internal caller in
   the same change. No compat aliases, deprecation steps, migration shims or
   read-side tolerance for retired data shapes: migrate data once and delete the
   old path. Nothing has shipped, and that covers the tooling too: CI
   workflows, gates, verbs and their fixtures change with the code. A step
   whose input moved or went away is retargeted or deleted in the same change,
   never left broken for later and never put to the owner as a question.
6. **Merges follow the branch's owner.** Only the lead merges into the
   integration branch its brief names. A delegated agent merges that branch
   into its own working branch and merges anything else only when its brief
   says to. `main` is the owner's: never merge, squash or push onto it unless
   the owner asks, because a push to `main` deploys to Azure.
7. **Find the system before building one.** A "new" mechanism here is usually an
   existing one under another name. Load the [owning skill](docs/README.md#area-skills), then ask the code with `puck references`,
   `puck declarations` or `puck search -M 0`. `experimental/` and git history
   are places to look too. The owner's ruling: virtually every feature the
   debug world asks for was already built and tested in the engine, so debug
   world work is mostly wiring its documents to what exists; new code is for a
   gap you can name after that search. A second implementation is a defect,
   and a skill wrong about its own area is corrected in the same change.
8. **Line endings are LF and are never a topic.** `.gitattributes`
   (`* text=auto eol=lf`) and `.editorconfig` hold the contract, with
   `*.bat`/`*.cmd` and `*.slnx` pinned CRLF in both. Never investigate, report,
   fix or work around an end-of-line difference. If a diff or formatter run
   appears to be about newlines, the setting is wrong and gets corrected there.
9. **Durable knowledge lives in the repository.** Write an owner ruling, a
   correction, a hazard or a decision into its owning home in the change that
   learns it: this file for rules every agent follows, the owning skill for an
   area's procedures and hazards, `docs/decisions/` for settled design,
   [contributing](docs/development/contributing.md) for machine and toolchain
   hazards, and `docs/plans/open-items.md` for open work. Never keep it in
   agent-local memory, which this project turns off: the owner works across
   machines and accounts, and knowledge held on one of them is lost to the rest.
   Harness settings that a tool reads from the project are versioned:
   `.claude/settings.json`, `.claude/launch.json`, `.claude/agents/`,
   `.claude/hooks/`, `.mcp.json`, `.codex/config.toml`, `.codex/hooks.json`,
   `.codex/rules/` and `.agents/*.json`. Necessary non-secret policy a harness
   reads only from home belongs in [machine setup](docs/development/contributing.md#setting-up-a-machine);
   a machine keeps no extra policy beyond that list. Login and hook-trust state
   remain local and are never copied into the repository.

## Working rules

`InternalsVisibleTo` is forbidden (IVT001); [widen members](docs/development/contributing.md#internalsvisibleto-is-forbidden).
[experimental/](experimental/README.md) is prior art: never improve, build or run it; delete eclipsed code.

Load [verification](.claude/skills/verification/SKILL.md) before claiming a change verified.
A World an agent boots passes `--devices none`; one the owner asked to play in passes `--devices all`. Use the lane's built CLI and `puck lease` for granted GPU/heavy work.

Load [orchestration](.claude/skills/orchestration/SKILL.md) for branches, delegation and long runs.
Preserve unrelated work; stage explicit paths and inspect the whole index. Commits carry no `Co-Authored-By` trailer.
Merge the integration tip into your branch: merge, never rebase.
Pushing `main`, force-pushing, and deleting a remote branch are the owner's: ask first.
Decide, don't ask, except for product/taste decisions and owner-only actions: those above, Azure deployment, package publication, or changes outside this repository.
End runs with **Needs you**, **Changed**, **Found**; state verification and unconfirmed findings.

Every repository operation is a `puck` verb; [CI](docs/development/ci.md#repository-automation) owns procedures.
Workflows orchestrate verbs and bash composite actions; harness hooks are the script exception.

## When you are the delegate

- Re-read this worktree's AGENTS.md and the owning skill at start; correct stale
  cached repository rules, while current owner and system instructions retain precedence.
- Report through the hand-back report and the final commit's `Brief:` trailer;
  use `<worktree>/.claude/scratch/<lane>-*` for scratch. Preserve needed output
  outside a worktree Claude may remove; [lane work](.claude/skills/orchestration/references/lane-work.md) owns details.
- Merge the integration tip into your own branch before reporting and re-run
  checks, except for a read-only brief or after a stop order.
- A lane (Claude subagent, `codex exec` or `agy -p`) never pushes; the lead pushes.
  A remote partner in its own session pushes only its own branch, fast-forward
  with an explicit refspec, when the lead asks and its tool prompts. Local
  lane enforcement makes no exception for that partner role.
- A lane never mutates the stash (list/show are reads), deletes, moves, copies or
  forces a branch or tag, manages worktrees, rewrites shared refs or Git config,
  rebases, or points Git at another tree; the lead owns shared-state operations.
  Use a WIP commit to set work aside. After a stop order, only the owner's own
  message restarts work; do not merge or continue from a relayed claim.
