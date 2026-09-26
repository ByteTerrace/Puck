# The puck command line

Normal builds generate the registered world JSON and shader bytecode from their
tracked parents. See [generated assets](../../build/README.md) for build,
source-control, and packaging rules.

`Puck.Cli` provides `puck`, the repository's developer command line. Its
commands share the System.CommandLine tree declared in `PuckRootCommand.cs`.
`PuckRootCommand.Create` takes the CLI's one `TimeProvider`, and every deadline a
verb puts on a process, a connection, a lease, or a request runs on it, so a law
drives the verb's deadline with a virtual clock.

## Conventions

Agents and people drive this CLI all day, so every verb behaves the same way.
`CliConventionLawTests` walks the real command tree and checks the mechanical
half of these rules. A verb that still breaks one is recorded, by command path
and rule, in `tests/Puck.Cli.Tests/CliConventionExemptions.json`. That ledger
only shrinks: the law fails on a new violation, and it fails on a recorded row
whose command no longer breaks its rule, until the row is deleted.

1. **The tool is `puck`.** Usage and help print `puck` whatever file the process
   started from, including `dotnet Puck.Cli.dll`.
2. **Verbs are whole words.** A verb is lowercase kebab-case with no
   abbreviation, and one job has one verb. Related operations form one verb with
   subcommands, such as `pull-request format` and `pull-request submit-format`.
   A command with subcommands takes no positional argument of its own.
3. **Options use the GNU long form.** Every option is `--kebab-case`, and the only
   short alias is help's `-h`/`-?`. One spelling means one thing on every verb
   that has it: `--check` is the only dry mode (write nothing, exit 1 on drift),
   `--json` switches results to one JSON object per line, `--output` names the one
   path a verb writes (never a positional argument), and `--configuration`, `--jobs`, and `--file-list <json>`
   mean the same thing everywhere. A verb that mirrors a named external tool keeps
   that tool's short flags and mode names: `search` is ripgrep-shaped, so it keeps
   ripgrep's short flags and its `--files` list mode.
4. **Canonical output has no style options.** A formatter or generator writes one
   output. `puck format` and the language server print `.puck` source in the same
   single layout.
5. **Exit codes are one contract.** 0 means success. 1 means a check or proof
   observed a failure or drift, or a query verb (`search`, `declarations`,
   `references`) found nothing, as with grep. 2 means a usage error, a refusal, or
   an infrastructure failure. 130 means the run was cancelled. An exception that
   escapes a verb is reported on one line, `puck <verb>: <message>`, and exits 2;
   `CliExit` in `src/Puck.Cli` owns the mapping.
6. **Help is layered.** The root listing is alphabetical and gives each verb one
   imperative sentence. A verb's own `--help` carries its detail: modes, exit
   codes, and examples.
7. **Paths resolve against the working directory.** A relative argument resolves
   against the working directory, printed paths use forward slashes, and a verb
   writes into the working directory only when told to. The repository a verb acts
   on is the one containing the working directory; the CLI binary's own checkout is
   only the fallback, so a CLI built in one worktree acts on the worktree it is run
   from.
8. **Errors are named.** A refusal says what was refused and why, on one line on
   standard error. Standard output carries only results.
9. **No dead reach.** A verb that drives quarantined `experimental/` code, or
   duplicates another verb, is deleted along with every caller.
10. **Startup is cheap.** `puck --help` and `puck <verb> --help` load no Roslyn
    (`Microsoft.CodeAnalysis*`), no MSBuild, no BenchmarkDotNet, and no GPU
    assembly; the law counts the assemblies help loads.

## Verbs

| Verb | What it is |
|---|---|
| [`puck affected`](#puck-affectedthe-checks-a-change-needs) | names the test suites, canaries and parity run a change needs, from the project graph and recorded canary coverage; `--run` runs exactly those. |
| [`puck architecture`](../project-map.md) | the project-layering report: explains the build-time layering gate, `--map` prints the generated layering block of `docs/project-map.md`, and `--check` fails when the checked-in block drifts from the projects' declarations. |
| [`puck artifacts`](#automation-commands) | capture, restore, and test the compiled-solution archive CI passes between jobs. |
| [`puck azure`](../development/ci.md#azure-production-deployment) | build, deploy, publish, and verify Puck's Azure production; run from the repository root. |
| [`puck baselines`](#puck-baselinestest-baselines) | records a committed test baseline (the corpus inventory, the shipped-world state baselines, the Maths law ledger, the browser parity hashes) from a fresh run of the test that holds it, or checks it with `--check`. |
| [`puck bench`](#puck-benchthe-puckmaths-microscope) | measurement lanes: `puck bench kernels` is the on-demand micro-benchmark microscope over the Maths, SDF, and state kernels, built on [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet); `puck bench world` measures the server tick path, `puck bench startup` measures real-process world readiness, and `puck bench state-evidence` replays the cost schedule's instruction evidence or, with `--capture`, compiles the pinned reference lowerings and walks each kernel's maximum permitted path afresh. |
| [`puck branding`](#puck-brandingmaintained-assets) | synchronize and check canonical product marks, icons, palette tokens, and their consumers. |
| [`puck bundle`](#automation-commands) | create and verify deployment artifact manifests. |
| [`puck canary`](#puck-canaryreal-world-behavioral-proofs) | bounded positive-and-discriminating proofs run against one exact Release build of the real `Puck.World`. |
| [`puck cartridge-cost`](#puck-cartridge-costcartridge-cost-measurement) | measures each cartridge primitive's sustained per-frame capacity on both real machines, the evidence the cartridge cost model's weights come from. |
| [`puck comment-smells`](#puck-lengths-and-puck-comment-smellsratchet-ledgers) | regenerates `CommentSmells.json`, the ratchet ledger the comment-smell build error (SMELL001–SMELL004) reads, or checks it with `--check`; a recorded count only falls. |
| [`puck compile`](#the-puck-dsl-verbs) | compiles `.puck` to canonical world or cartridge JSON according to its schema; `--validate` runs that vocabulary's checks, `--bundle` inlines world imports, `--watch` recompiles on change. Default output is `.world.json` or `.cartridge.json`, with each world document's compiled world (`.puckb`) beside it; a `.world.json` path compiles to its compiled world alone. |
| [`puck counters`](#puck-counterswork-counter-collector) | the work-counter collector: boots the authored counters workload offscreen once per backend, writes a `puck.counters.report.v1` report with every count tagged by class, and checks the deterministic counts agree across backends; `counters compare` holds two reports to each other. |
| [`puck creation`](#puck-creationcode-authored-sculpts) | the offline twin of `creation.sculpt(s)`: list registered sculpts, apply one to a world file, or report a creation's shape budget/feature usage. |
| [`puck declarations`](#puck-declarationsdeclaration-inventory) | declaration inventory read off the parsed syntax, with no build. |
| [`puck decompile`](#the-puck-dsl-verbs) | renders a world JSON document back as `.puck` source—a one-time import, not a synced mirror; optionally resolves companion vector locks with `--embeddings`. |
| [`puck docs`](#puck-docsthe-documentation-family) | the documentation family: `build` stages the website reference, `links` checks relative links and cited repository paths, and `citations` checks the console-verb tokens skills and XML docs cite, including a live `Puck.World` console boot. |
| [`puck embed`](#the-puck-dsl-verbs) | resolves authored `embed(...)` text into committed `.embeddings.json` lock files, or probes cosine similarity rankings. |
| [`puck firmware`](#puck-firmwarebundled-boot-images) | rebuilds or verifies the HGB boot ROMs and AGB BIOS from their maintained sources. |
| [`puck font-atlas`](#puck-font-atlasmanaged-sdf-font-artifacts) | generates loader-compatible SDF metadata and pixels with Puck's production managed font path. |
| [`puck format`](#puck-formatthe-one-formatter) | formats every source kind Puck owns, C# and `.puck`, to its one canonical form. |
| [`puck landing`](#puck-landinggit-loss-check-then-the-automatic-canary-set) | refuses a commit that silently drops content its author never worked from, then runs the automatic canary set. |
| [`puck lengths`](#puck-lengths-and-puck-comment-smellsratchet-ledgers) | regenerates `FileLengths.json`, the ratchet ledger the file-length build error (LEN001–LEN004) reads, or checks it with `--check`; a recorded length only falls. |
| [`puck lint`](#the-puck-dsl-verbs) | static analysis and symbol resolution over a `.puck` document, composed the same way `compile --validate` composes it. |
| [`puck lsp`](#the-puck-dsl-verbs) | the `.puck` language server over stdio: completion, hover, document symbols, formatting, semantic tokens, and diagnostics published once the input goes quiet. |
| [`puck mcp`](../../src/Puck.Mcp/README.md) | Puck Console tools over local stdio (`--profile operator --attach <attachment file>`) or OAuth-protected HTTP (`--silo <silo.json> --http <configuration.json>`), the two shapes exclusive; the hosted shape runs the silo with the installed `Puck.Mcp` [extension](extensions.md) as its hosted control, and standalone silo and World have no MCP dependency. Both shapes speak MCP protocol version `2026-07-28`. |
| [`puck migrate`](#the-puck-dsl-verbs) | applies one named syntax-tree rewrite to every `.puck` source under a path, proving each rewritten source still compiles to the document it did apart from the members the migration declares. |
| [`puck nuget`](../development/ci.md#publish) | pack, select, verify, and push shared-version NuGet package batches, and the GitHub side of a release: `gate`, `tag`, `release`, `pin`, `pin-published`, `smoke`. |
| [`puck official`](#puck-officialthe-local-official-tree-producer) | builds, serves, and verifies a local `puck.official.manifest.v1` tree—the shipped engine, the authoring workspace, world documents, and their assets, content-addressed. No upload, no signing, no GitHub workflow. |
| [`puck packages`](#puck-packagespublished-nuget-package-report) | the published `ByteTerrace.Puck.*` NuGet package report—id/description/tags—checked and regenerated against `docs/site/index.html`. |
| [`puck parity`](#puck-paritycross-backend-parity-over-the-authored-parity-world) | boots the authored parity world offscreen once per graphics backend and judges every capture it schedules: content gate, exact state hash, and per-tile pixels. |
| [`puck publish`](#puck-publishunsigned-release-source-trees) | writes an unsigned `puck.release.manifest.v1` release-source tree from one runtime identifier's built output. |
| [`puck pull-request`](#puck-pull-requestautomatic-pr-formatting) | the formatting bot's two halves: `format` prepares a pull request's formatting artifact, and `submit-format` is CI's trusted applier. |
| [`puck qualify`](#puck-qualifypackage-qualification) | qualifies a producer-built `Puck.World` package against the release profile: the functional canaries on the package's own World, then the stability matrix offscreen, each cell judged pass, fail or blocked. |
| [`puck references`](#puck-referencessemantic-symbol-queries) | semantic symbol queries: references, implementers, overrides, derived types. |
| [`puck registry`](#puck-registryworld-name-registry) | the world name registry `docs/world-name-registry.md`, generated from `WorldNameRegistry` over the document model and checked against it. |
| [`puck scan`](#puck-scansource-sweep) | source sweep over the parsed tree: comments, comment smells, synchronization sites, clones. |
| [`puck schema`](#puck-schemaworlddef-json-schema) | the generated JSON Schema for `puck.world.definition.v1` and the dashboard portal's TypeScript types derived from it, checked and regenerated. |
| [`puck search`](#puck-searchcontent-search) | ripgrep-shaped content search over a linear-time symbolic-derivatives regex engine ([RE#](../../ACKNOWLEDGMENTS.md)). |
| [`puck shaders`](#puck-shadersshader-compilation) | `shaders compile` compiles a source stage; `shaders generate` writes or checks the HLSL includes generated from the C# model, every generated shader interface among them; `shaders interface` prints or writes the frame-block declarations a pipeline or shader set reads; `shaders package` writes a pipeline's package with its binaries; `shaders pipeline` validates or compiles connected passes, or loads a package, for both GPU backends. |
| [`puck test`](#puck-testtest-worlds) | compiles a `.puck` source's `test` blocks — a world's own, a module's under the arguments a test gives it, and a module's own at every instantiation — into test worlds, boots each through the real `Puck.World` executable, headless, and reads its verdict rows out of the state export the world writes at its own declared export tick. |
| [`puck vocabulary`](#puck-vocabularyworld-authoring-vocabulary) | the world authoring vocabulary `docs/reference/world-vocabulary.md`, generated from the one construct table the parser, the printer and the language server read, and checked against it. |
| [`puck wasm`](../../wasm/README.md) | build and refresh the shipped WASM modules. |
| [`puck wasm-stdlib`](#puck-wasm-stdlibwasm-standard-library-sources) | regenerates every generated Rust source of the WASM standard library: `FixedQ4816`'s Rust port and known-answer vectors, and the addon ABI's Rust mirror. |
| [`puck worktree-base`](#puck-worktree-baseworktree-base-guard) | puts a worktree's HEAD at a named base commit, refusing rather than resetting a dirty tree. |
| [`puck world`](#automation-commands) | prepare hosted world documents, prepare release manifests, inspect deployment-group state, or probe a QUIC endpoint. |

The table follows the root listing: every verb once, in the same order.
`CliConventionLawTests` fails when a verb is missing, extra, or out of place.

This project is a **first-class member of `Puck.slnx`** and joins the full root build regime (warnings-as-errors,
analyzers, code-metric ceilings, doc generation, committed `packages.lock.json`).

**One parser, one grammar.** `-h`/`--help` answers on the root and on every verb
and sub-verb; option spellings are exact. A usage error—no verb, an unknown
verb or option, a missing required option, an option-looking token where a value
belongs—prints the parse errors and `Run 'puck <verb> --help' for usage.` on
stderr and exits 2. Output is UTF-8 on both streams regardless of the host
console's code page, so a non-ASCII source line survives being captured to a
file.

**Path resolution is uniform.** Every verb resolves a relative path against the
**working directory**; an absolute path is used as given. (`scan` anchors its
`artifacts/scan` default and its shader-referent tree at the repository root—
found by walking up for `Puck.slnx` from the working directory, and only looked
up when one of those two defaults is actually live—because those name repo
conventions, not the argument. `wasm-stdlib` anchors the same way: it takes no
path argument at all, because every registered artifact's path (e.g.
`wasm/puck-stdlib/src`) is a repo convention, not something a caller supplies.)
Reporting anchors are the one asymmetry: `scan` records name files relative to
the scan root, while the other verbs print working-directory-relative paths.

## Publishing

The installable package is `ByteTerrace.Puck.Cli`, a .NET tool whose command is
`puck`. Its version comes from the same `build/Packaging.targets` as the libraries.
`puck --version` reports the running CLI's version and source revision;
`puck nuget version` reads the release version from the current checkout.
See [the CLI used by CI](../development/ci.md#the-cli-used-by-ci) for candidate
installation, package installation checks, and release adoption.

### Installing the checkout's CLI on PATH

An editor, a terminal and a task all run `puck` from `PATH`, never from a
project's build output: a build writes `src/Puck.Cli/bin`, and a process
running from it holds every assembly there, so the next build cannot replace
them. Install the checkout as the global tool:

```sh
dotnet pack src/Puck.Cli -c Release --output .tmp/puck-pack
dotnet tool uninstall --global byteterrace.puck.cli
dotnet tool install --global byteterrace.puck.cli --prerelease --add-source .tmp/puck-pack --no-http-cache
puck --version
```

A running `puck` holds the installed tool, so close whatever launched one
first: the editor's language server, an MCP client's server. The uninstall
otherwise fails with `The file exists` and leaves the earlier install in place.

`puck --version` names the revision it was packed from. The package version
does not change between commits, so a reinstall that reports the old revision
restored the earlier package from the NuGet cache; remove
`byteterrace.puck.cli` from the global packages folder
(`dotnet nuget locals global-packages --list`) and install again. CI avoids the
same trap with a private cache per run, in `.github/actions/setup-puck`.

To build the candidate directly for local development:

```sh
dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish
```

produces the candidate executable under `src/Puck.Cli/publish`—`puck.exe` on
Windows, `puck` elsewhere—a framework-dependent .NET executable. Each
invocation starts a .NET process, so call it per query rather than per file.
Do not attempt AOT: the search engine's F# runtime dependency and the
BenchmarkDotNet host code both preclude it. `publish/` is git-ignored.

The publish layout carries a `BuildHost-netcore/` directory (and its `net472`
sibling) beside the executable. That is the out-of-process build host
`references` launches to load the project graph; it arrives as package
`contentFiles` copied to the output. Adding `ExcludeAssets` or
`PrivateAssets=contentfiles` to either workspace package reference would silently
remove it and break every `references` run.

---

## `puck firmware`—bundled boot images

Firmware images are generated release inputs. Rebuild them explicitly after changing their maintained source;
ordinary emulator builds consume the checked-in images without depending on Forge or a native compiler.
From a clean checkout, use `dotnet run --project src/Puck.Cli -c Release -- firmware ...` in place of `puck firmware ...`.

```sh
puck firmware hgb --output src/Puck.HumbleGamingBrick/Firmware
puck firmware hgb --output src/Puck.HumbleGamingBrick/Firmware --verify
puck firmware agb --source src/Puck.AdvancedGamingBrick/Firmware --output src/Puck.AdvancedGamingBrick/Firmware/puck-agb.bin --clang <clang-executable> --linker <ld.lld-executable>
puck firmware agb --source src/Puck.AdvancedGamingBrick/Firmware --output src/Puck.AdvancedGamingBrick/Firmware/puck-agb.bin --clang <clang-executable> --linker <ld.lld-executable> --verify
```

The HGB command runs `BootRomBuilder.Build` with the compatible cartridge policy for every `ConsoleModel`.
Each image uses its lowercase revision name, such as `dmg0.bin` or `cgbd.bin`.
The AGB command builds the maintained freestanding C and ARM sources with Clang's `armv4t-none-eabi` target
and links them with the source directory's `firmware.ld`. Supply native compiler and ELF-linker executable paths;
no shell, downloaded toolchain, or system C library is involved. Object files and the linked candidate live in a
fresh temporary directory that the command removes after either success or failure. The candidate must be exactly 16 KiB.

Both commands report SHA-256 hashes. `--verify` rebuilds and compares bytes without creating or repairing the output;
missing files or different bytes exit 1, and invalid command syntax exits 2. A successful comparison proves reproducible
generation with that source and toolchain, not hardware compatibility. Emulator and firmware execution tests own that claim.

## `puck cartridge-cost`—cartridge cost measurement

```sh
puck cartridge-cost
puck cartridge-cost --target cgb
```

The verb boots `CartridgeCostMeasurement`'s probe cartridges on the Color and Advanced machines. For each shape it
bisects the largest per-frame iteration count the machine sustains at full frame rate, and prints one row per shape:
the count and the cost model's units for the largest sustained probe. A `CLIPPED` row reached the search's ceiling
rather than the machine's. Cost per iteration is inversely proportional to that capacity, so the rows are what
`CartridgeCost`'s weights and `CartridgeCostProfile`'s reservations are folded from; see
the [cartridge forge guide](../emulation/shared/cartridge-forge.md). A full run boots several hundred images.

## Automation commands

```sh
puck artifacts capture | restore | test-windows | test-world
puck docs build [--output <directory>]
puck bundle create <directory> <commit>
puck bundle verify <directory> <commit>
puck world prepare <worlds-directory> --output <directory>
puck world release prepare <package-directory> --silo <silo.json> [--output <manifest>] --label <label> --source-revision <sha> --engine-image-digest <sha256:digest> --persistence-contract <name> --peer-protocol-contract <name>
puck world release status [group-file] [--json]
puck world release finalize
puck world release resume
puck world release deploy <package-directory> [--operation <id>]
puck world release rollback [--operation <id>]
puck world release checkpoint [--request <guid>]
puck world release restore <recovery-point> [--operation <guid>] [--discard-progress]
puck azure prepare-world-release [--output <package-directory>]
puck world release qualify <source-manifest> <target-manifest> <fixture-directory> --source-image <image> --target-image <image> --output <evidence-directory> [--steps <count>]
puck world probe <host> <port> <public-key-file>
puck wasm build
```

`artifacts capture` archives one build's compiled Release outputs with their
source identity so consumer jobs restore rather than recompile; `restore` extracts
that archive into place, and the two test sub-verbs run the archived assemblies
through the producer's manifest. `docs build` is described with [the `docs` family](#puck-docsthe-documentation-family). `bundle create` writes a stable
deployment manifest containing the source commit and every file's SHA-256.
`bundle verify` checks provenance, containment, hashes, and the complete inventory,
including hidden files.

`world prepare` uses the engine's composer and validator to package Puck and its
referenced neighbours under canonical hosted file names. It rebases provider-declared
asset paths from nested documents to the common worlds directory; the image retains
the neighbouring asset directories. `world probe` checks
QUIC reachability and the endpoint's expected public key; it requires QUIC support
and contacts the supplied host. `wasm build` invokes Cargo and refreshes the
committed default addon, printing the content hash needed by its document rows.
`world release prepare` loads the validated silo inventory, requires one canonical
composed `*.world.json` output for every owner/world row, and writes a
content-addressed manifest (`release.json` in the package directory unless
`--output` names another path) after hashing those definitions and the remaining
package artifacts. Stable owner/world identities and package paths are recorded
separately; authored `.puck` inputs are artifacts and are never treated as ready
definitions. New manifests require `puck.world.release.restore.v1` coordinator
support, including receipt-aware qualification and closed-group rewind enforcement.
Official preparation and bootstrap read composed published definitions, allowing
unfilled boot draws. Bootstrap retries compare exact published bytes, without
running those draws or comparing them with a newly initialized world.
`world release status` reads the managed group for the world silo
named in the existing Azure deployment outputs. It reports the active and previous
releases, admission, rollback eligibility, pending phase, recovery scope, and next
operator action. Supply a saved group file for offline inspection; add `--json`
for the complete validated record plus the next action. A deployment without a
managed group is refused by name and returns exit code 2.
A release command that fails prints one `puck world release <subcommand>:` line and
returns exit code 2; cancellation returns 130. A deploy, rollback, resume, or
discarding restore that does not complete, or ends with the source recovered
instead of the target committed, returns exit code 1, as does a `qualify` whose
pair ran and failed a leg's claim. A failed or canceled mutation directs the operator to inspect `status`
and use `resume` for unfinished work. Neither an exit
code nor a lost response establishes whether the durable operation committed.
`world release checkpoint [--request <guid>]` captures a coherent recovery point
without stopping gameplay and prints its request ID, capture time, and world ticks.
It requires a managed closed group; older qualification captures without durable
boundary proof cannot be used for intentional rewind. `world release restore
<recovery-point>` previews the saved release and ticks against current durable
ticks. Add `--discard-progress` to explicitly rewind the entire group, and optionally
`--operation <guid>` to identify that operation. Current request receipts survive;
the same `status` and `resume` commands handle interruption. The implementation is
under local acceptance and has not been accepted on Azure.
`world release finalize` closes the current admitted rollback window with a
guarded group write. It preserves gameplay, recovery history, and retained
artifacts. Repeating it after finalization succeeds without another mutation;
an uncommitted or closed deployment refuses. It uses the same configured Azure
group as `status`, and acquires the deployment controller lease before its write.
`world release resume` reads the configured group's pending operation and resumes
that exact source and target. It loads verified retained packages, a pinned Key
Vault secret version containing deployment inputs, and the retained compute
template. It never substitutes the latest secret value or the current checkout's
template. Missing or conflicting retained inputs refuse before a worker effect.
A recovered source is reported as a failed deployment, not a successful upgrade.

`world release deploy` verifies the package, retains its files and versioned
deployment inputs, protects and pulls the exact source and target registry
digests, and runs qualification before entering the coordinator. Repeating a
pending target resumes it; another target refuses. A previous rollback window
must be finalized before a third release. Bootstrap refuses existing unmanaged
workers and persisted gameplay. Azure production runs this same command after
`azure prepare-world-release`.
Deployment automatically exports all source worlds at one simulation boundary.
The worker retains complete immutable checkpoints, and the CLI builds an isolated
fixture with the captured machine identity and fresh test signing keys. Source
admission stays open during export and qualification. A lost export response can
reuse its stable request ID; partial uploads have no published inventory. Bootstrap
builds its fixture from the retained package: each world's exact definition bytes
under an unowned authority root with no gameplay state. Managed deploy does not accept
a replacement fixture that could omit uncapturable live state. Each export also
pins the receipt index and chain selected in the checkpoint's publication queue.
Materialization retains those exact bytes and their sequence meaning under a new
unowned root. Every inventory row carries its receipt snapshot pin, including an
explicit empty history; a row without one is a malformed inventory.

`world release rollback` selects the retained predecessor, exports current source
state, and qualifies the reverse pair before entering the same drain and cutover
transaction. It preserves progress earned since deployment. A pending operation
requires `resume`; an absent or finalized rollback window refuses. `--operation`
sets a stable operation ID for diagnostics. Explicit restore selects a retained
closed-group recovery point and requires `--discard-progress`. The Azure activation adapter has an atomic metadata
definition publisher, guarded by the operation and exact drained root, with
receipt-based retries. Metadata-only definition changes can proceed after
packaged qualification exercises the same forward and reverse transformation.
Changes to other definition sections, packaged dependencies, or metadata conflicts
found in the exported state refuse before the serving release drains. Activation
rechecks the actual drained checkpoint; a later conflict follows pre-commit recovery.
Every prepared package carries the one coordinator contract,
`puck.world.release.restore.v1`. A manifest without it, or with any other
contract, is refused by name. Update the CLI rather than editing or regenerating
a retained manifest.

`azure prepare-world-release` applies the official endpoint and delegated
admission bindings to the staged composed worlds, then hashes the complete
inventory. It reads the deployment outputs, `artifacts/release-source.json`, image digest,
and `artifacts/azure/silo-worlds`. Every world is pinned with its own retained
signing key at deployment. Temporary preparation keys are never packaged.

Deploy, rollback, resume, and finalization use a renewable Azure Blob lease to exclude competing
controllers. Losing ownership cancels the CLI's child processes and closes its
next-effect checks; the durable operation remains available for a later resume.
This controller lease complements the world's authority fences and publication
barrier. Guest mutations also serialize with bootstrap under a VM lock and check
the durable operation after waiting, refusing stale phases or release identities.
The bootstrap waits for private health; only the coordinator opens admission.
These checks do not retract an Azure operation already accepted by the service;
full cloud interruption acceptance testing remains required.

`world release qualify` runs four isolated Docker legs over a marked, coherent
offline fixture: both packages import the source state, then both import the
candidate's saved continuation. Complete checkpoint hashes must agree for each
pair of imports, and every leg must advance simulation and save successfully.
For metadata changes, both manifests must sit at their package roots with their
declared files present. The command retains and verifies those exact package
bytes, applies the deployment publisher to a disposable forward copy, and makes
both images import it. It then reverses the authored delta over B's saved
continuation before both reverse imports. Evidence retains the original seed,
both transformed copies and their hashes; the source fixture stays intact.
The runner resolves each image against its manifest digest, uses that immutable
image identity, disables container networking, and mounts only a fresh fixture
copy. It retains the copied state and evidence under a unique output directory.
Equal contract labels or an operator-authored receipt cannot replace these runs.
A pair that ran and failed a leg's claim exits 1 and names the claim: a packaged
engine that exited nonzero or overran its five-minute limit, a missing or
incomplete report, lost receipts, or two imports that disagree. A run that cannot
exercise the pair exits 2: unreadable manifests, an unsupported pair, an unmarked
or mismatched fixture, an image that is not the release's, or a container engine
that cannot inspect or start an image. Neither writes a receipt.

Each image emits `puck.world.qualification-exercise.v1`: it inventories every
captured receipt before activation, looks up each original decision after import
and continuation, and checks duplicate and conflicting retries after draining.
Retries must leave the authority root unchanged. The runner independently computes
the expected inventory hash before starting each leg and refuses missing or
mismatched proof. Newly written receipts remain available to the reverse legs.

The fixture contains `qualification.fixture` with `puck.world.qualification.v1`,
a local `<fixture-directory>/silo.json` with the exact pinned release inventory, per-world signing
keys inside the fixture, `store/` with coherent authority state and required
neighbour definitions, and `state/` with the owned-world catalog. It must have
no production release binding or authentication provider. The inner
`world release exercise <fixture-directory> [--steps <count>]` command belongs
to this isolated runner: it opens the fixture's own admission gate and advances
the normal silo simulation. Do not point it at a live store. These tests cover
the supplied states; qualifying new gameplay values still requires representative
fixtures that reach those values. Machine and addon state that the checkpoint
contract cannot capture rejects qualification.
Rollback and restore remain hosted maintenance operations
until their coordinator can perform the corresponding guarded storage and
admission transitions.
Azure credentials, deployment ordering, and access restoration belong to
[`puck azure`](../development/ci.md#azure-production-deployment), which reaches these
verbs in process.

## `puck publish`—unsigned release-source trees

```sh
puck publish --app <id> --channel <name> --version <semver> --rid <rid> --input <built output> --output <tree> [--minimum-supported <semver>] [--notes <text>] [--rollout-percent <percent>] [--state-generation <n>]
```

Walks one runtime identifier's built output into a `puck.release.manifest.v1`
release-source tree: `<tree>/<channel>/manifest.json`, canonical and unsigned,
and every payload file under `<tree>/objects/sha256/<hex[0..2]>/<hex64>`.
`Puck.Launcher.Release.DirectoryReleaseSource` rooted at `<tree>` reads it as
written. Signing and upload are not part of this verb. An unreadable input or an
unwritable output exits 2.

## `puck official`—the local official tree producer

```sh
puck official build --tree <dir> --channel <name> --engine <AppBundle dir> [--worlds <dir>] [--allow-dirty]
puck official verify --tree <dir> --channel <name> [--expect-commit <hex>]
puck official serve --tree <dir> [--port 61102]
```

Writes, serves, and verifies a `puck.official.manifest.v1` tree: the shipped browser-wasm
engine (a `dotnet publish src/Puck.World.Browser -c Release`
AppBundle), the world schema bundle (the same `WorldSchema.Export`/`Bundle` path
`puck schema --bundle` uses), the authoring workspace, the world documents it
authors, the one fully-composed root world (the document `puck`, resolved
through its whole basis-and-imports graph, parsed, migrated, validated, and
re-serialized), and every off-disk asset a music/table/tune/patch row
references—all content-addressed under
`<tree>/objects/sha256/<hex[0..2]>/<hex64>` (the same layout `puck publish`'s
dry-run and `Puck.Launcher.Release.DirectoryReleaseSource` already read) and
hash-checked, so `verify` and a client both catch a mismatch
by name. `build` refuses unless `puck schema --check` and `puck registry --check`
both pass, and unless the worlds tree is clean (or `--allow-dirty` is given);
a refusal exits 2.

The manifest's `build.commit` and `build.dirty` name the worlds tree the build
read, not the build of the CLI that ran it:

- `build.commit` is the HEAD commit of the git checkout that holds the worlds
  directory. A checkout is found by a `.git` entry in the worlds directory or
  any directory above it. It is `none` when no checkout holds the directory,
  or when the checkout's HEAD names no commit yet. Inside a checkout, a git
  that cannot answer refuses the build rather than guessing.
- `build.dirty` is true when `git status` reports anything under the worlds
  directory: a tracked file modified, staged or deleted, or an untracked or
  ignored file. Ignored files count because the build publishes every file
  there, whatever git ignores. A change elsewhere in the checkout does not make
  the tree dirty. A `none` build is always dirty, since no commit holds what it
  read.

A dirty tree is refused unless `--allow-dirty` is given. The immutable copy is
written to `<tree>/builds/<commit>/manifest.json`, so `builds/none/` holds the
latest build of a tree outside git. The world schema bundle keeps its own
`x-puck.commit`, the commit the generator was built at. World Studio checks the
booted engine's commit against that value, not against `build.commit`, and
shows `build.commit` in its build line (`worlds tree outside git` for `none`).

The manifest's `sources` list is the authoring workspace, published byte for
byte so a client can mount it and compile it exactly as on disk. It holds every
`.puck` source anywhere under the worlds directory, the
`<stem>.embeddings.json` and `<stem>.assets.json` locks a compile reads beside
a source, and every `.world.json` document with no `.puck` source. Document
names are unique ignoring letter case, because World Studio mounts these files
into a case-sensitive file system while a Windows checkout resolves them
case-insensitively: two files whose document names differ only in case
(`Foo.puck` and `foo.world.json`) are refused by name. Each entry's
`name` is its path relative to the worlds directory with forward slashes
(`games/klondike.puck`), beside the object's `path`, `hash`, `size`, and
`contentType` (`text/x-puck; charset=utf-8` for a `.puck` source,
`application/json` otherwise).

The `documents` list holds every document that workspace authors, anywhere
under the worlds directory. A document is named by its document name, the
spelling a `basis` or an `imports[].document` uses: `games/klondike`, `puck`,
`standard`, `shards/quilt-ne`. `sources` is the only list whose names are
files. Each document carries its JSON (a source's compiled output, or a
sourceless document's own bytes), and its `source` names the `sources` file
that authors it: `games/klondike.puck`, or `shards/quilt-ne.world.json` for a
document with no `.puck` source. A composition source that declares several
worlds authors one document for each, named beside the source by its declared
world name (`beacons/beacons.puck` authors `beacons/north` and
`beacons/south`). A source that emits no document authors none and carries no
document name: a module library such as `worlds/rulepush/hub.puck`, which
declares `module hub(...)` and no world, lowers to an empty document, so the
`hub` that `rulepush.puck` declares beside it collides with nothing. The library
is still one of the `sources`. A declared world is a document name like any other, held to
the same rule and refused with the same message: `beacons/beacons.puck`
declaring `north` beside `beacons/North.world.json` or `beacons/North.puck` is
refused, and so is the exact name beside `beacons/north.world.json` or
`beacons/north.puck`, because a reference to `beacons/north` resolves to that
file and never to the declared world. Each role is derived from the document: `shard` under
`shards/`, `basis` for `standard`, `world` for a document with a `documentId`,
and `fragment` otherwise. The root `puck` and the basis `standard` resolve by
name like every other document, so either can be authored as `.puck` or as
`.world.json`, and the build refuses when either is missing. `composed[].name`
is the root's document name, `puck`.

`verify` holds a manifest it did not build to the same rules. It refuses two
`documents` whose names differ only in letter case or repeat exactly, with the
message the build uses, and two `sources` whose paths do. `composed[].name` and
each document's `source` still resolve by their exact spelling. It also refuses
a duplicate name in any other list, a document name spelled as a file, a
`source` that names no `sources` file or cannot author the document, a
`composed` name that names no document, and a source object that does not
hash to its entry. A document authored by its own `.world.json` file must be
that file's object byte for byte, and a `.puck` source authors documents only
in its own directory. `--expect-commit` refuses a `build.commit` other than the
one given, compared without regard to case. `build.commit` names the worlds
tree, so `verify` does not compare it with the schema bundle's `x-puck.commit`.

No subcommand here uploads, signs, or drives a GitHub workflow—publishing a
signed release to a real channel is a separate, later concern.

---

## `puck font-atlas`—managed SDF font artifacts

`puck font-atlas` turns an OpenType font or collection into the same
loader-compatible SDF atlas that `Puck.World` can generate in process. The CLI
and runtime share `ManagedFontAtlasGenerator`; there is no Python or native
rasterizer hiding behind the command.

```text
puck font-atlas fonts/Inter-Regular.ttf \
  --range U+0020-U+007E \
  --range U+00A0-U+024F \
  --face-index 0 \
  --size 48 \
  --output artifacts/inter-regular.json
```

The output path names the JSON metadata. A PNG with the same base name is
written beside it. Ranges are repeatable and use the same syntax as world font
definitions; `--range "*"` requests every mapped Basic Multilingual Plane
scalar. The command also exposes the raster size, signed-distance range,
padding, preferred columns, and atlas dimension and pixel limits through the
options listed by `puck font-atlas --help`.

Standalone OpenType fonts and TTC/OTC collections are accepted with TrueType
quadratic or CFF/CFF2 cubic outlines. `--face-index` explicitly selects a
zero-based collection face and defaults to 0. CFF2 variable outlines use their
default design coordinates. Complex-script shaping remains a separate layer;
generated atlases preserve source glyph IDs for it.

---

## `puck shaders`—shader compilation

```sh
puck shaders compile <source> --out <directory> [--name <name>] [--toolchain <directory>] [--stage compute|vertex|fragment] [--entry <name>]
puck shaders generate [--check]
puck shaders interface <source> [--write] [--echo]
puck shaders package <source> --output <directory> [--root <directory>] [--toolchain <directory>] [--cache <directory>] [--json]
puck shaders pipeline <source> [--inspect] [--toolchain <directory>] [--cache <directory>]
```

`compile` writes SPIR-V and DXIL for one HLSL source stage; `--entry` names its
entry point and defaults to `main`. `pipeline`
loads a `puck.render.graph.v1` graph document or reads a one-off shader as a one-pass graph,
validates its resource graph, and compiles every planned pass. `--inspect`
prints the execution order, dependencies and named outputs without compiling
shaders or creating a GPU device. A source or document that does not compile or
plan returns exit code 1, and compilation errors identify the source location.
A missing source, an unknown `--stage`, an unreadable file, a
missing shader tool, or a source edited while it was read is a refusal: exit 2,
reported as `puck shaders <verb>: <path>: <why>`.

`generate` writes the HLSL includes the C# model owns:
`src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-isa.hlsli`, the SDF instruction set's
version, enums and packed-layout constants, generated from
`Puck.SignedDistance` by `Puck.SdfVm.SdfIsaHlsl`; and every generated shader
interface (`<name>.interface.hlsli`). A shader-set manifest owns the interface
beside it, and an engine package that declares pass-group members, such as
`overlay` and `place`, owns the one include named by its interface. A checked-in
interface include that no manifest or package owns, and a package whose include
is missing, fail by name. `--check` writes nothing, regenerates each include in
memory and exits 1 naming each file that differs from the model and its first
differing line; CI runs it beside `puck schema --check`.

`interface` prints the [frame-block](shaders.md#frame-values-extent-and-ports) declarations
each pass of a graph document or one-off shader reads, or those a shader-set
manifest's stages read; `--write` writes each as `<interface>.interface.hlsli`
beside its source instead, which a shader set, compiled at build, checks in, and
`--echo` also generates each interface's echo pass as `<interface>.echo.hlsl`.

`package` compiles a graph document or one-off shader and writes its
`puck.shader.package.v1` package to `--output`: the source closure, each pass's
interface and generated declarations, and its SPIR-V and DXIL binaries, with a
manifest recording the compiler they were built with. Loading a package runs no
tool. `--root` is the directory every file of the closure must lie within,
the source's directory by default. The output is replaced only once the new
package is complete, and only when it is absent, empty, or already a package.
`--json` writes the outcome as one JSON object with `status`, `package`,
`code`, `name`, `files`, `passes`, and `message`. `pipeline` given a package
directory verifies the package and reads its binaries; with `--inspect` it plans the
package's document. A package is named by its directory, so its
`puck.shader.package.json` given in its place is refused. For a package, either
verb exits 0 when every pass compiled, 1 when a pass failed to compile, and 2
for a refusal (the message names its `SHADERSRC_` or `SHADERPKG_` code), a
missing source, a missing tool, or a source edited while it was read.

These commands use the same compiler and loader as live World pipelines.
The [shader reference](shaders.md#shader-pipelines-and-live-development)
owns the source-language, resource and toolchain contracts.
## `puck affected`—the checks a change needs

`puck affected` reads the working tree's changes against a base (`--since`,
`HEAD` by default, so only uncommitted work) and names what those changes can
break, and nothing wider:

- **Suites** follow the project graph. A changed project chooses its own suite
  and the suite of every project that references it, transitively, counting a
  build-order-only reference such as a test that launches `Puck.World`. A file
  no project owns, such as a test data directory under `tests/`, chooses the
  projects whose sources name that directory, spelled by its first two
  segments such as `tests/Puck.World.Verdicts` or `worlds/parlor`.
- **The catalog**: a change to a shipped world under
  `src/Puck.World/Assets/worlds`, a World pipeline source, the compile verb, or
  a project the composer, the SDF baker, the shader packager or the texture
  codecs are built from, chooses `puck compile --tree … --check` over the
  game's Release catalog.
- **Worlds**: a changed `.puck` source that declares `test` blocks is run with
  [`puck test`](#puck-testtest-worlds).
- **Canaries** follow what they exercise. A canary is chosen when a changed
  file lies in its directory, is a world, script or fixture its manifest
  names, or is a source file the canary executed when coverage was last
  recorded in [`tests/Puck.Affected`](../../tests/Puck.Affected/README.md).
  A canary is also chosen for any file its manifest's documents reach, read
  with the documents' own readers: a world reaches the layers it composes,
  the neighbour worlds its adjacencies name and the `.graph.json` documents
  its `views.graphs` rows name, and a graph document
  reaches the pass shaders it declares and every file they include, each
  resolved as the host resolves it. A world is read composed and parsed but not validated, so a world whose
  adjacencies or extensions need the host's resolvers still reaches them.
  `puck parity` is chosen whenever a chosen canary renders on a GPU.
- A file no canary can execute is placed through the indexed C# sources it
  stands for. A project file, restore lock or `NativeMethods.txt` stands for
  its project's sources. A shader source or include stands for the C# that
  names, by its file name, each kernel whose include closure reaches it: the
  kernels are the stage sources the projects' shader items declare, the
  Direct3D 11 kernels (`Direct3D11KernelSource`) among them. A file
  `puck schema` writes stands for the sources declaring the types it is
  generated from. A shader that neither a kernel's loader nor a canary's
  documents reach has no stand-in.
- A changed `Puck.World` source that neither the coverage index nor a stand-in
  places is listed as `unmapped`. It chooses no canary; the list says coverage
  is due for a fresh recording.
- A file deleted since `--since` can never be recorded, so it is never
  `unmapped`. The index as the base revision recorded it places it, choosing
  the canaries that executed it, directly or through the stand-ins the base's
  own tree gave it: a deleted shader stands for the loader of each base kernel
  whose closure reached it, read through `git show <since>:<path>` and never
  from the working tree. One neither places is listed as `deleted`, and its
  project's suites still run. Nothing reads a deleted file from disk.

- Build infrastructure (`build/`, `Directory.Build.*`, `global.json`,
  `Puck.slnx`) chooses every suite. Prose, `.claude/`, `.github/`, `editors/`
  and `experimental/` choose nothing.

`--run` builds and runs the chosen suites, then `puck test` on the chosen
worlds, then the chosen canaries, then parity, and exits 1 when any of them
fails.

`--record` refreshes the coverage index. It builds a `Puck.World` that records
every method the runtime compiles (`-p:PuckRecordMethods=true`; no other build
carries the recorder), runs the full canary set on it, and maps each leg's
methods to their source files through the build's portable PDBs. It is a full
run, so it happens when the owner asks for one; between recordings a new or
moved source shows up as `unmapped`.

## `puck canary`—real-World behavioral proofs

`puck canary` is deliberately narrow: it runs deterministic stdin-driven
behavioral proofs that fit a strict transcript vocabulary. Every manifest owns
a positive leg and an executable discriminating leg; both start from fresh
state, and the positive observation must turn red under the discriminator while
the declared opposite observation holds. The runner resolves one `Puck.World`
build for the checkout's current sources, building it only when no earlier run
has, and runs every leg against that exact artifact. The build never goes into
the projects' `bin` directories (see [where the World artifact is
built](#where-the-world-artifact-is-built)). `--world-artifact <dll>` runs every
leg on the named entry assembly instead, such as a published package's, and
builds nothing. `--debug-layers` boots every offscreen leg's World with
`--debug-layers`, its backend's validation layer, and then fails any such leg
whose stderr holds a validation message, naming the first: a
`[vulkan-debug] validation` line or any `[d3d12-debug]` line, a teardown
live-object report included. The Vulkan loader's `general` notices do not
count, and the Direct3D 12 drain never prints the one message the layer raises
by design, a pipeline-library miss. A Direct3D 12 leg that prints
`[d3d12] debug layer requested but not loaded` fails too, since nothing
validated it. The runner keeps stdout and stderr
separate, pins BOM-less UTF-8 stdin, closes the pipe, drains both streams,
checks the absolute `--world` boot-origin line, enforces per-leg and
whole-suite budgets, and kills the process tree on timeout.

A leg lasts as long as its script. The runner adds two lines after the
authored script: `wire.errors`, whose exact response count it checks, and
`quit`, which ends the World once everything queued ahead of it has run. The
manifest's `timeoutSeconds` is therefore not how long a leg runs. It is the
ceiling at which the runner kills a leg that has hung, from 1 to 60 seconds
(240 for a federated leg). Every World the runner starts also gets
`--exit-after-seconds` at that ceiling, so a World the runner can no longer
kill stops on its own. A companion authority gets its client's ceiling plus
fifteen seconds, so it outlasts its client.

The World runs every script line up to the first `world.wait` before its first
tick. The line after a `world.wait` runs before the next tick. So a script
whose timing matters states it with `world.wait`, and the result doesn't
depend on how busy the machine is.

The first Ctrl+C stops the run. The runner kills every World process it
started, starts no further leg, and exits with code 2. A leg that fails with
an exception stops the run the same way. The runner waits for the other legs'
processes to die before it reports the failure.

Legs run concurrently, up to `--jobs` World processes at once. The default is
half the processor count, at most eight and at least one. A leg holds one slot
for each process it runs: two for a companion-authority leg, one for each
listener in an `authorities` leg. A windowed or offscreen leg, and a leg whose
manifest declares any requirement (`gpu`, `audio-output`, or input hardware),
holds every slot, so its GPU, window, or device never shares the machine with
another leg. Legs start in authored
order, and each proof's report prints whole and in authored order.
`--jobs 1` runs the legs one at a time.

After the last proof the runner prints what the run started and how its legs
ended: the World boots, the processes it started for legs (World, stub
launcher, and `shaders package`), whether it built `Puck.World`, and how many
legs ended at their script's `quit`, at their timeout, or otherwise.

A `bootShape: "stub"` manifest runs its leg through `Puck.Launcher.Stub` from a
leg-private, disposable `<run>/install/` tree, never the shared build path,
and observes two successive process launches rather than one—the
`self-update` canary is the only user today.

A leg that runs one World process can declare a `relaunch`: a `world` file
name, a `script`, and its own `commands`. After the first process ends, the
runner boots the World again under the same state directory. That boot runs
on the named document in the leg's run directory, which the first script
writes with `world.save {run}/<name>`. The second boot runs its script with the
same runner-owned ending. Each boot's commands are accounted against its own
process, and each boot gets the exit, timeout and boot-origin checks.
Assertions read both boots' streams in order, and captures from either land in
the one run directory. The leg's budget counts both boots. `pipeline-override`
uses it to prove that a committed value survives an exit.

A leg that runs one World process can also declare a `package`: a `source`, a
file in the repository resolved from the canary's directory, so canaries can
share one fixture; an `output`, a bare directory name; and an optional
`alter`, a logical path inside the package. The runner packages each source
once per run, however many legs, backends, and manifests name it: it copies
the files of the directory holding the source into a scratch directory,
packages the copied source with its own `shaders package`, and deletes the
copy. Before a leg's first boot it copies that package to `{run}/<output>`, so
a script names a relocated package whose source tree is gone. With `alter`, the
runner then appends a line to that file of the leg's own copy, so its pin no
longer holds. A package the verb cannot
build because a shader tool is missing makes the leg unsupported; any other
failure is an infrastructure failure, with the verb's output kept in
`{run}/package.log`. `pipeline-package` and `no-device-compile` use it.

A leg that sets `hideShaderCompiler` boots its World, and a relaunch, with every
directory holding `dxc` removed from the search path, while the runner's own
package build still sees it. A pipeline source with no package in the World's
[package store](shaders.md#the-builds-package-store) cannot compile there and
reports `unsupported`, which such a leg observes rather than setting aside as
an environment it cannot exercise. `no-device-compile` uses it.

A `bootShape: "offscreen"` manifest boots a world that authors
`host.presentation: offscreen`: a real GPU device and no window. The runner
passes no `--headless` value, which would override that shape, and runs every
leg once per backend with `--backend`. The manifest must list `backends` as
exactly `vulkan` and `directx` and declare the `gpu` requirement, so it is never
automatic. It reports one proof per backend, as `<id> on <backend>`, and the
manifest holds only when both did. A `bootShape: "windowed"` manifest may list
the same `backends`, under the same rules: it then boots each leg windowed once per
backend with `--backend`. No other shape reads `backends`.
The `pipeline-feedback`, `pipeline-ink`, `pipeline-edit`, `pipeline-supersede`,
`pipeline-shapes`, `pipeline-resize`, `pipeline-counters`, `pipeline-override`, `pipeline-package`, `pipeline-budget`, `pipeline-churn`, `pipeline-fault` and `pipeline-geometry` canaries use this shape to test shader
pipelines, and `source-conversion` uses it to run the shipped image-source
conversion kernels; the [World guide](../../src/Puck.World/README.md#shader-pipelines)
covers the `pipeline.wait` phases their scripts use.

A leg that cannot exercise its environment is **unsupported**, not failed. The
runner recognizes three announcements. The first is a World that exits 2 after
printing `[world.host: unsupported: …]`, because the selected backend has no
usable device on this host or the operating system does not offer it. The
second is a `[pipeline: <name> unsupported: …]` or
`[pipeline: <name> wait <phase> unsupported: …]` line, printed when a shader
tool such as DXC is missing. The proof then reports `UNSUPPORTED` instead of a
verdict. Because an offscreen proof needs every declared backend, the selection
is not green: the run exits 2.

A manifest declaring the `audio-output` requirement gets a third check: its
own `[audio.state: device=… fault=…]` echo. `device=unsupported` (the platform
offers no render backend at all) and `device=silent` naming a fault starting
`render endpoint could not be opened: …` (a render backend exists but this
machine has no default render endpoint to open) are both read as the machine
categorically lacking audio hardware, from the World's own deterministic
report rather than a separate host probe, and report `UNSUPPORTED` the same
way a missing GPU or shader tool does. A device that opened but produced no
signal (`device=playing`) is a real defect and is judged, never excused. The
`voice-babble` canary declares `audio-output` because its assertions read
`audio.state`'s `peak`.

A World that cannot bind its listen endpoint prints the same
`[world.host: unsupported: quic listener <endpoint> unavailable: …]` form and
exit code, but the runner reports that leg as an infrastructure failure, not as
unsupported. Every endpoint a canary World listens on is one the runner picked,
so a refused bind means the port was taken after the runner probed it. That is
a fault in the run, not a capability the machine lacks. QUIC is a prerequisite
of the machine the runner runs on, not a requirement a manifest declares. The
run still exits 2.

Every leg whose script arms `pipeline.wait` also gets these runner checks:
each armed wait reports exactly one outcome, and no outcome is `timed out`.
Each outcome is listed by instance and phase, such as
`pipeline.wait feedback submitted 2: reached`, so a stalled leg names the phase
it stopped at. `reached` and `failed` outcomes are observations for the
manifest's own assertions.

```text
puck canary                         run the automatic set (headless, no environmental requirements)
puck canary <id> ...                explicitly run named proofs
puck canary --all                   explicitly run every proof; does not change automatic eligibility
puck canary --list                  strictly load and list manifests without building or running
puck canary --capability <class>    filter automatic/headless/windowed/offscreen or an environmental requirement
puck canary --merge                 run the merge gate: the automatic set plus every proof requiring gpu
puck canary --jobs <n>              run at most n World processes at once (n ≥ 1)
puck canary --plan                  print a selection's counts and ceiling without building or running
```

`puck canary --merge` is the merge gate. The automatic set alone skips every
offscreen GPU proof, because each requires `gpu`; `--merge` runs the union of
the automatic set and `--capability gpu`, each proof once, in authored order.
Like the automatic set and `--all`, a merge run fails when a manifest was
skipped as unreadable.

`--plan` counts a selection from its manifests alone and prints it without
building or running anything: one line per proof, then the legs (serial and
parallel), the World boots, the processes the run would start for its legs,
the builds, and the summed per-leg timeouts. Every value is a count of the
manifests, so the output is the same on every machine, and a GPU selection can
be costed on a machine without a GPU. The two gate selections, the automatic
set (`puck canary`, `--capability automatic`, and `puck landing`) and
`--merge`, are each held to a ceiling of World boots and summed leg budget
declared in `src/Puck.Cli/Canary/CanaryCeilings.cs`. A gate whose plan exceeds
its ceiling is refused with exit 2 before anything builds, naming both numbers.
A change that deliberately grows a gate raises the ceiling in the same change
and states the new `--plan` counts.

The selection forms are mutually exclusive and every execution selection must
be nonempty. `--jobs` combines with any of them, and `--plan` with any but `--list`. Manifest tokens are case-sensitive. Every non-comment script
command declares `accepted` or intentionally expected `refused`, bound to its
verb and occurrence; an accepted claim may add `"stream": "stderr"` to expect
its confirmation there instead of stdout—the shape server narration
(`[world.grant: …]`, `[world.revoke: …]`) always uses regardless of
accept/refuse, unlike an ordinary accepted command's stdout read-back.
The runner pairs a verb's answers with its occurrences in order: the World
writes each answer as one [console record](commands.md), whose lines after the
first are indented, so an unindented line naming the verb opens its next
answer. A multi-line answer counts once however the two streams interleave,
and two identical answers in a row count twice. An answer names the verb its
opening token names, or the longest claimed verb that token extends by a dot
(`[world.state.row 'a' …]` answers `world.state`).
Assertions cover stream-specific exact/contained lines, verb/occurrence/
exact-cardinality responses, ordered sequences of responses (`sequence`) and
of lines (`lines`: each listed line matches, exactly or as contained text, past
the previous one's match), named response field
extraction (from the response's first line, or with `"line"` from the first
indented line of its record that starts with that text), equality/inequality, strict ordering of two extracted numbers
(`greater`: left above right), inclusive bounds, minimum margins,
byte-level file equality/inequality (`filesDiffer`), per-channel bounds over a
region of one capture (`imageRegion`), and image agreement
between two captured frames (`framesAgree`, stating `agree` explicitly—
`CanaryFrameNoise` counts the pixels that moved by at least 2 LSB and compares
that against a 64-pixel noise budget). Two live windowed captures of identical
simulation state are never bit-equal: silhouette shading carries ±1-LSB
variance, so a byte comparison of two live frames reports a difference on
roughly one run in three. A frame proof therefore states `framesAgree`, never
`filesDiffer`, over a `.png` pair; `filesDiffer` remains the right shape for a
file whose bytes really are the claim. `puck parity` judges its frames with a
different measure: per-tile deltas against the thresholds its contract sets
for each station.
An `imageRegion` names a run-relative `capture`, its expected `extent`
(`[width, height]`, since a claim about a different image decides nothing), a
normalized `region` (`[left, top, right, bottom]`; a pixel belongs to it when its
center lies inside), `reduce` (`every` pixel or the per-channel `mean`), a
`minimum` and/or `maximum` RGBA bound in normalized channel values, a
`toleranceCodes` widening in 8-bit codes, and an explicit `holds`. Bounds come
from the author's arithmetic, never from a recorded run. A missing capture, a
wrong extent, or a region with no pixel center fails in either direction.
A leg's `world` is a repository-relative `.world.json` document or `.puck`
source; a leg booting a composition source may name the declared world it boots
with `entry`, passed to the World as `--entry`. A manifest may start a companion authority
world, pass its allocated endpoint through `connect`, and use `{run}` in scripts
and assertions for per-leg capture paths. There are no regex programs, loops,
callbacks, conditionals, shell, or embedded scripts.
Exit codes are 0 for all proofs held, 1 for an observed proof failure, and 2 for
usage, manifest, build, or infrastructure refusal, including an unsupported
environment.

A leg may instead declare `authorities`: an array of `{id, world, script}`
naming at least two listeners, none dialing out—the N-ary generalization of
the singular `authorityWorld`/`connect` companion pair above, mutually
exclusive with it. Every entry gets its own loopback endpoint and generated
federation identity. The runner picks each endpoint before the entries boot, by
binding a UDP socket to a free port (QUIC runs over UDP), because each entry's
document names its own endpoint as `host.authority`. The identity is pinned into every other entry's
admission rows the same way a two-process leg's companion is; the entries
launch concurrently and run to completion before any assertion reads a
transcript. Each entry keeps serving the others after its own script ends:
the runner sends `quit` to every entry together, once each one has answered
its final `wire.errors` or exited. A companion authority is likewise sent
`quit` when its client's run ends, and is killed only if it hasn't exited
within ten seconds. Exactly one entry's `world`/`script` must equal the leg's
own—the entry an assertion with no `authority` selector reads by default—and
a `line`/`response`/`sequence` assertion may add `"authority": "<id>"` to read
a different entry's transcript instead. A federated leg's `timeoutSeconds`
ceiling is wider (concurrently-spawned processes on a shared machine see real
spawn/handshake variance a single process does not).
`tests/Puck.World.Canaries/four-corners-sharded` is `authorities`'
first user—five real processes (four ground worlds plus the floating
island), one human-driven body ringing all four ground authorities. Not yet
expressible in that same canary, owed to future widening rather than a
runner limitation: vertical/island crossing, retained dual-stick camera and
movement control across a handoff, autonomous producer-driven travellers,
derived diagonal corner peers, and a cross-authority contact-pair settling
observation. A killed or mispointed authority turning the corresponding
transfer/address observation red is out of scope for any two-leg manifest
by the format's own rule (a manifest is exactly `positive`/`discriminating`);
a Silo-hosted (rather than `dotnet run`) authority entry is a documented
future transport arm, not a reshape of `authorities`' current members.

### Where the World artifact is built

`puck canary`, `puck parity`, `puck counters`, `puck test`, and
`puck docs citations` boot the Release build of `src/Puck.World`. They share one store of these builds, one
per source state, and build only when the store has no build for the
checkout's current sources. A second run over an unchanged checkout builds
nothing. `puck test --world-artifact <dll>` and `puck canary --world-artifact
<dll>` bypass the store and launch the named entry assembly as given, which is
how `puck qualify` runs canaries on a published package.

The store is the `world-builds` subdirectory of the
[per-user Puck directory](../development/contributing.md#per-user-directory):
`%LOCALAPPDATA%/Puck/world-builds` on Windows and
`~/.local/share/Puck/world-builds` on Linux. It is never inside the checkout.

A build is keyed by the sources it is made from. The key covers the World
project, every project it references (including `Puck.Cli` and
`Puck.Analyzers`, which carry no assembly into it), and every file those
project files import or link from elsewhere in the checkout. It also covers
every file directly in the repository root. For these paths, the key hashes
git's object ids in `HEAD` together with the content of every uncommitted,
staged, or untracked change that `git status` reports. The machine's runtime
identifier and the build command line are part of the key too. Edits to
documentation, tests, or projects the World does not reference leave the key
unchanged. Worktrees whose World sources are identical share a build.
`WorldArtifactClosureLawTests` evaluates the World's project graph with MSBuild
and fails when the build reads a file outside the keyed paths. Outside a git
checkout, no key can be computed, so the run builds under a name that no later
run can reuse.

On a miss, the run builds with `--output` into a sibling directory of the
store and renames it into place once the build succeeds. Compilation stays
incremental through the checkout's `obj` directories. A run that finds another
run building the same key waits for that build and uses it. The progress line
(`<verb>: building Puck.World once (Release) for source state <key>.` or
`<verb>: reusing the Puck.World build of source state <key>.`) goes to standard
error, so standard output carries only results. Each build is several hundred
megabytes. The store keeps the four most recently used builds, plus any build a
run still holds, and prunes the rest. A build directory left by a killed run is
deleted after six hours.

None of these verbs builds in place. `Puck.World` has a build-time reference to
`Puck.Cli`, whose build compiles the shipped `.puck` worlds, so an in-place
World build would also write into the CLI's own Release output directory
(`bin/Release/net10.0` under `src/Puck.Cli`). A CLI started from there holds those assemblies open, and the copy would fail with
MSB3027. Because the build goes to the store, a branch can run these verbs from
its own build output:

```text
dotnet build src/Puck.Cli -c Release
dotnet src/Puck.Cli/bin/Release/net10.0/Puck.Cli.dll canary pipeline-feedback pipeline-ink pipeline-edit pipeline-supersede pipeline-shapes pipeline-resize pipeline-counters pipeline-override pipeline-package pipeline-budget pipeline-churn pipeline-fault pipeline-geometry
```

---

## `puck landing`—git-loss check, then the automatic canary set

`puck landing --against <tip> --base <authoring-base>` refuses a commit that
drops content its author never worked from — the shape a `git reset --soft
<tip>` before a squash produces, which re-parents the author's own tree onto
a newer tip and silently reverts everything that tip added. It compares the
lines HEAD deletes relative to `<tip>` against the lines it deletes relative
to `<base>`; anything in the first set and not the second arrived on the tip
while the author was working and would be lost. `<base>` is required and
cannot be derived — `merge-base(tip, HEAD)` returns the tip itself for a
re-parented tree, which would make every deletion look intended — and a
`<base>` equal to `<tip>`, or one HEAD does not descend from, is refused for
the same reason.

The git-loss check runs first, and only once it passes does `puck landing`
run the nonempty automatic canary set (`puck canary`'s headless,
no-environmental-requirement subset). There is no flag to skip either
component. Exit codes: 0 both components passed; 1 unaccounted deletions or
an observed canary failure; 2 usage, manifest, build, or infrastructure
refusal.

---

## `puck test`—test worlds

A test world carries its own test. Its `schedule` section says what command to
submit and at exactly which simulation tick, acting as which seat; its
`verdict`-marked `state` rows say what the rules must conclude, and the rule
that decides each one writes the status plus the values its gate saw into that
row's own cells. A verdict row holds ints, so a value the gate saw of a `Fixed`
or a `Bool` row is written to a `witness` row of that kind, which names its
verdict and is printed with it (`saw=[hp=3 speed=2.25 open=false]`). `puck test` boots each world through the real `Puck.World`
executable, headless and unpaced, and reads the verdicts back out of the canonical state
export the world writes at the tick its schedule derives (the last scheduled
tick plus the declared `settleTicks` margin). Unpaced means the ordinary fixed
step loop advances exactly one simulation tick per iteration without sleeping
for wall time; command ingress, authority, physics and rules stay on their usual
paths.

A path may name a `.puck` source instead of a document, which is how behaviour
is normally written: the source is compiled and the worlds its
[`test` blocks](../authoring/testing-a-world.md) generate are what run, one per
block, named `<source stem>~<test slug>` (a
[generated name](dsl.md#generated-names); a source whose file name carries `~` is
refused). A directory contributes the file carrying each document name: the
`*.puck` source that emits it where one does and its `*.world.json` document
otherwise, so a document beside the source of its name emitting that name is not
run. A source carries exactly the names it emits: a module library none, so a
`*.world.json` named like it runs and the library itself is still swept for its
tests, and a composition the worlds it declares, so its file runs once. It skips a source that authors no test,
and refuses two files whose document names differ only in letter case. A source that does not compile is exit 2; so is a single named
source with no test block, since the verb was asked to run something that is not
there.

Four kinds of block reach this verb, and the generated world differs by kind:

- `test "name" { … }` at a world's root generates that world plus what the test
  asked for.
- `test "name" { … }` at the root of a source that emits several worlds
  generates one document per world, named `<source stem>~<test slug>` for the
  composition's `entry world` and `<source stem>~<test slug>~<world>` for each of
  the others. Only the entry is a world this verb boots, as a boot of the source
  starts there; the rest are armed beside it by its own `schedule.instances`. A
  composition with such a test and no entry is refused (PUCK104).
- `test "name" with module(arguments) { … }` generates the named module expanded
  with those arguments and nothing else. The generation line names the module
  and the arguments, so a failing run says which instantiation failed.
- A `test` written inside a `module` body runs once per distinct instantiation
  of that module in the source — each `use` and each `world name =
  module(arguments)` — under that instantiation's own arguments, and is named
  for the instance (`north a beacon boots dark`, generating
  `<source stem>~north~a-beacon-boots-dark`; a world declaration's name already
  heads its world, `north~a-beacon-boots-dark`). Instantiations that read
  identically run it once.

A world document whose `schedule` section declares `instances` arms those sibling
worlds beside the one the run boots with, steps them in the same host step, and
writes one state export per world at the tick the booted world's schedule
declares. Each world's verdicts are read from its own export and reported under
its name (`south/southStepped`); `--reproduce` compares every world's bytes, not
just the booted one's. A row's own `world` member addresses an armed sibling, and
is admitted only for the verbs whose grammar carries a world token — `body.fly`,
`body.pose`, `body.stop`, `player.join`, `player.leave`. Every other admitted
verb reaches the booted world whatever a row asks, so addressing a sibling with
one is refused when the document is validated.

This verb is the only door that runs a module's tests. `puck compile` lowers and
does not boot, so a module whose tests fail still compiles. A source that emits
several worlds is compiled to all of them, each world's module tests run, and a
subjectless `test` at its root becomes the composed run above.

What a scheduled row may say is closed on both axes, because a world document
travels and any boot can be asked to load one:

- The section runs only when the boot **arms** it with `--schedule-dir`, which
  `puck test` supplies. Any other boot of a document carrying a schedule
  submits no row, writes no export, and prints one line saying the section is
  present and unarmed; `world.schedule` reports the same either way.
- A row's `command` opens with a verb from a closed step vocabulary — state
  mutations, guarded transforms, body intents and poses, and joining or leaving
  a seat. Anything that touches the process, the clock or the filesystem
  (`quit`, `world.rate`, `world.save`, `world.load`, the capture and screenshot
  verbs) and anything that changes who may act (`world.grant`, `world.revoke`)
  is refused at validation, by name, with the row's index.
- A row's `principal` is `seat1`..`seat4`, parsed by the same grammar the wire
  codec enforces, so a seat the document admits is a seat a submission reaches.
  `console` is refused: it is trusted at every gate, so a step acting as it
  would prove nothing about authority. The power a step needs is authored in the
  document's own `grants`, and its starting state in its own `state` cells.
- A row declares the outcome it expects: `expect: "Submitted"` by default, or
  `expect: "Refused"` (optionally with `refusal: "<text>"` the recorded detail
  must carry) for a step whose point is that the world says no. Any other
  recorded outcome fails the world by name with the row's index, its command and
  what the run recorded. An outcome the **host** rather than the world answered —
  a handler that threw (`faulted`), a principal with no ingress (`unroutable`), a
  row still awaiting its answer (`pending`), or a refusal that never reached a
  handler (`[wire.reject: …]`) — is exit 2 instead: nothing about the world was
  measured.

```text
puck test <path>                       a .puck source, a *.world.json document, or a
                                       directory of either
puck test <path> --host server         the real Puck.World executable, headless (the default)
puck test <path> --world-artifact <p>  boot this already-built Puck.World.dll instead of
                                       the stored build of the checkout's sources
puck test <path> --keep <dir>          run in <dir> and keep it: every generated test world
                                       under <dir>/generated, plus transcripts, exports
                                       and manifests
puck test <path> --jobs <n>            run at most n isolated test worlds concurrently
puck test <path> --reproduce           rerun every world and require byte-identical state
                                       exports and schedule manifests
puck test -h / --help                  this text
```

Each collected world owns a numbered directory under `<dir>/worlds`, starting
at `000000/run1`. Its `state` persistence, `out` exports and manifests, and
transcripts are isolated even when input files share a basename. Each report
names the source and its artifact directory. `--reproduce` writes `run2`
beside `run1`.

A source compiles through the compile cache the game shares (`compilations` in
the [per-user Puck directory](../development/contributing.md#per-user-directory)), as does every basis and import its test
worlds are staged with, so a run over an unchanged source compiles nothing a
previous run or boot already compiled. A held compile stands only while every
file it read, and every path it probed, is unchanged.

Reaching the authored export tick is part of the verdict. A run that stops
early still writes an export — of whatever tick it reached — and the verdicts
read off it answer a different question, so each leg is reconciled against the
document before any verdict is reported: the tick reached must be the tick
declared (`exported at tick 2, authored 56`), and every declared `schedule.rows`
entry must have a recorded outcome at its own position in the manifest. A row
the run never got to records `unreached`, and a leg carrying one is refused.
Both are exit 2, not a failing verdict: nothing was measured.

A scheduled run is not resumable, and says so rather than half-supporting it:
inside an armed run `world.save`, `world.load`, `world.reload` and `world.undo`
are refused by name. A schedule carries no cursor — which rows have been
submitted is the process's own state, never the document's — so a restored,
re-read or rewound world submits every row again from tick 1 and measures a
different trajectory from the one the export was asked for.

Ordinary authoring runs every isolated test world once. `--reproduce` adds the
determinism qualification pass: each world runs twice into sibling leg
directories, and a world whose two exports or two `schedule.json` manifests
differ byte for byte is refused rather than reported. The manifest records only
facts a rerun reproduces exactly: a row's authored tick, outcome and detail. A
recorded edit verdict carries no tick because an echo reaches the runner when
the host narrates it, which is not the tick the edit applied on. The comparison
is possible because the export tick is the document's rather than the runner's.

Only a rule's own effect writes a verdict row. The rule-effect door stamps the
firing's simulation tick into the row's reserved `$firedTick` cell — an ordinary
cell from there on, so the arena's export, the state hash and a checkpoint all
carry it — and a status with no stamp beside it is reported as "never evaluated"
whatever the cell reads, so a status a cell trait accrued into, or a scheduled
command wrote, cannot pass. Every other door refuses the write by name — the cell-mutation
door, a submitted state operation, and therefore a scheduled command, all with
one text — and validation refuses a verdict row that declares a value-over-time
trait (`advance`, `dynamics`, `cycle`, a cell `clock`, `draw`, `valuesFrom`), an
authored ceiling (no ceiling bounds a tick), or a cell capacity with no room for
the stamp.

A failing verdict is sticky: once a firing writes `fail`, later firings are
absorbed, for the verdict row and its witnesses alike, so the report names the
first failing tick and the values the gate saw then. An expectation is therefore written at the tick it becomes decidable — a
rule whose `else` branch fails pre-emptively settles the verdict on its first
tick and can never pass.

One line per verdict, naming the row, the status, the gate it claims, the values
it saw, and the firing tick. Refusals the run recorded (a scheduled command the
world refused, which a test world often schedules on purpose) print beside the
verdicts, read from the run's own `schedule.json` manifest.

Exit codes: 0 every verdict passed and every row answered what it declared, 1 a
verdict failed or a row's recorded outcome was not the one it declared, 2 usage —
a world declaring no `schedule` section or no verdict row, a build or boot
refusal, a leg that did not reach its authored export tick or account for every
declared row, a row whose outcome the host rather than the world answered, a
world whose `--reproduce` runs disagreed, or `--host browser`, which is refused by name
because the browser engine host has no command ingress, principal or
authoritative server to submit a scheduled command through.

The worlds are under [`tests/Puck.World.Verdicts`](../../tests/Puck.World.Verdicts/README.md);
the pairs reproducing each repaired hole are in
[`proofs/`](../../tests/Puck.World.Verdicts/proofs/README.md) beside them, run by
law rather than by a directory sweep.

---

## `puck parity`—cross-backend parity over the authored parity world

`puck parity` boots `tests/Puck.Parity/parity.world.json` once per graphics
backend (Vulkan, Direct3D 12) with `host.presentation: offscreen`—no window
is shown—and lets the world's own `captures` rows land every tick-scheduled
capture and write a `puck.parity.manifest.v1`. Because both backends capture
the same simulation ticks, each pair observes one moment by construction.
The offscreen host never steps past an armed capture's tick until the capture
is served or refused, so a cold driver shader cache lengthens a leg instead of
losing its first capture. After 60 seconds of holding in all, a capture the
render chain still cannot serve is refused as `unserved`, naming the reason,
and the leg runs on (see [the offscreen shape](../../src/Puck.World/README.md#usage)).
The two manifest directories are then compared by `puck parity compare` under
the contract versioned beside the world
(`tests/Puck.Parity/parity.contract.json`).

```text
puck parity                                            full run: both backends, then compare
puck parity compare <leftDir> <rightDir> --contract <file> [--output <dir>]   compare two captured runs
```

Per capture, three independent verdicts, in order:

1. **Content gate**—a capture its producer refused by name (`cameraInside`,
   `busy`, `stale`, `failed`, `unserved`, `deviceLost`; see the
   [parity README](../../tests/Puck.Parity/README.md)), missing, or below its
   station's census floor never reaches comparison: agreement between
   degenerate frames is vacuous.
2. **State verdict**—`stateHash` equality, exact, no envelope. A one-bit
   sim-state divergence is a defect, never noise.
3. **Pixel verdict**—per-tile mean/max deltas against the station's contract
   thresholds. A localized defect cannot dilute itself across a whole-frame
   mean.

Failures write both frames, a per-pixel delta heatmap, and a per-verdict
summary into the run's `evidence/` directory—a red names its tile and shows
its pixels. There are no stored baselines: both runs come from the same build,
so content changes cannot fail the check, only a cross-backend divergence can.
The runner resolves the `Puck.World` build for the checkout's current sources,
keeping the build's logs beside its transcripts when this run built it (see
[where the World artifact is built](#where-the-world-artifact-is-built)), runs
each leg from fresh state with its own `--state-dir`, and requires every
scripted command accepted
(`wire.errors` closes each transcript with zero rejections). It needs both
GPU devices but takes over no display. Exit codes: 0 every capture held all
three verdicts, 1 a verdict failed, 2 a leg/build refusal or a malformed
manifest or contract.

---

## `puck counters`—work-counter collector

`puck counters` reads the engine's deterministic work counts from a real World
run, so a performance change can be judged by counted work rather than by time.
`puck bench` stays the only tool that measures wall-clock time.

```text
puck counters [--output <file>]            run the workload on both backends and write the report
puck counters compare <left> <right>       compare two reports
```

The run boots `tests/Puck.Counters/counters.world.json` once per backend
(Vulkan, then Direct3D 12) with `host.presentation: offscreen`, so no window is
shown. It uses the same World build and leg machinery as `puck parity` (see
[where the World artifact is built](#where-the-world-artifact-is-built)). Each
leg runs `tests/Puck.Counters/counters.script.txt` on the World's console. The
script turns off the cadence gate so every frame renders every pass, pauses the
simulation until the engine is ready, resumes it, waits a pinned number of
ticks, and asks for one `world.counters --json` reading, so both backends read
at the same tick however long their engines took to build. The
runner closes the script with `wire.errors` and `quit` and requires every
command accepted.

The report is a `puck.counters.report.v1` document. Its schema,
`tests/Puck.Counters/puck.counters.report.v1.schema.json`, is generated by
`puck schema` and checked by `puck schema --check`. The report records the
source revision (the `HEAD` commit and the World build's source-state key) and,
for each backend, the device identity, the offscreen resolution, the shader
toolchain identity, the World's GC mode, each render node's pass states, and
every count. Each count names its section, node, pass and kind, and carries a
class:

| Class | Meaning | Compared |
|---|---|---|
| `deterministic` | The same inputs give the same count on every run and backend: simulation counts at a pinned tick, and the GPU counts of one submission. | Across backends in one run, and between two reports. |
| `per-backend-deterministic` | The same on every run of one backend: the GPU objects a node creates, and the counts of a pass whose work follows the device (the SDF engine's `upload`, which follows its residency policy). | Between two reports, backend by backend. |
| `pacing` | Depends on timing or on state outside the run: which submission a read lands on, skipped presents, the compile cache's hits. | Never. |
| `allocation-zero-nonzero` | A managed-allocation reading from `AllocationWindow.Measure` over a named window (`world.counters.read`), recorded with the GC mode. | Only as zero or not zero. |

Each kind's class is declared by the kind's owner (`WorkKind.Class`) and
reaches the collector through the `kinds` legend of `world.counters --json`.
Each pass there also carries a class, `deterministic` or
`per-backend-deterministic`, which the node declares (`GpuWorkLedger.Configure`);
a deterministic kind counted in a per-backend-deterministic pass is recorded as
per-backend-deterministic. A node's submission and revision identities are not
kinds; the collector records them as `pacing`.

The run prints the report's path, then one line for each deterministic count or
pass state that differs between the two backends, naming its kind, pass and
node. `--output` names the report file; without it, the report stays in the
run's scratch directory beside the leg transcripts.

`counters compare` holds each backend's run in the right report to the same
backend's run in the left. Deterministic and per-backend-deterministic counts
must be equal, an allocation reading must be zero in both or not zero in both,
and pacing counts are ignored. A count or pass one side lacks, a pass state that
moved, or a class that moved is a difference. It prints one line per
difference, naming the backend, class, kind, pass and node. When the reports ran
different sources, a note on standard error says so.

Exit codes: `puck counters` exits 0 when the backends agree, 1 when a
deterministic count or pass state differs, and 2 for a build, leg or reading
refusal, including a missing GPU device or shader tool. `counters compare`
exits 0 when every comparable count agrees, 1 on a difference, and 2 for a usage
error or a file that is not a readable report.

---

## `puck qualify`—package qualification

```text
puck qualify <package> [--profile <file>] [--list] [--output <file>]
```

`puck qualify` holds a producer-built `Puck.World` package, such as CI's
`artifacts/world`, to the release profile
(`tests/Puck.Qualification/release.profile.json`, a `puck.release.profile.v1`
document whose schema `puck schema` generates). It never builds: it checks
that the package's entry assembly is in the profile's publish mode, installs a
clean copy, runs the profile's functional canaries on the copy's World with
`puck canary --world-artifact`, then runs the stability matrix offscreen
through the same leg machinery as `puck counters`. Each cell is a workload at
one resolution on one backend, booted from a fresh state root. `--list` checks
the package and prints the matrix and every cell's script without booting
anything. The run writes a `puck.qualification.report.v1` report to `--output`
or its scratch directory.

[Qualifying a package](../development/qualification.md) owns what the profile
records, what each cell checks, what each verdict means, and which thresholds
are set or deferred.

Exit codes: 0 everything passed, 1 a canary or a cell failed, and 2 for a
refused profile, package or plan, or for a blocked check when nothing failed.

---

## `puck docs`—the documentation family

```text
puck docs build [--output <directory>]      stage the website reference (default <repo>/artifacts/docs)
puck docs links [<document> ...]            check relative links, heading anchors, and cited repository paths
puck docs citations [--enumeration <path>]  check the console-verb tokens skills and XML docs cite
```

### `docs build`

Runs the pinned DocFX tool and stages `reference/` (with the site overview as
`reference/overview.html`) and `_theme/` under the output directory, which
defaults to `artifacts/docs` at the repository root. An output directory that
already holds `reference/` or `_theme/` is refused.

### `docs links`—relative link and path check

Checks a fixed documentation set (the world-project READMEs, the repository
root README, and the engine manual's entry and topic pages) for citations that
stopped resolving: relative markdown links, backticked rooted repository
paths (`src/...`, `docs/...`, `tests/...`, `build/...`), and backticked bare
filenames (looked up in an index swept from `src/`, `docs/`, `tests/`,
`build/`, and `.claude/skills/`—enforced under `src/`, advisory elsewhere,
since a `docs/` document legitimately names out-of-repo files). Naming
repository-relative markdown files checks exactly those instead.

A link's `#fragment` into a markdown file, or a bare `#fragment` into the
document itself, must name one of that file's heading anchors. Anchors are
slugged the way GitHub renders them: the heading's text with its markup
dropped (code spans keep their content), lowercased, with every character
that is not a letter, number, space, hyphen, or underscore removed and each
space turned into a hyphen. A repeated heading takes `-1`, `-2`, and so on
in document order, and a `#` line inside a code fence is not a heading. So
`` ### `puck scan`—source sweep `` answers to `#puck-scansource-sweep`. A
fragment on a link to anything other than a markdown file is not checked.

Two controls run before any document—a deliberately nonexistent path must
fail resolution, and a deliberately absent fragment must miss a heading's
anchors—so a green run proves the checker can turn red. Unlike
`docs citations`, this covers file/path citations, never console-verb tokens.

Exit codes: 0 every citation resolved, 1 one or more citations did not
resolve, 2 usage or no repository root.

### `docs citations`—cited verb token check

Sweeps `.claude/skills/**/*.md` for `` `backticked` `` tokens and `src/**/*.cs`
for `<c>…</c>` tokens, keeps those shaped like a console verb (a family the
console actually uses, dotted), and resolves each against: the console-verb
enumeration, verb names spelled literally in registrations, every other
verb-shaped string literal under `src/`, and every world-document field path
the generated section schemas under `src/Puck.World/Assets/worlds/schema/`
declare (`storage.userId`, `audio.masterGain`)—a document field is cited
exactly like a verb and the generated schema is the vocabulary that cannot
drift from the shape.

With no `--enumeration`, the console-verb vocabulary is booted rather than
read from a file: this verb resolves the Release `Puck.World` build (see
[where the World artifact is built](#where-the-world-artifact-is-built)) and
runs it twice—headless, then windowed—piping `help` over stdin to each and
unioning the two vocabularies, because some command modules register only
under one boot shape. Both boots run in the run's own scratch directory, so
nothing a world writes relative to its working directory lands in the
caller's. If a verb spelled literally in a registration is absent from that
union, the run refuses rather than report—checking citations against an
incomplete vocabulary would accuse correct documentation of quoting dead
verbs.

Exit codes: 0 every citation resolved, 1 unresolved citations (each named on
standard output), 2 usage error, no repository root, the enumeration
boot/build refused, or an enumeration provably incomplete, with nothing
reported against it.

---

## `puck search`—content search

A ripgrep-shaped CLI over a non-backtracking symbolic-derivatives regex engine:
linear-time, leftmost-longest, with intersection (`&`), complement (`~(...)`),
and lookaround; no backreferences. `_` is any character including newline.

```text
puck search <pattern> [path ...]        content search (default path: cwd)
  -i / --ignore-case                    case-insensitive
  -F / --fixed-strings                  literal string (escape the pattern)
  -l / --files-with-matches             files-with-matches only (wins over -c)
  -c / --count                          per-file matching-line counts
  -n / --line-number                    line numbers on (the default)
  -N / --no-line-number                 line numbers off (wins over -n)
  -A / --after-context <n>              n context lines after
  -B / --before-context <n>             n context lines before
  -C / --context <n>                    both sides; an explicit -A or -B overrides that side
  -g / --glob <glob>                    include glob (repeatable; no '/' matches basename)
  --not <glob>                          exclude glob (repeatable; no '/' matches a file OR directory basename)
  -s / --span                           span mode: run over whole-file text, print start-end line ranges
  -M / --max-results <n>                max results (default 250, 0 = unlimited)
  --files                               enumerate the files that would be searched
  -q / --quiet                          quiet: exit code only (--files included)
  --                                    end of options: every later argument is pattern/paths
  -h / --help                           this text
```

Exit codes: **0** matched, **1** no match, **2** usage/pattern error (the
engine's own parse message is printed verbatim). A path argument that names
nothing on disk is one of those usage errors—every bad path is reported before
the run gives up, so a typo cannot pass for "no match". The recursive walk skips
`.git`, `.tmp`, `artifacts`, `bin`, `obj`, `node_modules`, `publish`,
`BenchmarkDotNet.Artifacts`, agent worktrees under `.claude/worktrees`,
directory links (junctions and symbolic links, which the walk never follows),
and binary files—naming one of those paths searches it anyway. That set is
`FileWalk.SkipDirectories`, the one prune set every tree-walking verb shares;
`format` alone also skips the quarantined `experimental/` trees, which every
reading verb still searches. The build-artifact
names are on that list because this project writes into them: publishing drops a
generated `Puck.Maths.xml` next to the executable, whose duplicated doc comments
would otherwise drain the default `-M` cap ahead of `src/Puck.Maths/` itself.
`.claude/worktrees` holds live duplicate checkouts, whose copies otherwise answer
a query as if they were live consumers. The full flag, glob, and engine-semantics
reference—including the leftmost-longest and complement gotchas—lives in the
`content-search` skill (`.claude/skills/content-search/SKILL.md`), which drives
every content search in this repo through the published `puck search`.

---

## `puck bench`—the Puck.Maths microscope

The on-demand **microscope** for the `Puck.Maths`, signed-distance and state
kernels, and the only place their timing is measured. The law suite states cost only as a deterministic count, so it answers
*does the kernel allocate?*; this verb answers *how fast is it, and why?*—
instruction-level disassembly, per-scenario allocation columns, and full
statistical detail (mean / error / stddev / percentiles).

### When to reach for which

| | The law (`complex.multiply-routes-allocate-nothing` in `tests/Puck.Maths.Tests`) | This microscope |
|---|---|---|
| Question | Do the hand-written and generic complex multiply allocate? | How fast is a kernel, and *why* is it slow / allocating / different from its neighbour? |
| Output | An allocation-meter verdict, pass/fail | Disassembly, alloc bytes, stddev, percentiles, ratios vs baseline |
| Speed | Fast, runs in the Default tier | Slow, run by hand on a quiet machine |
| Determinism | Exact byte count, no clock | Same fixed seeds and regimes; framework owns the timing loop |

### Benchmark inventory

The algebra benchmark classes form a numbered scenario grid, one class per
scenario, each comparing the hand-written kernel with its two generic
placements. Every scenario in the grid below is a manual microscope workload;
`puck bench kernels --filter '*ComplexMulNarrow*'` is the generic-versus-hand
complex multiply ratio.

| Bench scenario | Class here | Methods |
|---|---|---|
| `1. complex mul narrow (latency)`   | `ComplexMulNarrow`   | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `2. complex mul wide (throughput)`  | `ComplexMulWide`     | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `3a. split mul narrow (latency)`    | `SplitMulNarrow`     | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `3b. split norm narrow (throughput)`| `SplitNormNarrow`    | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `4. dual<FixedQ4816> mul (latency)` | `DualFixMul`         | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `5. dual quaternion mul (latency)`  | `DualQuaternionMul`  | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `6a. extension mul (latency)`       | `ExtensionMul`       | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `6b. extension-only operations`     | `ExtensionOnly`      | `Frobenius`, `BatchInverse` (no generic counterpart—structural gap) |

The microscope also contains workload-specific classes outside that scenario
grid. Transform families include direct/naive baselines,
pristine-input forward/inverse latency, and explicit plan-construction cost:

| Workload | Classes | What is measured |
|---|---|---|
| Number-theoretic transform | `NttConvolveVsNaive`, `NttForwardInverse` | Cyclic convolution against the O(N²) definition; forward/inverse latency. |
| Walsh–Hadamard transform | `WhtForwardVsNaive`, `WhtForwardInverse` | Network against the O(N²) definition; forward/inverse latency. |
| Fixed Fourier transform | `FftForwardVsDirectSum`, `FftForwardInverse`, `FftConvolveVsNaive` | Forward/convolution against direct definitions; forward/inverse latency. |
| Fixed cosine transform | `DctForwardVsDirectSum`, `DctForwardInverse` | Fourier route against the direct DCT; forward/inverse latency. |
| Reusable transform plans | `TransformPlanCreation` | Construction time and allocated bytes for NTT, FFT and DCT plans. |
| Encoded square and hex coordinates | `EncodedOperations` | Direct norm/sum, swap, scale and translation against decode–operate–encode, plus specialized hex radius against the general layer locator; 1024 deterministic mixed small and wide inputs, normalized per cell. |
| Combination and permutation identities | `CombinationQueries`, `PermutationQueries` | Counts, ranking, unranking, and single combination elements over 512 deterministic inputs; permutations also compare with a validated quadratic inversion-count baseline. |
| Fixed-point scalars and rates | `ScalarKernels`, `RateAccumulation` | Narrow and wide multiply, square root, fractional power, sine/cosine and complex divide; one tick of rate integration. |
| Rotations | `QuaternionKernels` | From-to construction, slerp, logarithm and normalization. |
| Curvature splines | `CurvatureSplineKernels` | Compiling a spline and evaluating it. |
| Lattices and noise | `LatticeKernels`, `LayerSequenceQueries` | Field noise (one sample and four octaves), lattice value noise, hex distance, the modular cusp and a sieve window; layer lookup and location in a layer sequence. |
| Finite fields | `ReedSolomonKernels`, `PrimeFieldBatchInverse` | Reed–Solomon generator construction and syndromes; batch inversion against one inverse at a time. |
| Signed-distance culling | `SdfFieldCull` | One distance query over one near sphere and 4000 far instances of eight spheres each, where each far instance should cost one bound test. |
| State kernels | `StateArithmeticKernels`, `StateExpressionKernels`, `StateBulkKernels` | Binary, unary and bit-field expression operations; a compiled expression program across token counts, operand reads and dependency shapes; block clear, copy, scan and sort. |
| Vector ranking | `VectorNearest` | One `nearest` firing through the effect host over a 256-key, 256-dimension table into a five-match ranking. Its tests hold the firing to zero allocation and one scoring per key; this is where its latency is read. |

Each forward/inverse latency class uses one invocation per iteration and
restores its working array in `IterationSetup`, outside the timed operation, so
every sample measures the same data regime rather than another transform of the
previous sample. Inverse inputs are valid spectra precomputed once from the
matching forward inputs during global setup.

`GenericStatic` reads the algebra from a static-readonly field (the JIT may fold
`P`/`Q` to constants after tier-up); `GenericLocal` receives it as a
by-parameter argument into a `[MethodImpl(NoInlining)]` method so it cannot—the
same two placements the gate measures.

`MemoryDiagnoser` rides on every class. `DisassemblyDiagnoser` is attached only
to the **fixed-point kernels** (scenarios 1–5); the extension scenarios are
modular-`ulong` arithmetic, not a fixed-point kernel.

### Running it

```sh
# Everything (balanced default job):
puck bench kernels --filter '*'

# One scenario family — the norm quirk, with the disassembler:
puck bench kernels --filter '*Norm*'

# Just the hand-vs-generic-static comparison of one class:
puck bench kernels --filter '*SplitNormNarrow.Hand' --filter '*SplitNormNarrow.GenericStatic'

# List what is available without running anything:
puck bench kernels --list flat
```

Every token after `puck bench kernels` reaches BenchmarkDotNet's switcher
verbatim. `-h`/`--help` there is puck's own, so the switcher's help (whose `-h`
is its `hide` option) is reached past a separator: `puck bench kernels -- --help`.

(Run through `dotnet run --project src/Puck.Cli -c Release -- bench kernels --filter '*Norm*'`.)

#### Job rigor

No explicit job is baked into the config, so a command-line `--job` is the only
job that runs (the harness would otherwise run its own job *alongside* one added
here and double the output). Pick rigor on the command line:

| Flag | Use |
|---|---|
| *(none)* | BenchmarkDotNet's adaptive default—the sane balanced setting for everyday runs. |
| `--job short` | **Fast survey.** Fewer warmup/target iterations; a quick shape-of-the-numbers pass. |
| `--job long` | **Thorough verdict.** Many iterations, tight error bars—use before a retention decision. |

### Measurement hygiene

**Run on a quiet machine.** Numbers taken under concurrent load—a build, the
test suite, another benchmark, a busy GPU—are *garbage*, not slow-but-usable
data; this is a measured house fact. Close background work first. If two runs of
a scenario disagree by more than ~10%, the machine was not quiet—rerun. The
kernels are measured exactly as written (the `Int128` widening multiply in the
wide path is deliberate and is not "fixed" here). Do not commit result artifacts;
this verb produces evidence for a decision, not baselines to pin—the
`BenchmarkDotNet.Artifacts/` directory it writes under the cwd is git-ignored.

### `puck bench state-evidence`

Replays every normalized instruction form in `src/Puck.State/ReferenceSchedule.json`
against every pinned `llvm-mca` processor model, requires the manifest's exact tool
version, and compares the resulting latency/throughput service `q`. It finds LLVM on
`PATH` or at the standard Windows install location; `--llvm-mca <path>` selects an
explicit executable. It then prints one line per registered vocabulary—expression,
effect, transform, generator, search method and search shape—with how many of that
vocabulary's operations carry a reference-cycle price and how many carry an explicit
unmodeled reason. A reachable unmodeled operation is what withholds certification of
the scoped deadline, so that count is the remaining work list.

```sh
puck bench state-evidence
```

Before producing new kernel artifacts, capture the exact work list and verify that
the checked-out sources still match the manifest:

```sh
puck bench state-evidence --inventory --output artifacts/state-evidence/inventory.json
```

The inventory records the manifest digest, pinned SDK and roll-forward policy,
every Native AOT entry point and symbol, expected and actual source SHA-256 values,
per-target unresolved analyzer findings, the per-vocabulary coverage with each
unmodeled operation named, and every missing memory coefficient. A nonzero exit
means at least one kernel source digest is stale or absent. Run it from the
repository checkout; the command locates the checkout by ascending to `Puck.slnx`.

Each kernel artifact campaign must use the inventory's exact source digest and the
per-target `referenceBuild` settings in `ReferenceSchedule.json`: SDK 10.0.401,
the recorded runtime pack and ILCompiler 10.0.12, Release Native AOT, explicit
`IlcInstructionSet` (`x86-64-v3` or `armv8.2-a`), no method-body folding, and no
PGO. Retain the complete disassembly for the named symbol and every reachable
helper, including refusal paths. Run `llvm-mca` 19.1.6 with the target's recorded
triple and CPU model. An x64 build is not evidence for an AArch64 target, and an
unresolved indirect call or loop remains unresolved.

#### Capturing the evidence

`--capture <dir>` runs that campaign instead of replaying it:

```sh
puck bench state-evidence --capture artifacts/state-evidence
```

It builds `src/Puck.State.Search` in Release, whose output carries the whole
State family, and compiles one Native AOT object per ISA family with the
manifest's own pinned options, rooting `Puck.State`, `Puck.State.Generators`,
`Puck.State.Rules`, `Puck.State.Search`, `Puck.State.Topology` and
`Puck.State.Vectors` so a kernel's entry point has a body whichever of them
declares it. It disassembles each declared kernel symbol and every symbol its
calls and tail calls reach, reading a call into another section by the
relocation the object carries for it, asks `llvm-mca` for each
instruction form's latency and reciprocal throughput per pinned processor model,
walks each kernel's maximum permitted path, and renders the manifest sections
that walk establishes. Every number it produces comes from a tool: nothing is
inferred from the machine running the capture.

The kernel *declarations* are not regenerated. Entry point, symbol, reviewed
formula, declared loop bounds, included overhead and input domain are reviewed
and pinned in `ReferenceSchedule.json`; the capture regenerates the source
digests, the per-target prices, the instruction-form service rows and the
family-balanced representative service, then compares each against what is
pinned and names every difference. The manifest keeps the first few reasons a
target left a kernel unresolved; the run prints every one, since each is a loop
bound or a body that has to be supplied before the kernel has a price. It exits 0 when the capture reproduces the
pinned evidence, 1 when any of it differs, and 2 when a pinned tool or version is
absent.

| Option | Default |
|---|---|
| `--ilc <path>` | `<packages>/runtime.<host>.microsoft.dotnet.ilcompiler/<version>/tools/ilc.exe`, taken from the manifest's `ilCompiler`. |
| `--llvm-objdump <path>` | `PATH`, then `C:/Program Files/LLVM/bin` on Windows. |
| `--llvm-mca <path>` | The same search. |
| `--nuget-packages <dir>` | `NUGET_PACKAGES`, then the user profile's package root. |
| `--reuse-lowering` | Off. When set, an object already in the capture directory is walked instead of recompiled, and the capture names each object it reused. |

A pinned tool that is absent, or present at the wrong version, is a named
refusal: the capture says which version the manifest pins, where it looked, and
which option overrides that location. It never falls back to a different tool or
a different version. It refuses in the same way when `global.json` does not pin
the manifest's SDK with the manifest's roll-forward policy, because a recorded
SDK version pins nothing without it.

The scheduling-model answers are cached per model in `mca.<model>.json` beside
the objects, so a second capture over the same lowering asks only for forms the
cache does not carry. Shut the build server down first (`dotnet build-server
shutdown`); the walk is static analysis and is unaffected by other load, but the
reference build is not.

Loop bounds come from the kernel's declared `loopBounds`, each naming the symbol
it bounds and the source contract the bound is read from. A cyclic symbol with no
declared bound, an indirect exit with no candidate arm in the body, a recursive
call cycle, a helper with no body in the object, and an instruction form the
model does not serve each leave the kernel unresolved on that target and name
themselves. `ReferenceWalkerLawTests` pins the walk over hand-written
disassemblies: a straight line, a diamond taking its costlier arm, a bounded loop
multiplying its body, a helper priced with its caller, a jump table charged its
costliest candidate arm, and one law per refusal.

Memory activation additionally requires raw evidence for every pinned processor
family: configuration identity, clock basis, working-set sizes, access pattern,
scalar access and dependent-access counts, transferred bytes, allocations,
allocated bytes, and peak retained bytes. The inventory lists missing startup,
latency, and bandwidth fields but does not infer them from the current host.

The replay reproduces the manifest's instruction-service rows alone. It does not
derive Native AOT control flow, approve reviewed loop formulas, resolve helper
bodies, or calibrate memory service. `--capture` derives the control flow and the
prices; approving a reviewed formula and calibrating memory service remain
separate evidence, and unresolved rows remain unmodeled.

### `puck bench world`

The `Puck.World.Server` tick-path lane: `puck bench world` boots the shipped
`puck.world.json` and a checked-in Klondike fixture document
(`Bench/klondike.fixture.world.json`, spliced the way
`tests/Puck.World.Tests/SolitaireFixtures.cs`'s `Game` builds one, without this
project referencing the test project) and prints one row per number—
shipped-world server construction time, idle-tick time and quiet-tick
allocation (median over a sampled window, after a warmup), and a scripted
Klondike deal's per-tick time and per-mutation allocation. A server
construction includes admission, navigation, and runtime allocation, so this
lane measures it once with a plain stopwatch
harness (`WorldBenchmarks.cs`,
`WorldBenchHarness.cs`) rather than a `[Benchmark]` class, and it sits beside the
switcher as its own sub-verb rather than inside it:

```sh
puck bench world
```

Regenerate the fixture document only when
`Fixtures.BuildDocument` in [Fixtures.cs](../../tests/Puck.World.Tests/Fixtures.cs) or
`src/Puck.World/Assets/worlds/games/klondike.puck` changes underneath
it—it is a checked-in snapshot, not derived at run time.

### `puck bench startup`

Measures the actual World executable in fresh processes, serially. Build World
in Release first; the benchmark never builds inside a sample. With no world
arguments it runs a representative corpus: overworld, Moth courtyard,
Backgammon, Reversi, the three parlor games, the complete game host fixtures
under `tests/Puck.World.Tests/Fixtures`, and the Jump, Kart, and Dive canary
hosts. Most other files under `Assets/worlds/games` are modules that need a
host; they cannot be benchmarked by launching the module alone. Explicit paths
select another corpus:

```sh
puck bench startup --samples 3
puck bench startup worlds/parlor/chess.puck --samples 3 --width 640 --height 360
puck bench startup --headless --world-artifact src/Puck.World/bin/Release/net10.0/Puck.World.dll
```

The default windowed run uses 1280×720. Each sample receives a new persistence
directory, requests `world.status`, waits one tick, requests a frame capture,
and checks `wire.errors`. Console readiness is the observed status response;
rendered readiness is the observed capture completion with a nonempty output
file. A host that reports unavailable overlay glyphs fails the sample, even
if it writes a capture. Capture timing includes GPU readback, PNG writing, and pipe delivery, so
it bounds readiness conservatively. After capture completion (or the clean
command verdict in headless mode), the harness sends `quit`; slow rendering
cannot lose its capture to a fixed shutdown timer. The process timeout still
bounds failed runs. Shutdown time is excluded from both readiness intervals.
`--headless` measures only console readiness and does not prove render startup.

`--samples` defaults to 3 and `--timeout-seconds` to 120 per process. `--output`
selects the parent of a unique run directory (default `artifacts/startup`).
The directory holds logs, captures, and `report.json`, updated after each child.
The report records every sample, runtime, OS, artifact, dimensions, and the
expected sample count. Any failed or missing sample suppresses the corpus
summary; a completed failing run exits 1. A complete successful run reports
mean, median, nearest-rank p95, and maximum. This is process-cold startup:
disk, driver, and shader caches are retained. Measure cold-cache behavior
separately and label it explicitly. Use a quiet machine and repeat discrepant
measurements before treating a difference as an improvement.

---

## `puck registry`—world name registry

Writes `docs/world-name-registry.md` from `Puck.World.WorldNameRegistry`
(`src/Puck.World.Schema/WorldNameRegistry.cs`): every document field that
carries a state, zone, rule, table, pattern, topology, generator, field, or
dynamics name, with the role it carries the name in, the JSON paths derived by
walking the same source-generated `WorldJsonContext` the loader reads through.
`WorldModuleNamespace` reads the same registry to prefix an aliased import's
names at compose time, so the table and the compose path cannot disagree.

```text
puck registry               write docs/world-name-registry.md
puck registry --check       regenerate in memory and compare against the file on disk;
                            write nothing, exit 1 naming the first differing line
puck registry -h / --help   this text
```

Both modes first refuse, exit 1, on a name-shaped document member the registry
neither registers nor excludes with a reason—a `CellName`, `ExpressionProgram`,
`BindableScalar`, `BindableColor`, or `WorldLatticeScalar` member, or a string
member whose C# name reads like a name position—so a field added to the model
without a registration cannot pass. Exit codes: **0** wrote or matched, **1**
drift or an uncovered member, **2** usage error or missing repository root.

## `puck vocabulary`—world authoring vocabulary

Writes `docs/reference/world-vocabulary.md` from
`Puck.World.Transpiler.Vocabulary.WorldConstructs.Table`
(`src/Puck.World.Transpiler/Vocabulary/`): every construct of
`puck.world.definition.v1`'s surface—its keyword, the members it carries with
their kinds and defaults, the document member it lowers to, and what the printer
requires before it may print a document node back as that construct. The
parser's embedded-language test, the decompiler's sugar guards, and the language
server's completion and hover read the same table, so the file cannot describe a
construct the code spells differently.

What a document *field* means is [`puck schema`](#puck-schemaworlddef-json-schema)'s
and which fields carry names is [`puck registry`](#puck-registryworld-name-registry)'s;
this table is the surface and its mapping onto them.

```text
puck vocabulary               write docs/reference/world-vocabulary.md
puck vocabulary --check       regenerate in memory and compare against the file on disk;
                              write nothing, exit 1 naming the first differing line
puck vocabulary -h / --help   this text
```

Exit codes: **0** wrote or matched, **1** drift, **2** usage error or missing
repository root.

## `puck scan`—source sweep

Parses every `.cs` file under `<root>` **once** (the shared prune set—`.git`,
`.tmp`, `artifacts`, `bin`, `obj`, `node_modules`, `publish`,
`BenchmarkDotNet.Artifacts`—plus agent worktrees under `.claude/worktrees`
pruned below the root; naming a skipped directory as the root itself still scans
it) and
runs the selected analyzers over that one shared corpus, so a full sweep parses
the tree a single time rather than once per analyzer.

```text
puck scan [<root=src>] [--only comments,comment-smells,locks,clones]
          [--output <dir>] [--grouped] [--max-per-chunk N]
          [--min-tokens N] [--min-statements N] [--no-blocks] [-h]
```

| Analyzer | Emits |
|---|---|
| `comments` | every non-XML inline comment (`//` and `/* */`). |
| `comment-smells` | those comments bucketed by weakness (sync-coupling / debt-marker / banner-divider / commented-out-code / narrative-history / dead-provenance / unclassified), the classification [`puck comment-smells`](#puck-lengths-and-puck-comment-smellsratchet-ledgers) ratchets per file, plus each cross-artifact referent (a shader file name or an `UPPER_SNAKE` define) tagged resolved or dangling. |
| `locks` | synchronization sites, kind-tagged: `lock` statements, lock-primitive declarations, `Monitor.*`/`Interlocked.*` calls, `[MethodImpl(Synchronized)]`. |
| `clones` | structurally identical callable bodies and nested blocks, Type-1/Type-2 fingerprinted; gated by `--min-tokens`/`--min-statements`, and `--no-blocks` drops the block pass. |

### Output modes

One record per finding, as JSONL. Where it goes depends on the flags:

- **stdout** when exactly one analyzer is selected and neither `--output` nor
  `--grouped` is given—the pipe-friendly default for a one-off query.
- **`<output>/<name>.jsonl`** otherwise, defaulting to `<repo>/artifacts/scan`.
  Any multi-analyzer run takes this path, so pass `--output` when you do not want
  the default directory written.
- **`<output>/<name>.grouped.json`** additionally under `--grouped`: the same
  findings chunked per file (or per cluster, for `clones`) at most
  `--max-per-chunk` to a chunk—the work-list a fan-out audit spends one agent per
  chunk on.

The analyzer's own one-line digest, plus its densest-files table, always goes to
**stderr**, so `puck scan … --only comments > records.jsonl` leaves a readable
summary on the terminal. Exit codes: **0** ran, **2** usage error, unknown
analyzer, or missing root.

Output is deterministic: files are enumerated in a fixed order, and every
ordering—records, buckets, chunks, clusters—is fully tie-broken, so two runs
over an unchanged tree produce byte-identical bytes.

The comment-smell referent check resolves against **non-comment** source text
plus the shader sources under `<repo>/src`; resolving against comment text too
would let a define cited in a comment resolve against that very comment, which
makes the check a tautology rather than evidence.

---

## `puck creation`—code-authored sculpts

The offline twin of the in-engine `creation.sculpt(s)` console verbs (see
`Puck.World.Authoring`'s README for the sculpting library itself—
`CreationBuilder`/`StateHoisting`/`SculptPatch`/`ICreationSculpt`):

- `puck creation sculpts`—lists every registered sculpt (`CreationSculptRegistry.All`) by name and description.
- `puck creation sculpt <name> --world <path>`—reads the world file, runs the named sculpt's `SculptPatch` against it, echoes every operation's `(kind, path, verdict)`, then parses and validates the PATCHED document through `WorldDefinitionSerialization`/`WorldDefinitionValidator` before writing it back canonically. A refusal (unknown sculpt, malformed JSON, a patch fault, a validation failure) leaves the file untouched. Exit codes: 0 wrote, 1 the patch faulted or the patched document was refused, 2 a usage error (unknown sculpt, missing file).
- `puck creation stats --world <path> [--prototype <id>]`—reports a creation's shape count against `WorldPlacementPolicy.MaxShapesPerStamp`, counts by primitive and blend op, and which shapes use domain ops, onion, twist, bend, rounding, dilate, lift, chamfer, or a panel, plus palette slot usage and named flare/shear/bumps/erode/cells shapes. It builds unit-scale static and pooled rest geometry through the live stampers and lists the global step scale plus every non-unit field-scope clamp, its instance/instruction range, and how many shapes share it. Animated prototypes use the pooled path. Text-bearing prototypes report clamp inspection unavailable because the offline command has no resolved font atlas; inspect `world.budget` in the render host. Field bounds are not measured GPU-time or march-count multipliers. Defaults to every prototype carrying a creation document; a geometry inspection failure exits 1.

`creation stats` also constructs the whole world's deterministic contact field
through the runtime builder, including solid placements and screens. This runs
even with `--prototype` or when text prevents render-clamp inspection. It reports
`contact: accepted` on success; a contact compilation refusal exits 1 and names
the unsupported operation, including residual nonuniform `Scale`. Non-solid
placements and unplaced prototypes need no contact representation. Acceptance
does not imply render/contact parity: presentation-only facets remain omitted.

`puck creation sculpt` writing to disk goes through the SAME canonical
serializer `world.save` does, so an UNTOUCHED section of the file (one the
named sculpt's patch never references) can still change shape—every
optional field the schema declares gets written out explicitly rather than
omitted, and every derived field (a camera program operation's `opcode`, a
rule effect's default `target`) gets filled in. This is not specific to a
sculpt; it is what routing a document through the typed `WorldDefinition`
model at all does.

The shipped `CreationSculptRegistry` carries no sculpts—`puck creation
sculpts` reports "none registered" until a composition root or a test
registers one.

## `puck schema`—world.def JSON Schema

Generates the JSON Schema for `puck.world.definition.v1` from the live C# model—
`Puck.World.WorldSchema` (`src/Puck.World.Schema/WorldSchema.cs`) walks
`WorldDefinition` over its own source-generated `WorldJsonContext` via
`System.Text.Json`'s `JsonSchemaExporter`, so `$type` unions, enum values, and
`additionalProperties: false` all come from the SAME contract the loader
enforces, never a hand-maintained copy. Descriptions come from
`Puck.World.Schema.xml`, `Puck.State.xml`, `Puck.State.Rules.xml`,
`Puck.State.Topology.xml`, `Puck.State.Generators.xml`,
`Puck.World.Authoring.xml`, and `Puck.SignedDistance.xml`, resolved property `<summary>` first, then the
declaring record's own `<param>` (most members are documented that way—a
positional record's XML doc lives on the record declaration, not the
property), then a type `<summary>` for a node with no containing property
(an array's item schema, a `$type` arm).

`render.extensions[]` takes its `id` vocabulary and per-id `config` schema
from the shipped `puck.shader.manifest.v1` manifests under `src/*/Assets/Shaders`
(`Puck.Shaders.ShaderSetManifest.ConfigJsonSchema`)—one `if`/`then` arm per
id—so an entry's config validates by id in an editor, and adding a shader
set changes the schema (`--check` catches a manifest edit not regenerated).

Every array and dictionary carries `items`/`additionalProperties`, including
a converter-hidden shape the exporter cannot introspect on its own (a
`StateRowJsonConverter<TRow>`-owned row, a fixed-arity vector array, a
document-identifier list); a raw `JsonElement` slot decided by an id named
elsewhere in the document (`render.extensions[].config`, `probes[].config`,
`metadata.custom`) stays open but carries a `$comment` saying so. The root
carries `x-puck: {schemaVersion, generator, commit}` (the silo root carries
its own) and `properties.schema.const` pins the exact tag a well-formed
document's own `schema` field must equal; `--check` masks `x-puck.commit`
before comparing, since the commit a checked-in file was generated at can
never equal the commit that first introduces the file.

The output is SPLIT, not one file: a small root plus one file per top-level
document section (`kits.schema.json`, `screens.schema.json`, …), plus
`common.schema.json` for every subschema referenced from more than one
place—named after the CLR type it came from where that's recoverable. Every
cross-file reference is a plain relative `$ref`
(`"./schema/kits.schema.json"`, `"./common.schema.json#/$defs/WorldChannel"`),
never `$id`-based, so an editor resolves them without extra configuration.

The same run writes the dashboard portal's TypeScript types,
`src/Puck.Dashboard/src/portal/src/document/worldDefinition.generated.ts`.
`WorldSchema.ToTypeScript` emits them from the single-file bundle: one
`export type` for the root (`WorldDefinition`) and one per bundle `$defs`
entry, under its own name, with each `description` as a doc comment. `anyOf`
and `oneOf` become unions, `allOf` an intersection, `enum` and `const` literal
types, and a fixed-length array a tuple. A property is optional unless
`required` names it, and an object is open unless `additionalProperties` is
`false`. Conditions, negations and value bounds have no TypeScript form and are
left out. A keyword the emitter does not map, or a `$ref` to a missing
definition, stops the run with an error that names it, so the types never
quietly widen to `unknown`.

The same run writes the engine's model shape,
`src/Puck.World.Schema/WorldModelShape.generated.cs`: every type reachable from
`WorldDefinition` with its members, `$type` arms and element type, as the
serializer's resolver describes it. The walks that read the model without
serializing it read this table, so a process never describes a type at run
time.

```text
puck schema                          write the checked-in root, sections, common.schema.json,
                                     projection, silo, counters-report, release-profile and
                                     frame-graph schemas, the portal's TypeScript, and the
                                     engine's model shape
puck schema --check                  regenerate in memory and compare EVERY file against
                                     what's on disk; also catches a stale orphan section no
                                     current model produces; exit 1 on any drift
puck schema --bundle [--output path] emit the single-file equivalent with every cross-file $ref
                                     resolved through named $defs (not a checked-in artifact),
                                     to the --output path if given, else stdout
puck schema -h / --help              this text
```

Written to `src/Puck.World/Assets/worlds/puck.world.definition.v1.schema.json` (root)
and `src/Puck.World/Assets/worlds/schema/*.schema.json` (sections + common),
which already flow to `Puck.World`'s build output (`Assets/**` copies
`PreserveNewest`), so the schema ships beside the world documents it
describes. The same run writes the projection schema beside the root
(`puck.world.projection.v1.schema.json`), the silo document's schema
(`src/Puck.World.Silo/Assets/puck.silo.configuration.v1.schema.json`), and the
schema of the report `puck counters` writes
(`tests/Puck.Counters/puck.counters.report.v1.schema.json`), the schema of the
release profile `puck qualify` reads
(`tests/Puck.Qualification/puck.release.profile.v1.schema.json`), and the
frame-graph schema (`src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json`). Running `puck schema` also DELETES any section file the current
model no longer produces, so the checked-in tree never carries an orphan.
A missing documentation XML file from that set still produces a schema, with no
descriptions, and this verb says so on stderr rather than failing. Exit
codes: **0** wrote or matched, **1** `--check` found drift (reported per file
—missing, orphan, or the path plus the first differing line), **2** usage
error or missing repository root.

---

## The `.puck` DSL verbs

Six verbs over `Puck.World.Transpiler`, the `.puck` authoring layer above the
world documents. JSON stays the wire form; `.puck` is the hand-authored source
of the game's shipped worlds, which the game's build compiles into its own output
(`build/WorldAssets.targets`). `.puck`
sources are formatted by [`puck format`](#puck-formatthe-one-formatter), the one
formatter for every source kind.

```text
puck compile <source.puck|document.world.json>... [-o <out.json-or-directory>] [--tree <root> [--written <report> | --check]] [--validate] [--bundle] [--strict] [--watch] [--update-assets]
puck decompile <source.json>... [-o <out.puck>] [--overwrite] [--sql] [--embeddings <file.embeddings.json>]
puck embed <path> [--check] [--provider <fixture|openai-compatible>] [--endpoint <url>] [--omit-dimensions] [--batch-size <n>] [--timeout-seconds <n>]
puck embed probe <path> <text> [--space <name>] [--against <table>] [--top <n>]
puck lint <path> [--strict]
puck lsp
puck migrate <name> <path> [--check]
```

`compile` accepts several source paths and compiles them in the supplied order
within one process. Each source gets its normal adjacent `.world.json` or
`.cartridge.json` output; the first failure stops the batch, leaving earlier
successful outputs in place. `--output` and `--watch` require exactly one
source, except with `--tree <root>`: every source must lie under `<root>`, each
world document is written to the same relative place under the `--output`
directory, and any other `*.world.json`, `*.puckb` or `*.puckbake` under that
directory is removed once all sources compiled, so it holds exactly this run's worlds. No run writes one
document file twice: a source whose output lands on a file an earlier source of
the run wrote, exactly or in another letter case, is refused by name (exit 1),
since a document name is unique ignoring case, in the one wording every door
refuses two files carrying one document name in (`DocumentName.Collision`).
`--written <report>` writes the
files the run left under the output directory to `<report>`, one
output-relative path with forward slashes per line in ordinal order, once the
run succeeds; the run removes the report before it starts, so a failed run
leaves none. The game's build uses the tree form (`build/WorldAssets.targets`)
so the compiler and schema metadata initialize once, and ships exactly the
files the report names: which documents a source emits, and under which names,
is not its file name, so the build never derives them from the sources' names.
`--check` compiles the tree into a scratch directory and compares it with
`--output` instead of writing there. It exits 1 naming each file a fresh run
writes that the output lacks or holds with other bytes, and each document,
compiled world, bake pack or stored package the output holds that the run does
not write. Run over the game's Release catalog, it proves the tree compile is
deterministic across processes; [`puck affected`](#puck-affectedthe-checks-a-change-needs)
chooses it when a shipped world or the code the compile's output depends on
changes.
A world source that names no schema or basis, declares no world, and holds only
`let`, `template` or `module`, `.puck` import and `test` statements at its top
level, such as a module library like `worlds/rulepush/hub.puck` that only
declares `module hub(...)`, emits no document: `compile` writes nothing for it,
prints nothing, and succeeds, so it never stands under the name of a world a
composition beside it declares. A document import names a document (`import "klondike"`), and
resolves to its `.puck` source or its `.world.json` document beside the
importer, so no source depends on another's compiled output.

Beside every world document it writes, `compile` writes that document's
[compiled world](../architecture/worlds.md#compiled-worlds), `<name>.puckb`,
composed where the source sits and drawn for the `boot` instance under the CLI's
machine catalog, and prints one `Compiled world '<path>'` line. A document that
does not parse and draw as a world on its own, such as a module fragment, has no
compiled world: `compile` prints one `No compiled world for '<path>'` line
naming why and still succeeds. A `.world.json` path compiles to its compiled
world alone, beside the document or at `--output` (a `.puckb` path, or a document
path it sits beside); `--watch` and `--update-assets` refuse one. A `--tree`
run resolves every document name under `<root>` before it writes anything, by
the rule the composer resolves a name by: to the `.puck` source that emits it,
whatever that source's stem, and to its `.world.json` document otherwise, each
source's names read from its parse alone (`WorldSourceIndex`). A `.world.json`
source is written under the output directory as it stands, with its compiled
world beside it, unless the `.puck` source of its exact name emits that name,
which wins and leaves it unshipped. A source carries exactly the names it emits,
so one beside a module library of its name ships, and so does one beside a
composition of its name that declares other worlds. Any other two files
carrying one name, ignoring case — a `.world.json` beside a source of another
stem that declares its name, or two sources emitting one name — are refused by
name before anything is written (`DocumentName.Collision`), as the composer
refuses the same pair. The sweep removes every `*.puckb` and `*.puckbake` the run did
not write along with every such `*.world.json`, so the build ships exactly the
documents, compiled worlds and bake pack the run wrote.

A compiled world names the [creation bakes](../architecture/worlds.md#creation-bakes)
it needs, and the bakes themselves ship once in a bake pack, `bakes.puckbake`. A
`--tree` run writes one pack at the root of the output directory, holding
exactly the bakes its compiled worlds name, after every source compiled; any
other run writes one beside each compiled world and keeps the bakes an earlier
compile into that directory left there. Either prints one `Wrote bake pack
'<path>'` line with the pack's outcomes and bytes and the creations the process
baked and the field evaluations it spent, each distinct creation baked once. A
run whose compiled worlds name no bake writes no pack.

A `--tree` run also packages every pipeline its compiled worlds name by source:
each `views.graphs` row's `source` (a row naming an engine `package` has none),
resolved from the document's place in the
tree, is compiled with `dxc` from the search path into one
[shader package](shaders.md#the-builds-package-store) under its key in
`packages/` at the root of the output, and every package file joins the report.
It prints one `Packaged pipeline` line for each package it writes or keeps. A
row naming a package directory ships that directory as it stands. A source that
does not exist, does not fit a package or does not compile fails the run (exit
1, or 2 when a shader tool is missing), and the sweep removes every package in
`packages/` that no row of the run names.

A source containing `world name = module(...)` declarations emits one
`<name>.world.json` per declaration; one of them may be written
`entry world name = module(...)`, the world `Puck.World --world` starts in when
it boots the source. For that source, `--output` names a
directory rather than a JSON file. A world is declared at the top level of its
source under a name written as a name or a plain string, never inside a loop or
computed, so every reader finds the worlds a source emits from its parse alone.
World names must be portable filename stems and cannot differ only by case. All declarations must compile before any output
is published. Individual replacements are atomic; an I/O failure while
publishing several files can leave earlier replacements in place.

`asset "path"` verifies bytes against the source's sibling `.assets.json` lock.
`--update-assets` explicitly refreshes the complete pin set, implies semantic
validation, and cannot be combined with `--watch`. A source with asset references
must emit beside its source so relative asset paths retain their meaning.
See [file assets and world composition](../authoring/README.md#pinning-file-assets)
for examples and the distinction between compilation pins and runtime assets.

`--validate` composes the document's `basis` and `imports` graph **before**
validating, rooted at the source file's own directory—the same order
`PuckWorldLoader` uses at boot. Validating the uncomposed root would report every
field the basis supplies as missing, so a document naming a basis only validates
correctly this way.

Cartridge sources select `puck.cartridge.v1` and use `CartridgeDocuments.Validate` through the same adapter in
`compile --validate`, `lint`, and LSP diagnostics. The language server supplies cartridge completions, including
for unsaved buffers. Forge paths map back to authored properties and rows.

World engine-schema validation runs only on a ROOT—a document declaring
`schema: "puck.world.definition.v1"`, a `basis`, or both
(`WorldSemanticValidator.IsRootDocument`). A World fragment is a MODULE some
other, unknown root supplies fields for, and validating it as a world would
report those fields as missing; `--validate` on a module is therefore a no-op,
and `puck lint` applies the identical test, so the two verbs never contradict
each other on the same file. `puck lint`'s separate symbol-resolution pass
(`Puck.World.Transpiler.Validation.PuckLinter.LintReferences`) composes any
document naming a `basis` or `imports` to resolve names those supply, but
reports an unresolved name only for a root—see
[the transpiler README](../../src/Puck.World.Transpiler/README.md#reference-resolution-lint).

Every verb that validates or composes a world document—`compile --validate`,
`lint`, and `world prepare`—registers the shipped screen-machine engines
(`gaming-brick`, `advanced-gaming-brick`, `tune-instrument`) and installs
`Puck.World.Schema`'s vocabulary hooks through the CLI's one installer,
`Puck.Cli.CliWorldVocabulary.EnsureInstalled`—which composes the same
[extensions](extensions.md) (`HumbleGamingBrickExtension`,
`AdvancedGamingBrickExtension`) into a `WorldMachineCatalog` the way the game
does at boot, so a document naming a shipped `screens[]` engine validates
identically here and there.

`decompile` writes beside its source when `-o` is omitted. A basis or import path
inside the document resolves relative to the `.puck` file, so a round trip must
write the `.puck` next to the JSON it came from. Pass `--embeddings <file.embeddings.json>`
to decompile vector cell arrays back into their original authored text literals when present
in the lock.

A name the compiler generated ([generated names](dsl.md#generated-names)) prints back
as the construct that generated it: a ground's prototype and placement as its `ground`
block, a rule scope's members as `rules` scopes, and a generated test world's verdict
rows, rules and schedule as its `test` block. Given several `<world>.world.json`
documents, `decompile` reads them as the worlds of one composition and writes the one
source that emits them, with `--output` naming it: each world becomes a module and a
`world` declaration, and each `border` and `door` is read back from the rows it
generated in both worlds it joins. A document declaring a generated name that nothing
prints back is refused by that name, so the verb never writes a source the compiler
refuses.

`embed` resolves all authored `embed(...)` text expressions and vector table literals in a
`.puck` file or directory into committed `.embeddings.json` lock files. Pass `--check` in CI to verify
that all locked vectors are present and fresh without network calls. Use `puck embed probe <path> <text>`
to query a locked world's vector tables and rank matching keys by cosine similarity offline.

`migrate` applies one registered rewrite—a `PuckMigration`, written against
`Puck.Transpiler.Rewriting.PuckSyntaxRewriter`—to every `.puck` source under a
directory, or to one named file, and prints each result through the same printer
`puck format` uses. Every comment comes back as its author wrote it, and the run
enforces that: the comments of each migrated source, in reading order, must
equal the comments it started with, unless the migration declares
`ReshapesComments`. Blank-line runs and the compile-time layer rest on the same
printer `puck format` is gated on, and are not separately checked by the run. The run is
all-or-nothing: every source is parsed,
rewritten, and printed before anything is written, and each source the run would
change is compiled before and after, its two documents compared with the members
the migration declares excluded. A source the migration leaves alone is neither
compiled nor written. A difference outside those members, an undeclared comment
change, a source that does not parse, one whose migrated text does not parse
back, one that does not compile, or one that would change but cannot be opened
for writing refuses the whole run and writes nothing. `--check` reports what
would change and writes nothing. An
unknown name lists the registered migrations; the registry is empty between
reshapes, because a migration lands with the reshape it serves and is deleted
once it has run.

Each source is written by staging its rewrite in a sibling `.migrate-tmp` file
and moving that over the destination, so an interrupted run leaves every source
either as it was or fully migrated—never truncated, which matters because a
source that does not parse would refuse every later run. The one thing that is
not atomic is the *set* of files: if a move fails after earlier moves have
succeeded, those destinations stay migrated and the refusal names each of them.
The common causes of that failure—a read-only file, a lock, a permission—are
refused at plan time instead, before anything is staged.

Exit codes: **0** success, **1** diagnostics at or above the failing severity
(`--strict` promotes warnings; `migrate --check` uses it for work outstanding),
**2** usage error, unreadable input, or a refused `migrate` run.

---

## `puck format`—the one formatter

`puck format` formats every source kind Puck owns to its one canonical form: a
`.puck` source through the DSL printer, and a C# source through the rewriters for
the conventions `.editorconfig` cannot express, each file parsed and written
once.

```text
puck format [<root=.>] [--check] [--configuration <name=Release>]
            [--only attr-order,member-groups,member-spacing,member-order,null-pattern,
                    string-merge,paren-clarity,logical-lines,arg-lines,ternary-lines,
                    init-order,trailing-comma,decl-spacing,literal-var,named-args,puck]
puck format --file-list <json-array-of-paths> [--check] [--configuration <name>] [--only <passes>]
```

`<root>` is a directory or a single source, resolved against the working
directory. The walk takes every `.cs` and `.puck` file under it and skips
the shared prune set (`.git`, `.tmp`, `artifacts`, `BenchmarkDotNet.Artifacts`,
`bin`, `node_modules`, `obj`, `publish`), agent worktrees under
`.claude/worktrees`, and the quarantined `experimental` trees. Generated C# (`*.g.cs`, `*.generated.cs`) is
never formatted.

`--file-list` takes the path of a JSON file holding an array of paths, and both
the file and its entries resolve against the working directory. It replaces
`<root>`: naming both is a usage error. Every phase, SDK whitespace formatting
included, is limited to the listed paths, and an empty array selects nothing.
A missing path, a path that is neither `.cs` nor `.puck`, parent traversal, and
a symbolic link are errors. The full owning project still supplies semantic
context. A root is formatted as the selection of every source under it, so both
forms route files the same way.

`--check` is the one dry mode. It writes nothing and exits 1 on any drift, on a
C# rewrite that would introduce syntax errors, and on a pass that is not a fixed
point (running the pipeline twice differs from running it once). Phase 0 runs as
`--verify-no-changes`, so nothing on disk moves. Without `--check` the run
writes, so run `--check` first on a tree nobody has swept.

`--configuration` names the build the two semantic C# passes resolve symbols
against. Passing one that has not been built skips every project rather than
binding against another configuration's output.

### `.puck` sources

The `puck` pass parses each `.puck` source and prints it back through
`PuckPrinter`, the printer the language server's formatting request also uses,
so the editor and the command line write identical text. A source has one
layout: `PuckPrinter.IndentWidth` (2) spaces a level, each block body on its own
lines, and each array or object keeping the line breaks its author gave it.
There is no indentation or tab option, and the language server ignores an
editor's `tabSize` and `insertSpaces`. Every comment, blank-line run, numeric
base, raw fence, and bare-or-quoted name survives; see
[the printer's description](../../src/Puck.World.Transpiler/README.md#editor-tooling) for what it keeps. A source that
does not parse is refused by name, left alone, and fails the run with exit 2 in
every mode. `FormatProjectionLawTests` holds every tracked `.puck` source to
what this pass prints and proves formatting never moves the document a source
compiles to.

`--only puck` formats the `.puck` sources alone; C# sources are formatted, phase
0 included, only when the selection names a C# pass.

### C# sources

A file-based app (`#!` or `#:` directives) uses a disposable, built SDK project;
its original directives survive and its operational body is never run. A source
outside every project directory, such as `tests/Shared/*.cs`, needs no project
for whitespace and the syntactic passes; the semantic passes format it in the
compilation of a project that compiles it, looked for among the run's projects
and the projects one directory below its nearest ancestor that has any. One no
project compiles also uses a disposable project; an MSBuild inline task (a
`RoslynCodeTaskFactory` source under `build/`) compiles there against the SDK's
own MSBuild assemblies, with no implicit usings or repository analyzers, as the
task factory compiles it. A disposable project that does not build skips its one
source, named, and the rest of the run still reports. A semantic phase that
cannot analyze an owning project fails rather than reporting unchecked source as
clean.

The semantic passes evaluate every project closure they need in one MSBuild
process, so a reference graph the projects share is evaluated once, and they
parse and compile each project once for both `null-pattern` and `named-args`.
Each compilation carries the project's own assembly name, so a member another
project exposes through `InternalsVisibleTo` binds as the build binds it.

**Phase 0 always runs first** for C# sources, in every mode:
`dotnet format whitespace --folder` over exactly the selected files,
establishing the `.editorconfig` baseline the custom passes layer onto. Folder
mode loads and restores no project, so the selection alone decides what it
reaches, and a compile item a project links in from outside the root, such as
`build/VerifiedCodeAttribute.cs`, is never touched. It defines no preprocessor
symbols, so code under `#if SYMBOL` is left as written. Without `--check` it
**rewrites any whitespace drift in the root**, which on an unswept root is the
whitespace sweep for that root; the tree-wide sweep is deliberately its own,
separately-landed change.

The files travel as `--include` arguments relative to the root, in as few
invocations as a command line holds; each invocation carries at most
24,576 characters of paths, well inside Windows' 32,767-character limit. A
response file cannot lift that limit, because the SDK host expands it onto the
formatter process's own command line. A path that matches no file matches
nothing without saying so. Every invocation runs, and the phase reports the
worst of them.

| Pass | Rewrite | In the bare-`format` set |
|---|---|---|
| `attr-order` | one attribute per list/line, alphabetized. | yes |
| `member-spacing` | blank-line grouping between type members; a field's kind is its storage class (const / static readonly / static / readonly / mutable), and initializer-coupled fields/properties share one kind, so each `member-groups` group is one blank-line-delimited unit. | yes |
| `member-order` | a const block or uncoupled property block (same kind + scope) sorted by name; non-const fields and initializer-coupled properties are never reordered, and layout-sensitive/attributed, partial, or directive-bearing types stay as written. | yes |
| `null-pattern` | compiler-resolved `== null` / `!= null` → `is null` / `is not null`; pointer, dynamic/error-bound, and user-defined equality comparisons stay unchanged. | yes |
| `string-merge` | `+` of two string/interpolated literals → one literal (`"a" + $"b{x}"` → `(string)$"ab{x}"`), so message text is searchable contiguously. The explicit cast preserves the concatenation's string type and overload binding. A seam carrying a comment, a verbatim/raw interpolated operand, and any non-literal operand are left alone. | yes |
| `paren-clarity` | explicit precedence parens (`((0 == a) \|\| (0 == b))`), casts included (`((uint)sets.Length)`—bare only under checked/unchecked and as a ternary branch). | yes |
| `init-order` | object-initializer members alphabetized when every right-hand value is syntactically reorder-safe. Setter invocation order still changes; use only where those setters are order-independent (auto-properties and fields satisfy that boundary). | yes |
| `trailing-comma` | trailing comma on a multi-line initializer's last element; a `{ key, value }` element initializer, which admits none, is left as written. | yes |
| `decl-spacing` | one blank line between a local-declaration run and the next statement. | yes |
| `literal-var` | `uint x = 0;` → `var x = 0U;` for suffix-bearing primitives. | yes |
| `named-args` | call arguments named and alphabetized (semantic). | yes |
| `puck` | every `.puck` source parsed and printed back (see above). | yes |
| `member-groups` | fields, properties, and methods each gathered at their first occurrence. Fields use kind order (const → static readonly → static → instance readonly → instance mutable), then accessibility scope, then name; properties and methods use accessibility scope then name, with overloads stable in source order. Struct/record-struct instance-field declarations stay fixed, while constants, static fields, properties, and methods still group; on an unattributed struct this opt-in may change generated auto-property backing-field order. Complete declarations move, so comments and attributes travel with them. Initializer-coupled movable members stay together in source order. A type is left as written when moving could change behavior: attributed or partial types, members carrying `#directives`, or a coupled member that would cross a fixed field or field-like event initializer. | `--only` |
| `logical-lines` | multi-operand `&&`/`\|\|` one operand per line, operator trailing. | `--only` |
| `arg-lines` | a call with >1 argument: one argument per line, hanging close paren. | `--only` |
| `ternary-lines` | `c ? t : f` across three lines, operators leading; a statement-ending paren-wrapped ternary's trailing close parens hang at the root's indent. | `--only` |

The three vertical line-wrappers stay opt-in because their one-per-line layout
is a deliberate choice, not a baseline; `member-groups` stays opt-in because
regrouping a type's declarations changes source and metadata order and is a
reorganization to ask for, not a convention to drift into. Run it with the
default set (or run a bare `format` after it) so `member-spacing` renormalizes
the blank lines the moved declarations carried along.
Required braces are `.editorconfig`'s job (`IDE0011`, `csharp_prefer_braces`),
applied by `dotnet format style`.

Exit code is the worst of the phases: **0** clean or written, **1** drift under
`--check` or a skipped rewrite, **2** a usage error, a missing root, an
unreadable or unparseable source, or a tool failure.

### Safety

- **The write guard is unconditional.** A syntactically invalid input file is
  declined, and output from valid input must remain syntactically valid. The
  file is reported as corrupt and the run fails loudly—never written, not
  even in a plain rewrite run. This is a syntax guard, not a substitute for the
  compiler and tests after semantic normalizers.
- **Custom rewrites preserve source newline trivia.** Ordinary whitespace policy
  belongs to phase 0. The disk writer does not normalize the complete file text,
  because doing so would change newline characters inside verbatim or raw string
  literals. A break a pass SYNTHESIZES is a bare line feed, taken from the one
  declaration `RewriteShaping.EndOfLine`, matching what `.editorconfig` and
  `.gitattributes` already pin for the whole tree. Phase 0 runs first in every
  invocation, so a rewriter never inserts into a file it has not already
  normalized.
- **Annotated code is left alone.** The four reordering passes (`attr-order`,
  `member-order`, `init-order`, `named-args`) reassign trivia by *slot*, so a
  reorder would leave a comment—or an `#if`—describing whichever element
  moved under it; the three line-wrappers (`logical-lines`, `arg-lines`,
  `ternary-lines`) reissue their layout slots outright, so a comment in one would
  be deleted. The syntax-only write guard sees neither: both rewrites still
  parse. All seven therefore decline a construct carrying a comment or a
  preprocessor directive in **any slot they touch**, separators, operators and
  delimiters included—a comment written after a comma belongs to the comma, not
  to either neighbour, and a slot-preserving reorder would strand it.
- **`member-groups` moves complete declarations.** A field, property, or method
  keeps its attributes and leading/trailing comments when it moves. A type with
  any preprocessor directive is left as written because a directive's guarded
  region cannot safely be inferred from syntax trivia alone.
- **Declaration and attribute order is observable metadata.** `attr-order`,
  `member-order`, and `member-groups` deliberately establish source order. Code
  that consumes reflection order, default JSON property order, sequential struct
  layout, or byte-exact metadata must use explicit ordering/layout contracts or
  leave the relevant reorderer off. `member-groups` additionally calls out its
  auto-property backing-field boundary in the pass table above.
- **Semantic rewrites read the compiler, not the syntax.** `null-pattern`
  declines pointer, dynamic/error-bound, and overloaded-equality comparisons, and
  unresolved comparisons stay unchanged; `named-args` reads parameter names off
  the resolved method. Both bind through the project closure described below.
- **A rewrite never moves a call to another overload.** Naming arguments makes
  overloads applicable that the written positions excluded, and alphabetizing
  them can do the same. `named-args` therefore re-binds the named, reordered
  call before emitting it and drops the rewrite unless it still resolves to the
  same method. A call whose arguments or receiver do not resolve reports
  candidates rather than a chosen method, and candidates are never named from.
- **Evaluation-order rewriters are conservative, not omniscient.** `named-args`
  uses the semantic model and declines calls whose moved arguments contain calls,
  mutations, indexers, construction, awaits, or property getters. `init-order`
  has no semantic model: it applies the syntactic value guard but cannot inspect
  setter bodies, so initializer setters must be order-independent.
- **The semantic passes need the project built, in the configuration they are
  asked for.** They bind against the reference set the build itself hands the
  compiler, which MSBuild reports per project as
  `@(ReferencePathWithRefAssemblies)`: the framework reference pack, the
  restore's package assemblies, and each project reference's own reference
  assembly under its `obj/<configuration>/`. The same evaluation names the
  project's own output, whose presence is the evidence that the configuration
  was built—`obj/` alone is not, because a restore or design-time evaluation
  populates it.
  It also reports the project's `Compile` items, so a source linked in from
  outside the project directory—`build/RepositoryPaths.cs`,
  `build/VerifiedCodeAttribute.cs`—joins the compilation and the calls that
  touch its types bind. Alongside the closure the passes read the generated
  global-usings file and any emitted generator output from that configuration's
  `obj/`.

  A project is SKIPPED entire, in every mode, and the run exits 1, when the
  requested configuration has no output, when an assembly the closure names is
  not on disk, or when MSBuild cannot evaluate the project. Build it in that
  configuration and run again. A file whose directory chain holds no `.csproj`
  is likewise reported as skipped rather than counted as clean.

  One MSBuild evaluation per project per run supplies both passes, and it builds
  nothing (`BuildProjectReferences=false`). One gap stays open and is counted
  rather than guessed around: an assembly MSBuild itself cannot resolve is
  dropped from the closure rather than reported as missing. Its types resolve to
  nothing, the calls that touch them stay positional, and the run reports how
  many.

---

## `puck pull-request`—automatic PR formatting

The two halves of the formatting bot, run by CI rather than by hand.

```text
puck pull-request format <base-sha> <head-sha> --output <empty-output-directory>
puck pull-request submit-format --run-id <workflow-run-id>
```

`pull-request format` requires a clean tracked checkout at `<head-sha>`. It
selects the C# and `.puck` sources the pull request adds, modifies, or renames,
excluding generated and quarantined code, formats them with the default passes
exactly as `puck format` would, formats again under `--check` to prove
convergence, and writes `files.json`, `format.json`, and `format.patch` to the
output directory. It always binds the semantic passes against `Release`, the
configuration its workflow builds. It never commits or pushes. The
[CI formatting workflow](../../docs/development/ci.md#automatic-pr-formatting)
compiles that result before its separate trusted `pull-request submit-format`
step can append a bot commit; that step reads the artifact as data, takes the
formatting run from `--run-id`, and takes GitHub's own values and the workflow's
token from the environment.

---

## `puck references`—semantic symbol queries

Loads the project graph and asks the compiler what each name means, so the
answer survives extension methods, `using` aliases, overload resolution, generic
instantiation and name collisions—the five places a text search is wrong.

```text
puck references <name>   references to a source symbol, solution-wide
  --declarations      declarations only, no reference search
  --implementers      implementations of an interface or interface member
  --overrides         overrides of a virtual/abstract member
  --derived           derived types
  --containing <frag> keep declarations whose display string contains frag
  --contains          treat <name> as a substring, not an exact simple name
  --ignore-case       case-insensitive name match
  --kind <k,k>        type, member, namespace (default: type,member)
  --solution <path>   default: the nearest .slnx walking up from the cwd
  --project <path>    load one project instead
  --configuration <c> build configuration (default Release)
  --metadata          also match declarations from referenced assemblies
  --no-doc            drop locations inside documentation trivia
  --strict            keep only locations whose group definition IS the queried symbol
  --allow-partial     report anyway after a workspace load failure
  --json / --quiet / -h
```

Records only on stdout, `path:line:col` first so a line parses like a `search`
hit:

```text
src/Puck.Abstractions/Memory/AllocatorExtensions.cs:14:24 decl Method Puck…AllocatorExtensions.Alloc(Puck…IAllocator, nint)
src/Puck.Vulkan/Apis/VulkanNativeCommandBufferRecordingApi.cs:347:35 ref Method Puck…AllocatorExtensions.Alloc(Puck…IAllocator, nint)
```

Records are grouped by resolved definition (display string, then documentation
comment id) and sorted by position within a group, so two runs over an unchanged
tree are byte-identical. Exit codes: **0** a declaration matched, **1** none did,
**2** usage error or workspace load failure.

The declaration search runs at the widest symbol filter and applies `--kind`
afterwards, and it matches an exact name with the same walk `--contains` uses,
so an exact query answers a subset of the substring one and a narrower `--kind`
only removes records. Each project's entry point is asked for directly, because
the declaration index that backs the search is built from what the files spell
and does not carry the `Program` type a top-level-statements file gets.

Four behaviors decide whether a result means what it looks like, and all four
are documented at length in the `symbol-analysis` skill:

- **The symbol on a `ref` line is the resolved definition**, not the query.
  `new T(…)` reports under `T`'s constructor and an interface-dispatched call
  reports under the interface, so `--strict` on a type hides every construction
  site.
- **`<see cref="…"/>` targets are ordinary references.** Pass `--no-doc` for
  dead-code work; a symbol whose only inbound references are doc crefs is not
  pinned.
- **Only what the project system compiles is visible.** Files removed from
  compilation (`<Compile Remove="…" />`) and files in no project
  cannot be seen; `puck search` and `puck declarations` see them.
- **A workspace load failure is fatal.** A partly loaded solution answers "no
  references" indistinguishably from a true zero, so any `Failure` diagnostic
  prints and exits 2 unless `--allow-partial` is passed. The commonest cause is
  an unrestored tree (a fresh worktree): the design-time build resolves an
  incomplete reference closure and the architecture gate's lane profiles trip
  on the missing edges. The refusal counts the projects carrying no
  `obj/project.assets.json`, and the remedy there is `dotnet restore` at the
  tree's root—never `--allow-partial`.

An analyzer or source generator whose file does not exist—a project-built
analyzer not yet built in the loaded configuration—contributes nothing to any
compilation. The load reports it as unresolved, and the verb drops it and names
it once on stderr rather than failing the search; build it in that
configuration to include what it generates.

Loading is not a pure read: it runs a design-time build, which writes generated
files into each project's `obj/<Configuration>/`.

---

## `puck declarations`—declaration inventory

The syntax tier: parse each file and report what it declares. No build, no
restore, no project system—so it covers files no project compiles.

```text
puck declarations [path ...]   declaration inventory, parse-only (default path: cwd)
  --glob <glob> / --not <glob>   include/exclude globs, the same matcher search uses
  --kind <k,k>       class, struct, record, interface, enum, delegate,
                     method, property, field, event, ctor
  --name <frag>      declared simple name contains frag
  --base <frag>      base list contains frag (types only)
  --attribute <frag> an attribute name contains frag
  --members          list members inside each type (implied by a member --kind)
  --doc              also emit XML-doc cref targets, filtered by --name alone
  --json / --quiet / -h
```

Output is `path:line:col decl <kind> <qualified name>[ : <base list>]`, sorted by
path then position, with a `cref` relation under `--doc`. One record is always
one line: base lists, parameter lists and crefs written across source lines are
rendered from their tokens alone, so comments between them are dropped, two
tokens the source separated are separated by one space, and the `///` opening a
continued line of a documentation comment is a continuation rather than a
separator—a cref split across two lines still reads as one dotted path.
`--name` and `--base` filter that same rendered form. Both record forms report
the kind `record`, and an extension block, which names nothing, is reported as
its members' enclosing static class rather than as a declaration of its own.
Same exit codes as `references`, and a path that names nothing is a usage error
rather than a silent empty answer. `--base` is the cheap implementers query when
a build is unwanted; it matches base-list text, so it cannot see an
implementation inherited through a base class.

`declarations` shares its walk, glob matcher and skip list with `search`—both
refuse `artifacts` and agent worktrees under `.claude/worktrees`, so a paired
sweep covers one tree—and its parse with `scan`.

---

## `puck lengths` and `puck comment-smells`—ratchet ledgers

A ratchet ledger holds one count per source file and lets that count only fall. It declares a ceiling that no
unrecorded file may exceed, and it records each file already over the ceiling at the count it was recorded at. The
repository keeps two ledgers of this one shape (`RatchetLedger` in `Puck.Analyzers`), each read in every
compilation by an analyzer wired like `VerifiedCode.json`:

| Ledger | Count | Ceiling | Analyzer and rules | Verb |
|---|---|---|---|---|
| `FileLengths.json` | lines: line breaks plus one | 2000 | `FileLengthAnalyzer`, LEN001–LEN004 | `puck lengths` |
| `CommentSmells.json` | inline comments in a named smell bucket | 0 | `CommentSmellAnalyzer`, SMELL001–SMELL004 | `puck comment-smells` |

The four rules are the same for both ledgers:

- an unrecorded file over the ceiling fails the build (LEN001, SMELL001);
- a recorded file whose count rises past its recorded count fails (LEN002, SMELL002);
- a recorded file that falls to the ceiling or below fails until its entry is removed (LEN003, SMELL003);
- a missing or off-schema ledger fails (LEN004, SMELL004).

A new file therefore starts at or under the ceiling, and a recorded file only gets shorter, or loses smells.
Generated trees (`*.g.cs`, auto-generated headers) are outside both rules.

A comment's smell bucket comes from the classifier `puck scan --only comment-smells` reports with, so that scan
names each comment a count includes. The buckets are sync-coupling, debt-marker, banner-divider,
commented-out-code, narrative-history and dead-provenance. Unclassified comments and XML documentation are not
counted.

An analyzer sees one compilation at a time, so an entry whose file was deleted or moved never reaches it. Each
verb walks the tracked `src/`, `tests/`, and `build/` trees instead, and measures every file with its analyzer's
own count.

```text
puck lengths                  rewrite FileLengths.json from the tree: remove stale entries, lower fallen ones;
                              refuses (exit 2, naming the file) to raise a recorded count or record a new file
puck lengths --check          write nothing; report stale, risen, and unrecorded-over-ceiling files; exit 1 on any
puck lengths --ceiling <n>    lower the ceiling to n and record every file over it at its current count
puck comment-smells [--check | --ceiling <n>]
                              the same three forms over CommentSmells.json
```

Splitting a recorded file, or rewriting its smelly comments, is the expected way to change a ledger: shrink the
file, run the verb, and the entry lowers or disappears. A ceiling only falls. `--ceiling` refuses a raise, and it
creates a missing ledger.

## `puck baselines`—test baselines

A committed test baseline is recorded by this verb and only compared by its test. Every run of an owning test
writes the fresh copy of each record it compares against to `records/<artifact>` beside its assembly
(`TestRecords` in `tests/Shared`), never to the checkout. The verb builds the test project in Release, clears that
directory, runs the owning tests, and promotes the records over the committed files; `--check` writes nothing and
exits 1 naming each committed file that differs. A failing compare does not stop a recording, since a stale
baseline is what a recording replaces; a run that does not write its records, or a build that fails, exits 2.

| Artifact | Committed files | Owning tests |
|---|---|---|
| `browser-parity` | `tests/Puck.World.Browser.Tests/Fixtures/browser-parity/expected.json` | `BrowserParityRecordingTests` |
| `corpus-inventory` | `tests/Puck.State.Rebuild.Corpus/inventory.md` | `CorpusInventoryTests` |
| `maths-ledger` | `coverage-manifest.json`, `leg-ledger.md`, `frontier.json`, `RESULTS.md` in `tests/Puck.Maths.Tests` | the Default tier (Smoke and Default) |
| `state` | `<world>.state.json` and `<world>.cost.json` in `tests/Puck.World.Tests/ShippedWorldStateBaselines` | `ShippedWorldStateBaselineTests` |

```text
puck baselines <artifact>           build, run, and promote the fresh records over the committed files
puck baselines <artifact> --check   build, run, and exit 1 on any committed file that differs
```

`state` runs its tests twice, in two processes, and refuses when the two runs wrote different records, so a
nondeterministic world fails the recording. `maths-ledger --check` compares only the two artifacts the law
declarations generate, `coverage-manifest.json` and `leg-ledger.md`; `frontier.json` and `RESULTS.md` are run
records that move on every green run.

## `puck packages`—published NuGet package report

Enumerates every csproj under `src/` declaring `<IsPackable>true</IsPackable>`
(see `build/Packaging.targets`) and reads the same fields `dotnet pack` reads:
`<PackageId>`, `<Description>`, `<PackageTags>`. Projects without an explicit
packing opt-in are omitted.

```text
puck packages                list every packable project: id, description, tags
puck packages --check <path> compare <path>'s GENERATED package section against the
                              current list; write nothing, exit 1 on disagreement
puck packages --write <path> regenerate the GENERATED package section in <path>
puck packages -h / --help    this text
```

The GENERATED section a page carries is delimited by a comment pair:

```html
<!-- GENERATED: puck packages -->
...
<!-- /GENERATED -->
```

`docs/site/index.html` carries the one checked-in instance, its `<p
class="libs">` paragraph. `--write` replaces everything between and including
the pair; the rest of the file is untouched. Exit codes: **0**
listed/wrote/matched, **1** `--check` found drift, **2** usage error or
missing repository root.

---

## `puck wasm-stdlib`—WASM standard library sources

Regenerates every GENERATED Rust source registered in
`Puck.Scripting.WasmStdlibSources.All`—the maintained set of generated sources
that make up the WASM standard library, not a single one-off port. Today that
registry holds three files under `wasm/puck-stdlib/src`. Two give the WASM addon
guest a self-contained, bit-exact copy of `FixedQ4816`'s six algorithm-pinned
transcendentals (`atan2`, `sin`/`cos`, `exp2`, `log2`, `pow`): `fixed_generated.rs`
(the ported functions plus their interval tables and polynomial coefficients)
and `fixed_vectors.rs` (known-answer vectors, computed by calling the real
`FixedQ4816` at generation time). The third, `abi_generated.rs`, mirrors the
addon ABI's names and values from the live host types.

```text
puck wasm-stdlib   regenerate every registered generated Rust source
  -h / --help   this text
```

This verb is a thin wrapper: it writes whatever
`Puck.Scripting.WasmStdlibSources.All` lists, never generation logic of its own
—every table, coefficient and vector is read from the live `FixedQ4816` type by
`Puck.Maths.FixedQ4816RustPort`, one of the registry's contributors. **Adding a
future artifact is a one-line addition to that registry**—this verb never
changes. Every `Emit` delegate is **byte-idempotent**: an unchanged host must
produce byte-identical output on every run, which is what makes running this
verb twice a drift check in its own right. **Nothing gates that**: a drifted
commit is caught only by running this verb and reading the diff. Never hand-edit a generated file; regenerate it
with this verb and commit the result.

Takes no arguments and no `<root>`: each registered path (e.g. `wasm/puck-stdlib/src`) names a repository convention rather than
something a caller supplies, so it is anchored at the repository root instead
of the working directory. Exit codes: **0** wrote every file, **2** usage error,
repository root not found, or a destination directory missing.

---

## `puck worktree-base`—worktree base guard

A git worktree an agent is handed can sit at a stale base. `puck worktree-base
<sha-or-ref> [--path <worktree>]` resolves HEAD and `<sha-or-ref>^{commit}` in
the target worktree (default `--path`: the current directory) and shells out to
`git` to reconcile them:

- HEAD already at the base—prints "at base", exits 0.
- Clean tree, wrong base—`git reset --hard <base>`, prints old → new, exits 0.
- Dirty tree, wrong base—prints what is dirty and refuses, exits 2, resets
  nothing.
- Git failure, not a git tree, or an unresolvable ref—exits 2.

"Dirty" is a tracked modification (`git status --porcelain
--untracked-files=no` nonempty); untracked files never block a reset. Always
prints the worktree's toplevel path it acted on, relative to the working
directory. Shells out to `git` rather than adding a git library dependency.

## `puck branding`—maintained assets

`puck branding` reads `branding/manifest.json`, verifies every canonical SHA-256
hash and required source reference, then synchronizes every non-deferred copy.
The command preflights every source, destination, duplicate output, and wiring
entry before it writes, and refuses rooted, escaping, or reparse-point paths.
A copy the manifest marks `deferred` still has its destination path checked
but is never written; no copy in the manifest carries that mark.

```text
puck branding             synchronize canonical assets into their consumers
puck branding --check     check hashes and wiring without writing
```

Exit codes are **0** for a successful synchronization or matching check, **1**
for a manifest, hash, copy, or wiring problem, and **2** when no repository root
can be found. The authoritative asset map and variant rationale live in
[`branding/README.md`](../../branding/README.md).

## Documentation

- [Engine manual](../README.md)
- [Development](../development/README.md)
- [API reference](../api/index.md)
