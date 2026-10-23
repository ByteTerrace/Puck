# Contributing to Puck

This guide explains how to investigate, change, and verify Puck. Use the
[project map](../project-map.md) for project ownership and dependency rules,
the [architecture guide](../architecture/README.md) for the engine's boundaries,
and the [CI guide](ci.md) for hosted validation and release procedures.

## Start here

- A run uses a versioned `puck.world.definition.v1` JSON document. CLI conveniences
  synthesize the same model; they do not create a second execution path.
- Vulkan and Direct3D 12 implement the same neutral GPU contracts. Direct3D 12 is the
  development backend; verify there, and qualify Vulkan when the owner calls for it.
- `Puck.World` composes the running application. Run it to verify rendering and
  complete game interaction. The World test suite covers document, protocol,
  authoritative simulation, and shipped game state programs; other checks below
  cover their own specific contracts.
- Emulator cores live under `src/` (`Puck.HumbleGamingBrick`, `Puck.AdvancedGamingBrick`)
  with hosting folded into the cores. Each core has its own POST battery.
- Authoritative simulation uses fixed-point values and per-tick command
  snapshots. Wall-clock time, ambient randomness, and floating-point state do
  not enter replay-bearing simulation.

## Setting up a machine

A machine builds and verifies Puck once it carries the toolchain below. The
list is the same for a person and for an agent; the Claude Code, Codex and Antigravity items
apply to any machine an agent session runs on. CI runs on Windows and Ubuntu
runners, so a Linux machine differs only where this list says; no runner is
macOS, and nothing here is verified there.

- **The .NET SDK `global.json` pins.** Roll-forward is disabled, so install
  exactly that version; `dotnet --version` run inside the checkout prints it.
  The full build also needs the `wasm-tools` workload
  (`dotnet workload install wasm-tools`), which `Puck.World.Browser` compiles
  with; the [CI guide](ci.md#shared-repository-tooling) shows the full local
  build with it.
- **DXC on `PATH`.** Every build compiles the shaders, so a machine with no GPU
  needs it too. Install the release CI pins (`.github/actions/setup-dxc/action.yml`
  names the version, the Windows and Linux archives and their checksums);
  [GPU support and shader builds](#gpu-support-and-shader-builds) explains what
  the build does with it. Only Windows builds DXIL.
- **`git`, and `gh` signed in** (`gh auth status`): the release verbs call
  `gh`, and agent sessions use it for pull requests and workflow runs.
- **Node.js** at the version CI pins (`node-version` in
  `.github/workflows/browser.yml` and `azure.yml`), for the dashboard's npm
  projects, which `.claude/launch.json`'s previews run, and the browser
  payload's Node harness.
- **PowerShell (`pwsh`).** The versioned `.claude/settings.json` runs the
  hooks under `.claude/hooks/` through `pwsh`, so a machine without it reports
  a hook error on every shell command and every edit. Windows PowerShell is
  not `pwsh`; on Linux, install PowerShell from Microsoft's package feed.
- **The global `puck` tool, installed from the checkout**, as
  [Installing the checkout's CLI on PATH](../reference/cli.md#installing-the-checkouts-cli-on-path)
  describes, for running verbs by hand. An installed tool keeps its commit while
  the checkout moves on: `puck --version` warns on stderr when the two differ, so
  reinstall after pulling. The agents' MCP server never uses it: it runs the
  checkout's existing Release build of `src/Puck.Cli`, falling back to the main
  checkout's, through `.claude/hooks/puck-mcp.ps1`. The launcher reports the
  selected source and stamp, which do not establish HEAD freshness; it builds
  only when neither checkout has an existing build
  ([the MCP adapter](../../src/Puck.Mcp/README.md)).
- **Claude Code.** The project's settings are shared and versioned:
  `.claude/settings.json` turns auto memory off, selects the Concise output
  style, enables the `puck` server from `.mcp.json` and the Codex plugin, names
  the plugin's marketplace, and registers the hooks, and `.claude/launch.json`
  holds the debug world's launch (`debug-world`: `--world worlds/debug
  --control --devices all`) and the preview configurations. Trust the folder when Claude Code asks,
  because the marketplace entry loads only in a trusted project. Version every project-supported setting in `.claude/settings.json`.
  The necessary home-only policy is `{"permissions":{"defaultMode":"auto"}}`;
  keep login and trust state local. There is no Claude-specific memory file, on purpose: Claude Code reads
  [`AGENTS.md`](../../AGENTS.md) natively when no memory file exists
  ([documentation policy](#documentation-policy)).
- **Codex.** Install the CLI (`npm install -g @openai/codex`), sign in
  (`codex login`), and install the Claude Code plugin `codex@openai-codex` on
  each machine: `.claude/settings.json` enables it and names its marketplace,
  so Claude Code offers the install when it loads the project, and
  `/codex:setup` checks the CLI afterwards. The versioned
  `.codex/config.toml` registers the `puck` MCP server for Codex, which reads
  it only once the project is trusted. In the checkout, open `/hooks`, review
  and trust each current definition in `.codex/hooks.json`. New or changed
  definitions are skipped until reviewed and trusted again
  ([official hook contract](https://learn.chatgpt.com/docs/hooks)). Test a supported
  shell path with the harmless policy probe `git --no-pager push --dry-run origin HEAD:main`:
  a lane must report refusal without executing it. A successful help command
  does not establish hook activation. Hooks are guardrails on supported tool
  paths, not a complete execution boundary.
  Build and copy the CLI using [verification](../../.claude/skills/verification/SKILL.md#run-verbs-from-your-own-cli-copy),
  then run `puck agents link` in each main checkout and fresh worktree;
  `puck agents link --check` verifies it. This creates one junction on Windows
  or directory symlink elsewhere from `.agents/skills` to that checkout's
  `.claude/skills`, refusing an ordinary directory or a link aimed elsewhere.
  Only the link is ignored; the project JSON files remain tracked
  ([native skill discovery](https://learn.chatgpt.com/docs/build-skills)).
  The [launch capability table](../../.claude/skills/orchestration/SKILL.md#launch-capabilities)
  distinguishes native exec from companion `task`. A linked worktree's common
  Git and lease directories may lie outside its workspace: discover the common
  directory with `git rev-parse --path-format=absolute --git-common-dir` and
  grant those actual paths in the permission profile. Do not require a fixed
  Windows checkout path. Login and hook trust remain per-user, never copied.
- **Antigravity.** Install its CLI and sign in with the owner's account. Run
  `agy -p "<brief>" --output-format text` from the worktree, without `--mode`.
  Headless auto behavior comes from `agentMode: "auto"` in the home-only
  `~/.gemini/antigravity-cli/settings.json`; the owner-approved settings below
  record its necessary non-secret policy. Auto mode does not consult the ask list; shared
  git-safety refuses lane pushes and shared-state operations.
  The committed `.agents/hooks.json` and `.agents/skills.json` supply hooks
  and skills; hooks run with `.agents/` as their working directory. MCP is
  exposed through the workspace plugin in `.agents/plugins.json`, whose
  plugin MCP configuration starts the shared launcher. A bare
  `.agents/mcp_config.json` is not a discovered server registration. Use a
  positive operation timeout of 600 seconds; `-1` fails the validator.
  [Launch capabilities](../../.claude/skills/orchestration/SKILL.md#launch-capabilities)
  separates the terminal sandbox from the host's rights.
- **Optional managed-stack diagnostics.** The owner may install
  `dotnet tool install -g dotnet-stack`; an agent uses an existing installation
  to capture a hung process before stopping it. Installation changes the machine
  outside this repository and remains owner-only.
- **A GPU machine** also follows [GPU support and shader builds](#gpu-support-and-shader-builds)
  and the [hardware and toolchain cautions](#hardware-and-toolchain-cautions),
  and runs GPU gates one at a time per GPU under the
  [verification skill](../../.claude/skills/verification/SKILL.md)'s rules.
- **A checkout older than the versioned Claude Code files.**
  `.claude/settings.json`, `.claude/launch.json` and `.claude/hooks/` were once
  ignored, so a checkout from then may hold untracked copies, and `git pull`
  refuses to overwrite them. Move them aside, pull, fold anything worth sharing
  from the old copies into `.claude/settings.json`, and delete the rest.

### Antigravity user settings

The owner approved recording this home settings file verbatim. The D:/Source
paths name this machine's checkout; use the actual checkout paths on another
machine. Login and trust state stays local; the workspace trust entry below
records the approved configuration, not portable login or trust state.

```json
{
  "agentMode": "auto",
  "model": "Gemini 4 Argon (High)",
  "permissions": {
    "allow": [
      "command(git)",
      "command(dotnet)",
      "command(pwsh)",
      "command(node)",
      "read_url(*)",
      "mcp(puck/*)",
      "write_file(D:/Source/ByteTerrace/Puck)",
      "write_file(D:/Source/ByteTerrace/Puck/.git)"
    ],
    "ask": [
      "command(regex:git push.*\\b(main|refs/heads/main)\\b)",
      "command(regex:git push.*(--force|\\s-f\\b|--delete|--mirror|\\s:))",
      "command(git reset)",
      "command(git clean)",
      "command(git branch -D)",
      "command(git worktree remove)",
      "command(git stash drop)",
      "command(git stash clear)",
      "command(git stash pop)",
      "command(git checkout --)",
      "command(git restore)",
      "command(git filter-branch)"
    ]
  },
  "trustedWorkspaces": [
    "D:\\Source\\ByteTerrace\\Puck"
  ]
}
```

## Find code and references

Use text search for file discovery, literals, JSON, HLSL, and project files. Use the
compiler for questions such as who references a symbol, what implements an
interface, whether code is unused, or whether a rename is safe. Text matching
misses extension methods, aliases, overload resolution, generated code, and XML
`cref` references.

Prefer the cheapest correct tool:

1. Puck search for orientation and non-C# text.
2. `puck declarations` for declaration, member, attribute, base-list, and XML-doc
   inventories—parse-only, no build.
3. `puck references` for cross-project symbol questions: references,
   implementers, overrides, derived types, dead-code candidacy.
4. `dotnet build Puck.slnx -c Release` after a refactor or documentation edit
   that changes `cref` values.

## File paths

Use `/` in authored paths, configuration, stored path identities, and path output,
including on Windows. That includes every MSBuild project, props, and targets
file: imports, item includes and excludes, links, package paths, and path
properties all spell `/`. Puck's own MSBuild readers still read `\` as a
separator the way MSBuild does, so the spelling is held by a `Puck.Cli.Tests`
law that refuses a backslash anywhere in a tracked MSBuild file outside
`experimental/`. Prefer current `System.IO` APIs that accept these paths
directly; do not convert `/` to the platform separator before file access.
Normalize platform-produced paths to `/` at Puck's output and storage boundaries
with `Puck.Abstractions.PuckPaths.Normalize`, which resolves a path to its full
form and replaces every platform separator with `/`. Compare two file-system
paths for equality, containment, or distinctness with `PuckPaths.Comparer` (a
`StringComparer`) or `PuckPaths.Comparison` (its `StringComparison`
equivalent): case-insensitive on Windows, where the file system itself ignores
case, and ordinal everywhere else. This is a file-system rule, not a text
rule — a document NAME (`Puck.Assets.WorldDocumentName.NameComparer`) is
case-insensitive on every platform, on purpose, and never shares this
comparer. Keep resolution semantics explicit: `Path.Combine` can replace an
earlier base with a rooted argument, whereas `Path.Join` only joins
components. They are not interchangeable. Preserve native device-path syntax
only at an interop boundary that requires it; backslashes in string escapes
and regular expressions are not file separators.

## Per-user directory

Puck keeps per-user state and caches in one directory, `Puck` under your local
application data: `%LOCALAPPDATA%/Puck` on Windows and `~/.local/share/Puck` on
Linux, or the temporary directory when the platform names no such folder
(`PuckUserDirectory` in `Puck.Abstractions`). Each owner keeps one lower-case
subdirectory there. Every cache among them is bounded:

| Subdirectory | Owner | Bound |
|---|---|---|
| `world` | The game's state root: profiles and replays (`--state-dir` replaces it) | none for its state; its `pipeline-cache` keeps 8 files per backend, and its `pipelines` shader cache is bounded as `shaders` is |
| `projections` | Content-addressed prototype bodies shared by projection recipients across joins and worlds | 4096 objects, 256 MiB |
| `compiled-worlds` | The compiled worlds boots derive, shared by every boot whatever its state root | 1024 files, 256 MiB |
| `world-builds` | The shared Release builds of `Puck.World` the CLI gates run | 4 builds |
| `law-trees` | The persistent shared-object proof clones and exclusive locks used by `puck laws prove` | 2 clones |
| `compilations` | The `.puck` compile cache the game and the CLI share | 1024 files, 256 MiB |
| `shaders` | The shader compile cache the build and the CLI share ([freshness](../reference/shaders.md#freshness)) | 4096 files, 1 GiB |
| `meshes` | The baked meshes every World and worktree shares, keyed by geometry and bake identity, one directory a bake identity's set (`SdfMeshStore`) | 8 GiB of whole sets, 64 recorded identities |
| `corpora` | The conformance corpora the emulator batteries fetch | none: a fixed fetched set |

Every bound follows one policy, `CacheRetention` in `Puck.Abstractions`: least
recently used out. An entry's last use is its stamp, the last write time of the
file or directory that stands for it; an owner stamps an entry when it reads
it, and writing one stamps it. When an owner writes, it removes the least
recently used entries beyond its bound, keeps the entry it is using whatever its
stamp, and leaves an entry another process holds open. A cache whose entries
come in sets one reader uses together (the `meshes` store, a set a bake
identity) removes a set whole, its last use its newest entry's, and never the
set it is writing (`CacheRetention.EnforceSetsOnWrite`). A process lists a
directory for this at most once a minute, so a build that publishes many
entries pays for one listing. Every cache is content-addressed, so an evicted
entry costs its next reader a derivation, never a different answer.
`puck shaders cache prune` applies the same policy with an age bound instead.

## Temporary directories

Every directory a run makes under the temporary directory follows one policy,
`RunDirectory` in `build/RunDirectory.cs`. `Directory.Build.targets` links it
into the CLI and every test and validation project, and `Directory.Build.props`
into file apps. A directory is named `<kind><process id>-<token>`: its kind,
which starts with `puck-` and names its owner, the process that created it, and
a unique token. A run that passes deletes it. A run that fails keeps it, names
its absolute path in a `run directory kept: <path>` line, and trims its kind: of
the kind's directories whose process has finished, the newest four stay and the
rest go. The first directory a process creates sweeps every kind the same way
and also removes any finished process's directory older than six hours, which
clears a killed run's leftovers and the evidence of kinds that never run again.
A directory whose process still runs is never removed, however old, and a
process id that a later process reuses is told apart by its start time. The
[CLI conventions](../reference/cli.md#conventions) list the verbs that follow
it and the directories that are deleted whatever the outcome.

A law takes its directory from `TemporaryDirectory`
(`tests/Shared/TemporaryDirectory.cs`, linked into every test project), never
from `Directory.CreateTempSubdirectory`, `Path.GetTempPath()` or
`Path.GetTempFileName()`. Its disposal follows the law's verdict: a passing law
deletes the directory, and a failing law keeps it and writes the kept line to
the law's output. A directory disposed while its law runs waits for the
verdict, which an assembly-level xUnit `BeforeAfterTestAttribute` reads once the
law ends. A deletion failure fails a passing law, so a handle the code under
test leaves open is caught; `bestEffortDelete` relaxes that for a law whose
host may still hold a file as it is disposed.

## Build output and hard links

Every project copies its dependency closure into its own `bin`, so a built
tree would hold each package and Puck assembly many times over.
`Directory.Build.props` makes those copies hard links instead: each package
file links to the NuGet global cache, each project reference to the referenced
project's `bin`, and each `None` or `Content` item with `CopyToOutputDirectory`
(assets, fonts, shader bytecode, world outputs) to the file the item names. A
built tree therefore holds one file per distinct output.

A hard link is the same file under another name, so writing an output file in
place writes every name at once: the NuGet global cache, other projects'
outputs, and the repository's own sources. Never open a file under `bin`, a
publish directory, or a file a build ships as content for writing, truncating
or appending. Replace it instead: write the new bytes with `AtomicFile`, or
delete the file before writing a new one. An inline MSBuild task, which cannot
reference `AtomicFile`, writes a temporary file beside the destination and moves
it over the destination. The compiler is the one writer that rewrites its
outputs in place (`obj/<name>.dll`, `.pdb` and `.xml`), so the copy from `obj` to
`bin` and the copy into a publish directory stay plain copies, and a `bin` file
never shares its file with a compiler output. A build into a directory that outlives it, such as the World
builds in `world-builds`, passes `--output` and links no content items, since
an editor saving a source in place would otherwise rewrite the kept build.

## C# file apps

Repository automation is Puck CLI: `puck --help` lists the verbs and
`puck <verb> --help` its options. Deployment, publishing, QUIC probes, and WASM
refresh are `puck` verbs, not scripts and not file apps.

`src/Puck.Azure.Resources/bootstrap.cs` is the repository's one C# file app, an
identity-team operation run outside CI. It uses the same `.editorconfig`,
compiler warnings, and Puck formatting conventions as the project-based code;
its file directives declare its dependencies, so keep its SDK, package and
project directives intact and its package versions pinned. The formatter's
conventions apply as they do to project code: named arguments where compiler
resolution and evaluation order permit, declaration spacing, explicit braces,
PascalCase constants, and an `Async` suffix on task-returning helpers. It starts
child processes through `ProcessStartInfo.ArgumentList` and checks each child's
exit code. `Directory.Build.props` links `build/RepositoryPaths.cs` into it so it
can locate checkout data at runtime without building Puck CLI; use that helper
rather than a second repository walker. Invoke it from within the checkout.
Compiler source paths can be remapped by CI and are not runtime file locations,
so never derive a runtime path from one.

Compile it without executing its operational code:

```sh
dotnet build src/Puck.Azure.Resources/bootstrap.cs -c Release
```

Use `puck format --file-list files.json` from the repository root, with a JSON array
of paths relative to the working directory, such as `["src/Puck.Azure.Resources/bootstrap.cs"]`. The CLI converts a
standalone app to a disposable SDK project, preserves its references and linked
helpers, compiles and formats the copy, then copies back only the selected source
with its file directives restored. `--check` leaves the original source
untouched. Ordinary project files still need their owning projects restored
and built in the configuration the run resolves symbols against—`Release` unless
`--configuration` says otherwise—or the semantic passes skip them and say so. See
[automatic PR formatting](ci.md#automatic-pr-formatting) for the CI bot and the
fork-PR patch path. Never run a repository-wide sweep to fix one entry point.

## Verification

### Edit to running

The target is under 30 seconds from saving an engine C# or HLSL file to seeing
it in the running debug world (the package `worlds/debug/`, booted with
`--world worlds/debug`), on Direct3D 12, the World's default backend on Windows.

**C#**: build and boot in one command, restore skipped:

```powershell
dotnet run --project src/Puck.World -c Release --no-restore -- --world worlds/debug
```

A Windows build of one project builds DXIL alone, so a Vulkan run
(`-- --backend vulkan`), which such a build refuses at boot, needs
`-p:PuckShaderSpirvEnabled=true` or a solution
build ([shader freshness](../reference/shaders.md#freshness)). Restore again
(drop `--no-restore`) after changing a package or project reference.

What the C# loop costs:

- **Warm build nodes.** `dotnet build` and `dotnet run` leave their MSBuild
  nodes running for the next build (node reuse is on by default), which then
  starts with every node's tasks loaded, jitted and compiled. A cold build
  starts every node afresh and pays that in each: a no-op build of
  `src/Puck.World` took 32 to 48 s cold and 8 to 9 s warm on the reference
  machine, under 60 to 90% load from other work. Idle nodes exit after a while,
  so the first build after a long pause is cold. The `-nodeReuse:false` every
  agent build and restore passes ([verification](../../.claude/skills/verification/SKILL.md))
  keeps shared agent machines free of lingering nodes and makes every agent
  build cold; an agent timing this loop says which it measured. The switch
  belongs to the build and the restore alone: `dotnet test` on the xUnit v3
  projects can forward unknown switches to the test host;
  [verification](../../.claude/skills/verification/SKILL.md#build-before-you-test)
  records the observed refusal. Build with `-nodeReuse:false`, then run
  `dotnet test --no-build`.
- **One compile.** An edit that keeps its project's public surface keeps its
  reference assembly, so no dependent recompiles: a body edit in `Puck.SdfVm`
  compiles `Puck.SdfVm` alone. The rest of the build is MSBuild's check of the
  other projects the World reaches, 64 in all with the world compiler's.
- **One inline task.** Every build operation the tree implements in its own
  code (the architecture gates, the shader build, the Direct3D 11 kernels, the
  stale package sweep) is one inline task (`build/PuckBuildTasks.targets`), so
  a cold node compiles it once rather than once per operation.
- **No rebake.** The World keeps its baked meshes between runs (`SdfMeshStore`,
  in the `meshes` device cache), keyed by geometry and by the bake's own code
  (the methods it reaches, `SdfMeshBakeClosure`) and kernels, which read their
  own interface and the field's generated vocabulary alone, so a reboot after
  an edit outside what the bake runs (a light, an exposure constant, a world
  pass-block value) loads every mesh
  (`[sdf.mesh-bake] done … dispatches=0 … loaded=N`) and a changed creation
  bakes its own groups alone.

**HLSL** (an SDF kernel module under `src/Puck.SdfVm/Assets/Shaders/Sdf`): keep
the World running, save the file, and type in its console or stdin:

```text
world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf
```

Each kernel installs as soon as its DXIL compiles, quickest first, and prints
`[shaders.reload: request=N installed pipelines=K kernels=…]`. A kernel
recompiles only when a file in its include closure changed, and each pixel
kernel's closure is its own stage: an edit to `passes/sdf-light-stage.hlsli`
recompiles the views kernel and nothing else, and every other
kernel is a cache hit. An edit to a module every kernel reads
(`passes/sdf-world.hlsli`'s modules) recompiles them all, and an edit to the
interpreter under `field/` recompiles the bake, and the slowest kernel's DXIL then decides when the
request settles. The reload writes into the build's shader cache, so the next
build publishes those kernels without compiling them. A change to a C# struct,
buffer layout or binding needs the C# loop instead.

The engine's build compiles no world: the debug world boots from its source
package (`--world worlds/debug`), compiled in memory and cached per user, so a
world edit is a `world.reload` and an engine edit rebuilds only the engine.

Measured on the reference machine (i5-8400, RTX 2060), with other builds holding
the CPU at the stated load:

| Loop | Phase | Time |
|---|---|---|
| C#, edit in `Puck.SdfVm` | `dotnet run` to `world.wait ready` | 22.6–24.7 s (31% load) |
| | of which `dotnet build`, nothing else changed | 10.6–14.3 s (44–54% load) |
| | of which the build after the edit | 12.7–15.3 s (12–65% load) |
| | of which boot to `world.wait ready` | 10.2–12.2 s |
A `Puck.World.Client` edit (one project, no world compile) and a light-stage
reload of the views kernel have no reading on the current code yet. The last boot reading was about 4 s of process start before the
document loads, 2.5 s of the render-capacity probe, and 4 to 5 s of window,
device, pipelines and first frames. The probe is measured from its emission
(`SdfProgramBuilder.MeasureCapacity`) and packs no program, so it allocates no
worst-case stream (118 million words on the debug world) and runs none of the
build's analysis.

### Verify a change

The [verification skill](../../.claude/skills/verification/SKILL.md) owns the
landing bar, CLI-copy procedure, GPU/heavy leases and interpretation of results.
Run the changed behavior and the affected tests on the merged head. Broader
qualification runs when the owner asks for it.

`puck gate` performs batch qualification: locked restore, solution build,
affected selection and static repository checks; `--gpu` adds device work.
The [CLI reference](../reference/cli.md#puck-gatethe-change-scoped-gate) owns its
ordered steps, concurrency, admission, recording and output, and
[CI](ci.md#which-ci-checks-the-local-gate-runs) maps those steps to hosted checks.
A package or project-reference change that drifts a locked restore needs
[`puck locks`](../reference/cli.md#puck-locksrestore-lock-files).

When a red proof would help, optionally run `puck laws prove <law> --fix <commit>`,
or pass `--file-list` naming the files of an uncommitted fix. It withholds the fix in a
worktree of its own, requires the law to fail there and pass once the fix is
restored, and prints the evidence block for the commit body. A law that passes
with its fix withheld cannot fail and is reported so; a build that fails in
either phase is refused rather than counted. Never prove a law by hand-reverting
files in a shared tree. See
[`puck laws prove`](../reference/cli.md#puck-laws-provea-law-against-its-fix).

### Engine changes and verification coverage

`Puck.Post` remains quarantined under `experimental/Puck.Post` and outside the
solution. Its historical stages are prior art, not current verification. Use
the live checks appropriate to the changed contract and report what remains
untested; no single check covers the complete engine.

While the debug world is the one live world, every other world and the
canary, parity, determinism, qualification, verdict and game fixtures sit under
`experimental/` with the laws that booted them, and their gates are not run
until the owner calls for qualification
([quarantined gates](../plans/open-items.md#quarantined-gates)). Until then
the live checks are the build, the suites' remaining laws, the generator
`--check` forms, and running the debug world.

The [World suites](../../tests/Puck.World.Tests/README.md) cover documents,
protocol, authoritative simulation, and shipped game state programs. The
architecture gate runs during builds, including `PUCKARCH008`: it rejects a
compiled dependency denied by `PuckArchitectureDeniedApi` in
`build/Architecture.props`, such as `System.Console` from `Puck.World.Server`.
The emulator batteries, Maths laws, build, and running application provide
additional checks with distinct scopes.

`puck parity --world <file>` boots a parity world, such as the quarantined
[authored parity world](../../experimental/tests/Puck.Parity/README.md),
offscreen once on Vulkan and once on Direct3D 12. Tick-scheduled captures receive
four verdicts: valid content, exact simulation-state hash agreement, the tick
each frame refreshed its bound regions at matching the capture's tick, and pixel
agreement under per-tile thresholds. Missing content or a camera inside geometry
fails before comparison; the state, tick and pixel checks are evaluated
separately after the content check passes. A failure records frames, a delta heatmap, and verdicts.

This comparison uses the contract beside the parity world, not a stored image
baseline. It requires both GPU backends but does not take over a display. Use it
for render-path, shader, presenter, or capture changes. Its authored stations
exercise specific contracts; passing them does not establish correctness for
every possible scene. See `puck parity --help` for the current command surface.

`puck canary --merge` runs the full canary set. It runs every
[real-World canary](../reference/cli.md#puck-canaryreal-world-behavioral-proofs)
a merge needs: the automatic set (headless, no environmental requirements) and
every canary requiring `gpu`, which includes the offscreen proofs on both
backends. A bare `puck canary` runs only the automatic set, so it never runs a
GPU proof. A GPU runs one canary or parity run at a time, since runs compete
for the device and for ports, and heavy CPU work beside one can push a leg past
its time bounds. Run the merge gate from a copy of the candidate's own CLI. `puck canary --merge --plan`
prints what the gate would run without a GPU. For a per-change GPU check on one
backend, `puck canary --capability gpu --backend vulkan` (or `directx`) runs
those proofs on that backend alone and names it in the verdict; it is not the
merge gate, which refuses `--backend` and holds both.

`puck canary`, `puck parity`, `puck test`, and `puck docs citations` share one
Release build of `Puck.World` per source state. The build lives in
`world-builds` in the [per-user directory](#per-user-directory), never in the
checkout. It is keyed by
git's view of the World's project closure, including uncommitted and untracked
changes. Running a second gate over an unchanged checkout does not rebuild, and
an edit to documentation or tests leaves the build in place. An edit under a
project the World builds produces a new key, so the next gate builds again.
[Where the World artifact is built](../reference/cli.md#where-the-world-artifact-is-built)
lists what the key covers and how old builds are pruned.

A verification check proves its own behavior independently: derive the
expected outcome another way than the code being checked, and prove that
behavior the check doesn't touch stays unchanged. Running the same replay
twice is an additional determinism check, never the proof of correctness,
because a replay hash covers only the explicitly hashed state trajectory, not
the journal or the whole document — undo and checkpoint restore need their
own assertions. A change that alters a persisted format (a world document,
an owned-identity document, an authority checkpoint or its journal, a replay
tape, or a release fixture) states which format it touches and whether old
data can still load: old data must never load under a new interpretation, so
a renamed or reshaped field makes the strict parser refuse the old form
rather than silently reinterpreting it.

Cross-host determinism has its own check.
[`puck determinism record`](../reference/cli.md#puck-determinismcross-host-determinism-attestation)
boots each scenario of
[the determinism manifest](../../experimental/tests/Puck.Determinism/determinism.json)
headless in-process and records every tick's per-system state hashes and the
world's document-level hashes. `puck determinism compare` names the first tick
and system, or document hash, where two recordings differ. CI records the
manifest on Windows and on Linux and fails on any divergence. Locally, record it
twice, or before and after a change, and compare the two streams. A deliberate
correction to simulation math or logic moves the hashes on both hosts alike, so
the cross-host compare still agrees and needs no re-recording. A host-dependent
difference, such as a writer whose line breaks follow the operating system, is
caught at the tick and system or the document hash it moves. When a change adds
a system or a document hash the manifest's worlds do not reach, add a scenario:
a small world, a few hundred ticks, and seats, intents or cell writes that make
the system run within them. Name the components it moves in its `exercises`;
`record` refuses a scenario that leaves one unchanged, and a law fails when a
per-tick component other than the declared topologies is exercised by no
scenario. The manifest covers no portal transfer, since that needs more than one
world instance.

For changes under `src/Puck.Maths`, also run the maths law suite. A plain
`dotnet test` runs the Default tier, the everyday gate and the only tier whose
line coverage CI counts (about half a minute on the reference machine);
Deep and Exhaustive cases are explicit, so a run opts into them, and each tier
is selected by its `tier` trait. Pass `-nodeReuse:false` to the build and the
restore only: `dotnet test` forwards a switch it does not know to the test
host, which rejects it ([verification](../../.claude/skills/verification/SKILL.md)
records the observed failure).

```powershell
dotnet build tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release -nodeReuse:false
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build --explicit on --filter-trait tier=Deep
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --no-build --explicit on --filter-trait tier=Exhaustive
```

Without `--explicit on`, a `tier=Deep` or `tier=Exhaustive` filter selects no
test; one law of a tier is selected by its id, the case's display name
(`--explicit on --filter-display-name <law-id>`).

### Device laws

A test class that opens a hardware GPU device carries
`[Trait("Category", "Gpu")]`, which the build requires of it (`GPU001`, from
`Puck.Analyzers`). The same trait schedules it. Every test project links
`tests/Shared/GpuDeviceCollection.cs`, whose collection factory puts every
class with the trait into one collection that disables parallelization. xUnit
runs that collection after all the parallel ones have finished, one law at a
time. A device law therefore never shares the GPU, the driver's shader
compiler or the processors with another law, and a law that turns on the
Direct3D 12 debug layer, which removes every device the process holds, runs
with no other device alive. A plain `dotnet test` of a suite needs no option to
keep its device laws apart. A class that names a collection of its own keeps
it, and that collection must disable parallelization too.

A run beside another GPU leg leaves the device laws out with
`--filter-not-trait Category=Gpu`; `puck affected` runs its suites that way, as
does CI's test job on its GPU-less runner, and `puck gate --gpu` runs the device
laws alone with `--filter-trait Category=Gpu`. CI's runner has no GPU yet
exposes a Direct3D 12 adapter that DXGI does not flag software, so
`DirectXTestDevices.Hardware()` does not skip there, and an SDF interpreter
kernel on it does not finish: the trait, not the skip, keeps a device law off
that runner.

### Game changes

Run the application to check composed game behavior, alongside relevant World tests:

```powershell
dotnet run --project src/Puck.World -c Release -- --world worlds/debug --devices none --exit-after-seconds 2
```

`--devices none` opens no gamepad and drops the window's keyboard and mouse
events, so the person at the machine never drives the run; agents always pass it
([the ruling](../../.claude/skills/puck-world/SKILL.md#an-agents-world-opens-no-physical-device)).

Use `Puck.World` for game verification. Do not add a `--validate-*` mode or
a `Puck.Post` stage for game-specific behavior unless explicitly requested.

The console and process stdin use the same command registry. Accepted results
are written to stdout and refusals to stderr; host log lines go to stderr too,
so stdout carries only answers. A driver that needs their combined
arrival order can merge the streams. End a run with `wire.errors` and check
`[wire.errors: 0 rejected]` when the result depends on every command being accepted.
The [World guide](../../src/Puck.World/README.md) owns the current scripting and
replay procedures. Creation review—isolated creation turntables with pinned
content time and camera poses—has no runnable host in `Puck.World`, and no plan
adds one. Treat it as an absent capability, not a pending one.

### Emulator changes

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --artifacts artifacts/hgb-post
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --bios <GBA_bios.rom>
```

The Humble battery's reference corpora are declared in its `corpora.json`
(pinned archive, version, SHA-256); `--fetch-corpora` fills the local cache
once and the stages resolve it without configuration. A run reports what each
corpus ROM did and exits 1 while any ROM fails; no file records an expected
outcome. The recipe above
writes to CI's `artifacts/hgb-post` directory, where a run leaves its report table,
its JSON summary and its JUnit results (all three written by `PostReport` in
`src/Puck.Machines.Post`). Iterate with `--filter`.
The Advanced battery works the same way: its corpora are pinned in its own
`corpora.json`, the BIOS and commercial cartridges are command-line flags, and
it exposes lockstep, trace, I/O-dump, render-hash, and divergence diagnostics;
see its project README.

### Performance changes

There is no maintained cross-engine benchmark score. `Puck.Bench` remains
quarantined under `experimental/`; do not build or revive its old suite.

Performance is judged by counted work, code and disassembly, and only counts
set a ceiling or gate a change. Timings (`puck bench`, `world.gpu-timing`,
`world.fps`) are evidence read beside the counts of the same run, never alone,
and are taken on a quiet machine with no build, test or other GPU work beside
them ([the rule](../decisions/rendering.md#the-pipeline-foundation)).

Two tools read the counts. For a live World workload, wait for rendered
frames, then sample `world.counters`: its `gpu` section is the per-pass counted
work (dispatches, barriers, uploads, created objects), its `allocation` section
says whether reading every count allocates, and every other section is a
registered engine counter, including the server's `state.arena`, `state.rules`
and `state.search`, and in a rendering World the shader compiler's
`shaders.compiler`, the SDF kernel loads under `shaders.sdf-kernels`, and, on
Vulkan,
`procedures.vulkan`. An owner whose counts need nothing more than a named set
of kinds holds a `WorkCounterSet` rather than writing its own source.
`world.budget` distinguishes live program size from
reserved capacity. For a repeatable reading, run
[`puck counters`](../reference/cli.md#puck-counterswork-counter-collector): it
boots the authored counters workload offscreen on each backend, writes a report
with every count tagged by class, and fails when a deterministic count differs
between the backends. Keep the report from before a change and compare it with
the one after, using `puck counters compare`.

Compare the same document, camera, resolution, quality settings, backend and
build configuration before and after the change. Report these conditions with
the result. See
[World graphics options](../../src/Puck.World/README.md#graphics-options).

### Browser engine changes (`Puck.World.Browser`)

Remove `C_INCLUDE_PATH` from the build shell first if the machine has a Cosmocc toolchain installed
(see "Hardware and toolchain cautions" below)—otherwise every native asset
compile in the steps below fails with libc header collisions that have
nothing to do with the change under test.

```powershell
dotnet build src/Puck.World.Browser -c Release
dotnet test tests/Puck.World.Browser.Tests -c Release
dotnet publish src/Puck.World.Browser -c Release
```

`tests/Puck.World.Browser.Tests` links `Engine/*.cs` as source and runs under
the ordinary net11.0 test host—no wasm runtime needed to exercise the pure
core. The wasm-specific proof is the Node harness, which needs the AppBundle
the `dotnet publish` line above produces and the system Node on `PATH`. Its
package declares no `engines` requirement, and CI runs it on the Node version
the `verify` job in `.github/workflows/browser.yml` pins (`node-version`). Use
the system install, not a version manager:

```powershell
dotnet publish src/Puck.World.Browser -c Release
cd src/Puck.Dashboard/src/portal
node --test tests/engine-wasm.test.cjs
```

The `.puck` authoring surface (a mounted workspace, `CompileSource`,
`ComposeSource`, and the language server's `Lsp`/`LspIdle` round trip) has its
own harness over its own fixtures, run from the repository root:

```powershell
node --test tests/Puck.World.Browser.Tests/wasm/sources.test.mjs
```

That harness and its fixtures are quarantined at the same paths under
`experimental/` while the debug world is the one live world.

Both harnesses skip themselves by name (never silently pass) when the payload
(`src/Puck.World.Browser/bin/Release/net11.0/publish/wwwroot`) is absent. The
`browser-parity` baseline the live harness compares against prices one fixture
document; the state hashes it also recorded are quarantined under
`experimental/` with the worlds they need.

See `src/Puck.World.Browser/README.md` for the AppBundle's real file layout
and sizes, the exact `[JSExport]` surface, the trim-warning baseline, and the
one verified scope boundary (no emulator core, so a document authoring a
`screens[].source.machine` engine—the shipped island's arcade district among
them—refuses by name rather than crashing).


## Working with world documents

The validator checks the complete document's semantics. A valid document must be buildable;
builders do not repeat validator checks. When a document field or polymorphic
kind changes:

1. Update the nullable model and XML documentation.
2. Add all semantic validation to `WorldDefinitionValidator`, which runs over
   the entire composed candidate document rather than the changed section
   alone—including an owned identity's document, which is validated the
   same way.
3. Register the type in `WorldJsonContext`; a polymorphic kind also needs its
   `[JsonDerivedType]` line.
4. Verify by running `Puck.World` and round-tripping the document over stdin.

`src/Puck.World.Schema/README.md` documents the serializer's construction
behavior; the procedure above is the complete add-a-field procedure.

## Configuration and diagnostics

No environment variable switches Puck. Durable configuration belongs in the
world document (genlock, for example, is the document's `host.genlock`); live
operations belong in console verbs; a run's diagnostics are flags; and a
committed test baseline is regenerated by a verb with a `--check` twin.

| To | Use |
|---|---|
| Turn on the backend's validation layer | `Puck.World --debug-layers` (the basic API layer), `puck canary --debug-layers`, `puck parity --debug-layers`, or a release profile's `debugLayers` for `puck qualify`, which all pass the basic level; the Vulkan device laws follow `HeadlessVulkanDevice.Validation` |
| Capture chosen frames in PIX or RenderDoc | `Puck.World --capture-tool pix` (Direct3D 12) or `--capture-tool renderdoc` (either backend), then the console's `gpu.capture <path> [<frames>]`; the tool must be installed (PIX on Windows under `Program Files/Microsoft PIX`, RenderDoc under `Program Files/RenderDoc`). Read a capture in `qrenderdoc`, or export its calls with `renderdoccmd convert -f <file>.rdc -o <file>.zip.xml -c zip.xml` ([Vulkan](../rendering/vulkan.md#gpu-captures), [Direct3D 12](../rendering/directx.md#gpu-captures)); PIX's analysis needs Windows developer mode, and a PIX timing capture an answered UAC prompt |
| Hunt with the GPU's own checks | `Puck.World --debug-layers full`: Direct3D 12 GPU-based validation with state tracking and synchronized queue validation, Vulkan GPU-assisted validation and best practices with NVIDIA's; the first frame takes minutes on the debug world while the layers instrument every pipeline, so no verb or gate uses it |
| Re-record a test baseline | `puck baselines <artifact>`, and `puck baselines <artifact> --check` to compare (see the [CLI reference](../reference/cli.md#puck-baselinestest-baselines)) |
| Run an opt-in test harness | the test's own explicit tests or fixture, named in its project's README |

The `ENV001` analyzer in `Puck.Analyzers` holds this in every build: it refuses
any read of the process environment whose variable is not named, with its
reason, in `EnvironmentReadAllowlist`. That list holds only values the operating
system, the .NET SDK, or the CI host defines, such as `PATH` and the
`GITHUB_*` variables the CI-facing verbs read. Both emulator batteries take
their inputs on the command line.

## GPU support and shader builds

The supported GPU floor covers RTX 2060, RTX 4070, the RDNA3 Steam Machine,
and the RDNA2 Steam Deck. Shaders target Vulkan 1.3 / SPIR-V 1.6 and Shader
Model 6.6. Do not raise that floor without evidence for every supported GPU.

DXC compiles the same HLSL sources to SPIR-V and, on Windows, DXIL during the
build ([freshness](../reference/shaders.md#freshness)); a Windows build of one
project, the development loop, skips SPIR-V unless
`-p:PuckShaderSpirvEnabled=true` asks for it, which `tests/Directory.Build.rsp`
does for a build of one suite. `dxc`
must be on `PATH` for these built-in kernels, and live pipeline sources compile
with the same DXC, resolved as the [shader guide](../reference/shaders.md#one-off-shaders)
describes. The build compiles through the runtime's `ShaderCompiler` into the
per-user shader cache, keyed by each source's include closure, its options and
the DXC, not by the checkout. A cold machine compiles missing outputs on the
cores MSBuild grants, the longest first. A fresh worktree reuses valid entries
with matching closures, options, and toolchain; editing one `.hlsli`
invalidates only the outputs that include it. No special build flags are
needed. The [shader reference](../reference/shaders.md#freshness)
owns the cache, its `PuckShaderCacheDirectory` override and the publication rules.
The `Puck.World` build also packages every pipeline source a shipped
world names into the [package store](../reference/shaders.md#the-builds-package-store)
beside the worlds, so a pipeline source row does one of two things. In a
developer checkout with DXC, an unedited shipped source loads its stored package
and an edited or new source compiles live, so authoring keeps its loop. In a
packaged runtime with no DXC, the shipped sources load their packages, nothing
compiles, and a source with no stored package is refused by
`SHADERPKG_ABSENT`. The Direct3D 11 camera and probe kernels and the display
encode the swapchain compositors draw compile at build, so no device compiles
them. A change to the
SDF C# ISA
must update the HLSL decoder in the same change. The SDF VM README lists
the exact C# and HLSL contract pairs and bytecode rebuild procedure.

The RTX 2060 and the RTX 4070 are the GPUs available for local testing, and
the RTX 2060 is the floor device counted work is judged on. Claims about the other
supported GPUs require vendor or driver documentation and should be framed as
unverified when no device run exists.

## Hardware and toolchain cautions

- The owner's development machine (Ryzen 7 7800X3D, RTX 4070) powers off
  without a bugcheck, a dump or a hardware-error record under sustained load, as
  a thermal or power trip does, most often while a windowed World renders and
  several lanes build and test beside it. Work runs on it at its normal load
  (bounding the load would hide the trip the diagnosis needs to catch); a lane
  whose session ends with the machine resumes from its worktree. LibreHardwareMonitor logs its sensors to a
  CSV each second beside its executable (the winget package's folder under
  `%LOCALAPPDATA%\Microsoft\WinGet\Packages`); after a trip, the last rows name
  the sensor that ran away.
- A build cut off by a power loss or a killed session can leave a truncated
  assembly under a project's `obj/` (seen as `obj/Release/<tfm>/ref/<Project>.dll`),
  and the next build fails reading it. Delete that project's `obj/` and build
  again; never test the binaries a failed build left behind.
- In PowerShell, quote colon-bearing switches forwarded after `--`: use
  `puck lease heavy --holder <lane> -- dotnet build Puck.slnx -c Release '-nodeReuse:false'`.
  Without the quotes, PowerShell can hand the child `-nodeReuse:` and `false`
  as separate arguments, which MSBuild refuses with MSB1041.
- Under a Remote Desktop session an NVIDIA GPU can hold its idle clocks (P8 or P5,
  a few hundred MHz) through a whole windowed run, so `host.frames` and
  `world.gpu-timing` read there are no frame-time verdict. Check the clocks with
  `nvidia-smi --query-gpu=pstate,clocks.gr --format=csv -l 5` beside the run, and
  take a timing on a console session. A capture's replay runs the GPU at its
  working clocks there, so RenderDoc's per-event GPU durations
  (`GPUCounter.EventGPUDuration`) compare one kernel across two builds or two
  backends; on an RTX 4070 over Remote Desktop the debug world presented about
  every 100 ms and the GPU held P8 at 210 MHz under Vulkan, P5 at 400 to 540 MHz
  under Direct3D 12, which made Vulkan's passes read twice as slow live while
  their replays ran faster than Direct3D 12's.
- Under Remote Desktop the client draws the cursor shape the session sets, so
  where the World hides the system cursor (`NativeWindowOptions.HideMouseCursor`)
  the client shows none at all: the World's drawn cursor is then the only
  pointer, and it shows only while the game sees pointer motion. Over a
  passthrough pane, which withholds that motion from the game, the World shows
  the system arrow instead (`INativeWindowCursor`). The session's mouse arrives
  as absolute raw input, which the window turns into relative motion; the
  pointer position, the pane hit and the passthrough client point come from
  `WM_MOUSEMOVE` either way. The client never follows a warp: the session's
  `SetCursorPos` succeeds and the client's next report puts the cursor back
  where its own cursor is, windowed or fullscreen, with or without
  `ClipCursor` (which only pins the session's cursor and stops all motion). So
  a camera drag over Remote Desktop ends where the client's cursor meets the
  edge of its screen, as World of Warcraft's mouse look does over the same
  session, and the window's pointer lock never warps a cursor an absolute
  report moved last (`Win32AbsolutePointer`). A local mouse, or a remote tool
  that sends relative motion, drags without end. Only the
  foreground window locks, and a test World an agent drives with
  `player.signal` locks the session's real mouse while it is foreground:
  minimize it first (a minimized window has no client area to lock to). The
  Remote Display Adapter reports its own signal
  rate (32 Hz in one session, `Display pacing: signal 32 Hz` on the boot log),
  which the pacer presents at whatever `host.targetHertz` says.
- In Git Bash on Windows, `python3` and `python` can resolve to the Microsoft
  Store alias, which waits on standard input until the command times out. Use
  the repository's own tools, or `sed`, `awk` or `perl`, instead.
- The Codex Windows sandbox reads profile folders through an inherited
  `CodexSandboxUsers` read-and-execute (RX) grant. A folder with inheritance
  disabled is unreadable to it; affected locations include
  `%LOCALAPPDATA%/Microsoft SDKs`, `%APPDATA%/NuGet` and `%LOCALAPPDATA%/NuGet`.
  MSBuild can then fail inside the sandbox with MSB4184 from
  `ToolLocationHelper.GetPlatformSDKLocation`, and NuGet with
  "Failed to read NuGet.Config due to unauthorized access". The machine's
  owner fixes the folder's permissions by re-enabling inheritance or granting
  `CodexSandboxUsers` read and execute on that folder. A folder a sandboxed run
  created, such as the checkout's `.codex/`, can end up owned by a
  `CodexSandbox*` account (`(Get-Acl <path>).Owner`); every sandboxed job then
  fails within seconds with `windows sandbox failed: helper_unknown_error`, and
  the sandbox log under `~/.codex/.sandbox/` names the folder. Move it aside,
  recreate it as the user, and copy its contents back.
- A workspace-only sandbox cannot write the per-user `Puck` caches just because
  it can build the checkout. Shader builds accept the
  `PuckShaderCacheDirectory` MSBuild override.
  `puck parity` and `puck counters` also need a writable `world-builds` store:
  `WorldArtifactStore` retries an access-denied lock open as contention until
  its deadline, so a quiet wait before any World process starts can be a cache
  permission block. `puck laws prove` needs its `law-trees` cache or writable
  Git metadata for its fallback worktree. Report these as setup blocks, with
  no GPU or red-law verdict, and run them in an environment with those writes
  available; changing `LOCALAPPDATA` does not override Windows known folders.
- If a sandboxed C# build reports CS0016 access denied for an output inside its
  writable worktree, retry with `-p:UseSharedCompilation=false`. This runs the
  compiler inside that build's sandbox instead of using a shared compiler server;
  deleting the output alone does not resolve a server running with different access.
- Git Bash's `kill` and `pkill` do not reach native Windows processes, even
  when they report success. Stop a process with PowerShell
  `Stop-Process -Id <pid>` and confirm it is gone. Never search from `/` in Git
  Bash: it walks every mounted drive.
- A physical controller connected when `Puck.World` boots writes `[gamepad]`
  diagnostics to standard error. A canary or manual check that asserts on
  standard error must allow for them.
- On the reference Windows/RTX 4070 system, enabling the Direct3D 12 debug
  layer can make `D3D12CreateDevice` fail with `0x887A0007`; it is opt-in.
- Vulkan import of a Direct3D 12 shared texture on NVIDIA uses handle type
  `D3D12_RESOURCE` (`0x40`).
- Direct3D 12 compute descriptor slots are packed in binding order. Derive
  pool sizes with `GpuDescriptorPoolSizes.ForSets`; do not treat a binding
  number as a heap offset.
- Full GPU removal can wedge the in-process NVIDIA Vulkan ICD. TDR recovery is
  supported; physical removal may require a new process.
- On a variable-refresh display, a window's pacing depends on whether it is in
  front: in front it runs variable refresh (up to 144 Hz on a 60 Hz desktop on
  the RTX 2060's display), behind another window it is composed at the desktop
  signal. Record which one a `host.frames` reading is, and the GPU clocks beside
  it. NVIDIA's Vulkan driver engages variable refresh only for a swap chain
  created while the window is in front, so a Vulkan World launched behind
  another window stays composed after it is brought forward until a resize
  recreates its swap chain ([host pacing](../reference/hosting.md#host-pacing)).
- The live Pocket Camera path uses CPU pixels. The zero-copy camera export
  infrastructure is built ahead for re-hosting and has no live check.
- RADV may select wave32 or wave64. New wave-intrinsic kernels must be
  subgroup-size-independent or explicitly request a supported size.
- The .NET host picks the SDK from the working directory's nearest
  `global.json`, and MSBuild picks a project's SDK from the project's; with
  neither, both roll to the newest SDK installed, preview or not. A verb or
  test that builds a scratch project outside the checkout takes its directory
  from the run-directory policy ([temporary directories](#temporary-directories)),
  pins it with `CliScratchDirectories.PinSdk`, which copies the checkout's
  `global.json` in, and runs the SDK command from that directory. An SDK
  command against a checkout project runs from the checkout.
- The pinned .NET 11 JIT (`11.0.100-rc.2.26504.105`) can emit GC info that
  reports a GC pointer live in a callee-saved register (`rbx`, `rsi`, `rdi`,
  `r12` to `r15`) at the return address of an inline P/Invoke, which the
  runtime's rule forbids: a thread inside the call is walked from its
  `InlinedCallFrame`, whose callee-saved register locations are null. A
  collection that lands while the thread is in that native call ends the
  process with `Fatal error. Internal CLR error. (0x80131506)` and exit code
  `0xC0000005`, on whichever thread allocated, so the printed managed stack
  names an innocent allocator. A dump's native stack shows
  `coreclr!GcEnumObject` (inlined `PromoteCarefully`) under
  `GcScanRoots`/`StackWalkFrames` reading address zero. The shape that
  triggers it is a CsWin32 COM call (`list->Method(...)`, whose inlined `this`
  is a byref) in an optimized loop under register pressure; the .NET 10 JIT
  compiles the same code correctly. `DirectXCommandListRecorder.RecordBackBuffer`
  is the one method a debug-world run jits with that GC info, so it carries
  `[MethodImpl(MethodImplOptions.NoOptimization)]` until the pinned SDK moves to
  a JIT without the defect. To check a build: capture a running `Puck.World`
  with `dotnet-dump collect --type Heap`, disassemble each jitted method with
  SOS `!u -gcinfo <MethodDesc>` in a debugger engine (`dotnet-dump analyze`
  has no disassembler on Windows), and look at each inline P/Invoke: a
  `mov byte ptr [rN+4],0` (the frame going preemptive) followed by `call`. A
  callee-saved register listed live at the instruction after that call is the
  defect. Remove the attribute only when no such site remains with it gone.
- Incremental builds can retain corrupted reference assemblies. Confirm
  suspicious behavior in a fresh worktree before attributing it to source
  changes, then clean only the affected `bin`/`obj` directories.
- GBA co-simulation compares instruction deltas because mGBA rebases cumulative
  cycle counters each frame. Puck's exposed PC is four bytes ahead of mGBA's
  pipeline representation.
- Windows Smart App Control, when it is on, blocks unsigned assemblies a build
  has just produced (`FileLoadException` `0x800711C7`), including a file-based
  app's never-seen Debug build; relocating the runfile cache does not help, so
  build or run those with `-c Release`. When a dependency is blocked rather
  than the entry assembly, `Puck.World` reports `[world] definition refused: …
  The type initializer for 'Puck.World.WorldJsonContext' threw an exception.`,
  which is not a document defect. Confirm a block in the
  `Microsoft-Windows-CodeIntegrity/Operational` log (event 3077). Rebuilding does
  not help, because deterministic builds reproduce the blocked bytes. Smart App
  Control has no exclusion list; the machine's owner turns it off in Windows
  Security.
- A machine with a Cosmocc toolchain installed may carry `C_INCLUDE_PATH`
  pointing at its `include` directory in the ambient shell environment. That
  path leaks into every `clang`/emscripten invocation a `Puck.World.Browser`
  `browser-wasm` build or publish shells out to and collides with
  emscripten's own libc headers (`COSMOPOLITAN_C_START_` redefined, `bool32`
  unknown type, dozens of "expected function body after function declarator"
  errors from `libc/calls/calls.h`). Remove `C_INCLUDE_PATH` from the shell environment before building or
  publishing that project; this is host contamination, not a project or
  workload defect.

## Verification practices

These practices keep each result tied to the behavior and configuration it is
meant to establish.

- Every durable artifact declares its own falsifier. A canary names what in
  the observation is bound to the variable under test — a pixel diff where
  nothing in frame tracks the variable proves nothing. A design document
  states the premises that would kill it, as re-runnable checks.
- Never write a status column. A status claim duplicates what the code
  answers better; a decision records what the code cannot answer — why, what
  was rejected, where a boundary sits — and stays irreplaceable even when
  stale. Keep decisions, delete status, and generate inventories or do
  without them.
- Security claims default the other way from feature claims. For a feature,
  unverified means not-done. For an escalation, unverified means still open —
  the cost of the other default is shipping a hole because its citation
  rotted.
- Verify by running, and by content. Exit code 0 is not success; audit the
  streams. A commit hash absent from a branch does not mean its content is
  absent, and a search hit is not a repository fact until the file is
  tracked.
- Choose verification inputs that differ from worked documentation examples.
  A nearby value can expose a boundary defect that the example does not.
- Run a control in the same configuration as the test and change exactly one
  variable. Remove confounding state, prove that the exercised command reaches
  the intended path, and choose a measurement that distinguishes the hypotheses.
- Read the refusal or failure reason. A control must fail at the intended check,
  with the intended message and error signal, rather than merely returning a
  failure.
- Prove a new law or canary by withholding the fix it pins: with the fix
  reverted and the law kept, the law must fail at its intended assertion, and
  with the fix restored it must pass. A law that passes with the fix withheld
  pins nothing.
- Re-run a failure seen under load once, alone, before believing it. A timeout
  or a wait for a port, listener or readiness that passes alone is a flake to
  report with its message; a wrong pixel, hash, count, refusal or asserted value
  is a failure whatever the load.
- When converting a prose rule into an automated check, ensure the derived rule
  rejects every prohibited case. Prefer a strict, observable check when the
  intended scope is uncertain; an overly broad check can pass without testing
  anything.
- When correcting a claim, update its tables, examples, summaries, comments,
  and cross-references in the same change so no contradictory source remains.
  Before a commit message claims every instance was corrected, search for the
  old text and confirm none remains, and re-read the whole edited paragraph: a
  replacement that matched mid-paragraph can leave the old claim standing
  beside the new one.
- Loop a first-error-wins instrument until a pass finds nothing new. A loader,
  parser, validator or compiler reports the first problem in each input and
  stops, so one pass samples a defect surface rather than exhausting it. Report
  how many passes the surface took.
- A check must be able to fail visibly. Never suppress a command's error stream
  inside a check, and read a program's exit status from the program itself,
  never from a pipeline added to format its output: a bash pipeline reports its
  last command's status unless `pipefail` is set. Commit before a checkout, so a
  dirty tree cannot silently refuse it, and assert the state you believe you set
  up.
- Allocation laws, Post stages and bench diagnostics measure through
  `AllocationWindow` (`Puck.Abstractions.Counting`), the one helper over
  `GC.GetAllocatedBytesForCurrentThread`; nothing else reads the counter.
  `Least` is the zero law. It takes the least of up to 16 windows, because a
  method's first run can charge the runtime's own work to whichever window is
  open. When every window allocated, it runs the body again under the runtime's
  `AllocationSampled` event and fails naming the window, the GC mode, and the
  types sampled on the measuring thread; the event carries no stack, so the
  culprit is named by type. `Measure` returns the same least as a count, for a
  ceiling. Both run the body more than once, so a window must leave its subject
  able to run the same path again: advance to fresh ticks or rows rather than
  repeating ones already consumed. `Total` runs a body exactly once, for a flow
  that cannot repeat, one tick sampled for a median, or a batch divided into a
  mean; a zero law never reads it. A background collection inflates the
  counter: it retires the thread's allocation context without subtracting the
  unused remainder, up to about 8 KiB the thread never allocated. Test and validation hosts therefore run
  with blocking collections (`ConcurrentGarbageCollection` in
  `Directory.Build.targets`). Setting `DOTNET_gcConcurrent=1` brings the false
  failures back, so don't set it when you run these suites.
- A host, service or object that runs background workers returns from its
  disposal only after every worker has joined: it exposes `DisposeAsync`, or a
  `Completion` task that completes after the last worker returns and that
  `DisposeAsync` awaits, and a law holds a worker inside its owner and fails if
  disposal or `Completion` returns first. A host awaits its services'
  `DisposeAsync`, so a service that stops without waiting hides from every
  teardown above it. `LocalControlServer.Dispose` only begins the stop, because
  its caller may be the pump its sessions wait for; `Completion` and
  `DisposeAsync` are the wait.
- A law that gives a host a state directory hands both to the shared
  `TemporaryDirectory` (`tests/Shared`): `Own(host)` registers the owner, and
  disposing the directory disposes its owners last registered first, awaiting
  `DisposeAsync` when there is one, then deletes, retrying while a handle closes.
  One bound (`teardownBound`, `TestLiveness.Bound` by default) covers owners and
  delete, and an owner that does not return fails the law by its type name with
  nothing deleted. The files' sizes and last write times are compared between
  delete tries as a secondary net; it sees only a write that lands before the
  delete succeeds, so shutdown is established by the owner's contract and never
  by watching the directory.
- XML documentation is a compile-time dependency. With warnings treated as
  errors, an unresolved member reference produces CS1574; verify documentation
  changes with the compiler when they affect member references.
- xUnit runs the laws of one class one after another and runs classes side by
  side, so a suite takes at least as long as its slowest class. When a class's
  laws each start processes, builds or scratch repositories, put its fixtures
  and helpers in an abstract base and its laws in sealed classes over it
  (`GateRunLaws`, `ShaderBuildTargetsLaws`). A law that proves a real process
  boundary keeps the process; share its cost instead, for example by planning a
  gate's change once and executing it under each runner.

## Code and documentation conventions

- Public APIs use XML documentation that describes current behavior, parameter
  units, ownership, lifetime, failure behavior, and determinism where relevant.
  Do not narrate the change that introduced the API.
- Name files, types, tests and law ids for their subject, never their history:
  no `Port`, `Migrated`, `Remediation` or retired-tool names, and no prose
  saying where code came from ("ported from", "moved from"). Retiring a surface
  deletes its name rather than calling it "the retired X". Provenance belongs
  in the commit message.
- A comment earns its place by stating what the code cannot and a reader would
  act on wrongly without: an invariant, a sign convention or unit, a packing
  layout, an external specification or hardware citation, or a coupling the
  compiler cannot check. Name the other side of such a coupling, and prefer
  removing the duplication that makes it necessary. If deleting a comment loses
  nothing the code and git history can't recover, delete it. Never write dates,
  commit references, citations of plans, reviews, or conversations, accounts of
  a defect or its fix, changelogs, commented-out designs, obsolete alternatives,
  capitals for emphasis, or counts and line numbers that drift. Delete a stale
  comment rather than replacing it with a longer one.
  `puck scan --only comment-smells` classifies the inline comments that break
  these rules.
- `*Options` denotes configuration-bound data.
- Command-module conventions are documented on `ICommandModule`; the split
  between the headless core and the presentation layer that adds the GPU host
  is documented on `WorldBootComposition`.
- CA1502, CA1505, and CA1506 are suggestion-level design signals. Simplify a
  design when they identify real coupling; do not add facades solely to change
  a metric.
- A strictly versioned format (a wire key, checkpoint or journal version,
  replay tape version, magic, or `puck.<name>.vN` schema) carries its own
  version or magic, which its codec writes in its header or handshake and
  refuses any other by name before any state changes. Nothing records a
  layout, so a change to a format's layout raises its version (or the digit
  of its magic) in the same change, to a value it never had before; a version
  that stays put or returns while its layout moves misreads old data instead of
  refusing it.
- A call through an unmanaged function pointer (`delegate* unmanaged`, any
  calling convention) may not use a signature that mentions a type parameter
  except behind a pointer: `Puck.Analyzers` fails the build with INTEROP001,
  because the call would throw `MarshalDirectiveException` at run time. Call
  through a closed signature, or cast the entry point to a view that passes the
  value as `T*`; declaring or passing a generic signature is fine.
- Code never reads an environment variable to switch itself: `Puck.Analyzers`
  fails the build with ENV001 on any `Environment.GetEnvironmentVariable`,
  `GetEnvironmentVariables`, or `ExpandEnvironmentVariables` call outside
  `EnvironmentReadAllowlist`, and on any read whose name is not a compile-time
  constant. Make a switch a flag, a document or profile setting, or a test
  fixture instead ([configuration and diagnostics](#configuration-and-diagnostics)).
- A listener takes its address from configuration and defaults to loopback
  (`127.0.0.1`, or `::1` where the code is IPv6-first). A QUIC listener is the
  one the address cannot narrow: on Windows, msquic opens its UDP port on
  `0.0.0.0` and `[::]` whatever address it is given, and Windows Firewall asks
  about every interface bind once per executable image path. So every process
  the repository starts locally that may listen runs under the shared host,
  `dotnet <assembly>.dll`, never a per-worktree apphost. `Directory.Build.targets`
  points `dotnet test` and `dotnet run` of every test project, and of an
  executable that sets `PuckRunUnderDotnetHost` (the World and the silo), at
  `dotnet exec <assembly>.dll`; the apphost is still built, because xUnit v3
  requires one and a published executable ships its own, but every test
  assembly refuses to run under it (`tests/Shared/SharedTestHost.cs`, exit 87),
  so never start a suite's `.exe` directly. Every `puck` runner
  starts `dotnet <suite>.dll`, and canaries, parity, counters and `puck test`
  start `dotnet Puck.World.dll`. One firewall decision for `dotnet.exe` then
  covers every worktree and run. `Puck.Analyzers` fails the build with NET001
  on `IPAddress.Any`, `IPAddress.IPv6Any`, Kestrel's `ListenAnyIP`, a
  port-only `TcpListener` or `UdpClient`, and any literal spelling `0.0.0.0`,
  `[::]` or a `*`/`+` wildcard URL host, outside the deployment sites
  `AnyAddressBindAllowlist` names with their reasons.
- A document field that carries a state, zone, rule, table, pattern, topology,
  generator, field, or dynamics name is registered in `WorldNameRegistry`
  (`src/Puck.World.Schema`); `puck registry --check` fails on an unregistered
  name-shaped member or a stale `docs/world-name-registry.md`, and
  `puck registry` rewrites the table.
- Derive descriptor counts, pool sizes, strides, and capacities from the data
  that defines them.
- .NET 11 is the only target. Verify runtime behavior and measure before preserving a hand optimization or making a performance claim.

## Documentation policy

Follow [Writing documentation](documentation.md) for prose, titles, filenames,
and navigation. Keep each document useful to the current tree, and give each fact one
authoritative home. Architecture manuals belong under docs/architecture;
contributor and CI procedures belong under docs/development; active plans and
settled cross-project decisions belong under docs/plans and docs/decisions.
Project-owned contracts and implementation detail belong beside their source
project. Other documents summarize and link to the owner.

Docs name no dates and no commit SHAs; verification evidence belongs in the
commit message that lands a change (see
[Write examples and claims precisely](documentation.md#write-examples-and-claims-precisely)).
Retire rollout logs, audits, migration diaries, commit archaeology, and
superseded plans. When moving or retiring a document, move every live contract,
limitation, and procedure to its canonical home before removing the old copy.
The root [README](../../README.md) routes to the document set; update its
routing whenever the set changes.

The rules for coding agents live in one file, the root
[`AGENTS.md`](../../AGENTS.md). Codex reads it, and so does Claude Code, which reads `AGENTS.md` natively only when no Claude Code memory
file (a CLAUDE.md, a CLAUDE.local.md, or one under `.claude/`) exists in the
directory or above it. The repository therefore commits none, at any level.
Antigravity also reads `AGENTS.md`; no `GEMINI.md` exists, on purpose. A second
rules file duplicates instructions and consumes its shared rules budget.

## InternalsVisibleTo is forbidden

No assembly grants another its internals, test assemblies included. If another
project or a test needs a member, make the member public. A grant hands a whole
assembly's internals to a friend, invisibly at every call site; widen the member
instead. If a member looks wrong to make public, that is evidence about the
design: say so. The build refuses both forms, the `[assembly:
InternalsVisibleTo]` attribute and the csproj `<InternalsVisibleTo>` item, with
IVT001 (`Puck.Analyzers`).
