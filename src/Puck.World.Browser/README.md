# Puck.World.Browser

The engine embedded in a browser tab: a `browser-wasm` publish of `Puck.World.Schema` +
`Puck.State`, and the `.puck` authoring toolchain (`Puck.World.Transpiler`: compiler,
composer, diagnostics and language server), behind a JSON-string `[JSExport]` surface. The studio
(`src/Puck.Dashboard`) loads this AppBundle as its native validator and rule tick,
replacing its TypeScript twin, through the TypeScript facade at
`src/Puck.Dashboard/src/portal/src/native/` (`engineTypes.ts`/`engineHost.ts`/
`inlineHost.ts`/`engine.worker.ts`—see that directory's own remarks).

## Layout

```text
Engine/       Pure C# core — no [JSExport], no JS-interop attribute. Linked as
              source into tests/Puck.World.Browser.Tests (a browser-wasm exe
              cannot be referenced as an ordinary assembly). BrowserParser.cs is
              the shared parse-validate pipeline. BrowserWorkspace.cs and
              BrowserLanguageServer.cs are the .puck authoring surface.
Exports/      The [JSExport] marshalling shim alone (BrowserExports + the JSON
              envelope types/converters it serializes through).
main.mjs      The one entry point the studio and the Node harness both import:
              createEngine(options) boots the Mono runtime from this AppBundle's
              own _framework/dotnet.js and resolves the Exports surface.
```

## Build and publish

```bash
dotnet build src/Puck.World.Browser -c Release
dotnet publish src/Puck.World.Browser -c Release
```

The project already declares `browser-wasm`. Do not add a command-line `-r`:
that global property reaches every referenced library and changes its NuGet
lock graph, breaking the next ordinary desktop restore.

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
publish from a checkout with a short path, such as one near a drive root.

## AppBundle—real layout, recorded from an actual publish

The publish command writes `bin/Release/net10.0/browser-wasm/AppBundle/`. Its sizes:

```text
AppBundle/
  Puck.World.Browser.runtimeconfig.json     2,421 B
  main.mjs                                  1,430 B
  package.json                                 19 B
  .stamp                                        0 B
  _framework/
    dotnet.native.wasm                   45,754,760 B   <- AOT'd native code for every assembly in the closure
    Puck.World.Schema.wasm                5,659,401 B   <- the document model + validator, IL (rooted, unstripped)
    System.Private.CoreLib.wasm           1,828,117 B
    Puck.State.wasm                         901,893 B
    Puck.World.Transpiler.wasm              613,637 B   <- the .puck compiler, composer, diagnostics, language server
    System.Private.Xml.wasm                 472,853 B
    Parlot.wasm                             462,101 B   <- Puck.Transpiler's scanner package
    System.Text.Json.wasm                   437,013 B
    Puck.Transpiler.wasm                    411,397 B   <- the .puck language core
    System.Linq.Expressions.wasm            364,309 B   <- reached through Parlot
    Puck.State.Rules.wasm                   351,493 B
    Puck.World.Authoring.wasm               310,533 B   <- rooted, unstripped (see "Trim baseline" below)
    Puck.World.Browser.wasm                 290,069 B
    Puck.Maths.wasm                         242,949 B
    dotnet.native.js                        242,796 B
    dotnet.runtime.js                       198,480 B
    Puck.Commands.wasm                      112,901 B
    Puck.Assets.wasm                        101,637 B   <- rooted, unstripped
    (+ ~60 more BCL/Puck.* assemblies and source maps, mostly metadata-only stubs post-AOT)
```

**Total AppBundle size: 60,636,449 bytes (57.83 MiB), uncompressed.** The
two transpiler assemblies, Parlot and the `System.Linq.Expressions` Parlot
reaches account for about 10.6 MB of it. The same engine without
`RunAOTCompilation` publishes far smaller; see "Performance" below for why the
switch is on despite the size. No mimalloc native binary lands in the
AppBundle (see "mimalloc" below). The native WebAssembly module alone (43.7
MiB, 75% of the payload) is every assembly's AOT-compiled native code; the
Schema assembly module (5.4 MiB) and its two `TrimmerRootAssembly` siblings
still ship their full IL
on top of that native code—`WasmStripILAfterAOT` (the SDK default, on)
strips a trimming-eligible method's IL body once AOT has compiled it
natively, but a `TrimmerRootAssembly`-rooted assembly is kept out of trimming
entirely (see "Trim baseline" below), so ILLink never marks its methods
eligible to strip and their IL survives untouched alongside the native code
that supersedes it at runtime—the dominant cost this switch carries, not
something a stripping flag can claw back without giving up the reflection
that rooting exists to keep real.

What a visitor downloads is smaller. `puck`'s website publisher stores every
official object Brotli-compressed at its highest quality, served with
`Content-Encoding: br`, whenever that saves at least a tenth of its size
(`AzureWebsite`); on that rule the `_framework` files of this publish transfer
as 10,918,600 bytes, the native module 45,754,760 → 8,073,955. `puck official
serve`, the local development server, serves them uncompressed.

### Cold-start time

Loading `main.mjs` under Node and calling `createEngine()` to a ready
`[JSExport]` surface: **~110-150 ms** on this machine under AOT (measured via
the Node harness's own per-test timings; a browser tab's first load also
pays one-time `WebAssembly.compile`/`instantiate` cost over a ~3.4x larger
binary, which this Node measurement does not isolate—a browser network
fetch's own cost is a separate, unmeasured concern here). `Version()` itself
(a call into an already-booted engine) answers in single-digit milliseconds
either way.

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
  as the AppBundle's own `_framework/dotnet.d.ts` names it. The
  callback signature is `(type, name, defaultUri, integrity, behavior) =>
  string | Promise<Response> | Promise<BootModule> | null | undefined`;
  returning `null`/`undefined` falls back to the default fetch. The studio
  uses this to feed content-hash-verified cached bytes instead of a network
  fetch (see `docs/architecture/worlds.md`'s remote-worlds default).
- `options.runtimeConfig` &rarr; `DotnetHostBuilder.withConfig(config: MonoConfig)`,
  for a caller booting against a relocated `_framework` (an official-content
  CDN path rather than this AppBundle's own).

No Node-specific API exists in `dotnet.d.ts`—`dotnet.js` detects a Node host
at runtime on its own (verified: `node --test` and a plain `node script.mjs`
both boot the engine with no special flags or globals).

## Determinism canary

`Puck.State.StateArena.ComputeHash()` folds every stored column of the session's
arena through one FNV-1a accumulator—the shared comparator between a native run
and a wasm run of the identical document, ticks, and writes. The two scripted
sequences below currently fold to the same hash: the row they write already
holds the value they write, and neither judged tick moves the arena.
`tests/Puck.World.Browser.Tests/BrowserParityRecordingTests.cs` runs two fixed
scripted sequences (write a scalar row then judge tick 1; judge tick 1 then
tick 2 with no write) over `games/tictactoe.puck` composed under
`standard.world.json`, and compares the resulting hash against
`Fixtures/browser-parity/expected.json`; `puck baselines browser-parity`
re-records that baseline from a fresh run. `src/Puck.Dashboard/src/portal/tests/engine-wasm.test.cjs` runs
the identical two sequences through the wasm build under Node and asserts the
same two hashes—this is the actual cross-runtime proof, not merely a native
self-check: both runs matched on the AppBundle this README's own numbers came
from.

Only `games/tictactoe.puck` composes standalone under
`standard.world.json` among the sampled fragments (`bowling`,
`billiards`, `poker`, `dominoes`, `freecell`, `hexlines`, `klondike`, and
`mancala` all refuse—each names a host register, a look, or a body motion
program that only the island's own body supplies, never the bare basis
alone); the two canary fixtures are two scripted-write cases over that one
document rather than two different fragments. The flagship `puck.world.json`
parses and validates too (see "Verified scope boundary" below) but stays
outside the canary fixtures above, which are scoped to the one document pair
both runtimes already cross-check.

## Verified scope boundary: no emulator core, no shader catalog, no probe kinds

`WorldDefinitionValidator` reads two injection seams before any document
parses—`WorldPostProcessVocabularyHook.PostProcessPackageCheck` and
`WorldProbeVocabularyHook.ProbeKindCheck`—normally wired by a composition
root's module initializer (`Puck.World.Client.WorldSchemaVocabularyHooks` for
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
island (`puck.world.json` over `standard.world.json`) succeeds, its `deferred[]`
naming every one of the three real GamingBrick console screens
`modules/arcade.world.json` (one of the island's fifteen imports) authors—
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
seventeen operand facts (`PhysicsQuiescentOperand`, `RegionOccupancyOperand`,
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
never established, a zero channel, and no navigation state.
`PlacementInfluenceOperand` alone reads `RuleFact.Absent`—an unrepresented
influence provider is unknowable, never a falsely safe zero, exactly as
`WorldRuleHost.Influence.cs` already answers it for the one real case that
reads `Absent` today. `puck.world.json`'s own `dive`/`kart`/`jump` modules
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
names a physics/body operand; `engine-timing.test.cjs` and
`engine-wasm.test.cjs` judge the composed island itself. Only
`BrowserParityRecordingTests`/`engine-wasm.test.cjs`'s own
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

`src/Puck.Dashboard/src/portal/tests/engine-timing.test.cjs` times every call
an editing loop makes against this AppBundle under Node, over the shipped
island (`puck.world.json` + `standard.world.json` + its imports,
`~2.4 MB` of source JSON, composing to a `~428 KB` standalone document); the
native columns are a recorded measurement of the identical calls under the JIT,
for comparison. Medians of three runs each, one machine. No test re-measures
them: the browser tests assert behavior, never elapsed time.

| Call | wasm interpreter (Node) | native (JIT), cold | native (JIT), warm |
|---|---:|---:|---:|
| `Parse` (tictactoe under basis) | 1,235 ms | 1,135 ms | 11 ms |
| Composing the full island | 12,733 ms | 2,605 ms | 1,996 ms |
| `Compile` (composed island) | 9,911 ms | 1,683 ms | 1,710 ms |
| `Judge` (tick 1, tictactoe) | 35 ms |—|—|
| `StateHash` (tictactoe) | 1 ms |—|—|
| `Cells` (chessBoard, 64 cells) | 7 ms |—|—|

Two things follow from the native cold/warm split. `Parse`'s huge cold-run
cost (1,135 ms) is almost entirely one-time JIT/static-init—it drops to
11 ms once the same process has already paid that cost once. Composing
and `Compile` do **not** drop the same way (2,605 ms → 1,996 ms; 1,683 ms →
1,710 ms)—that time is real per-call work, not warm-up, and it lives in
`Puck.World.Schema` (the `WorldDefinitionFileSource`/`WorldDocumentBasis`
`JsonNode` tree merge composition runs, and the
parse/migrate/validate/rule-compile pipeline `BrowserParser.TryParseAndValidate`
runs three times over the same ~428 KB document across one composition and
validation plus one `Compile` call) and `Puck.State.Rules` (the rule compiler `WorldFactsCompiler` runs)—
outside this project's own `Engine`/`Exports` boundary. The wasm interpreter
is roughly 5-6x slower than native-warm on both (composition 12,733 ms vs
1,996 ms; `Compile` 9,911 ms vs 1,710 ms), consistent with Mono's
interpreter-tier overhead relative to JIT, not a wasm-specific pathology.

**`RunAOTCompilation` is on.** With it (`WasmStripILAfterAOT` at its SDK
default of `true`—see "AppBundle" above for why it cannot strip the three
rooted assemblies), the wasm interpreter numbers above become:

| Call | AOT (Node) | vs. interpreter |
|---|---:|---:|
| `Parse` (tictactoe under basis) | 753 ms | 1.6x |
| Composing the full island | 2,347 ms | 5.4x |
| `Compile` (composed island) | 1,969 ms | 5.0x |
| `Judge` (tick 1, tictactoe) | 17 ms | 2.1x |
| `StateHash` (tictactoe) | 2 ms | ~1x |
| `Cells` (chessBoard, 64 cells) | 5 ms | 1.5x |

Composition and `Compile` together—the pair an edit-and-preview cycle pays—
take ~4.3 s under AOT against ~22.6 s under the interpreter, and the browser
tests and harnesses (`dotnet test tests/Puck.World.Browser.Tests`,
`engine-wasm.test.cjs`, `native.test.cjs`, `engineBoot.test.cjs`), including
the parity-hash assertions, hold under both. The cost is the AppBundle's size:
57.83 MiB with the `.puck` toolchain, over this package's own "~40 MB
uncompressed" guidance on either reading. A stricter reading of that ceiling
would argue for turning the switch off.

`WasmEnableSIMD` is not set, and its effect is unmeasured: measuring it needs
a clean rebuild (`rm -rf obj bin` first), because the SDK's incremental AOT
cache treats a SIMD and a non-SIMD build as equivalent. The bottleneck this
section explains—`JsonNode` tree merging and rule-table interpretation, not
numeric vector loops—makes SIMD an unlikely win regardless.

There is no separate interpreter-tiering/PGO MSBuild property in this SDK
(`Microsoft.NET.Runtime.WebAssembly.Sdk/10.0.11`)—`RunAOTCompilation` is the
only lever `BrowserWasmApp.targets`/`WasmApp.Common.targets` expose for
interpreter speed. `EventSourceSupport` already defaults to `false` for a
browser-wasm publish (the SDK's own default, unrelated to this project's
`<NoWarn>`); `DebuggerSupport`/`UseSystemResourceKeys` are general SDK
feature switches over diagnostics infrastructure and exception-message
strings, neither of which sits in composition's or `Compile`'s own call graph—
not tried, on that reading of the SDK source rather than a measurement.

Composition and `Compile` still cost ~2 s each on the full island even AOT'd and
native-warm (see the cold/warm table above)—real per-call work in
`Puck.World.Schema`'s `JsonNode` tree merge and `Puck.State`'s rule compilation,
outside this project's own boundary. A preview pays validation twice over the same
bytes: `ComposeSource` validates the world it composes, and `Compile` of that composed
text parses and validates it again before it installs a session. The studio runs
`CompileSource`/`ComposeSource` on a lazily started world engine and the language server
on a separate language engine, so neither blocks the other.

### What one diagnosis costs

A unit of `LspIdle` work is one tier of one document's diagnosis
(`WorldSourceDiagnostics.DiagnoseSource` or `DiagnoseSemantic`). Measured
through throwaway timing builds of this AppBundle under Node (AOT),
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
dependency's own item list. None of this ever reaches the AppBundle itself
(which only ever collects `_framework/*.wasm`)—`mimalloc.dll`/
`mimalloc-redirect.dll`/`libmimalloc.so` are litter in the sibling output
folders, never part of the shipped payload.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
