# Puck.Cli (`puck`)

The consolidated Puck **developer CLI**. One console app, one assembly (`puck`),
with hand-rolled first-positional verb dispatch:

| Verb | What it is |
|---|---|
| [`puck canary`](#puck-canary--real-world-behavioral-proofs) | bounded positive-and-discriminating proofs run against one exact Release build of the real `Puck.World`. |
| [`puck search`](#puck-search--content-search) | ripgrep-shaped content search over a linear-time symbolic-derivatives regex engine ([RE#](../../ACKNOWLEDGMENTS.md)). |
| [`puck bench`](#puck-bench--the-puckmaths-microscope) | the on-demand `Puck.Maths` micro-benchmark microscope, built on [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet). |
| [`puck scan`](#puck-scan--source-sweep) | source sweep over the parsed tree: comments, comment smells, synchronization sites, clones. |
| [`puck schema`](#puck-schema--worlddef-json-schema) | the generated JSON Schema for `puck.world.def.v1`, checked and regenerated. |
| [`puck format`](#puck-format--source-rewriters) | source rewriters for the conventions `.editorconfig` cannot express. |
| [`puck references`](#puck-references--semantic-symbol-queries) | semantic symbol queries: references, implementers, overrides, derived types. |
| [`puck declarations`](#puck-declarations--declaration-inventory) | declaration inventory read off the parsed syntax, with no build. |
| [`puck wasm-stdlib`](#puck-wasm-stdlib--wasm-standard-library-sources) | regenerates every generated Rust source of the WASM standard library — currently `FixedQ4816`'s Rust port and known-answer vectors. |

Unlike its retired `tools/` predecessors, this project is a **first-class member
of `Puck.slnx`** and joins the full root build regime (warnings-as-errors,
analyzers, code-metric ceilings, doc generation, committed `packages.lock.json`).

The first argument selects the verb; every remaining argument forwards to the
verb implementation unchanged — same flags, same output, same exit codes. `puck`
with no verb (or an unknown one) prints usage and exits 2. Output is UTF-8 on
both streams regardless of the host console's code page, so a non-ASCII source
line survives being captured to a file.

**Path resolution is uniform.** Every verb resolves a relative path against the
**working directory**; an absolute path is used as given. (`scan` anchors its
`artifacts/scan` default and its shader-referent tree at the repository root —
found by walking up for `Puck.slnx`, and only looked up when one of those two
defaults is actually live — because those name repo conventions, not the
argument. `wasm-stdlib` anchors the same way: it takes no path argument at all,
because every registered artifact's path (e.g. `wasm/puck-stdlib/src`) is a
repo convention, not something a caller supplies.) Reporting anchors are the
one asymmetry: `scan` records name files relative to the scan root, while the
other verbs print working-directory-relative paths.

## Publishing

```sh
dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish
```

produces `src/Puck.Cli/publish/puck.exe` — a framework-dependent .NET
executable. A trivial invocation costs ~0.18 s wall (measured quiet,
2026-07-24), cheap enough to call per query though not per file.
Do not attempt AOT: the search engine's F# runtime dependency and the
BenchmarkDotNet host code both preclude it. `publish/` is git-ignored.

The publish layout carries a `BuildHost-netcore/` directory (and its `net472`
sibling) beside the executable. That is the out-of-process build host
`references` launches to load the project graph; it arrives as package
`contentFiles` copied to the output. Adding `ExcludeAssets` or
`PrivateAssets=contentfiles` to either workspace package reference would silently
remove it and break every `references` run.

---

## `puck canary` — real-World behavioral proofs

`puck canary` is deliberately narrow: it runs deterministic stdin-driven
behavioral proofs that fit a strict transcript vocabulary. Every manifest owns
a positive leg and an executable discriminating leg; both start from fresh
state, and the positive observation must turn red under the discriminator while
the declared opposite observation holds. The runner builds `Puck.World` once,
runs that exact artifact sequentially, keeps stdout and stderr separate, pins
BOM-less UTF-8 stdin, closes the pipe, drains both streams, checks the absolute
`--world` boot-origin line, enforces per-leg and whole-suite budgets, and kills
the process tree on timeout.

```
puck canary                         run the automatic set (headless, no environmental requirements)
puck canary <id> ...                explicitly run named proofs
puck canary --all                   explicitly run every proof; does not change automatic eligibility
puck canary --list                  strictly load and list manifests without building or running
puck canary --capability <class>    filter automatic/headless/windowed or an environmental requirement
```

The selection forms are mutually exclusive and every execution selection must
be nonempty. Manifest tokens are case-sensitive. Every non-comment script
command declares `accepted` or intentionally expected `refused`, bound to its
verb and occurrence. Assertions cover stream-specific exact/contained lines,
verb/occurrence/exact-cardinality responses, ordered sequences, named response
field extraction, equality/inequality, inclusive bounds, minimum margins, and
byte-level file equality/inequality. A manifest may start a companion authority
world, pass its allocated endpoint through `connect`, and use `{run}` in scripts
and assertions for per-leg capture paths. There are no regex programs, loops,
callbacks, conditionals, shell, or embedded scripts.
Exit codes are 0 for all proofs held, 1 for an observed proof failure, and 2 for
usage, manifest, build, or infrastructure refusal.

This is not a universal verification gate. The richer PowerShell batteries
`undo-all-or-nothing`, `strict-definition-parse`,
`sdf-decode-sign-refusal`, `doc-links`, `addon-mutation-seam`, and
`four-world-boot-smoke` remain named, on-demand, and ungated. They are not
wrapped, ported, or executed by `puck canary`.

---

## `puck search` — content search

A ripgrep-shaped CLI over a non-backtracking symbolic-derivatives regex engine:
linear-time, leftmost-longest, with intersection (`&`), complement (`~(...)`),
and lookaround; no backreferences. `_` is any character including newline.

```
puck search <pattern> [path ...]   content search (default path: cwd)
  -i            case-insensitive
  -F            literal string (escape the pattern)
  -l            files-with-matches only (wins over -c)
  -c            per-file matching-line counts
  -n / -N       line numbers on (default) / off
  -A n          n context lines after
  -B n          n context lines before
  -C n          n context lines before and after
  -g <glob>     include glob (repeatable; no '/' matches basename)
  --not <glob>  exclude glob (repeatable; no '/' matches a file OR directory basename)
  -s            span mode: run over whole-file text, print start-end line ranges
  -M <n>        max results (default 250, 0 = unlimited)
  --files       enumerate the files that would be searched
  -q            quiet: exit code only (--files included)
  --            end of options: every later argument is pattern/paths
  -h / --help   this text
```

Exit codes: **0** matched, **1** no match, **2** usage/pattern error (the
engine's own parse message is printed verbatim). A path argument that names
nothing on disk is one of those usage errors — every bad path is reported before
the run gives up, so a typo cannot pass for "no match". The recursive walk skips
`.git`, `artifacts`, `bin`, `obj`, `node_modules`, `publish`,
`BenchmarkDotNet.Artifacts`, agent worktrees under `.claude/worktrees`, and
binary files — naming one of those paths searches it anyway. The build-artifact
names are on that list because this project writes into them: publishing drops a
generated `Puck.Maths.xml` next to the executable, whose duplicated doc comments
would otherwise drain the default `-M` cap ahead of `src/Puck.Maths/` itself.
`.claude/worktrees` holds live duplicate checkouts, whose copies otherwise answer
a query as if they were live consumers. The full flag, glob, and engine-semantics
reference — including the leftmost-longest and complement gotchas — lives in the
`content-search` skill (`.claude/skills/content-search/SKILL.md`), which drives
every content search in this repo through the published `puck search`.

---

## `puck bench` — the Puck.Maths microscope

The on-demand **microscope** for `Puck.Maths`. Where the in-suite bench-as-test
gate answers *did the ratio regress?* (fast pass/fail against baselines), this
verb answers *why is the number what it is?* — instruction-level disassembly,
per-scenario allocation columns, and full statistical detail (mean / error /
stddev / percentiles). Reach for it when a gate row moves and you need the cause,
not the verdict.

### When to reach for which

| | The gate (the test-suite ratio harness, `tests/Puck.Maths.Tests/BenchTests.cs`) | This microscope |
|---|---|---|
| Question | Did the generic/hand ratio regress past its ceiling? | *Why* is a kernel slow / allocating / different from its neighbour? |
| Output | One ns/op number per scenario, pass/fail | Disassembly, alloc bytes, stddev, percentiles, ratios vs baseline |
| Speed | Fast, runs in CI | Slow, run by hand on a quiet machine |
| Determinism | Fixed seeds, zero-alloc asserted | Same fixed seeds and regimes; framework owns the timing loop |

### Scenario ↔ bench mapping

Every benchmark class is named **1:1** after a scenario of the standalone
quadratic-algebra bench, and every method name is stable, so a row here lines up
with a row that bench produced on the same machine. That bench no longer builds;
of the grid below, **only scenario 1's generic/hand ratio is measured by anything
automatic** (the test-suite ratio gate). The other seven are exercised by this
verb, by hand, and nowhere else.

| Bench scenario | Class here | Methods |
|---|---|---|
| `1. complex mul narrow (latency)`   | `ComplexMulNarrow`   | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `2. complex mul wide (throughput)`  | `ComplexMulWide`     | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `3a. split mul narrow (latency)`    | `SplitMulNarrow`     | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `3b. split norm narrow (throughput)`| `SplitNormNarrow`    | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `4. dual<FixedQ4816> mul (latency)` | `DualFixMul`         | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `5. dual quaternion mul (latency)`  | `DualQuaternionMul`  | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `6a. extension mul (latency)`       | `ExtensionMul`       | `Hand` (baseline), `GenericStatic`, `GenericLocal` |
| `6b. extension-only operations`     | `ExtensionOnly`      | `Frobenius`, `BatchInverse` (no generic counterpart — structural gap) |

`GenericStatic` reads the algebra from a static-readonly field (the JIT may fold
`P`/`Q` to constants after tier-up); `GenericLocal` receives it as a
by-parameter argument into a `[MethodImpl(NoInlining)]` method so it cannot — the
same two placements the gate measures.

`MemoryDiagnoser` rides on every class. `DisassemblyDiagnoser` is attached only
to the **fixed-point kernels** (scenarios 1–5); the extension scenarios are
modular-`ulong` arithmetic, not a fixed-point kernel.

### Running it

```sh
# Everything (balanced default job):
puck bench --filter '*'

# One scenario family — the norm quirk, with the disassembler:
puck bench --filter '*Norm*'

# Just the hand-vs-generic-static comparison of one class:
puck bench --filter '*SplitNormNarrow.Hand' --filter '*SplitNormNarrow.GenericStatic'

# List what is available without running anything:
puck bench --list flat
```

(Run against the published executable, e.g.
`src/Puck.Cli/publish/puck.exe bench --filter '*Norm*'`, or through
`dotnet run --project src/Puck.Cli -c Release -- bench --filter '*Norm*'`.)

#### Job rigor

No explicit job is baked into the config, so a command-line `--job` is the only
job that runs (the harness would otherwise run its own job *alongside* one added
here and double the output). Pick rigor on the command line:

| Flag | Use |
|---|---|
| *(none)* | BenchmarkDotNet's adaptive default — the sane balanced setting for everyday runs. |
| `--job short` | **Fast survey.** Fewer warmup/target iterations; a quick shape-of-the-numbers pass. |
| `--job long` | **Thorough verdict.** Many iterations, tight error bars — use before a retention decision. |

### Measurement hygiene

**Run on a quiet machine.** Numbers taken under concurrent load — a build, the
test suite, another benchmark, a busy GPU — are *garbage*, not slow-but-usable
data; this is a measured house fact. Close background work first. If two runs of
a scenario disagree by more than ~10%, the machine was not quiet — rerun. The
kernels are measured exactly as written (the `Int128` widening multiply in the
wide path is deliberate and is not "fixed" here). Do not commit result artifacts;
this verb produces evidence for a decision, not baselines to pin — the
`BenchmarkDotNet.Artifacts/` directory it writes under the cwd is git-ignored.

---

## `puck scan` — source sweep

Parses every `.cs` file under `<root>` **once** (the artifact set — `.git`,
`artifacts`, `bin`, `obj`, `node_modules`, `publish`,
`BenchmarkDotNet.Artifacts` — plus agent worktrees under `.claude/worktrees`
pruned below the root; naming a skipped directory as the root itself still scans
it) and
runs the selected analyzers over that one shared corpus, so a full sweep parses
the tree a single time rather than once per analyzer.

```
puck scan [<root=src>] [-Only comments,comment-smells,locks,clones]
          [-OutDir <dir>] [-Grouped] [-MaxPerChunk N]
          [-MinTokens N] [-MinStatements N] [-NoBlocks] [-h]
```

| Analyzer | Emits |
|---|---|
| `comments` | every non-XML inline comment (`//` and `/* */`). |
| `comment-smells` | those comments bucketed by weakness (sync-coupling / debt-marker / banner-divider / commented-out-code / unclassified), plus each cross-artifact referent (a shader file name or an `UPPER_SNAKE` define) tagged resolved or dangling. |
| `locks` | synchronization sites, kind-tagged: `lock` statements, lock-primitive declarations, `Monitor.*`/`Interlocked.*` calls, `[MethodImpl(Synchronized)]`. |
| `clones` | structurally identical callable bodies and nested blocks, Type-1/Type-2 fingerprinted; gated by `-MinTokens`/`-MinStatements`, and `-NoBlocks` drops the block pass. |

### Output modes

One record per finding, as JSONL. Where it goes depends on the flags:

- **stdout** when exactly one analyzer is selected and neither `-OutDir` nor
  `-Grouped` is given — the pipe-friendly default for a one-off query.
- **`<OutDir>/<name>.jsonl`** otherwise, defaulting to `<repo>/artifacts/scan`.
  Any multi-analyzer run takes this path, so pass `-OutDir` when you do not want
  the default directory written.
- **`<OutDir>/<name>.grouped.json`** additionally under `-Grouped`: the same
  findings chunked per file (or per cluster, for `clones`) at most
  `-MaxPerChunk` to a chunk — the work-list a fan-out audit spends one agent per
  chunk on.

The analyzer's own one-line digest, plus its densest-files table, always goes to
**stderr**, so `puck scan … -Only comments > records.jsonl` leaves a readable
summary on the terminal. Exit codes: **0** ran, **2** usage error, unknown
analyzer, or missing root.

Output is deterministic: files are enumerated in a fixed order, and every
ordering — records, buckets, chunks, clusters — is fully tie-broken, so two runs
over an unchanged tree produce byte-identical bytes.

The comment-smell referent check resolves against **non-comment** source text
plus the shader sources under `<repo>/src`; resolving against comment text too
would let a define cited in a comment resolve against that very comment, which
makes the check a tautology rather than evidence.

---

## `puck schema` — world.def JSON Schema

Generates the JSON Schema for `puck.world.def.v1` from the live C# model —
`Puck.World.WorldSchema` (`src/Puck.World.Data/WorldSchema.cs`) walks
`WorldDefinition` over its own source-generated `WorldJsonContext` via
`System.Text.Json`'s `JsonSchemaExporter`, so `$type` unions, enum values, and
`additionalProperties: false` all come from the SAME contract the loader
enforces, never a hand-maintained copy. Descriptions come from
`Puck.World.Data.xml`, resolved property `<summary>` first, then the
declaring record's own `<param>` (most members are documented that way — a
positional record's XML doc lives on the record declaration, not the
property), then a type `<summary>` for a node with no containing property
(an array's item schema, a `$type` arm).

The output is SPLIT, not one file: a small root plus one file per top-level
document section (`kits.schema.json`, `screens.schema.json`, …), plus
`common.schema.json` for every subschema referenced from more than one
place — named after the CLR type it came from where that's recoverable. Every
cross-file reference is a plain relative `$ref`
(`"./schema/kits.schema.json"`, `"./common.schema.json#/$defs/WorldChannel"`),
never `$id`-based, so an editor resolves them without extra configuration.

```
puck schema                 write the checked-in root + sections + common.schema.json
puck schema --check         regenerate in memory and compare EVERY file (root, each
                            section, common.schema.json) against what's on disk; also
                            catches a stale orphan section no current model produces;
                            exit 1 on any drift
puck schema --stdout        emit the ROOT document to stdout instead of writing
                            (skips --check)
puck schema --bundle [path] emit the single-file equivalent with every cross-file $ref
                            inlined (not a checked-in artifact) — to [path] if given,
                            else stdout
puck schema -h / --help     this text
```

Written to `src/Puck.World/Assets/worlds/puck.world.def.v1.schema.json` (root)
and `src/Puck.World/Assets/worlds/schema/*.schema.json` (sections + common),
which already flow to `Puck.World`'s build output (`Assets\**` copies
`PreserveNewest`), so the schema ships beside the world documents it
describes. Running `puck schema` also DELETES any section file the current
model no longer produces, so the checked-in tree never carries an orphan.
A missing `Puck.World.Data.xml` still produces a schema, with no
descriptions, and this verb says so on stderr rather than failing. Exit
codes: **0** wrote or matched, **1** `--check` found drift (reported per file
— missing, orphan, or the path plus the first differing line), **2** usage
error or missing repository root.

---

## `puck format` — source rewriters

The rewriters for conventions `.editorconfig` cannot express, applied in one
parse-and-write per file.

```
puck format [<root=src>] [-WhatIf] [-Verify] [-h]
            [-Only attr-order,member-spacing,member-order,null-pattern,paren-clarity,
                   logical-lines,arg-lines,ternary-lines,init-order,trailing-comma,
                   decl-spacing,literal-var,named-args]
```

**Phase 0 always runs first**, for every mode and every `-Only` selection:
`dotnet format whitespace` over the projects that own corpus files (so the
corpus pruning governs which projects it can reach), establishing the
`.editorconfig` baseline the custom passes layer onto. It needs the projects
restored — and in **write mode it rewrites any whitespace drift in the root**,
which on an unswept root is the whitespace sweep for that root. Run `-WhatIf`
first; the tree-wide sweep is deliberately its own, separately-landed change.

| Pass | Rewrite | In the bare-`format` set |
|---|---|---|
| `attr-order` | one attribute per list/line, alphabetized. | yes |
| `member-spacing` | blank-line grouping between type members. | yes |
| `member-order` | a const block or property block (same kind + scope) sorted by name; non-const fields are never reordered. | yes |
| `null-pattern` | `== null` / `!= null` → `is null` / `is not null`. | yes |
| `paren-clarity` | explicit precedence parens (`((0 == a) \|\| (0 == b))`), casts included (`((uint)sets.Length)` — bare only under checked/unchecked and as a ternary branch). | yes |
| `init-order` | object-initializer members alphabetized. | yes |
| `trailing-comma` | trailing comma on a multi-line initializer's last element. | yes |
| `decl-spacing` | one blank line between a local-declaration run and the next statement. | yes |
| `literal-var` | `uint x = 0;` → `var x = 0U;` for suffix-bearing primitives. | yes |
| `named-args` | call arguments named and alphabetized (semantic). | yes |
| `logical-lines` | multi-operand `&&`/`\|\|` one operand per line, operator trailing. | `-Only` |
| `arg-lines` | a call with >1 argument: one argument per line, hanging close paren. | `-Only` |
| `ternary-lines` | `c ? t : f` across three lines, operators leading; a statement-ending paren-wrapped ternary's trailing close parens hang at the root's indent. | `-Only` |

The three vertical line-wrappers stay opt-in because their one-per-line layout
is a deliberate choice, not a baseline. Required braces are `.editorconfig`'s
job (`IDE0011`, `csharp_prefer_braces`), applied by `dotnet format style`.

### Dry modes

| Mode | Writes | Fails on |
|---|---|---|
| *(bare)* | yes | — |
| `-WhatIf` | **nothing** | any drift (exit 1), listing the files |
| `-Verify` | **nothing** | drift, a rewrite that would introduce syntax errors, or a pass that is not a fixed point (running the pipeline twice differs from once) |

Both dry modes run phase 0 as `--verify-no-changes`, so nothing on disk moves in
either. Exit code is the worst of the three phases: **1** for drift in a dry
mode or a skipped rewrite, **2** for a usage error, a missing root, or a tool
failure in write mode.

### Safety

- **The write guard is unconditional.** A pass whose output would leave a file
  with more syntax errors than it started with is dropped, the file is reported
  as corrupt, and the run fails loudly — never written, not even in a plain
  rewrite run.
- **Annotated code is left alone.** The four reordering passes (`attr-order`,
  `member-order`, `init-order`, `named-args`) reassign trivia by *slot*, so a
  reorder would leave a comment — or an `#if` — describing whichever element
  moved under it; the three line-wrappers (`logical-lines`, `arg-lines`,
  `ternary-lines`) reissue their layout slots outright, so a comment in one would
  be deleted. The syntax-only write guard sees neither: both rewrites still
  parse. All seven therefore decline a construct carrying a comment or a
  preprocessor directive in **any slot they touch**, separators, operators and
  delimiters included — a comment written after a comma belongs to the comma, not
  to either neighbour, and a slot-preserving reorder would strand it.
- **`named-args` needs the project built.** It resolves symbols against the
  project's real build closure — the built output under `bin/`, the restore's
  package assemblies from `obj/project.assets.json`, the generated global-usings
  file, and any emitted generator output. Without a build it reports which
  projects were degraded and leaves the calls it could not resolve positional.
  A file whose directory chain holds no `.csproj` is reported as skipped rather
  than counted as clean.

---

## `puck references` — semantic symbol queries

Loads the project graph and asks the compiler what each name means, so the
answer survives extension methods, `using` aliases, overload resolution, generic
instantiation and name collisions — the five places a text search is wrong.

```
puck references <name>   references to a source symbol, solution-wide
  --declarations      declarations only, no reference search
  --implementers      implementations of an interface or interface member
  --overrides         overrides of a virtual/abstract member
  --derived           derived types
  --containing <frag> keep declarations whose display string contains frag
  --contains          treat <name> as a substring, not an exact simple name
  -i                  case-insensitive name match
  --kind <k,k>        type, member, namespace (default: type,member)
  --solution <path>   default: the nearest .slnx walking up from the cwd
  --project <path>    load one project instead
  --configuration <c> build configuration (default Debug)
  --metadata          also match declarations from referenced assemblies
  --no-doc            drop locations inside documentation trivia
  --strict            keep only locations whose group definition IS the queried symbol
  --allow-partial     report anyway after a workspace load failure
  --json / -q / -h
```

Records only on stdout, `path:line:col` first so a line parses like a `search`
hit:

```
src/Puck.Abstractions/Memory/AllocatorExtensions.cs:14:24 decl Method Puck…AllocatorExtensions.Alloc(Puck…IAllocator, nint)
src/Puck.Vulkan/VulkanMarshalHelpers.cs:18:31 ref Method Puck…AllocatorExtensions.Alloc(Puck…IAllocator, nint)
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
  compilation (`<Compile Remove="scripts/**/*.cs" />`) and files in no project
  cannot be seen; `puck search` and `puck declarations` see them.
- **A workspace load failure is fatal.** A partly loaded solution answers "no
  references" indistinguishably from a true zero, so any `Failure` diagnostic
  prints and exits 2 unless `--allow-partial` is passed. The commonest cause is
  an unrestored tree (a fresh worktree): the design-time build resolves an
  incomplete reference closure and the architecture gate's lane profiles trip
  on the missing edges. The refusal counts the projects carrying no
  `obj/project.assets.json`, and the remedy there is `dotnet restore` at the
  tree's root — never `--allow-partial`.

Loading is not a pure read: it runs a design-time build, which writes generated
files into each project's `obj/<Configuration>/`.

---

## `puck declarations` — declaration inventory

The syntax tier: parse each file and report what it declares. No build, no
restore, no project system — so it covers files no project compiles.

```
puck declarations [path ...]   declaration inventory, parse-only (default path: cwd)
  -g <glob> / --not <glob>   include/exclude globs, the same matcher search uses
  --kind <k,k>       class, struct, record, interface, enum, delegate,
                     method, property, field, event, ctor
  --name <frag>      declared simple name contains frag
  --base <frag>      base list contains frag (types only)
  --attribute <frag> an attribute name contains frag
  --members          list members inside each type (implied by a member --kind)
  --doc              also emit XML-doc cref targets, filtered by --name alone
  --json / -q / -h
```

Output is `path:line:col decl <kind> <qualified name>[ : <base list>]`, sorted by
path then position, with a `cref` relation under `--doc`. One record is always
one line: base lists, parameter lists and crefs written across source lines are
rendered from their tokens alone, so comments between them are dropped, two
tokens the source separated are separated by one space, and the `///` opening a
continued line of a documentation comment is a continuation rather than a
separator — a cref split across two lines still reads as one dotted path.
`--name` and `--base` filter that same rendered form. Both record forms report
the kind `record`, and an extension block, which names nothing, is reported as
its members' enclosing static class rather than as a declaration of its own.
Same exit codes as `references`, and a path that names nothing is a usage error
rather than a silent empty answer. `--base` is the cheap implementers query when
a build is unwanted; it matches base-list text, so it cannot see an
implementation inherited through a base class.

`declarations` shares its walk, glob matcher and skip list with `search` — both
refuse `artifacts` and agent worktrees under `.claude/worktrees`, so a paired
sweep covers one tree — and its parse with `scan`.

---

## `puck wasm-stdlib` — WASM standard library sources

Regenerates every GENERATED Rust source registered in
`Puck.Scripting.WasmStdlibSources.All` — the maintained set of generated sources
that make up the WASM standard library, not a single one-off port. Today that
registry holds three files under `wasm/puck-stdlib/src`. Two give the WASM addon
guest a self-contained, bit-exact copy of `FixedQ4816`'s six algorithm-pinned
transcendentals (`atan2`, `sin`/`cos`, `exp2`, `log2`, `pow`): `fixed_generated.rs`
(the ported functions plus their interval tables and polynomial coefficients)
and `fixed_vectors.rs` (known-answer vectors, computed by calling the real
`FixedQ4816` at generation time). The third, `abi_generated.rs`, mirrors the
addon ABI's names and values from the live host types.

```
puck wasm-stdlib   regenerate every registered generated Rust source
  -h / --help   this text
```

This verb is a thin wrapper: it writes whatever
`Puck.Scripting.WasmStdlibSources.All` lists, never generation logic of its own
— every table, coefficient and vector is read from the live `FixedQ4816` type by
`Puck.Maths.FixedQ4816RustPort`, one of the registry's contributors. **Adding a
future artifact is a one-line addition to that registry** — this verb never
changes. Every `Emit` delegate is **byte-idempotent**: an unchanged host must
produce byte-identical output on every run, which is what makes running this
verb twice a drift check in its own right. **Nothing gates that today** — the
stage that iterated the registry in-process and compared each result against
what is committed left the build, so a drifted commit is caught only by running
this verb and reading the diff. Never hand-edit a generated file; regenerate it
with this verb and commit the result.

Takes no arguments and no `<root>` — unlike every other verb, each registered
path (e.g. `wasm/puck-stdlib/src`) names a repository convention rather than
something a caller supplies, so it is anchored at the repository root instead
of the working directory. Exit codes: **0** wrote every file, **2** usage error,
repository root not found, or a destination directory missing.
