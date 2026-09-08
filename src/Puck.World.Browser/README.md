# Puck.World.Browser

The engine embedded in a browser tab: a `browser-wasm` publish of `Puck.World.Schema` +
`Puck.State` behind a JSON-string `[JSExport]` surface. The studio
(`src/Puck.Dashboard`) loads this AppBundle as its native validator and rule tick,
replacing its TypeScript twin, through the TypeScript facade at
`src/Puck.Dashboard/src/portal/src/native/` (`engineTypes.ts`/`engineHost.ts`/
`inlineHost.ts`/`engine.worker.ts` — see that directory's own remarks).

## Layout

```
Engine/       Pure C# core — no [JSExport], no JS-interop attribute. Linked as
              source into tests/Puck.World.Browser.Tests (a browser-wasm exe
              cannot be referenced as an ordinary assembly). BrowserComposer.cs
              is the ComposeTree export's own core, over BrowserParser.cs'
              shared parse-validate pipeline.
Exports/      The [JSExport] marshalling shim alone (BrowserExports + the JSON
              envelope types/converters it serializes through).
main.mjs      The one entry point the studio and the Node harness both import:
              createEngine(options) boots the Mono runtime from this AppBundle's
              own _framework/dotnet.js and resolves the Exports surface.
```

## Build and publish

```
dotnet build src/Puck.World.Browser -c Release
dotnet publish src/Puck.World.Browser -c Release
```

The project already declares `browser-wasm`. Do not add a command-line `-r`:
that global property reaches every referenced library and changes its NuGet
lock graph, breaking the next ordinary desktop restore.

Both commands fail on this machine unless the `C_INCLUDE_PATH` environment
variable naming a Cosmocc toolchain's include directory is unset first — that
path leaks into every `clang`/emscripten invocation this build shells out to
and collides with emscripten's own libc headers (`COSMOPOLITAN_C_START_`
redefinitions, `bool32` unknown type). This is host machine contamination, not
a repository or project setting; `unset C_INCLUDE_PATH` before either command
above.

## AppBundle — real layout, recorded from an actual publish

The publish command writes `bin/Release/net10.0/browser-wasm/AppBundle/`:

```
AppBundle/
  Puck.World.Browser.runtimeconfig.json     2,421 B
  main.mjs                                  1,430 B
  package.json                                 19 B
  .stamp                                        0 B
  _framework/
    dotnet.native.wasm                   31,542,689 B   <- AOT'd native code for every assembly in the closure
    Puck.World.Schema.wasm                4,917,513 B   <- the document model + validator, IL (rooted, unstripped)
    System.Private.CoreLib.wasm           1,711,893 B
    Puck.State.wasm                         563,461 B
    System.Private.Xml.wasm                 471,829 B
    System.Text.Json.wasm                   425,749 B
    dotnet.native.js                        238,780 B
    Puck.Maths.wasm                         218,885 B
    dotnet.runtime.js                       198,480 B
    Puck.World.Authoring.wasm               173,317 B   <- rooted, unstripped (see "Trim baseline" below)
    Puck.World.Browser.wasm                 161,557 B
    Puck.Commands.wasm                      110,853 B
    Puck.Assets.wasm                         98,565 B   <- rooted, unstripped
    Puck.Physics.wasm                        65,285 B
    Puck.Attestation.wasm                    61,189 B
    (+ ~35 more BCL/Puck.* assemblies, mostly metadata-only stubs post-AOT)
```

**Total AppBundle size: 42,444,284 bytes (40.48 MiB), uncompressed** — up from
**12,600,621 bytes (12.02 MiB)** the same publish produced before
`RunAOTCompilation` (a 3.4x growth; see "Performance" below for why this
switch is on despite the size). No mimalloc native binary lands in the
AppBundle (see "mimalloc" below). `dotnet.native.wasm` alone (31.5 MiB, 74%
of the payload) is every assembly's AOT-compiled native code; `Puck.World.Schema.wasm`
(4.8 MiB) and its two `TrimmerRootAssembly` siblings still ship their full IL
on top of that native code — `WasmStripILAfterAOT` (the SDK default, on)
strips a trimming-eligible method's IL body once AOT has compiled it
natively, but a `TrimmerRootAssembly`-rooted assembly is kept out of trimming
entirely (see "Trim baseline" below), so ILLink never marks its methods
eligible to strip and their IL survives untouched alongside the native code
that supersedes it at runtime — the dominant cost this switch carries, not
something a stripping flag can claw back without giving up the reflection
that rooting exists to keep real.

### Cold-start time

Loading `main.mjs` under Node and calling `createEngine()` to a ready
`[JSExport]` surface: **~110-150 ms** on this machine under AOT (measured via
the Node harness's own per-test timings; a browser tab's first load also
pays one-time `WebAssembly.compile`/`instantiate` cost over a ~3.4x larger
binary, which this Node measurement does not isolate — a browser network
fetch's own cost is a separate, unmeasured concern here). `Version()` itself
(a call into an already-booted engine) answers in single-digit milliseconds
either way.

## `[JSExport]` API surface (as actually exported)

Every export takes and returns JSON **strings** — never raw bytes, never a
bare JS number for a 64-bit value (a `long`/`ulong` always crosses as a
decimal string, converted through `LongAsStringJsonConverter`/
`UInt64AsStringJsonConverter`). The runtime is single-threaded; a compiled
session lives behind an opaque decimal-string handle in
`BrowserSessionRegistry` between calls.

```csharp
string Version();                                                          // {schemaVersion, engine, commit}
string Parse(string json);                                                 // {ok, document, deferred[]} | {ok:false, errors:[{path,message}], deferred[]}
string ParseFragment(string fragmentJson, string hostJson, string alias);  // same shape as Parse, alias-stripped errors
string ComposeTree(string rootName, string documentsJson, string editedName, string editedJson); // documentsJson: {name: text}; editedName "" for none -> {ok, composed, document, deferred[]} | {ok:false, errors[], deferred[]}
string Canonicalize(string json);                                          // same shape as Parse
string Compile(string json);                                               // {ok, handle} | {ok:false, errors[]}
string Release(string handle);                                             // {ok}
string Rows(string handle);                                                // {ok, rows:[{name, kind, keyed, cells:[{key,value,text}]}]}
string Rebind(string handle, string json);                                 // {ok, error?}
string Judge(string handle, string tick);                                  // {ok, trace:{rules[{name,mode,evaluations[]}], writes[{row,key,old,new}], refusals[]}}
string ReadRow(string handle, string row, string key);                     // {found, value, text}
string WriteRow(string handle, string row, string key, string value, string write); // write: "set"|"add" -> {ok, error?}
string Evaluate(string handle, string expression, string kind, string tick); // kind: a CellKind member name ("Int"/"Fixed") -> {ok, value, error?}
string BoardMask(string handle, string row);                               // {ok, mask, error?} — only for a board of <= 64 cells
string StateHash(string handle);                                           // {ok, hash, error?}
string Cells(string topologyJson);                                         // {ok, cells:[{ordinal,key,x,y,z}], error?} — no handle; a topology is self-contained
```

`Rows` reports only a row's **authored** cells (`StateRow.Cells`); a dense
board's un-authored cells are readable individually through `ReadRow` or in
bulk through `Cells`/`BoardMask`, never enumerated in `Rows` — a large lattice
would otherwise dwarf the rest of the read-back for a board no studio user
paints sparsely.

## The runtime's Node/resource-loader hooks (`dotnet.d.ts`)

`main.mjs`'s `createEngine(options)` forwards:

- `options.resourceLoader` &rarr; `DotnetHostBuilder.withResourceLoader(loadBootResource?: LoadBootResourceCallback)`
  — **not** `withResourceLoad` (an earlier draft of this file guessed wrong;
  the real name is in the AppBundle's own `_framework/dotnet.d.ts`). The
  callback signature is `(type, name, defaultUri, integrity, behavior) =>
  string | Promise<Response> | Promise<BootModule> | null | undefined`;
  returning `null`/`undefined` falls back to the default fetch. The studio
  uses this to feed content-hash-verified cached bytes instead of a network
  fetch (see `docs/vision.md`'s remote-worlds default).
- `options.runtimeConfig` &rarr; `DotnetHostBuilder.withConfig(config: MonoConfig)`,
  for a caller booting against a relocated `_framework` (an official-content
  CDN path rather than this AppBundle's own).

No Node-specific API exists in `dotnet.d.ts` — `dotnet.js` detects a Node host
at runtime on its own (verified: `node --test` and a plain `node script.mjs`
both boot the engine with no special flags or globals).

## Determinism canary

`Puck.State.StateFrameHash.Compute(StateFrame)` folds a frame's `Values` span
through one FNV-1a accumulator — the shared comparator between a native run
and a wasm run of the identical document, ticks, and writes.
`tests/Puck.World.Browser.Tests/BrowserParityRecordingTests.cs` runs two fixed
scripted sequences (write a scalar row then judge tick 1; judge tick 1 then
tick 2 with no write) over `games/tictactoe.world.json` composed under
`standard.basis.json`, and compares the resulting hash against
`Fixtures/browser-parity/expected.json`. Set
`PUCK_BROWSER_PARITY_RECORD=1` to overwrite that baseline instead of comparing
against it. `src/Puck.Dashboard/src/portal/tests/engine-wasm.test.cjs` runs
the identical two sequences through the wasm build under Node and asserts the
same two hashes — this is the actual cross-runtime proof, not merely a native
self-check: both runs matched on the AppBundle this README's own numbers came
from.

Only `games/tictactoe.world.json` composes standalone under
`standard.basis.json` among the fragments this work sampled (`bowling`,
`billiards`, `poker`, `chess`, `dominoes`, `freecell`, `hexlines`, `klondike`,
`mancala` all refuse — each names a host register, a look, or a body motion
program that only the island's own body supplies, never the bare basis
alone); the two canary fixtures are two scripted-write cases over that one
document rather than two different fragments. The flagship `puck.world.json`
parses and validates too (see "Verified scope boundary" below) but stays
outside the canary fixtures above, which are scoped to the one document pair
both runtimes already cross-check.

## Verified scope boundary: no emulator core, no shader catalog, no probe kinds

`WorldDefinitionValidator` reads four injection seams before any document
parses — `WorldExtensionVocabularyHook.PostRenderExtensionCheck`,
`.ScreenMachineEngineCheck`, `.ScreenMachineCartridgeCheck`, and
`WorldProbeVocabularyHook.ProbeKindCheck` — normally wired by a composition
root's module initializer (`Puck.World.Client.WorldSchemaVocabularyHooks` for
the desktop client) to a real catalog answering `true`/`false`. Each is REQUIRED
(never absent-tolerant: an uninstalled hook throws), but answers `bool?` —
`true`/`false` from a host with a real catalog, or `null` from a host that
carries no catalog for that vocabulary AT ALL, which the validator routes to
its `deferred` collection rather than a refusal. `Engine/BrowserExtensionVocabulary.cs`
installs its own module initializer answering every one of those `null`: this
build carries no emulator core, shipped shader catalog, or probe-kind catalog
of its own (Architecture.props' exact-closure profile denies
`Puck.World.Protocol` and every extension-owning assembly), so "this host has
no catalog to check against" is the honest answer — distinct from a real
catalog's `false` refusal, and never a stub that would silently admit a
document naming a capability this engine cannot run.

**Verified consequence**: `Parse()`/`ComposeTree()` on the composed flagship
island (`puck.world.json` over `standard.basis.json`) succeeds, its `deferred[]`
naming every one of the three real GamingBrick console screens
`modules/arcade.world.json` (one of the island's sixteen imports) authors —
`screens[0].source.machine.engine: screen-machine engine 'gaming-brick'
registration deferred — this host carries no screen-machine engine catalog.`
(and two more, for `advanced-gaming-brick` and a second `gaming-brick` screen).
This is confirmed, expected behavior, not a defect:
`BrowserEngineTests.Parse_composed_puck_world_defers_its_unregistered_machine_engines`
pins it. A district or game fragment that authors no `screens[].source.machine`
row (most of the catalog) parses and compiles cleanly with no deferral at all.

**`Judge()` on the composed flagship island judges hostlessly, honestly.**
`BrowserSession`'s rule reader (`Engine/BrowserRuleReader.cs`) wraps a
`Puck.State.FrameHost` for every state read and write, and itself widens to
`IWorldRuleReader` (`Puck.World.Schema/IWorldRuleReader.cs`) — the world's
sixteen operand facts (`PhysicsQuiescentOperand`, `RegionOccupancyOperand`,
`ArgBodyOperand`, …) plus the two body-reference resolutions
`Puck.World.Server.WorldServer` answers from real bodies, machines, a clock,
and adjacencies. This engine ships none of those, so `BrowserRuleReader`
answers each one the honest vacuous fact a world with no bodies, no machines,
no clock, and no adjacencies gives — the same convention each
`WorldRuleFacts` prefix's own remarks and `WorldServer.RuleHost.cs`'s "no such
body"/"no such machine" reads already commit to for an absent host, applied
here for "there is no host at all": population `0`, physics vacuously
quiescent, no region occupants, no machine byte, no argmax/argmin/nearest
body (`-1`), the engine's largest representable distance between two bodies
that do not exist, no line of sight, never parked, perfectly upright, a link
never established, a zero channel, and no navigation state.
`PlacementInfluenceOperand` alone reads `RuleFact.Absent` — an unrepresented
influence provider is unknowable, never a falsely safe zero, exactly as
`WorldServer.Influence.cs` already answers it for the one real case that
reads `Absent` today. `puck.world.json`'s own `dive`/`kart`/`jump` modules
author rules reading these facts, so every such hostless read is recorded
onto the judged tick's own trace as `hostFacts[]`
(`{rule, operand, answer}`) — how an author sees which rules lean on a fact
this engine cannot supply from a real host, without the
`Arg_InvalidCastException` a bare `FrameHost` used to throw the moment such a
rule evaluated. `BrowserHostlessIslandTests` judges the composed island over
ticks 1-3 and pins both that no such throw occurs and that `hostFacts[]`
names a physics/body operand; `engine-timing.test.cjs` and
`engine-wasm.test.cjs` judge the composed island itself now, not a tictactoe
stand-in — only `BrowserParityRecordingTests`/`engine-wasm.test.cjs`'s own
determinism-hash fixtures still use `games/tictactoe.world.json`, for their
own fixed-baseline reason, unrelated to this boundary.

## Trim baseline

`PublishTrimmed=true`/`TrimMode=partial`, with `TrimmerRootAssembly` keeping
`Puck.Assets`, `Puck.World.Authoring`, and `Puck.World.Schema` whole (their
own `IsAotCompatible=false` reflection surface — schema/name-registry
reflection, the `puck.creation.v1` embedded-document converter, `WorldDocumentBasis`'s
`JsonNode` diff/merge logic — stays real and reachable rather than being
guessed at member-by-member). ILLink's trim ANALYSIS still runs over that IL
regardless of the root/copy action (rooting only stops removal, not
analysis), producing a recorded baseline of **31 warnings** with `NoWarn`
cleared (`IL2026 x 26`, `IL2070 x 4`, `IL2075 x 1`), suppressed in this
project's own `<NoWarn>` — none of the flagged call sites are reachable from
this project's own `Engine`/`Exports` surface today. Recorded once here as
the number to notice growing, not a target to shrink: a rise means new
reflection-dependent code became reachable from this project, worth a second
look before accepting.

`JsonSerializerIsReflectionEnabledByDefault` is forced back to `true` for the
same reason: a trimmed publish defaults that feature switch off, and
`System.Text.Json.JsonSerializer`'s generic `Serialize<T>`/`Deserialize<T>(…,
JsonSerializerOptions)` overloads (never the `JsonTypeInfo<T>` ones this
project's own `Engine` code uses) throw `NotSupportedException
("JsonSerializerIsReflectionDisabled")` at the first fragment composition —
verified against the real AppBundle under Node before this switch was added
(`WorldDocumentBasis`'s `JsonArray.Add` merge/diff logic hits it immediately).
Cost: `System.Text.Json.wasm` grows from 327,445 to 425,237 bytes (+30%,
+96 KiB) — the AppBundle total moved from ~12.0 MiB to ~12.4 MiB for this one
switch.

## Performance

`src/Puck.Dashboard/src/portal/tests/engine-timing.test.cjs` times every call
an editing loop makes against this AppBundle under Node, over the shipped
island (`puck.world.json` + `standard.basis.json` + all 16 imports,
`~2.4 MB` of source JSON, composing to a `~428 KB` standalone document);
`tests/Puck.World.Browser.Tests/EngineTimingTests.cs` times the identical
calls natively (JIT, no wasm interpreter) for comparison. Medians of three
runs each, this machine:

| Call | wasm interpreter (Node) | native (JIT), cold | native (JIT), warm |
|---|---:|---:|---:|
| `Parse` (tictactoe under basis) | 1,235 ms | 1,135 ms | 11 ms |
| `ComposeTree` (full island) | 12,733 ms | 2,605 ms | 1,996 ms |
| `Compile` (composed island) | 9,911 ms | 1,683 ms | 1,710 ms |
| `Judge` (tick 1, tictactoe) | 35 ms | — | — |
| `StateHash` (tictactoe) | 1 ms | — | — |
| `Cells` (chessBoard, 64 cells) | 7 ms | — | — |

Two things follow from the native cold/warm split. `Parse`'s huge cold-run
cost (1,135 ms) is almost entirely one-time JIT/static-init — it drops to
11 ms once the same process has already paid that cost once. `ComposeTree`
and `Compile` do **not** drop the same way (2,605 ms → 1,996 ms; 1,683 ms →
1,710 ms) — that time is real per-call work, not warm-up, and it lives in
`Puck.World.Schema` (the `WorldDefinitionFileSource`/`WorldDocumentBasis`
`JsonNode` tree merge `BrowserComposer.ComposeTree` calls into, and the
parse/migrate/validate/rule-compile pipeline `BrowserParser.TryParseAndValidate`
runs three times over the same ~428 KB document across one `ComposeTree` +
one `Compile` call) and `Puck.State` (`RuleEvaluator`'s own rule compilation) —
outside this project's own `Engine`/`Exports` boundary. The wasm interpreter
is roughly 5-6x slower than native-warm on both (`ComposeTree` 12,733 ms vs
1,996 ms; `Compile` 9,911 ms vs 1,710 ms), consistent with Mono's
interpreter-tier overhead relative to JIT, not a wasm-specific pathology.

**`RunAOTCompilation` landed.** With it on (`WasmStripILAfterAOT` at its SDK
default of `true` — see "AppBundle" above for why it cannot strip the three
rooted assemblies), the wasm interpreter numbers above become:

| Call | AOT (Node) | vs. interpreter |
|---|---:|---:|
| `Parse` (tictactoe under basis) | 753 ms | 1.6x |
| `ComposeTree` (full island) | 2,347 ms | 5.4x |
| `Compile` (composed island) | 1,969 ms | 5.0x |
| `Judge` (tick 1, tictactoe) | 17 ms | 2.1x |
| `StateHash` (tictactoe) | 2 ms | ~1x |
| `Cells` (chessBoard, 64 cells) | 5 ms | 1.5x |

`ComposeTree` + `Compile` together — the pair an edit-and-preview cycle pays —
drop from ~22.6 s to ~4.3 s. Every existing test and harness in this tree
(`dotnet test tests/Puck.World.Browser.Tests`, `engine-wasm.test.cjs`,
`native.test.cjs`, `engineBoot.test.cjs`) passes unchanged against the AOT
AppBundle, including the parity-hash assertions — `BrowserParityRecordingTests`'
own `Fixtures/browser-parity/expected.json` needed no re-record. Cost: the
AppBundle grows 3.4x, 12.02 MiB → 40.48 MiB (see "AppBundle" above) — over
this package's own "~40 MB uncompressed" guidance on a decimal-MB reading
(42.44 MB), inside it on the binary-MiB reading this README uses everywhere
else. Landed on the strength of the win and the ~1.2% MiB margin; a stricter
reading of that ceiling would argue for reverting this switch instead.

`WasmEnableSIMD=true` was tried alongside AOT and produced a byte-identical
`dotnet.native.wasm` to the non-SIMD AOT build — the SDK's own incremental
AOT cache treated the two runs as equivalent and never re-ran the AOT
compiler, so this switch is untested in isolation here, not measured at zero
effect; a clean rebuild (`rm -rf obj bin` first) would be needed to test it
honestly. The bottleneck this section exists to explain — `JsonNode` tree
merging and rule-table interpretation, not numeric vector loops — makes SIMD
an unlikely further win regardless.

There is no separate interpreter-tiering/PGO MSBuild property in this SDK
(`Microsoft.NET.Runtime.WebAssembly.Sdk/10.0.11`) — `RunAOTCompilation` is the
only lever `BrowserWasmApp.targets`/`WasmApp.Common.targets` expose for
interpreter speed. `EventSourceSupport` already defaults to `false` for a
browser-wasm publish (the SDK's own default, unrelated to this project's
`<NoWarn>`); `DebuggerSupport`/`UseSystemResourceKeys` are general SDK
feature switches over diagnostics infrastructure and exception-message
strings, neither of which sits in `ComposeTree`/`Compile`'s own call graph —
not tried, on that reading of the SDK source rather than a measurement.

`ComposeTree` and `Compile` still cost ~2 s each even AOT'd and native-warm
(see the cold/warm table above) — real per-call work in `Puck.World.Schema`'s
`JsonNode` tree merge and `Puck.State`'s rule compilation, outside this
project's own boundary. `BrowserExports.Compile` also re-runs
`BrowserParser.TryParseAndValidate` over the same composed bytes `ComposeTree`
already validated once — the studio's own two-call convention (`composeTree()`
then `compile()`), not a defect this project can unilaterally change without
reshaping that contract.

## mimalloc

`Directory.Build.props`' win-x64/linux-x64 mimalloc `None` items are
conditioned off `'$(RuntimeIdentifier)' != 'browser-wasm'`, but a referenced
class library (`Puck.Assets`, `Puck.Maths`, …) never sets a `RuntimeIdentifier`
of its own — it evaluates that condition as `'' != 'browser-wasm'` (true) and
still copies the native binaries into its **own** output, which then flows
into this project's `bin`/`publish` directories through ordinary
cross-project `CopyToOutputDirectory` propagation. This project's own
`PuckRemoveNativeMimallocFromWasmOutput` target deletes the stray copies
after every `Build`/`Publish` rather than intercepting the flow at each
dependency's own item list. None of this ever reaches the AppBundle itself
(which only ever collects `_framework/*.wasm`) — `mimalloc.dll`/
`mimalloc-redirect.dll`/`libmimalloc.so` are litter in the sibling output
folders, never part of the shipped payload.
