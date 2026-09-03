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
   deterministic numerics) and `Puck.GamingBricks` (the shared state-serialization
   substrate, forked-instance lifecycle, and the machine-neutral queued-host
   substrate) — never on shared substrate, backends, or composition roots. Each
   project's `Hosting/` folder carries its screen-machine engine adapter
   (`GamingBrickEngine`/`AdvancedGamingBrickEngine`,
   `MachineHost`/`AdvancedMachineHost`) over `Puck.GamingBricks`'s
   `QueuedMachineWorker` and the neutral screen-machine contracts in
   `Puck.Abstractions`. `Puck.World` consumes both cores through its `Hosting/`
   adapters or through composition-root debug hosts.
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

A stability level determines the evidence a change requires, never whether the
change is allowed.

## Layering

This block is GENERATED — `puck architecture --map` prints it from each
project's own `<PuckLayer>`/`<PuckKind>` declaration. Do not hand-edit it;
change the declaration and regenerate. The direction is the point: a
hand-written copy is a second source that drifts silently, and this one had
— it was missing three projects and still carried a row for one that had
been quarantined out of the repository.

```text
Optional extensions      Puck.World.AgentBridge  Puck.World.AgentHarness
Composition roots        Puck.Launcher.Stub  Puck.World  Puck.World.Silo
Validation               Puck.AdvancedGamingBrick.Post  Puck.GamingBricks.Post
                         Puck.HumbleGamingBrick.Post
Engine services          Puck.AdvancedGamingBrick
                         Puck.AdvancedGamingBrick.Forge  Puck.Audio
                         Puck.GamingBricks  Puck.HumbleGamingBrick
                         Puck.HumbleGamingBrick.Forge  Puck.Launcher
                         Puck.Overlays  Puck.Physics  Puck.Recording
                         Puck.SdfVm  Puck.ShaderVm  Puck.SignedDistance
                         Puck.Text  Puck.World.Addons  Puck.World.Authoring
                         Puck.World.Client  Puck.World.Console
                         Puck.World.Protocol  Puck.World.Schema
                         Puck.World.Server
Presentation             Puck.DirectX.Presentation  Puck.Launcher.Linux
                         Puck.Launcher.Windows  Puck.Vulkan.Presentation
Backends                 Puck.DirectX  Puck.Vulkan
Shared substrate         Puck.Commands  Puck.Hosting  Puck.Input
                         Puck.Networking  Puck.Platform  Puck.Platform.Linux
                         Puck.Platform.Windows  Puck.Scripting  Puck.Shaders
Leaf contracts and data  Puck.Abstractions  Puck.Assets  Puck.Attestation
                         Puck.Maths  Puck.Storage
(Test)                   Puck.Abstractions.Tests
                         Puck.AdvancedGamingBrick.Forge.Tests
                         Puck.Analyzers.Tests  Puck.Assets.Tests
                         Puck.Attestation.Tests  Puck.Audio.Tests
                         Puck.Cli.Tests  Puck.Commands.Tests
                         Puck.GamingBricks.Tests  Puck.Hosting.Tests
                         Puck.HumbleGamingBrick.Forge.Tests  Puck.Input.Tests
                         Puck.Launcher.Tests  Puck.Maths.Tests
                         Puck.Networking.Tests  Puck.Physics.Tests
                         Puck.Platform.Windows.Tests  Puck.Recording.Tests
                         Puck.SdfVm.Tests  Puck.ShaderVm.Tests
                         Puck.Shaders.Tests  Puck.SignedDistance.Tests
                         Puck.Text.Tests  Puck.World.Agents.Tests
                         Puck.World.Protocol.Tests  Puck.World.Schema.Tests
                         Puck.World.Tests
(Tool)                   Puck.Cli
(Analyzer)               Puck.Analyzers
```

The parenthesised rows are TERMINAL KINDS: they consume the tree and are never
consumed by it, so they carry no layer and sit above every row by construction.
A terminal-to-terminal edge is fine — `Puck.Probes.Tests` references
`Puck.Probes` as an ordinary assembly because it instantiates the probe
and drives it over compilations it builds itself.

Dependencies normally point downward. A same-row dependency is acceptable
when it does not introduce a backend or composition-root dependency — each
GamingBrick project's dependency on `Puck.GamingBricks` (rule 4 above) is
exactly this case.

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
| `PUCKARCH002` | The backend quarantine: only the Presentation row, composition roots, terminal kinds that introduce nothing, and explicitly reasoned named exceptions may hold a `Backends` assembly in closure. No live project currently needs a named exception. |
| `PUCKARCH003` | A ranked project holding a terminal-kind project in its closure. |
| `PUCKARCH004` | A lane profile whose closure no longer EQUALS its declaration — removals are as visible as additions. |
| `PUCKARCH005` | A missing, unknown, or contradictory `<PuckKind>`/`<PuckLayer>` declaration. |

## Contracts and data

| Project | Responsibility |
|---|---|
| `Puck.Abstractions` | Backend-neutral GPU, presentation, capture, timing, machine, lighting, and window contracts. It has no Puck dependencies and exposes no platform-native types. |
| `Puck.Maths` | Deterministic fixed-point numerics, world coordinates, vectors, and integer algorithms used by authoritative simulation — including the exact-algebra wing: finite fields, primality, presented algebra, and Reed–Solomon coding over `BinaryField<T>`. It also declares the two representation-neutral spatial seams, `IFieldEvaluator` (a scalar field and its gradient) and `IWorldQuery` (the five geometric verbs), so a field's producer and its consumers can be sibling libraries that never reference each other. |
| `Puck.Assets` | Content-addressed byte sources, hashes, a fixed-capacity LRU cache, and a persistent object store with named refs. It also carries the two codecs it does own: a minimal PNG/APNG codec (`PngEncoder`/`PngDecoder`) used for capture stills and font-atlas artifacts, and a spec-correct ISO/IEC 18004 QR encoder (`Qr/QrEncoder`, encode-only) a document field's screen source authors. Beyond that it identifies and moves bytes; it does not decode arbitrary formats. |
| `Puck.Storage` | The Azure object-blob store behind owned-world cloud sync: a routed byte store with a version-token seam (opaque read token + optional if-match write) for conditional overwrites, and an address projection for the platform's edge namespace versus a raw dev/emulator account. |

## Shared substrate

| Project | Responsibility |
|---|---|
| `Puck.Hosting` | Recursive `IRenderNode` hosting, capability propagation, terminal ownership, fixed-step simulation context, frame timing, and cross-thread publish buffers. |
| `Puck.Commands` | Typed commands, deterministic per-tick `CommandSnapshot`s (ephemeral — built, applied, and dropped within one tick; the world tape in `Puck.World` is the one recording surface), console dispatch, and binding profiles, sessions, and their vocabulary gate. |
| `Puck.Input` | Controller discovery and protocols, hotplug, routing arbitration, HID parsing, IMU fusion, haptics, and LampArray bind legends. Platform transports are injected. |
| `Puck.Platform` | The OS-neutral windowing/capture contracts (`INativeWindowFactory`/`INativeWindowBackend`, `IClipboardService`, `ICameraCaptureService`, `INativeImageCaptureService`, `IAudioRenderDeviceFactory`), the display-environment probe, the unmanaged allocator (`Puck.Memory`, mimalloc-backed), and the probes contracts (`Puck.Platform.Probes`: `ProbeReading`, `ProbeReadingRing`, `ICameraKernelHost`, `ProbeTrackPlayer`). No concrete platform backend lives here — `Puck.Platform.Windows`/`.Linux` each register the ones they carry. |
| `Puck.Platform.Windows` | Win32 windowing and clipboard, HID and Xbox (XInput/GameInput) controller transports, Media Foundation camera capture, Windows Graphics Capture feeds, the Media Foundation hardware video-encoder ladder (AV1→H.264), WASAPI loopback/microphone capture and render, and the generated CsWin32 native interop. |
| `Puck.Platform.Linux` | Wayland and XCB native windowing. No camera, recording, or audio-render backend exists yet — those seams register the declining/null implementations. |
| `Puck.Shaders` | Shader sets as data: the `puck.shader.v1` manifest (stages, bindings, config schema, push-constant block and its sources), its loader and config binder (`ShaderConfigBinding`, shared with probe kinds), the shipped-set catalog, and `FullscreenPassNode`, which runs a graphics set as one pass over an inner render node and takes a live per-frame config write (`TrySetConfig`) for a presentation binding to drive. Also compiled shader-bytecode loading, format detection, and validation, and the shared HLSL-to-SPIR-V/DXIL build recipe. Also the `puck.probe.v1` probe-kind manifest (`ProbeKindManifest`/`ProbeKindCatalog`) — a kind's declared input, channels, and (for a KERNEL-class kind) HLSL entry points, registered the same way a shader set is; ships the `ir-blob` kernel. References `Puck.Abstractions`, `Puck.Assets`, `Puck.Hosting`. |
| `Puck.Networking` | The dialect-agnostic wire substrate, packed as `ByteTerrace.Puck.Networking`: the socketless frame grammar (`FrameCodec`, `[u32 length][u8 kind][payload]`) and the bounded forward-only reader/writer pair (`WireReader`/`WireWriter`) every socket frames its bytes through — the reader latches a named refusal over untrusted bytes (invalid UTF-8 and non-finite lanes included) and never throws — plus the refusal vocabulary (`WireRefusal`/`WireFailure`) and the async stream framing built on them (`WireFrame`, one buffer per frame with `Body` a slice over it; an over-cap length is refused before anything is allocated). Also carries the generic Hello/identity handshake grammar (`HandshakeWireFormat`, the one Hello writer), the challenge/proof authentication contract (`IAuthenticator` — a verified identity is what the proof itself derives, never a caller's assertion), the persistent authenticated request/response lane state machine (`PersistentRequestLane`/`ILaneProtocol` — one per-request deadline answering `RequestTimedOut`, re-send only where the protocol's `MayResend` allows, a worker that survives anything the protocol throws), and, in `Puck.Networking.Peers`, the symmetric peer substrate: `Peer`/`PeerLink` over the `IPeerTransport`/`IPeerListener`/`IPeerConnection` seam (streams plus a datagram slot), `QuicPeerTransport` (`System.Net.Quic`, TLS 1.3 with a certificate on both sides, one inbound stream per connection), a handshake that binds the offered P-256 identity to the key the transport proved (`ChannelUnbound` or `IdentityKeyInvalid` otherwise, every refusal named to the far side), and `Puck.Attestation`-signed messages verified against the identity proved at handshake. A refused message names its `PeerRefusal` and leaves the link open; a frame-grammar violation closes it; every channel (`Events`, `IncomingLinks`, `HandshakeRefusals`) and the count of in-flight handshakes is bounded, and `PeerEvent.Closed`/`PeerLink.CloseFailure` name why a link closed. No server role. Carries no document or protocol vocabulary of its own; depends only on `Puck.Maths` and `Puck.Attestation`. |
| `Puck.Scripting` | Deterministic, fuel-metered WASM addons: the neutral host, the module validator, the ABI, and the core's own declared-channel-name table decoder (`AddonChannelNameTableReader`, resolved through the injected `IAddonChannelResolver`). It references neither `Puck.Commands` nor `Puck.Input` — that absence is the point of the assembly split and is enforced by an exact-equality lane profile, not merely current. |

## GPU backends and presentation

| Project | Responsibility |
|---|---|
| `Puck.Vulkan` | Vulkan bindings, device and resource factories, command recording, sharing, and synchronization. It contains no windowing or shader compiler. |
| `Puck.Vulkan.Presentation` | Vulkan presenter and compute-service adapters for the neutral hosting contracts. |
| `Puck.DirectX` | Direct3D 12 and DXGI device, resource, command, sharing, and synchronization implementation. |
| `Puck.DirectX.Presentation` | Direct3D 12 presenter and compute-service adapters. |
| `Puck.Launcher.Windows` | The Windows GPU-host block a composition root shares across windowed entry points: `Puck.Platform.Windows` windowing/clipboard, the allocator, and the launch-selected Vulkan or Direct3D 12 presenter. |
| `Puck.Launcher.Linux` | The Linux GPU-host block: `Puck.Platform.Linux` windowing/clipboard, the allocator, and the Vulkan presenter — the only backend in its closure. |

Cross-backend parity has one on-demand check: `puck parity` boots the real windowed `Puck.World` on both backends and compares the same fenced composed frame under the relaxed envelope. The broader engine contract remains without an automated gate.

## Engine services

| Project | Responsibility |
|---|---|
| `Puck.Launcher` | Generic application host: window loop, command pump, fixed-step accumulator, terminal control, genlock, and backend switching. Composition roots register platform and backend services. Optional self-update (`AddSelfUpdate`): `puck.release.v1` documents, a `sequence`-route `Puck.Attestation` claim as the anti-rollback mechanism, content-addressed delta staging, and an atomic `current`-pointer applier (`Puck.Launcher.Stub` reads the pointer at its own next launch) behind `update.status`/`update.check`/`update.apply`. An app that never calls `AddSelfUpdate` has no update path at all. |
| `Puck.SdfVm` | The SDF GPU engine: world renderer, frame sources, render assembly, debug tools, composition and anchor seams, camera views. Consumes `Puck.SignedDistance` for the program model and ISA. |
| `Puck.SignedDistance` | The signed-distance-function field as data: the instruction ISA, the packed-word program representation, the fluent authoring builder (including laying `Puck.Text`-authored strings out into marchable glyph geometry), the solid primitive vocabulary and its unit-size table (`SdfSolidPrimitive`/`SdfSolidGeometry`/`SdfSolidBounds`), the isometric domain-operator family as data (`SdfDomainOp`) with both of its readings — the point fold (`SdfDomainOps`) and the rigid copies it generates (`SdfDomainExpansion`/`SdfRigidFrame`, fixed point, for consumers that place geometry rather than transform a point) — and a warp-free deterministic fixed-point CPU interpreter (`Puck.SignedDistance.Queries`) that answers `Puck.Maths`' `IFieldEvaluator`/`IWorldQuery` seams, so authoritative simulation queries the same field a GPU renders. No GPU or shader-compiler dependency of any kind. |
| `Puck.Text` | Font-atlas models, text layout, and deterministic coverage-to-distance atlas generation. It is render-agnostic. |
| `Puck.Overlays` | Backend-neutral screen-space overlay UI: the shared ASCII-95 glyph SDF pack (loaded through a prepacked artifact beside the atlas), design tokens, the packed-record frame builder (panels, rects, fixed-cell text, icon chips, per-record viewport clipping, counted overflow with a tail-reservation priority policy), the per-channel lease table a host sizes with an `OverlayCapacity` (refused at construction when it over-subscribes a backstop), the console/binding-bar/editor-HUD/toast writers, and the one `UnifiedOverlayNode` decorator (a single GPU-timestamped fullscreen pass) that runs identically on both backends. Surfaces are CPU writers; a new surface is a new writer, never a new node or shader. Depends only on neutral contracts — no producer library. |
| `Puck.Physics` | Deterministic fixed-point simulation kernels: an exact pairwise gravity oracle, reusable Barnes–Hut monopoles, adaptive dual-tree FMM with M2M/M2L/L2L passes, shared compound body-collider vocabulary, dynamic and analytic-static contact geometry, the `IContactField` seam a grounded body resolves against with both providers behind it (`FixedStaticContactSolver` relaxes against a collider set; `FixedFieldContactSolver` measures against a scalar field through `Puck.Maths`' seams), a temporal-substep sequential-impulse rigid solver with persistent manifolds, and the body motion-program core in `Motion/` (`BodyMotionOp`, `CompiledBodyMotionProgram`'s admission/phase compiler, the per-body trigger and action-state IR, and the compiled `FixedMotionTuning`/`FixedBodyShaping` shapes the stages read). It owns no world schema, authority, walkability policy, or presentation seam: the authored-row translation into the motion IR stays in `Puck.World.Schema` and the per-phase execution stays in `Puck.World.Server.WorldBody`. Depends on `Puck.Maths` and `Puck.Abstractions` (the strict by-name enum converter the authored motion enums declare). |
| `Puck.Audio` | `Puck.Audio.Mixing`: the fixed-point mixer core (s16×Q16 gains, int32 accumulate, polynomial soft-clip, ramped coefficients) and the 32-voice deterministic synth (`AudioMixer`/`VoiceSynth`/`AudioSnapshot`/`MachineAudioRate`), presentation-adjacent state with no replay/hash contract of its own. Parses no document — `Puck.World.Audio.WorldVoicePatchFactory` (which stays in `Puck.World`) converts an authored `puck.synth.v1` patch into the runtime `VoicePatch` struct. Depends only on `Puck.Abstractions`, `Puck.Commands`, `Puck.Hosting`, `Puck.Maths`. |
| `Puck.Recording` | Everything downstream of a captured frame, in one project. `Capture/` is the still-frame half: the `ICaptureSink` that writes PNGs through `Puck.Assets`'s `PngEncoder`, and the FNV-1a frame-hash observer (GPU readback occurs upstream). The rest is the `puck.recording.v1` moving-picture graph: frame source → data-defined overlay compositor → encoder ladder → hand-rolled Matroska/WebM muxer, plus the managed-Opus (Concentus) audio lane and the `RecordingSession` that implements the same `ICaptureSink`. It defines the recording document, muxer, overlays, and session; the Media Foundation video-encoder ladder and WASAPI audio sources are the platform backend. Depends on `Puck.Abstractions`, `Puck.Assets`, and `Puck.Maths`. |
| `Puck.GamingBricks` | The substrate both GamingBrick cores build on. State-serialization and forked-instance lifecycle: `StateWriter`/`StateReader` (little-endian widths + `WriteBlock<T>` memcpy + `Reset` reuse), `SnapshotSection`, `ISnapshotable`, the flat `SnapshotImage`, the `SnapshotDivergence` localizer, `MachineInstance<,>`/`MachineFork<,>`/`MachineInstancePool<,>`, `ISnapshotableMachine`. The machine-neutral queued-host substrate: `QueuedMachineWorker` + `IQueuedMachineCore` adapter (worker thread, bounded FIFO with backpressure, triple-buffer publication with the upload lease, native-frame-keyed save-flush debounce, the vectorized framebuffer repack), `MachineTimeTravel<TInput>` (rewind, persistent-fork runahead, capped fast-forward), and `QueuedHostContractProbe`, which proves the queued-host contract for both cores' batteries. Per-core snapshot identity fields, component orders, and fingerprints stay in each core; the fingerprint primitive lives in `Puck.Maths`. References `Puck.Abstractions` and `Puck.Hosting` (`EngineTicks`' tick-to-cycle conversion). |
| `Puck.HumbleGamingBrick` | Deterministic SM83 machine in its DMG, CGB, and AGB-costume models (snapshots, forks, cartridges, link cable, PPU, APU, peripherals). Its `Hosting/` folder carries the thin adapter from the neutral screen-machine contract to the core over `Puck.GamingBricks`'s `QueuedMachineWorker`: an `IQueuedMachineCore` (pad mapping, KEY1-aware tick conversion, framebuffer, save persistence) plus the host shell and work-RAM peek. Inherits the substrate's queued/backpressure behavior. |
| `Puck.AdvancedGamingBrick` | Deterministic AGB-native ARM7TDMI machine (cycle-level bus, DMA, timers, PPU, APU, cartridges, snapshots, link cable). Its `Hosting/` folder carries the thin adapter from the neutral screen-machine contract to the core over `Puck.GamingBricks`'s `QueuedMachineWorker`: an `IQueuedMachineCore` (KEYINPUT mapping, exact tick conversion, framebuffer, save persistence, direct boot) plus optional explicit BIOS images and the host shell. |
| `Puck.HumbleGamingBrick.Forge` | The SM83 ROM forge, packable on its own (`ByteTerrace.Puck.HumbleGamingBrick.Forge`): `Sm83Emitter`, the public game framework (`GameFramework` and its kernel/memory-map/module closure, the `GameManifest`/`AssetLinker` declarative layer, `FrameworkCartridge`), the pure-C# `HgbImage` tile/palette encoders, `AudioDocumentCompiler` (a `puck.audio.v1` document into SM83 sound-driver data), the `Tune` cart builder/verifier (`TuneRom` compiles a tune document into a bootable Humble cart and boots it headlessly), and the worked-example `Games/` carts. Every cart self-verifies by driving a real Humble machine (`VerifyMachineDriver`/`VerifyMachineSettle`) before its bytes are handed out. Depends on `Puck.Assets` (the audio/synth document families) + `Puck.HumbleGamingBrick`. |
| `Puck.AdvancedGamingBrick.Forge` | The ARM7TDMI ROM forge, packable on its own (`ByteTerrace.Puck.AdvancedGamingBrick.Forge`): Thumb/ARM instruction emitters, the direct-boot cartridge builder (header, complement checksum, code/data windows; the retail logo bitmap is a caller-supplied parameter — direct boot never reads it), a minimal polling kernel (VBlank sync, keypad edges, mode-3 framebuffer, documented EWRAM state map), and a worked-example cart. Same discipline as the Humble forge: a cart self-verifies by driving a real Advanced machine before its bytes are handed out. Depends on `Puck.AdvancedGamingBrick`. |
| `Puck.World.Authoring` | The authored-content document families `Puck.World` embeds inline: the `puck.creation.v1` model (`CreationDocument`/`CreationCanonicalizer`, hold-style timeline frames, IK chains, camera eyes, faces, engraved text runs), `puck.music.v1` (`MusicDocument`) and the judge document, whole-document stamp reach (`CreationGeometry`, over `Puck.SignedDistance`'s primitive table), stamp emission/sampling, and grid-snap math (`GridSnap`). The audio/synth families and the document-neutral canonicalize/hash core live in `Puck.Assets`; this project carries the world-facing families over that same core. Host-side float on purpose — authoring/presentation math, outside the simulation-state determinism contract. Depends on `Puck.Assets` + `Puck.SignedDistance` + `Puck.Text`. |
| `Puck.World.Schema` | What a world IS: the document model, physically split out of `Puck.World` (headless P1) so the shape the sim runs on cannot itself reach presentation: `WorldDefinition` (every section record, including reciprocal `adjacencies`, `WorldHostDefaults`, the `identity` section an owned world carries, and the admission section's entries/grants/disclosure-tier vocabulary and the channel/intent vector a document's motion/kit rows compile against — both document-embedded, so both live here despite keeping the `Puck.World.Protocol` namespace their move did not rename), `WorldDefinitionValidator`, `WorldDefinitionSerialization`. Authored body colliders compile into the fixed vocabulary owned by `Puck.Physics`; geometry remains outside the document model. The `bodyMotionPrograms`/`kits.motion` authoring vocabulary lives here, and so does its translation into `Puck.Physics.Motion`'s IR (`BodyMotionProgramFactory`, `BodyActionSpecFactory`, `WorldMotionTuningFactory`, `CompiledBodyProducer`) — the instruction set, the compiler over it, and the compiled tuning shapes are the library's. Denied `Puck.Overlays`/`Puck.Input`/every Presentation and Backend project/`Puck.World.Protocol`/`Puck.World.Server` by an exact-equality lane profile — a member that would need one of those (the engine-default binding document, the live vocabulary check, the mutation-kind name catalog) crosses through an injection seam (`BindingVocabularyHook`, `MutationKindVocabularyHook`) the composition root wires with a module initializer, rather than a direct reference. |
| `Puck.World.Protocol` | What a world SAYS: the wire/tape vocabulary every submission into, and every delivery out of, the authoritative server travels as — `PlayerIntent`/`WorldCommand`/`WorldMutation`/`WorldGrant`/`WorldPrincipal`/`WorldSessionLever`, `WorldEntityAddress`, `SessionRequest`/`WorldSnapshot`/`WorldComposition`, `IServerLink`/`IClientSink`, `LoopbackTransport`, `WorldPrincipalMapping`. References `Puck.World.Schema` (the document shapes a submission carries or a grant addresses) and `Puck.Networking` (the transport-neutral frame/wire grammar its codecs frame payloads through). Same presentation denials as `Puck.World.Schema`, plus no `Puck.World.Server`. |
| `Puck.World.Server` | The authoritative world runtime, physically split out of `Puck.World` alongside `Puck.World.Schema`/`Puck.World.Protocol`: `WorldServer` (the fold, authority-operation serialization, the mutation journal, snapshot emission), `WorldGrants`/`WorldHandleTable`, `WorldPopulation`/`WorldBody` (the entity table and its per-tick advance), the compiler-derived adjacency overlap/contact field and FED3 federation codec (source-scoped escrow plus human/autonomous entity admission), `WorldOwnedWorlds` (the owned-world identity catalog and its cross-document durable-state door), the addon host seam (`IWorldAddonHost`/`WorldAddonReceipt` — the mounted guest runtime lives in `Puck.World.Addons`, which references this project rather than the reverse), the collision/contact/solid-field support a body advances against, and the true-deterministic-replay codec (`WorldReplayTape`/`WorldReplaySnapshot`/`WorldReplayVerdict`/`WorldReplayRefusal`/`WorldReplayCodecException`). `WorldScreenMachineEngines` (a static list) and `WorldPostRenderExtensions` (the `Puck.Shaders.ShaderSetCatalog` over the deploy's `Assets/Shaders` manifests) are the extension vocabularies every composition root's pre-container `WorldExtensionVocabularyHook` wiring reads, so a document declaring a `screens[]` machine engine or a `render.extensions[]` id validates identically in `Puck.World` and `Puck.World.Silo`. References `Puck.World.Schema`, `Puck.World.Protocol`, `Puck.Networking`, `Puck.Physics`, `Puck.Audio` (`WorldMachineHost` boots every machine at `MachineAudioRate.SampleRate`), `Puck.Storage`, `Puck.Hosting`, `Puck.AdvancedGamingBrick`, `Puck.HumbleGamingBrick`, `Puck.SdfVm`, `Puck.Shaders`; generic contact math and the body motion-program core live in Physics while Server retains pair selection, authority, grounding/walkability, obstruction reporting, and the per-phase execution of a compiled program over the body state it owns. Same presentation denials as `Puck.World.Schema`. |
| `Puck.World.Addons` | The mounted addon guest host, physically split out of `Puck.World.Server` so the authoritative server carries no WASM/Wasmtime surface of its own: `WorldAddonRuntime` (the `IWorldAddonHost` implementation — mounting, the three tick-boundary pump points, lifecycle verbs), `WorldAddonMutationDecoder` (the addon mutation seam's stage 6 decode), `WorldAddonWire` (the World-side ABI vocabulary mappings), `AddonMutateRefusal` (the `addon.mutate` door's cataloged refusal reasons), and `AddonSimulationPump` (the crossing from a guest's decoded cells to typed, vocabulary-validated submissions — `AddonActSubmission`/`AddonQuerySubmission`/`AddonAskSubmission`; whole-batch refusal; authority stays the consumer's). References `Puck.World.Server` (the seam it implements), `Puck.Scripting` (the WASM guest ABI), `Puck.Maths`, `Puck.Assets`. Same presentation denials as `Puck.World.Schema`. |
| `Puck.World.Client` | The presentation-facing client seam, physically split out of `Puck.World`: `PlayerRoster`/`WorldClient`/`SeatController` (the seat and roster surface), the composed-frame producer (`WorldFrameSource`) and its scene/view collaborators (`WorldSceneEmitter`, `WorldViewComposer`, and `WorldSceneEmitter`'s siblings `WorldSessionSceneEmitter`/`WorldAdjacencySceneEmitter`/`WorldSdfDocumentEmitter`), camera and view resolution (`WorldCameraRigCompiler`/`WorldSeatCameraResolver`/`WorldSeatViewports`), the stamp/animation pool (`WorldStampPool`/`WorldPlacementStamper`/`WorldScreenStamper`), the SDF document intake (`Sdf/SdfDocumentDecoder`/`SdfDocumentModel`/`SdfRefusal`), the narrow audio seams the frame/scene producers hold the root's `WorldAudioDirector` through (`IWorldAudioFrameFeed`/`IWorldAudioCueSink`, the `IWorldAudioLever` pattern), and the binding-authoring layer (`WorldSeatBindings`/`WorldDefaultBindings`/`WorldAffordances`/`CommandVocabulary` — the command-name constants nine root command modules forward to, single-sourced here since the modules themselves stay in `Puck.World` for their own Server coupling). References `Puck.World.Protocol` and `Puck.Audio`; never `Puck.World.Server`. `WorldClientSeats` (implements the Server seam `IWorldEmbodiedSeats`) and `WorldAudioDirector` (imports `Puck.World.Audio` types directly) stay in `Puck.World` instead. |
| `Puck.World.Console` | The control plane that follows the authority: `IWorldConsoleAuthority` (resolves the `WorldInstance` a console invocation addresses; the desktop's implementation, `WorldBootConsoleAuthority`, lives in `Puck.World` and always answers the boot row) and the server-only command modules moved out of `Puck.World` — `world.grant`/`.revoke`/`.grants`/`.why`, `world.dynamics`, `world.group.*`/`world.ownership.*`/`world.groups`, `world.population.spawn`/`world.looks`, `market.*`/`world.market`, `world.peers`/`world.projection`, `world.row.*`/`world.kits`/`world.assign`, `world.state.*`/`world.generate`/`world.state`, `world.update`, `world.wait` (with its tick-barrier gate, `WorldConsoleWaitGate`, moved in beside it). A module whose ctor or handlers touch `WorldClient`, `PlayerRoster`, the seat surface, views, HUD, the camera control application, screens, audio, or recording stays in `Puck.World` instead. References `Puck.World.Server`, `Puck.World.Protocol`, `Puck.World.Schema`, `Puck.Commands`, `Puck.Launcher` (`Puck.Networking` only transitively, through `Puck.World.Server`). Same presentation denials as `Puck.World.Schema`, plus no `Puck.World.Addons`. |
| `Puck.World.AgentBridge` | The opt-in, provider-neutral autonomous-participant extension: `WorldAgentBridge` scopes one ingress-capable `WorldPrincipal` to one body, sends observations through `IPrincipalServerLink` so Observe grants apply, builds typed motion/channel/stop commands from the current live `WorldChannelTable`, and returns submission receipts that never masquerade as authority verdicts. `WorldAgentMailbox` is its bounded worker-to-pump crossing and implements the launcher's existing `ISnapshotInputCapture` seam; `AddPuckWorldAgentBridge` is explicit host composition. It references Commands, Protocol, and Schema; it has no model, Harness, Console, MCP, or base-composition-root dependency. Nothing in the base world family references it. |
| `Puck.World.AgentHarness` | The optional Microsoft Agent Framework adapter over `Puck.World.AgentBridge`: five typed `puck_*` tools, Harness approval wrappers on mutations, stateful sessions supplied by `Microsoft.Agents.AI.Harness`, and safe defaults that omit file, web, shell, background-agent, and ambient skill discovery capabilities. A caller may inject an explicit `AgentSkillsSource` to import trusted user-defined skills. It accepts an injected `IChatClient`; provider credentials and persistent-agent lifecycle stay with an agent-capable composition root outside `Puck.World`. |

## Composition roots and validation

| Project | Responsibility |
|---|---|
| `Puck.World` | Document-driven (`puck.world.def.v1`, five checked-in charter/dev worlds plus the Four Corners stress examples, `--world`) network-shaped multiplayer host: fixed-point player state, automatic ownership handoff across invisible authored adjacencies, cross-authority neighbour projection/ghost rendering, a runtime mutation/journal/undo protocol vocabulary, principals + capability grants (addons included), per-player owned identity worlds (ordinary `puck.world.def.v1` documents carrying an `identity` section) with bindings layered onto the `Puck.Commands` stack, owned-world cloud sync through `Puck.Storage` (`storage.*` verbs), session write-back, native self-recording (`puck.recording.v1`, `--recording`, `capture.*` verbs), camera probes (the `probes` document section's live host `WorldProbes` — probe lifecycle, `probe.<name>` command axes, presentation-parameter and camera-control bindings — plus the `probe.status`/`probe.record` verbs), and SDF world rendering. The seat/roster/fly-camera/scene-emitter surface itself lives in `Puck.World.Client`, which this project references; `Puck.World` retains the audio director, the frame source, and every `*CommandModule`/document-hook root that needs the live server or a root-only composition type directly. Verify game behavior by running it. |
| `Puck.World.Silo` | The second substrate driving the moved `Puck.World.Server` host engine (`WorldInstanceHost`, boot-free): an Orleans silo (`--silo <puck.silo.def.v1 path>`) whose grains (`WorldGrain`, keyed by owner oid + world id) carry activation lifecycle only — game traffic never rides the Orleans wire, only the same `WorldPeerCall` the desktop uses. `WorldSiloHost` owns one boot-free `WorldInstanceHost`, an activation mailbox drained on the one tick thread `Puck.Launcher`'s headless terminal pumps, and per-row federation identity/checkpoint bookkeeping. `SiloCommandModule` carries `silo.status`/`.grains`/`.publish`/`.activate`/`.deactivate`/`.checkpoint`. References `Puck.World.Server`, `Puck.World.Protocol`, `Puck.World.Schema`, `Puck.World.Console`, `Puck.World.Client`, `Puck.Commands`, `Puck.Hosting`, `Puck.Launcher`, `Puck.Storage` (`Puck.Networking` only transitively, through `Puck.World.Server`). |
| `Puck.Launcher.Stub` | The self-update stub: an install's actual entry point once `Puck.Launcher.AddSelfUpdate` is registered. References nothing but the BCL — an empty lane-profile closure enforces this — so it parses no manifest and shares no code with the app it launches. Reads `current`/`last-good`/per-version `state-generation` and the health attempt counter beside itself, decides via the pure `StubDecisionTable`, `Process.Start`s the staged app with pass-through argv and inherited stdio, and forwards its exit code. Deliberately exempt from the mechanism it drives: a bricked stub needs a reinstall. |
| `Puck.HumbleGamingBrick.Post` | Humble core conformance, determinism, reference-ROM, save, and cross-generation link battery. |
| `Puck.AdvancedGamingBrick.Post` | Advanced core conformance, determinism, commercial-ROM, link, co-simulation, and diagnostic tooling. |
| `Puck.GamingBricks.Post` | The battery scaffold both Post projects reference: `PostVerdict`/`PostTier` (verdict class and fast→slow tier), `PostStageOutcome`/`PostStageResult` (a stage's return and its report row), and `CommandLineArguments` (flag-value lookup). Each battery's own `PostContext`, `IPostStage`, `PostBattery`, `PostReport`, and stage list stay in the owning project — they diverge per machine (BIOS image, corpus roots, console model) and are not shared. |

## Experimental projects

`experimental/` holds `Puck.BareMetal` (freestanding Native AOT runtime, UEFI
kernels, direct hardware bring-up), `Puck.Platform.Switch` (moved from
`src/Puck.Platform/Switch/`), and the quarantined `Puck.Demo`, `Puck.Post`,
`Puck.Bench`, and both `scripts/` trees (`scripts/world`, `scripts/recording`).
The quarantine rules — read as prior art, never build, run, fix, or revive —
live in
[CLAUDE.md](../CLAUDE.md) and
[experimental/README.md](../experimental/README.md); this map carries only
the structural fact: no experimental tree is in `Puck.slnx`, the root build,
or the architecture gate's scope — each carries its own
`Directory.Build.props`/`.targets` firewall pair that stops MSBuild's upward
discovery at the tree, so the isolation is structural rather than a path
filter somewhere else.

## Repository data and tools

| Path | Purpose |
|---|---|
| `docs/examples/` | Reference documents for the live authoring families: `creations/` (`puck.creation.v1`) and `tunes/` (`puck.audio.v1`). Nothing loads them; they are read by hand. |
| `src/Puck.Cli/` | The `puck` developer CLI, a first-class solution project: content search (`search`), the `Puck.Maths` benchmark microscope (`bench`), source sweeps (`scan`), the convention rewriters (`format`), the symbol-analysis verbs (`references`, `declarations`), and the layering report (`architecture`). Kind `Tool`: it consumes the tree and nothing consumes it. |
| `src/Puck.Probes/` | The repository's Roslyn probes — the `[VerifiedCode]` brand enforcement (VER001–VER010) and its code fixes. Kind `Probe`: `Directory.Build.props` hands it to every project as a compiler extension (`OutputItemType="Probe"`, `ReferenceOutputAssembly="false"`), which is why it never appears in any project's resolved reference set. |
| `build/` | Build policy the whole tree imports: the `[VerifiedCode]` marker source, the architecture ledger (`Architecture.props`), the gate (`Puck.Architecture.targets` + `PuckArchitectureGate.cs`), and the NuGet packaging policy (`Packaging.targets` — shared version and metadata, applied only to projects that opt in with `<IsPackable>true</IsPackable>`; the tree default is `false`). |
| `.claude/skills/` | Current factual and procedural agent references for repository-specific work. |
