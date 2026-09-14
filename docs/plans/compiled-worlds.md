# Compiled worlds

A world document is small, but a world is expensive to bring up. Before its
first tick, Puck composes the document, resolves draws, validates it, and then
derives collision fields, navigation bakes, topologies, compiled rules,
cartridge ROMs, font atlases, and a scene program from it. Every way a world
comes up repeats that work from JSON: desktop boot, `world.instance.start`, the
silo, checkpoint restore, rewind, and the replay drive. A checkpoint stores the
definition's JSON and none of what was derived from it.

A compiled world is a binary file that holds those derived products beside
the definition they came from. The same derivation code writes it, loading it
skips each step whose inputs are unchanged, and a product loaded from it is
bit-identical to one derived fresh. This plan names what a world derives, what
can and cannot be stored, the refactors the format depends on, and the order
the work should land in.

"Compiled world" and the `.pwc` extension are working names. "World image"
already means the Azure container image and the SDF engine's rendered frame, so
this plan avoids it.

## Implementation status

Reviewed against `1e4a2f806`. Nothing has landed. The inventory below was read
from the code at that commit; no step was timed. A separate boot profile is
being measured, and the stage order at the end waits on it: the inventory says
what can be stored, the profile says what is worth storing first.

## What a world derives before its first tick

The tables group derivation steps by subsystem. "Plain" means the product is
arrays, structs, or immutable records with no delegates, closures, live object
references, or native handles, so it can be written as data today.

### Document

| Step | Where | Product | Plain | Notes |
|---|---|---|---|---|
| Compose basis and imports | `WorldDefinitionFileSource.TryComposeLayers` | Composed JSON | Yes | The only cache that outlives a boot, keyed by path and catalog fingerprint, re-parsed on every hit, and gone when the process exits. |
| Lower `.puck` | `PuckWorldLoader`, `WorldDocumentEmitter` | Composed JSON | Yes | Serialized to a string twice before `WorldDefinitionLoader.TryLoad` decodes it again. |
| Parse and migrate | `WorldDefinitionSerialization`, `WorldDefinitionMigrations` | `WorldDefinition` | Yes | The schema is open: `WorldJsonVocabulary.Extend` adds polymorphic arms at runtime. |
| Draws and state references | `WorldDrawBootResolver`, `WorldStateDocumentValues` | `WorldDefinition` with drawn cells | Yes | Keyed by **instance identity** as well as `generation.worldSeed`. |
| Validate | `WorldDefinitionValidator.ValidateCore` | Verdict | — | Runs five times per desktop boot, and each run recompiles rules, search plans, bindings, and flock affinities and reloads table and music files. |

### Simulation space

| Step | Where | Product | Plain | Rebuilt on |
|---|---|---|---|---|
| Placement frames | `WorldPlacementFrameCompilation` | Frame map | Yes | New placements section |
| Placement spatial index | `WorldPlacementSpatial` | `WorldSpatialQueryIndex` | Yes | New placements section |
| Face catalog | `WorldFaceCatalog` | Face rows | Yes | New definition |
| Distribution offsets | `WorldPlacement` | `FixedVector3` lists | Yes | Every caller, uncached, about three times per boot |
| Analytic colliders | `WorldColliderSet` | `FixedStaticCollider[]` | Yes, except attached-row references | Population rebuild |
| Server solid field | `WorldSolidField.TryBuild` | `SdfProgram` words, `SdfFieldEvaluator`, lazily filled `SdfDistanceGrid` | Yes | Solid-affecting mutation, built twice per mutation; census `SolidBakeHash` is already a key |
| Navigation bake | `NavigationRuntime.EnsureBaked` | Ground, walkable, and edge arrays | Yes | Lazy on first route |
| Kits, motion, curves, gravity, flock grids | `WorldPopulation.CompileFixedTables` | Fixed-point tables | Yes | Population rebuild, not incremental |
| Topologies | `TopologyCompilation`, `WorldTopologyCompilation` | `CompiledTopology` | Yes | Lazy |
| Field program | `WorldFieldProgramCompilation` | `WorldFieldProgram` | Yes | New state section shape |
| Engagement, grants, input hold, music graph | `WorldServer` constructor | Runtime tables | Mostly | Constructor or every install |

### Rules, machines, and hosting

| Step | Where | Product | Plain | Notes |
|---|---|---|---|---|
| State catalog | `StateCatalog` | Descriptors and handles | Descriptors only | Each `StateHandle` holds a catalog-identity reference. |
| Rules, interactions, patterns, search, decisions | `WorldServer.RecompileRules` | `CompiledWorldRule[]` and plans | No | Operands and effects are open `OperandFact`/`EffectFact` subclass hierarchies; the search runtime and flock affinities hold delegates. Compiled at least six times per boot. |
| Pinned tables | `CompiledTable` | `long[]` keys and columns | Yes | Table files are read at least seven times per boot. |
| Channel tables | `WorldChannel` | Fixed arrays | Yes | Compiled independently by about seven consumers. |
| Cartridge ROMs | `CartridgeContentProvider`, `HgbCartridgeCompiler`, `AgbCartridgeCompiler` | ROM bytes, source hash, symbols | Yes | No cache by content hash. |
| Addon modules | `WasmModuleLoader` | Wasmtime `Module` | Native | Cached per `AddonHost` in memory only. |
| Seat binding profiles | `WorldSeatBindings`, `BindingProfile` | Compiled profiles | Unverified | Overlay gates read live state. |

### Presentation

| Step | Where | Product | Plain | Notes |
|---|---|---|---|---|
| Font atlas catalog | `WorldTextCatalog`, `ManagedFontAtlasGenerator` | Packed metrics and RGBA atlas | Yes | Full MTSDF generation from font files on every boot and on `world.load`/`world.reload`; no disk cache. |
| Capacity probe | `SdfCompositionFrameSource` constructor | Word, instance, and transform capacities | Yes | Frozen at boot. |
| Static scene | `WorldStaticSceneEmit`, `WorldPlacementStamper.EmitStatic` | Builder instruction streams | Yes | Merged with live bodies into one program, so a population change rebuilds all of it. Uses float trigonometry. |
| Client static field | `WorldFramePresenter.RebuildStaticField` | `SdfFieldEvaluator` | Yes | Rebuilt on every live program change. |
| Camera rigs | `WorldCameraRigCompiler` | `SdfCameraProgramSet` | Yes | Uncached for View and session screens. |
| Voice patches and tune ROMs | `WorldVoicePatchFactory`, `TuneRom.Build` | `VoicePatch` records, ROM bytes | Yes | Patch files are re-read on every audio reconcile. |
| Overlay glyph pack | `OverlayGlyphSdfPack` | POGP cache on disk | Yes | Already persisted; hashes the full atlas PNG on every boot. |
| Shader pipelines | `ShaderCompiler` | SPIR-V and DXIL | Yes | Already persisted under the state root. |

### What stays out

Some products are live or not a function of stored inputs, and a compiled
world never holds them:

- GPU objects: engines, pipelines, uploads, storage images. They are per device.
- Live runtimes: machine instances, mounted addon instances after `puck_init`,
  and the command registry with its handler delegates.
- Adjacency projections and neighbour solids, which the code documents as a
  nondeterministic boundary.
- The live scene program, field lattice bricks, and anything else that folds
  in population or field values.

## The file

A compiled world is a chunk container in the shape `PbakBundle` already uses
for cartridge bundles: a header, then a table of chunks, each a four-character
code, a length, a content hash, and a payload. Integers are little-endian,
payloads are padded to 8-byte alignment so a blittable array can be read as a
span in place, and variable-length integers use the canonical form in
`AutomaticSequenceCodec.cs`, whose reader already refuses non-minimal
encodings and trailing bytes. That writer and reader are internal to
`Puck.Assets` today; they move to a shared public home rather than gaining a
second implementation.

### Keys

The header records what the whole file was compiled against:

- format version;
- engine build identity, because a derivation's code is part of its input;
- machine catalog fingerprint, which already covers the `extensions` directory;
- the definition hash, taken over the canonical JSON bytes so the forty or so
  existing `sha256-64` pins in checkpoints, replay, escrow, and release
  manifests keep their meaning;
- instance identity, for the drawn definition.

Each chunk then records the derivation that produced it, that derivation's
version, and the hashes of the inputs it read beyond the definition: asset
files, neighbour documents, and, for float products, the CPU identity. A
loader that finds a mismatched chunk derives that product fresh and keeps the
rest. A file with a mismatched header is not a compiled world for this build
and is ignored whole. Neither case repairs or adapts data.

### Chunks

| Code | Holds | Depends on |
|---|---|---|
| `DEFN` | The drawn, resolved definition | Header only |
| `ASST` | Content hashes of every asset file read | Asset files |
| `PLCE` | Placement frames, spatial index, distribution offsets | `DEFN` |
| `SOLD` | Server solid program, evaluator, and a fully baked distance grid | `DEFN`, kits |
| `NAVB` | Navigation bake arrays per domain | `SOLD` |
| `TOPO` | Compiled topologies | `DEFN` |
| `POPL` | Kits, motion, curves, gravity, colliders, channels | `DEFN` |
| `TBLS` | Compiled tables | Table files |
| `RULE` | Compiled rules, patterns, search plans | `DEFN`, `TBLS` |
| `ROMS` | Cartridge ROMs, source hashes, symbols | Cartridge sources |
| `WASM` | Serialized Wasmtime modules | Module bytes, Wasmtime build, CPU |
| `FONT` | Packed font atlas catalog | Font files |
| `SCNE` | Capacity probe, static scene streams, client static field | `DEFN`, `FONT` |
| `AUDI` | Voice patches, tune ROMs | Patch and tune files |

`DEFN` is compact canonical JSON in the first version. The 61 hand-written
`Utf8JsonReader` converters and the runtime-extended schema make a binary
definition encoding a rewrite of its own, and parsing is the smallest cost in
the inventory. If the profile shows deserialization matters once everything
else is loaded from chunks, a source-generated binary reader for
`WorldDefinition` replaces this chunk's encoding and nothing else.

### Where it is written

`puck compile` writes a compiled world beside the JSON it writes for a world
document, and `build/WorldAssets.targets` produces them for shipped worlds the
way it produces their JSON. The runtime writes one into the state root on a
miss, keyed by the header, so JSON-authored and hand-edited worlds benefit on
their second boot. Checkpoints, replay tapes, and instance starts reference the
compiled world by its header hash, so restore, rewind, and replay load products
instead of re-deriving them.

## Refactors the format depends on

A file only helps if the code has one place to hand it to. The inventory shows
several places where it does not yet.

1. **Validate once.** A load should produce one validation receipt that the
   server constructor, the machine host, and post-build wiring accept instead
   of validating again. This is valuable without any file format and is the
   first stage. Mutation and reload already reuse the exact definition's
   receipt; [State consolidation](state-consolidation.md#further-compilation-reuse-and-verification)
   records that extending it to boot and restore needs explicit ownership,
   because catalog shape alone is not a safe key.
2. **Give caches content keys.** Almost every in-process cache is a
   `ConditionalWeakTable` or `ReferenceEquals` check on a section or row
   instance. A definition loaded from a file has fresh instances and misses all
   of them. The loader must seed those caches, or the caches move to content
   keys, before chunks can feed them.
3. **Store rule operands by ordinal.** Serializing `RULE` needs `StateHandle`
   minted against a loaded catalog from an ordinal, and a closed tag table for
   `OperandFact` and `EffectFact` subclasses that each vocabulary registers.
   [State addressing on the tick path](state-addressing.md) and the common
   compiled value source in [State consolidation](state-consolidation.md)
   touch the same types and should settle their shape first.
4. **Split the scene program.** The static scene has to be its own stream
   before it can be stored, and the population-driven part rebuilt alone.
   Moving static stamping to the fixed-point path the collision field already
   uses would make `SCNE` bit-portable instead of keyed by CPU.
5. **Remove duplicate work.** Tables compiled twice in the constructor,
   channels compiled per consumer, distribution offsets computed three times,
   and solids built twice per mutation cost the same whether or not a compiled
   world exists. Fix them where they sit.

## Stages

Each stage lands with its gate. The order of stages 3 to 6 follows the boot
profile.

1. **One validated load.** Completion: a desktop boot, instance start, and
   checkpoint restore each run `ValidateCore` once and compile rules once, with
   state hashes unchanged.
2. **Container, header, `DEFN`, `ASST`.** Completion: `puck compile` and the
   runtime cache write compiled worlds; `Puck.World` boots every shipped world
   from one; a mismatched header is ignored and a mismatched chunk is re-derived.
3. **Simulation chunks** (`PLCE`, `SOLD`, `NAVB`, `TOPO`, `POPL`, `TBLS`),
   after content-keyed caches.
4. **Rules** (`RULE`), after ordinal operands and closed fact tags.
5. **Machines and addons** (`ROMS`, `WASM`). Confirm that the Wasmtime 44
   binding exposes module serialization before scheduling `WASM`.
6. **Presentation** (`FONT`, `SCNE`, `AUDI`), after the scene split.
7. **Checkpoint, replay, and instance references.** Completion: restore,
   rewind, and replay drive load products from the compiled world their
   checkpoint names.

## Verification

Loading must never change what a world does. For every shipped world, a law
test derives each product fresh and loads it from a compiled world, and
compares the two byte for byte. `puck landing` gains a canary that boots a
shipped world from JSON and from its compiled world and compares `stateHash`
at a fixed tick, and `puck parity` runs its captures from compiled worlds on
both backends. A deliberate change to a derivation moves the chunk version and
re-records the compiled worlds in the same change.

## Open decisions

- **What ships.** Whether shipped worlds carry compiled worlds as build output
  beside their JSON, or the runtime cache alone produces them.
- **Trust.** A compiled world lets boot skip validation because the compiler
  validated under the same build and catalog. Worlds received from peers or
  storage may need to validate anyway.
- **Draws.** A compiled world is specific to one instance identity. Spawned
  instances could share every chunk that does not read drawn cells if chunks
  declare that dependency, or re-derive from `DEFN` onward.
- **Cartridge documents.** `puck.cartridge.v1` compiles to a ROM already;
  whether it also gets a compiled form outside a world is not decided.
