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
dotnet publish src/Puck.World.Browser -c Release -r browser-wasm
```

Both commands fail on this machine unless the `C_INCLUDE_PATH` environment
variable naming a Cosmocc toolchain's include directory is unset first — that
path leaks into every `clang`/emscripten invocation this build shells out to
and collides with emscripten's own libc headers (`COSMOPOLITAN_C_START_`
redefinitions, `bool32` unknown type). This is host machine contamination, not
a repository or project setting; `unset C_INCLUDE_PATH` before either command
above.

## AppBundle — real layout, recorded from an actual publish

`dotnet publish -r browser-wasm` writes `bin/Release/net10.0/browser-wasm/AppBundle/`:

```
AppBundle/
  Puck.World.Browser.runtimeconfig.json     2,421 B
  main.mjs                                  1,430 B
  package.json                                 19 B
  .stamp                                        0 B
  _framework/
    dotnet.js                                37,898 B
    dotnet.js.map                            51,818 B
    dotnet.boot.js                           15,339 B
    dotnet.native.js                        225,371 B
    dotnet.native.js.symbols                191,724 B
    dotnet.native.wasm                    1,523,626 B
    dotnet.runtime.js                       198,480 B
    dotnet.runtime.js.map                   276,757 B
    Puck.World.Browser.wasm                 161,557 B
    Puck.World.Schema.wasm                4,917,513 B   <- the document model + validator
    Puck.World.Authoring.wasm               173,317 B
    Puck.State.wasm                         557,317 B
    Puck.Assets.wasm                         97,029 B
    Puck.Attestation.wasm                    61,189 B
    Puck.Commands.wasm                      110,853 B
    Puck.Physics.wasm                        65,285 B
    Puck.SignedDistance.wasm                 25,349 B
    Puck.Text.wasm                           34,053 B
    Puck.Hosting.wasm                         4,869 B
    Puck.Abstractions.wasm                    7,429 B
    Puck.Maths.wasm                         218,373 B
    System.Private.CoreLib.wasm           1,711,381 B
    System.Text.Json.wasm                   425,237 B
    System.Private.Xml.wasm                 471,829 B
    (+ ~30 more BCL assemblies and 14 System.CommandLine resource satellites)
```

**Total AppBundle size: 12,600,621 bytes (12.02 MiB), uncompressed.** No
mimalloc native binary lands in the AppBundle (see "mimalloc" below) — the
`_framework/*.wasm` set above is the complete network payload. `System.CommandLine`
and its 14 locale resource satellites (~117 KiB combined) ride in only because
`Puck.Cli`'s own document-loading code is reachable from `Puck.World.Schema`
through `WorldAssetRowLoader`/canonicalizers shared with the CLI; nothing in
this project's own `Engine`/`Exports` calls into `System.CommandLine` itself.

`Puck.World.Schema.wasm` (4.8 MiB, 39% of the payload) dominates because
`TrimmerRootAssembly` keeps it — and `Puck.World.Authoring`/`Puck.Assets` —
whole (see "Trim baseline" below): trimming is disabled for exactly the three
assemblies whose reflection this document model depends on, so their full IL
ships rather than a analyzer-verified subset.

### Cold-start time

Loading `main.mjs` under Node and calling `createEngine()` to a ready
`[JSExport]` surface: **~150-250 ms** on this machine (measured via the Node
harness's own per-test timings; a browser tab's first load also pays one-time
`WebAssembly.compile` cost the Node CLI's JIT-adjacent `node.exe` build may
warm differently). `Version()` itself (a call into an already-booted engine)
answers in single-digit milliseconds.

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
