# Puck project map

This map describes the current responsibility and dependency boundary of each
project. See [agent-guide.md](agent-guide.md) for verification procedures.

## Dependency rules

1. All production code lives in split `Puck.*` projects. The former
   `src/Puck` and `src/Puck.Avatars` monoliths are not part of the repository.
2. GPU API types remain inside their backend projects. Shared code depends on
   the neutral contracts in `Puck.Abstractions`.
3. `Puck.Launcher` is backend- and platform-neutral. A composition root
   registers windowing, presenters, and content.
4. `Puck.HumbleGamingBrick` and `Puck.AdvancedGamingBrick` split internally: the
   core emulator depends only on leaf contract/data projects (`Puck.Maths` for
   deterministic numerics, `Puck.Snapshots` for the shared state-serialization
   substrate) — never on shared substrate, backends, or composition roots. Each
   project's `Hosting/` folder carries its screen-machine engine adapter
   (`GamingBrickEngine`/`AdvancedGamingBrickEngine`,
   `MachineHost`/`AdvancedMachineHost`) — the one place the project touches
   shared substrate, bridging to `Puck.Hosting`'s `QueuedMachineWorker` and the
   neutral screen-machine contracts in `Puck.Abstractions`. `Puck.World`
   consumes both cores through its `Hosting/` adapters or through
   composition-root debug hosts.
5. `Puck.World` is the composition root. It defines no shared engine contracts.
6. Every project declares its own `<PuckKind>` and, if that kind is ranked, its
   `<PuckLayer>`. Those declarations are authoritative and the build enforces
   them: see [the architecture gate](#the-architecture-gate) below.

## Stability

- **Stable:** the Vulkan and Direct3D 12 backend implementations,
  `Puck.Maths`, and `Puck.Assets`.
- **Mostly settled:** the neutral GPU and hosting seams, command snapshots,
  input transport architecture, the SDF instruction contract, and the run
  document validation funnel. Changes require contract-level verification.
- **Fluid:** `Puck.World`, document additions, authoring tools, and emulator
  integration. Treat these as consumers, not architectural precedent.

## Layering

This block is GENERATED — `puck architecture --map` prints it from each
project's own `<PuckLayer>`/`<PuckKind>` declaration. Do not hand-edit it;
change the declaration and regenerate. The direction is the point: a
hand-written copy is a second source that drifts silently, and this one had
— it was missing three projects and still carried a row for one that had
been quarantined out of the repository.

```text
Composition roots        Puck.SdfVm.Bench  Puck.World
Validation               Puck.AdvancedGamingBrick.Post
                         Puck.HumbleGamingBrick.Post
Engine services          Puck.AdvancedGamingBrick  Puck.Forge
                         Puck.HumbleGamingBrick  Puck.Launcher  Puck.Overlays
                         Puck.Recording  Puck.SdfVm  Puck.Text  Puck.World.Data
                         Puck.World.Server
Presentation             Puck.DirectX.Presentation  Puck.Vulkan.Presentation
Backends                 Puck.DirectX  Puck.Vulkan
Shared substrate         Puck.Commands  Puck.Hosting  Puck.Input  Puck.Platform
                         Puck.Scripting  Puck.Scripting.Simulation
                         Puck.Shaders
Leaf contracts and data  Puck.Abstractions  Puck.Assets  Puck.Maths
                         Puck.Snapshots  Puck.Storage
(Test)                   Puck.Analyzers.Tests  Puck.Maths.Tests
                         Puck.World.Tests
(Tool)                   Puck.Carriage  Puck.Cli
(Analyzer)               Puck.Analyzers
```

The parenthesised rows are TERMINAL KINDS: they consume the tree and are never
consumed by it, so they carry no layer and sit above every row by construction.
A terminal-to-terminal edge is fine — `Puck.Analyzers.Tests` references
`Puck.Analyzers` as an ordinary assembly because it instantiates the analyzer
and drives it over compilations it builds itself.

Dependencies normally point downward. A same-row dependency is acceptable
when it does not introduce a backend or composition-root dependency. Each
GamingBrick project's `Hosting/` folder is the deliberate internal exception:
it is the one place the core emulator's project touches shared substrate,
bridging to `Puck.Hosting` and the neutral screen-machine contracts in
`Puck.Abstractions`.

### The architecture gate

The rules above are ENFORCED, not described. `build/Puck.Architecture.targets`
runs in every in-scope project's build immediately before `CoreCompile`, over
the RESOLVED reference set rather than the declared one — so an edge that
arrives transitively is checked exactly like one written in the csproj. Policy
lives in [build/Architecture.props](../build/Architecture.props), which carries
the reasoning beside each rule; `puck architecture` is the report surface.

The resolved set is what makes it worth having. `Puck.Launcher.csproj` names
three projects and resolves four: `Puck.Commands` arrives through `Puck.Hosting`
and `Puck.Input`, so a reviewer reading project files clears Launcher and is
wrong.

Quarantined trees are outside the gate, because quarantine means ungated.
`experimental/` is excluded by a `Directory.Build.props`/`.targets` firewall
pair in each quarantined tree rather than by a path filter here — a filter that
happens to exclude the right directories reads identically to one that excludes
the wrong ones.

| Code | Rule |
|---|---|
| `PUCKARCH001` | An upward edge: a dependency pointing at a higher row. |
| `PUCKARCH002` | The backend quarantine: only the Presentation row, composition roots, terminal kinds that introduce nothing, and named exceptions may hold a `Backends` assembly in closure. `Puck.Post` is the one named exception. |
| `PUCKARCH003` | A ranked project holding a terminal-kind project in its closure. |
| `PUCKARCH004` | A lane profile whose closure no longer EQUALS its declaration — removals are as visible as additions. |
| `PUCKARCH005` | A missing, unknown, or contradictory `<PuckKind>`/`<PuckLayer>` declaration. |

## Contracts and data

| Project | Responsibility |
|---|---|
| `Puck.Abstractions` | Backend-neutral GPU, presentation, capture, timing, machine, lighting, and window contracts. It has no Puck dependencies and exposes no platform-native types. |
| `Puck.Maths` | Deterministic fixed-point numerics, world coordinates, vectors, and integer algorithms used by authoritative simulation — including the exact-algebra wing: finite fields, primality, presented algebra, and Reed–Solomon coding over `BinaryField<T>`. |
| `Puck.Assets` | Content-addressed byte sources, hashes, and a fixed-capacity LRU cache. It identifies and moves bytes; it does not decode them. |
| `Puck.Storage` | The Azure object-blob store behind owned-world cloud sync: a routed byte store with a version-token seam (opaque read token + optional if-match write) for conditional overwrites, and an address projection for the platform's edge namespace versus a raw dev/emulator account. |
| `Puck.Snapshots` | The shared deterministic state-serialization substrate both GamingBrick cores build snapshots on: `StateWriter`/`StateReader` (little-endian widths + `WriteBlock<T>` memcpy + `Reset` reuse), `SnapshotSection`, `ISnapshotable`, the flat `SnapshotImage`, and the `SnapshotDivergence` localizer. Per-core snapshot identity fields, component orders, and fingerprints stay in each core; the fingerprint primitive lives in `Puck.Maths`. |

## Shared substrate

| Project | Responsibility |
|---|---|
| `Puck.Hosting` | Recursive `IRenderNode` hosting, capability propagation, terminal ownership, fixed-step simulation context, frame timing, cross-thread publish buffers, and the machine-neutral queued-host substrate (`QueuedMachineWorker` + `IQueuedMachineCore` adapter: worker thread, bounded FIFO with backpressure, triple-buffer publication with the upload lease, native-frame-keyed save-flush debounce, and the vectorized framebuffer repack; `QueuedHostContractProbe` proves its observable contract for both cores' batteries). |
| `Puck.Commands` | Typed commands, deterministic per-tick `CommandSnapshot`s (ephemeral — built, applied, and dropped within one tick; the world tape in `Puck.World` is the one recording surface), console dispatch, binding profiles and sessions, feature switches, and intent sources. |
| `Puck.Input` | Controller discovery and protocols, hotplug, routing arbitration, HID parsing, IMU fusion, haptics, and LampArray bind legends. Platform transports are injected. |
| `Puck.Platform` | Win32 windowing, HID and controller transports, Media Foundation camera capture, Windows Graphics Capture feeds, the Media Foundation hardware video-encoder ladder (AV1→H.264) and WASAPI loopback/microphone capture sources behind `AddRecordingPlatform` (`Puck.Recording`'s Windows backend), and generated native interop. |
| `Puck.Shaders` | Compiled shader-bytecode loading, format detection, and validation. |
| `Puck.Scripting` | Deterministic, fuel-metered WASM addons: the neutral host, the module validator, the ABI, and the core's own declared-channel-name table decoder (`AddonChannelNameTableReader`, resolved through the injected `IAddonChannelResolver`). It references neither `Puck.Commands` nor `Puck.Input` — that absence is the point of the assembly split and is enforced by an exact-equality lane profile, not merely current. |
| `Puck.Scripting.Simulation` | The Simulation-lane adapter, and the one project permitted to hold the scripting core alongside the engine's command/input vocabularies: `AddonSimulationPump` — the crossing from a guest's decoded cells to typed, vocabulary-validated submissions (`AddonActSubmission`/`AddonQuerySubmission`/`AddonAskSubmission`; whole-batch refusal; authority stays the consumer's) — and `AddonSourceCatalog`, the `InputSources`-derived source-id vocabulary now consulted only by `Puck.World`'s binding-vocabulary check, since the wasm ABI's own input channel resolves declared channel names through `Puck.World`'s `IAddonChannelResolver` instead. |

## GPU backends and presentation

| Project | Responsibility |
|---|---|
| `Puck.Vulkan` | Vulkan bindings, device and resource factories, command recording, sharing, and synchronization. It contains no windowing or shader compiler. |
| `Puck.Vulkan.Presentation` | Vulkan presenter and compute-service adapters for the neutral hosting contracts. |
| `Puck.DirectX` | Direct3D 12 and DXGI device, resource, command, sharing, and synchronization implementation. |
| `Puck.DirectX.Presentation` | Direct3D 12 presenter and compute-service adapters. |

The backend-parity summary and table were deleted 2026-08-02: both reported a
per-capability parity status that `Puck.Post`'s quarantine made unverifiable.
Nothing measures cross-backend parity today.

## Engine services

| Project | Responsibility |
|---|---|
| `Puck.Launcher` | Generic application host: window loop, command pump, fixed-step accumulator, terminal control, genlock, and backend switching. Composition roots register platform and backend services. |
| `Puck.SdfVm` | SDF program model and builder, C#↔HLSL instruction contract, world renderer, frame sources, render assembly, debug tools, composition and anchor seams, camera views, and deterministic world queries. |
| `Puck.Text` | Font-atlas models, text layout, and deterministic coverage-to-distance atlas generation. It is render-agnostic. |
| `Puck.Overlays` | Backend-neutral screen-space overlay UI: the shared ASCII-95 glyph SDF pack (loaded through a prepacked artifact beside the atlas), design tokens, the packed-record frame builder (panels, rects, fixed-cell text, icon chips, per-record viewport clipping, counted overflow with a tail-reservation priority policy), the console/binding-bar/editor-HUD/toast writers, and the one `UnifiedOverlayNode` decorator (a single GPU-timestamped fullscreen pass) that runs identically on both backends. Surfaces are CPU writers; a new surface is a new writer, never a new node or shader. Depends only on neutral contracts — no producer library. |
| `Puck.Forge` | The forge: authored content as data, and the cartridges it compiles to. `Authoring/` holds the document families and the editor model every consumer shares — the `puck.creation.v1` model (`CreationDocument`, `CreatorIntent`, `AvatarPrimitive`), `puck.audio.v1` (`AudioDocument`) and `puck.synth.v1` (`SynthPatchDocument`), their one document-neutral canonicalize/hash core (`DocumentCanonicalizer`) and per-family adapters, canonical stamp geometry (`CreationGeometry`), bounded edit history (`EditHistory<T>`), grid-snap math (`GridSnap`), and the sculpt model (`SculptModel` with `SculptChain`/`ChainSolver` — the frame-rate creation editor: shapes, palette, hold-style timeline, and IK chains under a caller-supplied stamp budget; presentation-pure, no GPU types). The cartridge half compiles those documents down: `AudioDocumentCompiler` (a `puck.audio.v1` document into SM83 sound-driver data), the `Tune` cart builder/verifier (`TuneRom` compiles a tune document into a bootable Humble cart and boots it headlessly), and the internal SM83 game framework closure they require (kernel, manifest/linker, modules, cartridge assembler). The framework stays internal until a later forge lift publicizes more. The quarantined Demo's originals remain the read-only behavioral oracle — read at `experimental/Puck.Demo`, never built there — and the library carts byte-match them. Depends on `Puck.Assets` + `Puck.HumbleGamingBrick` + `Puck.SdfVm`. |
| `Puck.Recording` | Everything downstream of a captured frame, in one project. `Capture/` is the still-frame half: the `ICaptureSink` that writes PNGs, the frame-observer seam, the FNV-1a frame-hash observer, and a dependency-free PNG encoder (GPU readback occurs upstream) — the surface `Puck.SdfVm`, `Puck.Overlays` and the GamingBrick batteries write screenshots and per-frame hashes through. The rest is the `puck.recording.v1` moving-picture graph: frame source → data-defined overlay compositor → encoder ladder → hand-rolled Matroska/WebM muxer, plus the managed-Opus (Concentus) audio lane and the `RecordingSession` that implements the same `ICaptureSink`. It defines the recording document, muxer, overlays, and session; the Media Foundation video-encoder ladder and WASAPI audio sources are the platform backend. Depends only on `Puck.Abstractions`. |
| `Puck.HumbleGamingBrick` | Deterministic shared GB/GBC/AGB-costume SM83 machine (snapshots, forks, cartridges, link cable, PPU, APU, peripherals). Its `Hosting/` folder carries the thin adapter from the neutral screen-machine contract to the core over `Puck.Hosting`'s `QueuedMachineWorker`: an `IQueuedMachineCore` (pad mapping, KEY1-aware tick conversion, framebuffer, save persistence) plus the host shell and work-RAM peek. Inherits the substrate's queued/backpressure behavior. |
| `Puck.AdvancedGamingBrick` | Deterministic GBA-native ARM7TDMI machine (cycle-level bus, DMA, timers, PPU, APU, cartridges, snapshots, link cable). Its `Hosting/` folder carries the thin adapter from the neutral screen-machine contract to the core over `Puck.Hosting`'s `QueuedMachineWorker`: an `IQueuedMachineCore` (KEYINPUT mapping, exact tick conversion, framebuffer, save persistence, direct boot) plus optional explicit BIOS images and the host shell. |
| `Puck.World.Data` | The world DOCUMENT model and wire/tape PROTOCOL, physically split out of `Puck.World` (headless P1) so the shape the sim runs on cannot itself reach presentation: `WorldDefinition` (every section record, including `WorldHostDefaults` and the `identity` section an owned world carries), `WorldDefinitionValidator`, `WorldDefinitionSerialization`, and the whole `Protocol/` surface (`PlayerIntent`, `WorldCommand`/`WorldMutation`/`WorldGrant`/`WorldPrincipal`/`WorldSessionLever`, `SessionRequest`/`WorldSnapshot`/`WorldComposition`, `IServerLink`/`IClientSink`, `LoopbackTransport`, `WorldPrincipalMapping`). Denied `Puck.SdfVm`/`Puck.Overlays`/`Puck.Input`/every Presentation and Backend project/`Puck.World.Server` by an exact-equality lane profile — a member that would need one of those (the engine-default binding document, the live vocabulary check) crosses through `BindingVocabularyHook`, an injection seam the composition root wires with a module initializer, rather than a direct reference. |
| `Puck.World.Server` | The authoritative world runtime, physically split out of `Puck.World` alongside `Puck.World.Data`: `WorldServer` (the fold, the mutation journal, snapshot emission), `WorldGrants`/`WorldHandleTable`, `WorldPopulation`/`WorldBody` (the entity table and its per-tick advance), `WorldOwnedWorlds` (the owned-world identity catalog and its cross-document durable-state door), `WorldAddonRuntime` and its wire/receipt types, the collision/contact/solid-field support a body advances against, and the true-deterministic-replay codec (`WorldReplayTape`/`WorldReplaySnapshot`/`WorldReplayVerdict`/`WorldReplayRefusal`/`WorldReplayCodecException`). References `Puck.World.Data`, `Puck.Scripting.Simulation`, `Puck.Storage`, `Puck.Hosting`; same presentation denials as `Puck.World.Data`. |

## Composition roots and validation

| Project | Responsibility |
|---|---|
| `Puck.World` | Document-driven (`puck.world.def.v1`, four checked-in worlds, `--world`) network-shaped local multiplayer game host: fixed-point player state, a runtime mutation/journal/undo protocol vocabulary, principals + capability grants (addons included), per-player owned identity worlds (ordinary `puck.world.def.v1` documents carrying an `identity` section) with bindings layered onto the `Puck.Commands` stack, owned-world cloud sync through `Puck.Storage` (`storage.*` verbs), session write-back, native self-recording (`puck.recording.v1`, `--recording`, `capture.*` verbs), and SDF world rendering. Verify game behavior by running it. |
| `Puck.SdfVm.Bench` | A real GPU/CPU ceiling-measurement harness for `Puck.SdfVm`'s contributed dynamic geometry — boots its own Vulkan-or-Direct3D-12 window host (`Puck.Launcher` + `SdfWorldRenderBuilder`, the same generic assembly `Puck.World` composes) driving `Puck.SdfVm.Debug.SdfBenchScene`'s `DynamicMatrix` ladder to completion, then exits. A measurement tool, not a game. |
| `Puck.HumbleGamingBrick.Post` | Humble core conformance, determinism, reference-ROM, save, and cross-generation link battery. |
| `Puck.AdvancedGamingBrick.Post` | Advanced core conformance, determinism, commercial-ROM, link, co-simulation, and diagnostic tooling. |

## Experimental projects

`experimental/` holds `Puck.BareMetal` and the quarantined `Puck.Demo`,
`Puck.Post`, `tools/`, and both `scripts/` trees; the GamingBrick cores live in
`src/` alongside the rest of the split projects. **The whole directory is off
limits** by owner ruling (2026-08-02): no agent reads, edits, builds, runs, or
cites anything under it. No experimental tree is in `Puck.slnx`, in the root
build, or in the architecture gate's scope — each carries its own
`Directory.Build.props`/`.targets` firewall pair that stops MSBuild's upward
discovery at the tree, so the isolation is structural rather than a path filter
somewhere else.

| Project | Responsibility |
|---|---|
| `Puck.BareMetal` | Freestanding Native AOT runtime, UEFI kernels, native experiments, and direct hardware bring-up. |
| `Puck.Demo` | Quarantined 2026-08-01 by owner ruling, and off limits — do not open it, including for reference. It does not build at this path anyway: its seventeen `ProjectReference`s are relative paths written when it lived under `src/`, and they all dangle. `dotnet restore` there EXITS 0 while silently discarding every one of them (warning-level "Skipping project ... because it was not found"), and the build then fails as a flood of `CS0234` errors pointing at source files rather than at the dangling edges — so do not trust a green restore in that tree. **Nothing is porting its behavior anywhere.** The plan that sequenced that work was deleted; capabilities that lived only here are absent from the product with no scheduled return. |
| `Puck.Post` | Quarantined 2026-08-02 by owner ruling, and off limits. It was the engine's power-on self-test across CPU, same-device GPU, cross-backend, and live-subsystem tiers. **Nothing gates the shared engine contract today** — the cross-backend render path, the SDF VM ISA, the document schemas, and the deterministic numerics have no automated check. Do not cite it as coverage and do not write a stage for it. |

## Repository data and tools

| Path | Purpose |
|---|---|
| `docs/examples/` | Reference documents for the live authoring families: `creations/` (`puck.creation.v1`) and `tunes/` (`puck.audio.v1`). Nothing loads them; they are read by hand. |
| `src/Puck.Cli/` | The `puck` developer CLI, a first-class solution project: content search (`search`), the `Puck.Maths` benchmark microscope (`bench`), source sweeps (`scan`), the convention rewriters (`format`), the symbol-analysis verbs (`references`, `declarations`), and the layering report (`architecture`). Kind `Tool`: it consumes the tree and nothing consumes it. |
| `src/Puck.Analyzers/` | The repository's Roslyn analyzers — the `[VerifiedCode]` brand enforcement (VER001–VER010) and its code fixes. Kind `Analyzer`: `Directory.Build.props` hands it to every project as a compiler extension (`OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"`), which is why it never appears in any project's resolved reference set. |
| `build/` | Build policy the whole tree imports: the `[VerifiedCode]` marker source, the architecture ledger (`Architecture.props`), and the gate (`Puck.Architecture.targets` + `PuckArchitectureGate.cs`). |
| `tools/` | Quarantined under `experimental/` (2026-08-02) and off limits. It held batteries, generation, and frame utilities. |
| `.claude/skills/` | Current factual and procedural agent references for repository-specific work. |
