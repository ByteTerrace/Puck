# Symbol-analysis complete reference

This preserves the full semantic/syntax behavior, flags, traps, recipes, and
file-based-app fallback. Read the relevant section when the compact skill
routes here.

## Contents

- [Picking the tier](#picking-the-tier)
- [`puck references`](#puck-references--the-semantic-tier)
- [`puck declarations`](#puck-declarations--the-syntax-tier)
- [Recipes](#recipes)
- [When neither verb fits](#when-neither-verb-fits)

Two verbs of the consolidated `puck` developer CLI (`src/Puck.Cli`) are
invoked from the repository root. Prefer the portable published assembly:

```text
dotnet src/Puck.Cli/publish/Puck.Cli.dll <verb>
```

If it is absent, bootstrap from the tracked project:

```text
dotnet run --project src/Puck.Cli/Puck.Cli.csproj -c Release -- <verb>
```

Every command below writes just `puck <verb>` for short. Never depend
exclusively on the ignored Windows launcher
`src/Puck.Cli/publish/puck.exe`. Refresh published output after tool changes with
`dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish` from the repo
root. `puck <verb> -h` prints that verb's usage and exits 0; `puck` with no verb
lists every verb of the CLI on stderr and exits 2, as an unknown verb does.

| Verb | Tier | Answers |
|---|---|---|
| `puck references <name>` | **semantic** — loads the project graph, resolves symbols | who references it, what implements it, what overrides it, is it dead |
| `puck declarations [path…]` | **syntax** — parses files, no build | what is declared where, base lists, attributes, doc crefs |

**The core rule:** if the question contains *uses*, *references*, *implements*,
*overrides*, *dead*, *unused*, *rename*, or *safe to delete* about C# code, text
output is **not evidence**. Text matching cannot see extension methods, `using`
aliases, overload resolution, generic instantiation, or name collisions. Both
directions of that error are live in this tree and verified:
`AllocatorExtensions` looks dead when searching for the type name, while its
`Alloc` member has semantic call sites. Inverted, many references to
`QuadraticAlgebra<TScalar>.Element` do not spell `Element` because a
`using QFixElem = …` alias renames it.

## Picking the tier

| Question shape | Tool |
|---|---|
| Where is a literal string / a config value / a non-C# asset (HLSL, JSON, project files)? Orientation in unfamiliar code? | `puck search` — see the `content-search` skill. It is the only sanctioned content search. |
| Did my rename or refactor break anything? | `dotnet build` — the compiler is the cheapest full-semantic oracle, and this repo compiles IDE0005 as an error. Do not search for stragglers; build. |
| What does this tree declare? Members, base lists, attributes, doc crefs. Files no project compiles. | `puck declarations` |
| Who references this? What implements or overrides it? Is it dead? Is a delete safe? | `puck references` |
| Code metrics, call-graph shape, a walk no flag expresses | ad-hoc file-based app — recipe at the end. |

`references` costs a solution load (a design-time build per project, scaling
with the project count); `declarations` costs a parse per file and no build at
all. `--project` narrows `references` to one project's closure, which is cheaper
and answers a different question — see the closure trap.

## `puck references` — the semantic tier

```
references <name>   references to a source symbol, solution-wide
  --declarations      declarations only, no reference search
  --implementers      implementations of an interface or interface member
  --overrides         overrides of a virtual/abstract member
  --derived           derived types
  --containing <frag> keep declarations whose display string contains frag (ordinal)
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
  --json              one JSON object per line
  -q / -h
```

Output is records and nothing else — no counts, no banner:

```
src/Puck.Abstractions/Memory/AllocatorExtensions.cs:14:24 decl Method Puck.Abstractions.Memory.AllocatorExtensions.Alloc(Puck.Abstractions.Memory.IAllocator, nint)
src/Puck.Vulkan/VulkanMarshalHelpers.cs:18:31 ref Method Puck.Abstractions.Memory.AllocatorExtensions.Alloc(Puck.Abstractions.Memory.IAllocator, nint)
```

`path:line:col` leads, so a line parses like a `search` hit and pastes into an
editor as a jump target. Paths are working-directory-relative and
forward-slashed; running from a subdirectory changes only the prefix (verified:
the same query from the repo root and from `src/Puck.Maths` differs only in
`src/` vs `../`). Records are grouped by resolved definition and sorted by
position inside a group; two runs are byte-identical (verified). **Exit codes: 0
a declaration matched, 1 none did, 2 usage error or workspace load failure.**

### The symbol on a `ref` line is the RESOLVED definition, not what you asked for

The search cascades, and the cascade is not configurable. Both forms are
verified here:

- **Constructor cascade.**
  `references CaptureFrame --containing Puck.Abstractions.Capture` reports
  `src/Puck.Hosting/FrameCaptureController.cs:169` under
  `CaptureFrame.CaptureFrame(Surface, long, ulong)` — the constructor — because
  the site is `new CaptureFrame(…)`. **`--strict` on a type therefore hides
  every construction site** (verified: strict drops exactly that location).
- **Interface cascade.** `references CaptureFrame --containing ISdfFrameSource`
  reports locations under the interface method and under each implementation's
  own method, because an interface-dispatched call resolves to the interface
  while a direct call resolves to the implementation.

Read the definition column. When you want "sites that mention this exact
symbol", `--strict`; when you want "everything that reaches this behavior",
the default.

### Documentation crefs are real references

`<see cref="…"/>` targets come back as ordinary reference locations — not
implicit, not candidate. Verified: `references IAllocator` includes
`src/Puck.Abstractions/Memory/AllocatorExtensions.cs:4` and `:6`, which are the
`<see cref="IAllocator"/>` in that class's doc comment; `--no-doc` drops those
and the other documentation-only locations without dropping code references.

**Consequence for dead-code work:** a symbol whose only inbound references are
doc crefs is **not pinned**. Run `references <name> --no-doc`; a result that is
only `decl` lines is the dead-code candidate. Without `--no-doc` a cref-only
result reads as a live consumer and the dead code survives.

### This tier is blind to anything the project system does not compile

Verified in this tree: `experimental/scripts/recording/mux-check.cs:111`
contains `session.Consume(new CaptureFrame(` — a self-contained file-based
`.NET` app under the quarantined `experimental/` tree, in no project at all
(none of it is reached by the root build; see `experimental/README.md`). No
reference query reports that line.

So **"zero references" can mean "zero compiled references"**. Pair a deletion
decision with `puck search -M 0 -l <name>` or `puck declarations` over the same
tree — the two walk one identical file set, see the walk contract below;
`declarations` still parses that uncompiled script and lists what it declares.

### Other verified behavior worth knowing before you trust a result

- **`--project` narrows the closure, and the narrowing is silent.**
  `references Alloc --project src/Puck.Abstractions/Puck.Abstractions.csproj`
  reports the declaration but no consumers; the solution-wide form reports
  consumers. `OpenProjectAsync` loads the project and its project-reference
  closure, so every consumer outside it is invisible. Use `--project` to ask
  about one project, never to ask "is this used".
- **Declarations are source-only by default.** `--metadata` widens the search to
  referenced assemblies and is usually noise: on `Alloc` it adds
  `System.Runtime.InteropServices.NativeMemory.Alloc(nuint)` and its two-argument
  overload (verified), each of which then drives a reference search that can only
  come back empty. A declaration outside source is reported at `<assembly>:0:0`.
  `--contains` is source-only and pairing it with `--metadata` is a usage error.
- **A load failure is fatal by default.** Any `Failure` diagnostic prints as
  `references: workspace: <message>` and exits 2, because a partly loaded solution
  answers "no references" indistinguishably from a true zero. Verified against a
  solution naming a missing project: exit 2 with the diagnostic, and
  `--allow-partial` turns the same run into exit 0 with the partial answer.
  `Puck.slnx` itself loads with no diagnostics at all (verified, repeatedly).
- **Loading is not a pure read.** It runs a design-time build, which creates
  `obj/<Configuration>/net10.0/` and writes `AssemblyInfo.cs`,
  `AssemblyInfoInputs.cache` and `GeneratedMSBuildEditorConfig.editorconfig`
  there (verified on a throwaway project outside the repo that had no `obj/`
  before the run). The default Configuration is Debug; `--configuration`
  changes it.
- **An unrestored project degrades silently, it does not fail.** Verified on a
  throwaway project outside the repo with a `PackageReference` and no
  `obj/project.assets.json`: the workspace reported no diagnostic, source
  declarations resolved normally, and the package's assembly was simply absent
  from the compilation (a query for a type in it found nothing). Restore before
  trusting an answer that crosses a package boundary.
- **A symbol with several partial declarations reports one line per
  declaration.** `references IWorldServerHost --implementers` lists
  `WorldServer` at multiple positions — one per `partial` file. That is
  the symbol's location set, not duplication.
- **`--implementers` works on an interface and on an interface member**
  (verified on `ISdfFrameSource` and on its `CaptureFrame` method).
- **`--declarations` is how you see a name collision.**
  `references CaptureFrame --declarations` separates the record struct from
  same-named methods on unrelated types and signatures. A text count conflates
  those declarations.
- **Declarations the compiler creates are found too** (verified). A record's
  positional properties (`references FrameIndex --declarations` reaches
  `CaptureFrame.FrameIndex`) and the `Program` type a top-level-statements file
  gets (including under `--project`) are real
  source declarations, and the exact-name query reports them. Two invariants
  ride on the same discovery path: an exact name answers a subset of what
  `--contains` answers, and a narrower `--kind` only ever removes records.

## `puck declarations` — the syntax tier

```
declarations [path ...]   declaration inventory, parse-only (default path: cwd)
  -g <glob> / --not <glob>   include/exclude globs, the same matcher search uses
  --kind <k,k>       class, struct, record, interface, enum, delegate,
                     method, property, field, event, ctor
  --name <frag>      declared simple name contains frag (ordinal)
  --base <frag>      base list contains frag (types only)
  --attribute <frag> an attribute name contains frag
  --members          list members inside each type (implied by a member --kind)
  --doc              also emit XML-doc cref targets, filtered by --name alone
  --json / -q / -h
```

Output is `path:line:col decl <kind> <qualified name>[ : <base list>]`, sorted by
path then position, with a `cref` relation under `--doc`. Exit codes are the same
0/1/2. Verified properties:

- **Inventory counts are outputs, not skill constants.** To establish a
  regression baseline, run `puck declarations src/Puck.Maths` against the
  revision under test and retain that run's output with the test evidence.
  Re-run after either the verb or the tree changes.
- **One record is one line.** Base lists, parameter lists and crefs are written
  across source lines often enough that this matters. Only the tokens are
  rendered: comments between them are dropped, two tokens the source separated
  are separated by one space, and the `///` that opens a continued line of a
  documentation comment is a continuation rather than a separator, so a cref
  split across two lines still reads as one dotted path. `--name` and `--base`
  filter that same rendered form.
- **A path that names nothing is a usage error** — `declarations: no such file
  or directory: <path>` and exit 2, so a typo cannot be read as "this tree
  declares nothing" (exit 1). A named file that is not `.cs` is called out on
  stderr and skipped.
- **It sees files no project compiles** (the `references` blind spot above), and
  it prunes `.git`, `artifacts`, `bin`, `obj`, `node_modules`, `publish`,
  `BenchmarkDotNet.Artifacts` and agent worktrees under `.claude/worktrees`.
  `puck search` prunes exactly the same set (verified), so the two verbs paired
  on one question cover one tree — neither reaches a stale duplicate checkout
  that would answer as a live consumer. Naming a pruned directory as a root
  still walks it, for either verb.
- **Both record forms report the kind `record`.**
- Two runs are byte-identical, and running from a different directory changes
  only the path prefix (verified).

`--base` is the cheap implementers query when a build is unavailable or
unwanted: `declarations --base ISdfFrameSource src` returns the same types
that `references ISdfFrameSource --implementers` does (verified). It matches
base-list *text*, so it cannot see an implementation inherited through a base
class, and an alias would fool it — that is the price of not building.

## Recipes

| Intent | Command |
|---|---|
| Who uses this symbol? | `puck references <name>` |
| Is this safe to delete? | `puck references <name> --no-doc`, then `puck search -M 0 -l <name>` for the uncompiled files |
| Where do these two same-named things differ? | `puck references <name> --declarations`, then re-run with `--containing <namespace-or-type>` |
| What implements this interface? | `puck references <name> --implementers` (or `puck declarations --base <name> src` with no build) |
| What overrides this member? | `puck references <name> --overrides` |
| What derives from this type? | `puck references <name> --derived` |
| What does this file/tree declare? | `puck declarations --members <path>` |
| What carries this attribute? | `puck declarations --attribute <frag> --members src` |
| What do the docs point at? | `puck declarations --doc --name <frag> src` |
| Machine-readable | add `--json` to either verb |

## When neither verb fits

Code metrics, call-graph shape, or any walk the flags do not express: write a
single-file .NET file-based app in the **session scratchpad** (never in the repo)
and run it with
`dotnet run -c Release -p:NuGetAudit=false <file>.cs -- <args>`. Release is
required by this repository's machine policy; disabling the audit prevents an
otherwise package-free probe from failing only because the vulnerability feed
is offline. The directives and traps that
still apply:

- `#:package Microsoft.CodeAnalysis.CSharp.Workspaces@<pinned>` for the semantic
  layer, or `Microsoft.CodeAnalysis.CSharp@<pinned>` for syntax only. Pin the
  version that `src/Puck.Cli/packages.lock.json` resolves — the compiler-platform
  packages pin each other at an exact version internally.
- `#:property PublishAot=false` — required as soon as anything reflects.
- **Do not add `Microsoft.Build.Locator`.** It fails the file-based-app build
  (MSBL001), and it fights the out-of-process build host in a project. Modern
  `MSBuildWorkspace` needs neither it nor a `RegisterDefaults()` call: it shells
  out to a `BuildHost-netcore/` process delivered with the package.
- Use `workspace.RegisterWorkspaceFailedHandler(…)`, not `workspace.WorkspaceFailed +=`
  — the event is `[Obsolete]`, and this repo compiles warnings as errors.
- `MSBuildWorkspace.OpenSolutionAsync` handles this repo's `.slnx` directly.
- Sort before printing. `ReferencedSymbol.Locations` comes back unordered.
- The design-time build writes `obj/` and defaults to `Configuration=Debug`, as
  above. Treat a run against the repo as a build, not a read.
