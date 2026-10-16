# Puck.World.Browser

The engine embedded in a browser tab: a `browser-wasm` publish of `Puck.World.Schema` +
`Puck.State`, and the `.puck` authoring toolchain (`Puck.World.Transpiler`: compiler,
composer, diagnostics and language server), behind a JSON-string `[JSExport]` surface. The studio
(`src/Puck.Dashboard`) loads the published payload as its native validator and rule tick,
replacing its TypeScript twin, through the TypeScript facade at
`src/Puck.Dashboard/src/portal/src/native/` (`engineTypes.ts`/`engineHost.ts`/
`inlineHost.ts`/`engine.worker.ts`—see that directory's own remarks).

## Layout

```text
Engine/           Pure C# core — no [JSExport], no JS-interop attribute. Linked as
                  source into tests/Puck.World.Browser.Tests (a browser-wasm exe
                  cannot be referenced as an ordinary assembly). BrowserParser.cs is
                  the shared parse-validate pipeline. BrowserWorkspace.cs and
                  BrowserLanguageServer.cs are the .puck authoring surface.
Exports/          The [JSExport] marshalling shim alone (BrowserExports + the JSON
                  envelope types/converters it serializes through).
wwwroot/main.mjs  The one entry point the studio and the Node harness both import,
                  published beside _framework/: createEngine(options) boots the Mono
                  runtime from the payload's own _framework/dotnet.js and resolves
                  the Exports surface.
```

## Build and publish

```bash
dotnet build src/Puck.World.Browser -c Release
dotnet publish src/Puck.World.Browser -c Release
```

The project uses `Microsoft.NET.Sdk.WebAssembly`, the one SDK on .NET 11 that
lays out a bootable browser payload: it converts the trimmed assemblies to
webcil, writes the boot configuration into `_framework/dotnet.js`, and publishes
the lot under `bin/Release/net11.0/publish/wwwroot/` (the plain
`Microsoft.NET.Sdk` with `WasmMainJSPath` builds, relinks the runtime and then
writes no bundle at all on this SDK, on either runtime flavour). The runtime is
Mono's (`UseMonoRuntime=true`), compiled ahead of time; `Performance` below
records why. `_framework` file names carry no fingerprint and no `.br`/`.gz`
twin: the payload is content-addressed and compressed downstream (`puck official
build`, the website publisher), and the official boot path finds
`dotnet.native.wasm` by name.

The SDK sets the project's `browser-wasm` runtime identifier. Do not add a
command-line `-r`: that global property reaches every referenced library and
changes its NuGet lock graph, breaking the next ordinary desktop restore.

Both commands fail on a machine whose `C_INCLUDE_PATH` environment variable
names a Cosmocc toolchain's include directory: that path leaks into every
`clang`/emscripten invocation this build shells out to and collides with
emscripten's own libc headers (`COSMOPOLITAN_C_START_` redefinitions, `bool32`
unknown type). It is a host setting, not a repository or project one;
`unset C_INCLUDE_PATH` before either command above.

On Windows, the publish also fails from a checkout whose path is too deep.
The AOT step writes a tokens file for each assembly, named after the
assembly, inside the checkout. When that file's full path would pass Windows'
260-character `MAX_PATH` limit, the step skips the write without reporting
it. The only error comes later and names a missing `*_compiled_methods.txt`,
which points away from the cause. Assemblies with the longest names hit the
limit first. A worktree under `.claude/worktrees/` is already deep enough, so
publish from a checkout with a short path, such as one near a drive root, or
map the worktree to a drive letter first (`subst P: <worktree>`) and publish
from there.

## The payload—real layout, recorded from an actual publish

The publish command writes `bin/Release/net11.0/publish/wwwroot/`. Its sizes:

```text
wwwroot/
  main.mjs                                  1,508 B
  _framework/
    dotnet.native.wasm                   55,001,557 B   <- AOT'd native code for every assembly in the closure
    Puck.World.Schema.wasm                7,321,869 B   <- the document model + validator, IL (rooted, unstripped)
    System.Private.CoreLib.wasm           2,066,201 B
    Puck.State.wasm                         912,649 B
    Puck.World.Authoring.wasm               692,489 B   <- rooted, unstripped (see "Trim baseline" below)
    Puck.World.Transpiler.wasm              638,217 B   <- the .puck compiler, composer, diagnostics, language server
    System.Private.Xml.wasm                 549,657 B
    System.Text.Json.wasm                   502,041 B
    Parlot.wasm                             463,129 B   <- Puck.Transpiler's scanner package
    Puck.Transpiler.wasm                    438,025 B   <- the .puck language core
    Puck.SignedDistance.wasm                377,609 B
    System.Linq.Expressions.wasm            364,825 B   <- reached through Parlot
    Puck.State.Rules.wasm                   353,545 B
    Puck.World.Browser.wasm                 345,881 B
    Puck.Maths.wasm                         271,113 B
    dotnet.native.js                        212,145 B
    dotnet.runtime.js                       197,560 B
    Puck.Assets.wasm                        178,953 B   <- rooted, unstripped
    (+ 50 more BCL/Puck.* assemblies, dotnet.js and its boot configuration, dotnet.d.ts)
```

**Total payload size: 72,605,895 bytes (69.2 MiB), uncompressed**, 69 files
under `_framework`. The native WebAssembly module alone (52.5 MiB, 76% of the
payload) is every assembly's AOT-compiled native code; the Schema assembly
module (7.0 MiB) and its two `TrimmerRootAssembly` siblings still ship their
full IL on top of that native code—`WasmStripILAfterAOT` (the SDK default, on)
strips a trimming-eligible method's IL body once AOT has compiled it natively,
but a `TrimmerRootAssembly`-rooted assembly is kept out of trimming entirely
(see "Trim baseline" below), so ILLink never marks its methods eligible to
strip and their IL survives untouched alongside the native code that
supersedes it at runtime—the dominant cost AOT carries, not something a
stripping flag can claw back without giving up the reflection that rooting
exists to keep real. The same engine without `RunAOTCompilation` publishes at
19.2 MB; see "Performance" below for why the switch is on despite the size. No
mimalloc native binary lands in the payload (see "mimalloc" below).

What a visitor downloads is smaller. `puck`'s website publisher stores every
official object Brotli-compressed at its highest quality, served with
`Content-Encoding: br`, whenever that saves at least a tenth of its size
(`AzureWebsite`); on that rule the `_framework` files of this publish transfer
as 12,884,989 bytes, the native module 55,001,557 → 9,599,282. `puck
official serve`, the local development server, serves them uncompressed.

### Cold-start time

Loading `main.mjs` under Node and calling `createEngine()` to a ready
`[JSExport]` surface: **about 160 ms** on this machine under AOT, and
about 95 ms under the interpreter (a browser tab's first load also pays
one-time `WebAssembly.compile`/`instantiate` cost over the 52.5 MiB native
module, which this Node measurement does not isolate—a browser network fetch's
own cost is a separate, unmeasured concern here). `Version()` itself (a call
into an already-booted engine) answers in single-digit milliseconds either
way.

## `[JSExport]` API surface (as actually exported)

Every export takes and returns JSON **strings**—never raw bytes, never a
bare JS number for a 64-bit value (a `long`/`ulong` always crosses as a
decimal string, converted through `LongAsStringJsonConverter`/
`UInt64AsStringJsonConverter`). The runtime is single-threaded; a compiled
session lives behind an opaque decimal-string handle in
`BrowserSessionRegistry` between calls.

```csharp
string Version();                                                          // {schemaVersion, engine, commit}
string Parse(string json);                                                 // {ok, document, deferred[]} | {ok:false, errors:[{path,message}], deferred[]}
string ParseFragment(string fragmentJson, string hostJson, string alias);  // same shape as Parse, alias-stripped errors
string Canonicalize(string json);                                          // same shape as Parse
string Compile(string json);                                               // {ok, handle} | {ok:false, errors[]}
string AnalyzeCosts(string json);                                          // {ok, validated, report, validationErrors[], deferred[]} | {ok:false, errors[]} — no session/arena
string Release(string handle);                                             // {ok}
string Rows(string handle);                                                // {ok, rows:[{name, kind, keyed, cells:[{key,value}]}]}
string Costs(string handle);                                               // {ok, report, error?}
string Rebind(string handle, string json);                                 // {ok, error?}
string Judge(string handle, string tick);                                  // {ok, trace:{rules[{name,mode,evaluations[]}], writes[{row,key,old,new}], refusals[]}}
string ReadRow(string handle, string row, string key);                     // {found, kind, value} — see "One cell value on the wire"
string WriteRow(string handle, string row, string key, string value, string write); // write: "set"|"add" -> {ok, error?}
string Evaluate(string handle, string expression, string kind, string tick); // kind: a CellKind member name ("Int"/"Fixed") -> {ok, value, error?}
string BoardMask(string handle, string row);                               // {ok, mask, error?} — only for a board of <= 64 cells
string StateHash(string handle);                                           // {ok, hash, error?}
string Cells(string topologyJson);                                         // {ok, cells:[{ordinal,key,x,y,z}], error?} — no handle; a topology is self-contained
string MountSources(string filesJson);                                     // filesJson: {path: text} -> {ok, error?}; replaces the whole /worlds tree
string WriteSource(string path, string text);                              // {ok, error?}
string CompileSource(string path);                                         // {ok, document, worlds:[{name, document, entry, sourceMap}], diagnostics:[{code, severity, message, path, line, column, length}], sourceMap:{pointer: {path, line, column, length, module}}}
string ComposeSource(string path);                                         // {ok, composed, diagnostics:[{code, severity, message, path, line, column, length}]}
string Lsp(string message);                                                // [every JSON-RPC message the server wrote in reply]
string LspIdle();                                                          // {ran, pending, messages:[publishDiagnostics...]}
```

## Authoring `.puck` sources

The studio edits `.puck` sources, and the engine's own toolchain compiles, diagnoses,
completes, formats and colours them; nothing of the language is reimplemented in
TypeScript. The toolchain runs unchanged over a workspace mounted at `/worlds` in the
runtime's in-memory file system (Emscripten's MEMFS, which .NET's `System.IO` reaches on
`browser-wasm`): `MountSources` replaces the whole tree, `WriteSource` writes one file.
Paths crossing these exports are workspace-relative and forward-slashed
(`games/klondike.puck`); a path that climbs out of the workspace, is rooted, or carries a
backslash is refused. The engine installs the transpiler's document composer as its local
document source when it loads, as the game and the CLI do, so a `basis` or import name
resolves to a mounted `.puck` source exactly as it does on disk.

`CompileSource` compiles one source once and reports exactly what the language server
publishes for it at full depth: the source tier (parse, import walk, lowering, lint) and the
semantic tier over the same compilation (the engine's validation of the composed world,
reference lint). `document` is the canonical document
text `puck compile` writes, or `null` when the source emits several worlds (each then in
`worlds`) or does not compile. Lines and columns are 1-based, as the compiler's
`SourceSpan` has them; a line of 0 is a finding about the whole document. Every `path` is
workspace-relative, including a diagnostic or source-map origin in another source (a
basis or an imported module). A `.world.json` document is JSON already: `CompileSource`
hands it back canonicalized, or refuses it when it is not a JSON object.

`ComposeSource` composes one file's whole `basis`-and-imports graph through the same
document source: a `.puck` source is compiled first, compiling every mounted source it names
on the way, and a `.world.json` document composes as the JSON it is, so a JSON root can
import a `.puck` fragment and a `.puck` source can name a JSON basis. It then validates the
composed world with the semantic tier the language server runs (composition refusals, the
engine's validation, the reference lint) and returns those findings with the source tier's:
`ok` means the graph composed and nothing reported an error, and `composed` is present
whenever composition itself succeeded, valid or not, so a caller can inspect what failed. A
check this engine cannot answer, because it carries no catalog for it (a machine engine, a
post-process package, a probe kind), is a `PUCK118` diagnostic of severity `information`:
a deferral, never a finding, so the shipped island composes `ok`. It
is the standalone document a preview hands to `Compile`. A composition source composes its
entry world. A finding the composed world raises about an element an imported document
authored is reported against the root, at line 0, because composition records no provenance
for the elements it merges.

`Lsp` hands one JSON-RPC message to the one `PuckLanguageServer` (see the
[Puck.World.Transpiler editor tooling](../Puck.World.Transpiler/README.md#editor-tooling))
and returns every message it wrote in reply. Documents are addressed
`file:///worlds/<path>`, and opening or changing one writes its text into the workspace,
so the open buffers are the workspace that `CompileSource`, `ComposeSource` and the
diagnosis of every other source read. An edit never diagnoses inside `Lsp`: the worker
cannot interrupt a running call, so it calls `LspIdle` whenever its inbox is empty, one
unit of diagnostic work (the most recently edited document, at its latest text) per call,
yielding between calls while `pending` is true, so a completion or hover is never queued
behind a diagnosis. Each `publishDiagnostics` carries the version it diagnosed. The
diagnosis depth is the client's choice in `initialize`
(`initializationOptions: {"diagnostics": "source"}` runs the source tier alone, and `"full"`,
the default, adds the semantic tier as a unit of its own); the studio runs its language
engine at `"source"` and takes composed-world findings from `ComposeSource` on its world
engine.

`Costs` projects the installed compilation's shared `WorldCostReport`. A known
cycle bound carries a decimal-string count; an unmodeled or overflowed bound
carries `null`, never a misleading zero. Model and evidence identities travel
with the report. Heuristic work is a separately labelled string. Repeated reads
reuse the compiled analysis; a successful `Rebind` replaces it. The portal's
`costs(handle)` facade exposes exact cycle counts and multipliers as `bigint`
in both inline and worker hosts. This is authored cost analysis, including
operations the preview host cannot execute, not a preview timing measurement.
`AnalyzeCosts` runs the same parse, migration, draw resolution, structural
validation, and rule compilation over a draft without installing a session or
allocating its arena. A draft whose programs compile still returns its report when
ordinary validation refuses installation; `validated` remains false and
`validationErrors` carries those refusals, so the report never implies admission.
Here `validated` means local structural validation; platform checks the browser
cannot perform remain explicit in `deferred`. Malformed JSON or a document whose rule programs cannot compile returns the
ordinary analysis-failure diagnostics.

`Rows` reports only a row's **authored** cells (`StateRow.Cells`); a dense
board's un-authored cells are readable individually through `ReadRow` or in
bulk through `Cells`/`BoardMask`, never enumerated in `Rows`—a large lattice
would otherwise dwarf the rest of the read-back for a board no studio user
paints sparsely.

### One cell value on the wire

A cell crosses as the tag-and-payload pair `Puck.State.CellValue` is, never as
sibling members a reader has to pick between: `kind` names the case and `value`
is that case's own spelling. `Rows` carries the tag once per row, so its
`cells[]` entries spell the payload alone.

| `kind` | `value` |
|---|---|
| `Int` | the 64-bit value as a decimal string |
| `Fixed` | the raw `FixedQ4816` bits as a decimal string—the same raw channel `WriteRow` takes, never a decimal reading of the number |
| `Bool` | `"true"` or `"false"` |
| `Text` | the text itself |
| `Vector` | the base64url components |

`value` is `null` where the arena holds no such cell; `kind` is `null` only when
no row of the session addresses the read at all (`found: false`).

## The runtime's Node/resource-loader hooks (`dotnet.d.ts`)

`main.mjs`'s `createEngine(options)` forwards:

- `options.resourceLoader` &rarr; `DotnetHostBuilder.withResourceLoader(loadBootResource?: LoadBootResourceCallback)`,
  as the payload's own `_framework/dotnet.d.ts` names it. The
  callback signature is `(type, name, defaultUri, integrity, behavior) =>
  string | Promise<Response> | Promise<BootModule> | null | undefined`;
  returning `null`/`undefined` falls back to the default fetch. The studio
  uses this to feed content-hash-verified cached bytes instead of a network
  fetch (see `docs/architecture/worlds.md`'s remote-worlds default).
- `options.runtimeConfig` &rarr; `DotnetHostBuilder.withConfig(config: MonoConfig)`,
  for a caller booting against a relocated `_framework` (an official-content
  CDN path rather than this payload's own).

No Node-specific API exists in `dotnet.d.ts`—`dotnet.js` detects a Node host
at runtime on its own (verified: `node --test` and a plain `node script.mjs`
both boot the engine with no special flags or globals).

## Cross-runtime parity

`BrowserParityRecordingTests` (`tests/Puck.World.Browser.Tests`) prices the
presentation dimension of `Fixtures/browser-parity/presentation.world.json`
through `BrowserCostAnalyzer`, exactly as the `AnalyzeCosts` export spells it,
and compares the result with the committed
`Fixtures/browser-parity/expected.json`; `puck baselines browser-parity`
re-records that baseline from a fresh run.
`src/Puck.Dashboard/src/portal/tests/engine-wasm.test.cjs` sends the same
document through the published engine's `AnalyzeCosts` under Node and asserts
the same report, so the native and wasm engines are held to the identical
bytes in and report out—the actual cross-runtime proof, not a native
self-check. Both halves hold on the payload this README's own numbers came
from.

The canary's other half, `Puck.State.StateArena.ComputeHash()` (every stored
column of a session's arena folded through one FNV-1a accumulator) over two
scripted sequences of `games/tictactoe.puck` composed under `standard.puck`, is
quarantined with those worlds under
`experimental/tests/Puck.World.Browser.Tests` while the debug world is the one
live world; it returns when the live tree authors a fragment and a basis. The
flagship island parses and validates too (see "Verified scope boundary" below)
but stays outside the fixtures.

## Verified scope boundary: no emulator core, no shader catalog, no probe kinds

`WorldDefinitionValidator` reads two injection seams before any document
parses—`WorldPostProcessVocabularyHook.PostProcessPackageCheck` and
`WorldProbeVocabularyHook.ProbeKindCheck`—normally wired by a composition
root's module initializer (`Puck.World.WorldSchemaVocabularyHooks` for
the desktop client) to a real catalog answering `true`/`false`. Each is REQUIRED
(never absent-tolerant: an uninstalled hook throws), but answers `bool?`—
`true`/`false` from a host with a real catalog, or `null` from a host that
carries no catalog for that vocabulary AT ALL, which the validator routes to
its `deferred` collection rather than a refusal. `Engine/BrowserExtensionVocabulary.cs`
installs its own module initializer answering every one of those `null`: this
build carries no emulator core, render graph package catalog, or probe-kind catalog
of its own (Architecture.props' exact-closure profile denies
`Puck.World.Protocol` and every extension-owning assembly), so "this host has
no catalog to check against" is the honest answer—distinct from a real
catalog's `false` refusal, and never a stub that would silently admit a
document naming a capability this engine cannot run. Machine checks use an
explicit `IMachineValidationCatalog` per invocation. This browser supplies none,
so machine admission appears in the deferred collection; its assembly initializer
cannot change another host's machine catalog.

**Verified consequence**: `Parse()` on the composed flagship
island (`experimental/src/Puck.World/Assets/worlds/puck.world.json` over `standard.puck`) succeeds, its `deferred[]`
naming every one of the three real GamingBrick console screens
`modules/arcade.puck` (one of the island's fifteen imports) authors—
`machines[0] (arcade$cgb-screen).configuration: validation is deferred because
no machine catalog was supplied for 'gaming-brick'.` (and two more, for
`advanced-gaming-brick` and a second `gaming-brick` screen), beside three
`screens[n].source.machine.output` deferrals. This is expected behavior, not a
defect. A district or game fragment that authors no `screens[].source.machine`
row (most of the catalog) parses and compiles cleanly with no deferral at all.

**`Judge()` on the composed flagship island judges hostlessly, honestly.**
`BrowserSession`'s effect host (`Engine/BrowserRuleReader.cs`) is a
`Puck.State.Rules.ArenaEffectHost` over the session's `StateArena`, widened to
`IWorldFacts` (`Puck.World.Schema/IWorldFacts.cs`)—the world's
eighteen operand facts (`PhysicsQuiescentOperand`, `RegionOccupancyOperand`,
`ArgBodyOperand`, …) plus the two body-reference resolutions and the two
host-owned row reads `Puck.World.Server.WorldServer` answers from real bodies,
machines, a clock, and adjacencies. This engine ships none of those, so `BrowserRuleReader`
answers each one the honest vacuous fact a world with no bodies, no machines,
no clock, and no adjacencies gives—the same convention each
`WorldRuleFacts` prefix's own remarks and `WorldRuleHost.cs`'s "no such
body"/"no such machine" reads already commit to for an absent host, applied
here for "there is no host at all": population `0`, physics vacuously
quiescent, no region occupants, no machine byte, no argmax/argmin/nearest
body (`-1`), the engine's largest representable distance between two bodies
that do not exist, no line of sight, never parked, perfectly upright, a link
never established, a zero channel, no navigation state, and no board cell under a body (`-1`).
`PlacementInfluenceOperand` alone reads `RuleFact.Absent`—an unrepresented
influence provider is unknowable, never a falsely safe zero, exactly as
`WorldRuleHost.Influence.cs` already answers it for the one real case that
reads `Absent` today. `experimental/src/Puck.World/Assets/worlds/puck.world.json`'s own `dive`/`kart`/`jump` modules
author rules reading these facts, so every such hostless read is recorded
onto the judged tick's own trace as `hostFacts[]`
(`{rule, operand, answer}`)—how an author sees which rules lean on a fact
this engine cannot supply from a real host. A rule whose facet this host does
not advertise is refused admission at load (`RuleNeeds.Admit`) and never
evaluated; its refusal rides every judged tick's `refusals[]` under the category
`RuleNotAdmitted`.

A state row lying over a dense lattice (a physics field) is installed
`HostOwned`: the arena stores no columns for it, and this engine serves no
host-owned row, so its cells read as absent. `BrowserHostlessIslandTests` judges the composed island over
ticks 1-3 and pins both that no such throw occurs and that `hostFacts[]`
names a physics/body operand. Only
`BrowserParityRecordingTests`'s own
determinism-hash fixtures use `games/tictactoe.puck`, for their
own fixed-baseline reason, unrelated to this boundary.

## Trim baseline

`PublishTrimmed=true`/`TrimMode=partial`, with `TrimmerRootAssembly` keeping
`Puck.Assets`, `Puck.World.Authoring`, and `Puck.World.Schema` whole (their
own `IsAotCompatible=false` reflection surface—schema/name-registry
reflection, the `puck.creation.v1` embedded-document converter, `WorldDocumentBasis`'s
`JsonNode` diff/merge logic—stays real and reachable rather than being
guessed at member-by-member). ILLink's trim ANALYSIS still runs over that IL
regardless of the root/copy action (rooting only stops removal, not
analysis), producing a recorded baseline of **31 warnings** with `NoWarn`
cleared (`IL2026 x 26`, `IL2070 x 4`, `IL2075 x 1`), suppressed in this
project's own `<NoWarn>`—none of the flagged call sites are reachable from
this project's own `Engine`/`Exports` surface today. Recorded once here as
the number to notice growing, not a target to shrink: a rise means new
reflection-dependent code became reachable from this project, worth a second
look before accepting.

`Puck.Transpiler`'s scanner, the Parlot package, collapses its own analysis to
one `IL2104`, also suppressed. Every warning behind it (`IL2026`, `IL2055`,
`IL2072`, `IL2075`, `IL2090`, read with `TrimmerSingleWarn=false`) sits in
Parlot's parser-combinator compilation, which Puck's hand-written parser never
calls: it uses Parlot's scanner, cursor and parse context only. Copying the
package untrimmed does not stop the analysis, so the suppression is the record.
The same granular read shows `Puck.World.Schema`'s own reflection surface as
the rest of the baseline.

`JsonSerializerIsReflectionEnabledByDefault` is forced back to `true` for the
same reason: a trimmed publish defaults that feature switch off, and
`System.Text.Json.JsonSerializer`'s generic `Serialize<T>`/`Deserialize<T>(…,
JsonSerializerOptions)` overloads (never the `JsonTypeInfo<T>` ones this
project's own `Engine` code uses) then throw `NotSupportedException
("JsonSerializerIsReflectionDisabled")` at the first fragment composition
(`WorldDocumentBasis`'s `JsonArray.Add` merge/diff logic hits it immediately).
Cost: the published `System.Text.Json` WebAssembly module is 425,237 bytes with
the switch and 327,445 bytes without it (+30%, +96 KiB).

## Performance

The tables below record the calls an editing loop makes against this payload
under Node, for the three runtime flavours this SDK can publish: Mono compiled
ahead of time (the shipped flavour), Mono's interpreter (the same project
without `RunAOTCompilation`) and CoreCLR's interpreter (`UseMonoRuntime=false`,
.NET 11's default flavour for `browser-wasm`, published with
`WasmBuildNative=false` because this SDK's CoreCLR relink fails in `wasm-ld`
with an undefined `SystemJS_ExecuteTimerCallback`). Two documents: the island
(`experimental/src/Puck.World/Assets/worlds/puck.world.json` with
`standard.puck` and its imports, mounted together with the live tree it still
imports from, composing to a 1.14 MB standalone document) and the debug world
(`src/Puck.World/Assets/worlds/debug.puck`, the one live world, composing to
286 KB). "Cold" is the first call in a fresh process and "warm" the median of
the next two, one machine. No test re-measures them: the browser tests assert
behavior, never elapsed time.

The island:

| Call | Mono AOT, cold | Mono AOT, warm | Mono interpreter, cold | Mono interpreter, warm | CoreCLR interpreter, cold | CoreCLR interpreter, warm |
|---|---:|---:|---:|---:|---:|---:|
| `ComposeSource` | 3,078 ms | 767 ms | 10,417 ms | 2,557 ms | 188,474 ms | 49,411 ms |
| `Compile` (the composed island) | 203 ms | 153 ms | 715 ms | 564 ms | 5,357 ms | 5,439 ms |
| `Judge` (tick 1) | 31 ms | 6 ms | 54 ms | 22 ms | 112 ms | 91 ms |
| `StateHash` | 4 ms | 2 ms | 32 ms | 24 ms | 99 ms | 97 ms |

The debug world:

| Call | Mono AOT, cold | Mono AOT, warm | Mono interpreter, cold | Mono interpreter, warm | CoreCLR interpreter, cold | CoreCLR interpreter, warm |
|---|---:|---:|---:|---:|---:|---:|
| `ComposeSource` | 1,376 ms | 65 ms | 2,684 ms | 229 ms | 15,859 ms | 5,214 ms |
| `Compile` | 50 ms | 31 ms | 335 ms | 108 ms | 1,446 ms | 1,423 ms |
| `Judge` (tick 1) | 24 ms | 0 ms | 23 ms | 2 ms | 25 ms | 2 ms |

| Flavour | Payload, uncompressed |
|---|---:|
| Mono AOT (this publish) | 72,605,895 B |
| Mono interpreter (the same closure, `RunAOTCompilation` off) | 19,243,674 B |
| CoreCLR interpreter (the same closure, `UseMonoRuntime=false`) | 22,436,268 B |

**`RunAOTCompilation` is on, and the runtime is Mono's.** Composition and
`Compile` together—the pair an edit-and-preview cycle pays—take about 0.9 s
warm on the island under AOT, about 3.1 s under Mono's interpreter and about
55 s under CoreCLR's, whose interpreter also pays 15 s before the first
composition of the debug world. The cost is the payload's size: 69.2 MiB
against 18.4 MiB for the interpreter, over this package's own "~40 MB
uncompressed" guidance on either reading. A stricter reading of that ceiling
would argue for turning the switch off; the editing loop argues for keeping
it. The browser tests and harnesses (`dotnet test
tests/Puck.World.Browser.Tests`, `engine-wasm.test.cjs`, `native.test.cjs`,
`engineBoot.test.cjs`), the parity assertion included, hold on every flavour.

CoreCLR's equivalent of AOT, ReadyToRun for `browser-wasm`, is implemented in
dotnet/runtime for .NET 12 (dotnet/runtime#132339 and its follow-up #133378,
both in the 12.0 milestone) and is absent from this SDK: its CoreCLR targets
(`BrowserWasmApp.CoreCLR.targets`) carry no ReadyToRun or crossgen handling,
the CoreCLR runtime pack ships IL-only framework assemblies, and
`RunAOTCompilation` on that flavour only switches native symbol stripping on
for CI builds. The CoreCLR flavour comes back into consideration when an SDK
ships ReadyToRun for `browser-wasm`; its interpreter cannot serve the editing
loop.

SIMD is on (this SDK links `libmono-wasm-simd.a` when exception handling is
on, its default), and its effect is unmeasured: the bottleneck this section
explains—`JsonNode` tree merging and rule-table interpretation, not numeric
vector loops—makes it an unlikely lever. There is no separate
interpreter-tiering/PGO MSBuild property in this SDK; `RunAOTCompilation` is
the only lever the Mono targets expose for interpreter speed.

A preview pays validation twice over the same bytes: `ComposeSource` validates
the world it composes, and `Compile` of that composed text parses and validates
it again before it installs a session. The studio runs
`CompileSource`/`ComposeSource` on a lazily started world engine and the
language server on a separate language engine, so neither blocks the other.

### What one diagnosis costs

A unit of `LspIdle` work is one tier of one document's diagnosis
(`WorldSourceDiagnostics.DiagnoseSource` or `DiagnoseSemantic`). Measured
through throwaway timing builds of this payload under Node (AOT),
warm (third of three runs), over copies of shipped sources mounted as a
workspace. The source tier is all a language engine at `"source"` depth runs;
full depth adds the semantic tier as a following unit.

| Root | Source tier | Semantic tier | Root compiles | Basis compiles |
|---|---:|---:|---:|---:|
| `genesis/card.puck` (3,862-line `.puck` basis) | 20 ms | 6,313 ms | 1 | 2 |
| `parlor/chess.puck` (`.puck` basis) | 461 ms | 148 ms | 1 | 2 |
| `rulepush/rulepush.puck` (4 worlds, module imports, no runtime basis) | 1,102 ms | 397 ms | 1 | 0 |

The semantic tier composes the root's graph twice (validation and reference
lint), and each composition proves every `.puck` link in the chain still reads
(`PuckDocumentComposer.StillReads`) by compiling it again, so a root with a
`.puck` basis compiles that unchanged basis twice per diagnosis; for
`genesis/card.puck` that was 2,338 ms of its semantic tier in an earlier
stage-by-stage measurement. The rest is the composition's `JsonNode` merge and
the engine's validation of a large composed world. These are dated evidence for
those builds, not a gate.

## mimalloc

`Directory.Build.props`' win-x64/linux-x64 mimalloc `None` items are
conditioned off `'$(RuntimeIdentifier)' != 'browser-wasm'`, but a referenced
class library (`Puck.Assets`, `Puck.Maths`, …) never sets a `RuntimeIdentifier`
of its own—it evaluates that condition as `'' != 'browser-wasm'` (true) and
still copies the native binaries into its **own** output, which then flows
into this project's `bin`/`publish` directories through ordinary
cross-project `CopyToOutputDirectory` propagation. This project's own
`PuckRemoveNativeMimallocFromWasmOutput` target deletes the stray copies
after every `Build`/`Publish` rather than intercepting the flow at each
dependency's own item list. None of this ever reaches the payload itself
(`publish/wwwroot` collects static web assets alone)—`mimalloc.dll`/
`mimalloc-redirect.dll`/`libmimalloc.so` are litter in the sibling output
folders, never part of the shipped payload.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
