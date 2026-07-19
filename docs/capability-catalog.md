# Puck capability catalog

This catalog describes the capabilities available in the current tree. See
[project-map.md](project-map.md) for ownership and
[agent-guide.md](agent-guide.md) for verification commands.

| Status | Meaning |
|---|---|
| ✅ | Implemented and covered by an automated battery or a documented hardware run |
| 🔶 | Implemented with a configuration, hardware, or integration limitation |
| 🧪 | Contract or component exists, but the end-to-end path is not complete |
| ⛔ | Intentional current limit |

## Rendering and SDF worlds

| Capability | Status | Current behavior |
|---|---|---|
| SDF virtual machine | ✅ | `Puck.SdfVm` builds a packed instruction stream evaluated by matching HLSL on Vulkan and Direct3D 12. The VM includes 3D primitives, lifted 2D primitives, hard/smooth/chamfer blends, point transforms, repeats, symmetry and wallpaper folds, stochastic cell jitter, twist and bend, domain warp, field displacement, and one-deep field scopes. |
| Scoped field composition | ✅ | `PushField` and `PopField` isolate accumulator-reading operations such as intersection, onion, dilate, and displacement before composing the result into its parent field. Run-document `group` objects expose the same depth-one model. |
| Safe sphere tracing | ✅ | The renderer uses relaxed sphere tracing with program-derived Lipschitz scaling, fold-boundary step limits, footprint-adaptive termination, analytic normals, ambient occlusion, and soft shadows. A finite-difference normal path remains available for comparison. |
| Instance acceleration | ✅ | A host-built uniform grid produces per-tile masks before the beam and view passes. Parked instances use a negative-radius sentinel and are skipped without changing reserved capacity. `MaxInstances` is 16,384. |
| GPU-driven rendering | ✅ | Instance culling, beam construction, indirect dispatch, per-view rendering, and composition execute on the GPU. Timing labels and neutral timestamp contracts expose per-pass cost. |
| Materials and lighting | ✅ | Materials provide albedo, emissive, metallic, roughness, and opacity channels. Smooth blends cross-fade material ownership. Sun, ambient, screen-emitted colored light, fog, dithering, soft shadows, and ambient occlusion share one shading path. |
| Diegetic screens | ✅ | Up to 32 `ScreenSlab` surfaces sample live image providers, apply CRT treatment, and contribute room light. Clearing a provider restores the procedural no-signal material. Moving screen frames and decals update per frame. |
| World text | ✅ | Glyphs can be marched as extruded field geometry for engraving, embossing, and signage. A separate glyph-decal tier renders dense reading text at the material hit without adding geometry to the march. Both tiers use the shared `Puck.Text` atlas and layout model. |
| Multi-view rendering | ✅ | Up to five viewports compose SDF views and hosted child surfaces. Per-view render scale uses integer-derived extents; native scale follows an exact-copy path. `ViewStack` registers up to 64 named views and refreshes a deterministic budget of four rendered views per frame. |
| Dynamic transforms | ✅ | Per-frame position and orientation buffers animate instances without rebuilding the program. Capacity is declared when the renderer is constructed and every required slot must be supplied. |
| Runtime destruction | ✅ | The SDF debug surface stores replayable subtraction carves, supports deterministic meteor placement, and can keep thousands of persistent carve records. A carve-bake planner replaces settled dense hard-subtraction clusters with sampled distance-field bricks. Smooth subtraction remains analytic. |
| Sampled regions | ✅ | `SampledRegion` reads a trilinearly sampled distance brick from a fixed GPU pool and composes it as an ordinary shape. The engine slices brick baking across produced frames and falls back conservatively when the pool is unavailable to a shader variant. |
| World queries | ✅ | `IWorldQuery` offers fixed-point raycast, sphere cast, overlap, ground height, and line-of-sight operations. `BakedWorldQuery` reads a deterministic heightfield artifact; `SdfFieldEvaluator` interprets the supported warp-free live program subset and also exposes field distance and gradient. |
| Debug and inspection | ✅ | The `sdf` console mode exposes shapes, blends, operations, field scopes, views, camera control, program information, a gallery of adversarial scenes, carve controls, and GPU timing. Debug views include depth, normals, rays, material id, iteration count, termination, field slice, mask density, overshoot, and a per-pixel field-evaluation-count heatmap. |
| SDF microbenchmark | ✅ | `sdf.bench` measures shapes, operations, instance ladders, carves, and moving-instance stress through the same renderer. It reports per-pass GPU time from a deterministic camera and workload. |
| Hardware ray query | 🔶 | An inline ray-query path provides a parity and capability probe when the selected GPU exposes ray queries. Raster/compute rendering remains the primary path. |

The exact instruction ids, buffer lanes, descriptor bindings, and C#↔HLSL
sync pairs are maintained in the `sdf-world` skill.

## GPU backends and presentation

| Capability | Status | Current behavior |
|---|---|---|
| Vulkan backend | ✅ | Hand-maintained Vulkan bindings and factories implement compute, resources, synchronization, external sharing, presentation, and device-loss reporting behind neutral contracts. |
| Direct3D 12 backend | ✅ | Direct3D 12 and DXGI provide the matching resource, command, synchronization, sharing, and presentation behavior. |
| Single-source shaders | ✅ | DXC compiles common HLSL to SPIR-V and DXIL. Shader assets are validated and selected by bytecode format rather than backend-specific source. |
| Cross-backend parity | ✅ | Shared scenes are compared across Vulkan and Direct3D 12 with evidence-calibrated pixel thresholds, strict opt-in thresholds, solidity checks, and differential fuzzing. |
| Zero-copy surface sharing | ✅ | Vulkan-to-Direct3D and Direct3D-to-Vulkan external image import/export paths carry GPU surfaces without a CPU copy. Synthetic camera sharing exercises the generic mechanism. |
| Runtime backend switch | ✅ | `BackendSwitcher` recreates the renderer on the alternate backend while preserving host state and capability ownership. |
| Mixed-backend composition | 🔶 | The engine can host imported surfaces from the other backend. Run-document `world.produce` cross-backend assembly remains an explicit capability rejection. |
| Present modes and pacing | ✅ | Present descriptors expose immediate, FIFO, mailbox, and adaptive behavior as supported. Genlock and present-timing diagnostics measure and align an external rhythm without entering simulation state. |
| Device-loss handling | 🔶 | Render nodes receive device-loss callbacks and rebuild resources. A process restart may still be required after full NVIDIA Vulkan device removal. |
| GPU timing | ✅ | `IPassTimingSource`, `GpuTimingControl`, and `FrameTimingHub` expose live per-pass GPU and CPU timing to console switches, run documents, the benchmark, and diagnostics. |

## Run documents and composition

| Capability | Status | Current behavior |
|---|---|---|
| `puck.run.v1` | ✅ | One JSON document describes host configuration, scene, viewports, screen sources, graph intent, input, addons, validation, or fuzzing. `RunDocumentValidator` collects semantic failures before graph construction. |
| CLI-to-document funnel | ✅ | Demo launch options synthesize the same document records used by `--run`; graph builders do not maintain a parallel flag-only path. |
| JSON Schema | ✅ | `Puck.Demo --emit-schema` generates `schema/run.schema.json` from the document model. The run-document battery checks schema sync and round-trips the examples. |
| SDF authoring surface | ✅ | Scene objects cover materials, transforms, blends, modifiers, groups, screens, glyphs, and the supported SDF shape and operation vocabulary. Validator limits match engine capacities. |
| Graph intents | ✅ | `overworld` builds the game experience and `world` renders a document-authored SDF world. Validation and fuzzing are separate root intents. Unsupported live-camera and cross-backend production requests fail during preflight with attributed errors. |
| Viewport sources | ✅ | Perspective and orbit cameras, GamingBrick machines, and hosted view surfaces populate viewports. Screen-source records can bind a machine viewport's native framebuffer into world geometry. |
| WASM addons | ✅ | A document can declare deterministic, fuel-metered WASM modules. Each addon receives fixed-point tick input and emits neutral virtual-pad commands through a frozen ABI. |
| Direct ROM boot | ✅ | `--rom` synthesizes an immersed overworld document with the cartridge inserted. Work-RAM exit and victory conditions can return control to the host cleanly. |

## Content sources and capture

| Capability | Status | Current behavior |
|---|---|---|
| GamingBrick screen machines | ✅ | Neutral `IScreenMachineEngine` adapters host both the SM83 family (`gaming-brick`) and native ARM7TDMI GBA (`advanced-gaming-brick`), map standard pads, step exact tick budgets, and publish their native framebuffers. Both hosts share one machine-neutral queued-host substrate (`Puck.Hosting`'s `QueuedMachineWorker`), so the optional `IQueuedScreenMachine` capability preserves FIFO tick/input segments behind a finite pending window on either core, applies producer backpressure without dropping authoritative work, and exposes completed/pending/capacity progress; the save-flush debounce keys on native ~59.73 Hz frames. The World default continuously runs a BIOS-backed `.gba` cartridge on its fifth diegetic screen. |
| Neutral audio capability | 🔶 | An optional `IAudioMachine` capability on the queued-host substrate drains each core's presentation-side ring (Humble `AudioOutputComponent`, Advanced `AgbApu.DrainSamples`) into a preallocated buffer. State-of-record audio advance stays unconditional on both cores by design — only the presentation-side mix/ring write gates on an attached sink, so emulated state never depends on presentation config. A `queued-host-audio` stage gates the contract in both batteries. Puck.World attaches its screens to this capability: a WASAPI render device, a mixing core, always-on machine audio, and a data-driven cue table (`world.speaker.*`, `world.tune.*`, `audio.state`, `world.volume`). |
| Hosted child surfaces | ✅ | Child render nodes occupy viewport slots and can provide emulator, resample, pixelate, guest, or nested-world content. |
| Live camera | 🔶 | The CPU-pixel camera tier feeds the Pocket Camera peripheral. A Direct3D zero-copy capture/export implementation exists for re-hosting, but `live-camera` run-document sources are rejected until a current render-node consumer is wired. |
| Window capture | ✅ | On Windows 10 2004 (build 19041) or newer, Windows Graphics Capture binds a selected window through the compositor as a fixed-extent, fixed-cadence, nonblocking latest-result-wins world screen feed. Target close is explicit (`IsEnded`), teardown is bounded, and non-Windows hosts report the capability unsupported. On the Direct3D 12 World host the feed additionally copies its WGC Direct3D 11 frames into consumer-provisioned Direct3D 12 shared simultaneous-access textures (round-robin, same-adapter Direct3D 11→Direct3D 12) the host samples directly — zero-copy, no CPU round-trip; the CPU latest-result path stays live for the Vulkan host and the glow tap, running at a divided cadence in GPU mode. The Tier-B `capture` POST stage covers the CPU path (pixels, resize/minimize/hung/closed targets, concurrent consumption/disposal, resource reclamation); the Tier-C `capture-share` stage verifies the shared-texture transport end to end. |
| Whole-monitor capture | ✅ | On Windows 10 2004 (build 19041) or newer, the same Windows Graphics Capture feed binds a whole monitor through `IGraphicsCaptureItemInterop.CreateForMonitor`, reusing the fixed-extent, fixed-cadence, nonblocking latest-result-wins pump, scaling, bounded teardown, and the same optional Direct3D 12 shared-texture transport of the window path. Monitors are addressed by a 0-based index where 0 is the primary and the rest follow enumeration order; an out-of-range index reports failure. There is no owner window, so the feed ends only when the capture item closes (monitor disconnect). The Tier-B `capture` POST stage's lenient monitor scenario covers the primary-monitor CPU path, treating a headless/CI compositor that yields no frame as a skip. Captured output reflects the compositor's composited monitor, including occluded regions. |
| Frame capture | ✅ | GPU readback feeds observers, hashing, and dependency-free PNG output. Demo console scripts can settle, step, and capture within one process. |
| Native capture (recording graph) | ✅ | `Puck.Recording` records a live world to a modern free AV file as a data-defined `puck.recording.v1` graph: frame source → capture-only overlay compositor (text/rectangle/timecode burned into the file, never the game window) → codec ladder (AV1 preferred, H.264 fallback) → a hand-rolled Matroska/WebM muxer, with mic+system-audio Opus (managed Concentus) mixed to one track by default. `CapturingRenderNode` wraps the World render root permanently at zero idle cost and reads each armed frame back through `SdfEngineNode.ReadOutputPixels`; the Media Foundation hardware encoder ladder and WASAPI loopback/microphone sources are the Windows platform backend (`AddRecordingPlatform`), declining honestly off Windows. `capture.start`/`capture.stop`/`capture.status` are the Immediate control verbs. Verified by `proof.cs record` (negotiated codec, EBML doc-type match, audio/video track presence, capture-only overlay row, status honesty). |

## Determinism, commands, and simulation

| Capability | Status | Current behavior |
|---|---|---|
| Deterministic numerics | ✅ | `Puck.Maths` provides fixed-point values, world coordinates, deterministic vectors, and integer algorithms. Authoritative state does not depend on culture, CPU floating-point mode, or wall-clock time. |
| Command snapshots | ✅ | Physical input, console simulation commands, AI, scripts, replay, and network-shaped sources become typed command entries captured once per tick. Recordings reproduce the same command order and state hashes. |
| Fixed-step hosting | ✅ | `Puck.Launcher` owns the accumulator and calls `IFixedStepSimulation` with integer tick context. Rendering receives interpolation and presentation timing without mutating simulation. |
| Binding profiles | ✅ | Data-driven binding pages use ordered modifiers, hysteresis, latching, and persisted profile documents. A guided `BindingSession` captures deviations through a deterministic multi-press protocol. |
| Tick introspection | ✅ | Transcript, watch, explain, and hash-mark surfaces report command activation and before/after state hashes without becoming simulation input. |
| Persisted replay proofs | ✅ | Demo replay verbs capture, list, and verify deterministic overworld tapes through the same record/replay seams used by the engine. |
| Deterministic garden and RTS proofs | ✅ | Demo simulation pools grow plants and advance RTS units from fixed-point state and command input. The RTS path consumes `IWorldQuery` rather than reading render state. |
| Field-gravity proof | ✅ | A planetoid walker derives gravity from `SdfFieldEvaluator`'s live field gradient and advances as deterministic simulation while rendering through a dynamic transform. |

## Games and in-engine authoring

| Capability | Status | Current behavior |
|---|---|---|
| Overworld demo | ✅ | The default demo is one controller-driven arcade room with bootable cabinets, diegetic screens, live console verbs, device-costume switching, link play, creator mode, and staged screen layouts. Every user capability is reachable in one session. |
| Puck.World | ✅ | The game composition root is document-driven (`puck.world.def.v1`, `--world`) and supports up to four local seats and a larger simulated population, per-user player profiles, fixed-point locomotion, player cameras, screen machines, console scripting, and SDF rendering. Its README documents current operational limitations. |
| Puck.World world document | ✅ | `puck.world.def.v1` is the versioned world document (`WorldJsonContext` source-gen; `$type`-discriminated screens/cameras/predicates/effects). `--world <path>` loads it through the one thick `WorldDefinitionValidator` gate with a loud baked-default fallback on any failure; `world.save [path]` writes the active definition back canonically, so a load→save→load round-trip reproduces the file byte-for-byte (the ouroboros gate). Three checked-in worlds (`default`, `kart-remap`, `expo`) keep the loader a format rather than a single-input deserializer. Verified by `proof.cs worlddoc`. |
| Puck.World runtime mutation + undo | ✅ | Every world-document section is molded through one kind-tagged `WorldMutation` vocabulary (`world.kit.*`, `world.screen.*`, `world.scene.set`, `world.spawns.set`, `world.bindings.*`, …), submitted over `IServerLink.SubmitWorldMutation`, buffered and revalidated through the whole-document gate at the tick boundary before applying. Applied mutations append to a session journal; `world.undo [n]` restores the loaded base and replays the tail-trimmed journal through the same apply path — undo is replay, never a per-mutation inverse. `world.status` reports dirty (journal length) and undoable counts. Verified by `proof.cs mutate`. |
| Puck.World principals + grants | ✅ | Every protocol write submission carries an acting `WorldPrincipal` (seat, console, addon, or peer) checked against the server-side `WorldGrants` table (`Drive`/`Control`/`Mutate`/`Edit` over a body/screen/section/all subject, exclusivity as a grant property) before it applies; `world.grant`/`world.revoke`/`world.grants` are the verbs, and `WorldEngagement` is now a view over `Control` grants rather than a parallel table. An authored WASM addon (`.wat`/`.wasm`, mounted at boot from the world doc's `addons` section) drives a granted body through the same `IServerLink` a human seat uses, and `world.revoke` stops it mid-run — the addon-as-principal keystone proof. Verified by `proof.cs grants`. |
| Puck.World player document + bindings | ✅ | `puck.world.player.v1` is the per-user profile catalog (stable-id profiles with identity/motion/bindings/preferences sections, a monotonic `Revision`) that absorbs and retires `puck.world.profiles.v1` with a one-time on-disk migration. Bindings resolve through a four-layer document pre-merge — engine default ⊕ per-world `bindingOverlays` ⊕ profile bindings ⊕ live rebinds — compiled once per change onto the `Puck.Commands` `BindingProfileDocument`/`PagedInputBindings` stack (one `PagedInputBindings` per seat, hot-reloaded via `Reload`). `player.bind`/`player.bindings`/`profile.save`/`profile.doc` are the verbs. Verified by `proof.cs bindings`. |
| Puck.World storage (cloud-ready) | 🔶 | The player-document store reads/writes carry an opaque `VersionToken` (a content hash on the local backend, the Azure ETag once wired) plus an optional if-match on write, and splits into the per-user, per-profile blob layout (`world/player.json` catalog + `world/profiles/<id>.json` + a local-only `world/local.json` sidecar) the future cloud container uses unchanged. `storage.status` reports the honest current state: local tier authoritative, identity declined or explicit-override-only, cloud endpoint reserved but unwired. This is prepared, not proven — Azure target construction, claims-based identity, and the sync policy are deferred to the cloud arc; only the local backend is exercised today. Verified locally by `proof.cs storage`. |
| Puck.World session write-back | ✅ | `world.save` folds live, non-journaled session state — render levers, the live census and peer-source default, and runtime `screen.insert` machine binds — back into the document's own sections (saved-bytes-only: it composes the serialized snapshot and never mutates the in-memory definition or journal), and `world.status`'s `session-drift` hint names which dimensions still differ from a save. `Assets/worlds/expo.world.json`, the third checked-in world, is the standing genre-neutrality proof artifact: booting `--world expo.world.json` is a visibly different game (a sixth kit row, a `table` kit-assignment policy, three screens) authored entirely through this mutation/session vocabulary, never hand-edited. Verified by `proof.cs expodoc`. |
| Diegetic control plane | ✅ | The console is available on-screen and through stdin/stdout. A terminal screen mirrors output in the world, and an SDF action bar mirrors binding state while the overlay remains available. |
| Puck.World editor mode | ✅ | Per-seat editor sessions divert the seat's intent to honest idle, swap in fly/orbit cameras, flip the seat's active binding group, and resize the layout around a sole editor. Selection is client-local over stable row ids: look-ray picking through a document-derived fixed-point evaluator, plus a bounded proximity-candidate ring (nearest 16 within 32u, narrated by `editor.status`). Drag is a client-local pending-row preview; release submits one whole-row, grant-checked mutation whose frozen preview retires against its own apply or rejection (never an unrelated delivery). Deactivation — exit or seat departure — tears down the drag, frozen preview, and selection. Typed float surfaces reject non-finite values. Verified by `proof.cs editor-mode`, `editor-edit`, and `editor-cameras` on both backends. |
| Puck.World sculpt workbench | ✅ | The creation sub-editor: a per-seat `Puck.Authoring.SculptModel` (primitives, blends, 16-slot palette, hold-style timeline frames, analytic two-bone/spine IK chains, a bounded local undo ring with gesture coalescing) edits inside a client-local bench whose live preview composes a synthetic creation+placement through the SAME stamp path a committed placement uses — the proof pins stamp-equals-preview in pixels. Commit canonicalizes to ONE `UpsertCreation` (doc+hash from the same `CanonicalCreation`); live placements refresh on delivery (animated rows recreate on a content-hash change, release when frames delete). The sculpt mode is a page group (resting/bench/style/frames/rig chords) with `editor.sculpt.*` typed twins, an easel verb wiring a bench camera onto a screen row, and carried cameras/behavior/extensions surviving the round-trip. Verified by `proof.cs sculpt` on both backends. |
| Unified overlay UI | ✅ | `Puck.Overlays`' one backend-neutral decorator draws the console mirror, per-seat binding bars, per-seat editor HUDs, and mutation toasts from a single packed record buffer: per-record viewport clipping confines a seat's records to its split-screen rect, overflow is counted with a loud-once narration and a tail reservation that keeps the toast unstarveable, the glyph pack loads from a prepacked artifact (~19 ms warm versus ~2 s full-atlas decode), and the pass carries its own GPU timestamps (`world.gpu`'s `overlay` column; measured ≤~1 ms at a four-seat full load at 1280x800). Verified by `proof.cs ui-floor`/`editor-edit` and the in-process `scripts/overlay-envelope.cs` battery. |
| Design-token UI | ✅ | Shared spacing, typography, color, surface, and focus tokens drive the current overlay, console, and diegetic UI treatments. |
| Replay museum | ✅ | The overworld includes screen mounts and a Droste-door exhibit that can be wired to named replay and view sources through console verbs. |
| Creator | ✅ | The in-engine editor creates, selects, transforms, groups, colors, animates, saves, loads, previews, and forges `puck.creation.v1` content. Grid snapping supports world and object frames. |
| SDF-to-brick bake | ✅ | GPU rasterization plus deterministic CPU grading, palette fitting, tile assembly, and preview composition produce CGB/DMG-ready backgrounds and sprite sets with budget diagnostics. |
| PBAK asset format | ✅ | A versioned chunked blob stores baked tiles, maps, palettes, metasprites, and animation. The forge parses and links the same wire format it emits. |
| SM83 game framework | ✅ | The framework provides interrupt-driven runtime, input, OAM, background queues, PRNG, saves, text, sound, state dispatch, manifests, and asset linking for hand-authored cartridges. |
| Forged games | ✅ | Brickfall, Volley, Chroma, Klondike Solitaire, and five-card draw Poker build on the shared framework and self-verify by booting on a real Humble GamingBrick machine before writing output. |
| Avatar and world-lens forge | ✅ | Creation documents can be baked into avatar sheets and cartridges, including movement-mode variants and in-session subject-neutral forge commands. |
| Audio proof | ✅ | Framework sound tables and the APU driver produce cartridge audio. Forge verification writes and checks an emulated waveform alongside framebuffer proofs. |

## Input and devices

| Capability | Status | Current behavior |
|---|---|---|
| Switch Pro, Xbox, and DualSense | ✅ | USB transports cover buttons, sticks, triggers, IMU, hotplug, player slots, haptics, and sub-frame timestamps. DualSense Bluetooth is supported. |
| Switch and Xbox Bluetooth | 🧪 | Device-family seams exist; complete handshake and transport coverage is not implemented. |
| DualSense adaptive triggers | ✅ | Trigger effects and output reports are exposed through the device implementation. |
| Keyboard and mouse | ✅ | Window input enters the same command system and can participate in binding profiles and local player routing. |
| Input arbitration | ✅ | `InputArbiter` owns destructive device draining and routes stable lanes as multicast, per-player, owned, or suppressed. |
| HID LampArray | ✅ | Neutral lighting contracts, a Win32 HID transport, and a bind-legend composer drive dynamic per-lamp colors without a vendor SDK. |
| Steam Controller (classic + Triton) | 🔶 | Wired and receiver variants use the classic vendor protocol. The `0x1304` Triton dialect is implemented and hardware-verified (feature/rumble/IMU); the only pending item is its undecoded pairing-event framing. |
| Linux hidraw | 🧪 | Core input code is OS-neutral; a production Linux hidraw transport remains open. |

## Validation and measurement

| Capability | Status | Current behavior |
|---|---|---|
| `Puck.Post` | ✅ | Fail-isolated CPU, same-device GPU, cross-backend, and live-subsystem tiers verify shared engine behavior. The executable prints the current stage roster and summary. |
| Differential fuzzing | ✅ | Seeded document/program generation compares backend output and records reproducible artifacts for a divergence. |
| `puck.bench` | ✅ | A content-blind harness runs registered scenes, samples GPU and CPU timing, sweeps typed feature switches, scores against a versioned formula, and writes `puck.bench.v1` reports. |
| Feature switches | ✅ | `feature.list`, `feature.get`, `feature.set`, and `feature.reset` expose typed live or boot-only controls. Run documents can apply the same registry through `host.features`. |
| Emulator POST batteries | ✅ | Humble and Advanced batteries cover deterministic state, snapshot/restore/fork, hardware behavior, reference ROMs when available, saves, and link sessions. |

## GamingBrick emulation

| Capability | Status | Current behavior |
|---|---|---|
| Humble GamingBrick | ✅ | One shared SM83 core supports DMG, CGB, and AGB compatibility costumes with model-gated behavior, integer timing, PPU, integer APU, mappers, peripherals, battery saves, snapshots, restore, and fork. |
| Advanced GamingBrick | ✅ | The GBA-native core implements ARM7TDMI execution, bus and prefetch timing, DMA, event-scheduled timers (closed-form prescaler/cascade overflow scheduling restores the per-cycle span-collapse fast path during Direct-Sound gameplay, measured 167→349 machine-frames/s single-machine and 1508→3030 mf/s at 16-parallel on a Direct-Sound title, with a synthetic no-audio control unregressed), interrupts, PPU, APU, saves, RTC, snapshots, restore, fork, and diagnostic co-simulation. |
| Battery saves | ✅ | Cartridge SRAM and mapper clock state persist through explicit host storage. Emulated time advances only while the machine runs. |
| One snapshot substrate | ✅ | `Puck.Snapshots` provides one `StateWriter`/`StateReader`/`SnapshotSection`/FNV-1a-fingerprint/`ISnapshotable` surface shared by both cores (39 Humble + 9 Advanced component implementors), with per-core identity records and component discovery orders kept separate. A retained writer, exact-size buffer ownership, and bounded parked-instance pooling cut Humble `Fork` from 3.86 ms to about 62 µs (snapshot allocation to roughly image size) and Advanced `Fork` from 175 µs to 42 µs. |
| Link cable | ✅ | Deterministic pair or multiplayer sessions exchange serial data and survive snapshot/restore churn, including a credit-preserving suspend/resume token on both the Humble serial link and the Advanced link session so severing mid-run discards no per-console overshoot credit. Commercial GB/GBC trade flows and GBA link traffic are part of the optional ROM-backed verification. |
| GB Printer | ✅ | `GamePrinterDevice` transcribes SameBoy's printer protocol (magic/command/RLE/checksum/status pipeline) as an `ISerialPeer` on the existing link seam; completed print buffers reach the host as a machine-to-host event. A Tier A printer stage covers RLE round-trip byte-identity, busy/done timing, and mid-image machine+printer snapshot churn. |
| CGB infrared channel | 🔶 | `InfraredPort` implements the RP register (0xFF56, Color-gated) and the HuC1/HuC3 cartridge IR windows as two views of one light line, with peer-plus-hardware-self-sensing (SameBoy `Core/memory.c`/`Core/timing.c`): the CGB costume unconditionally reads its own lit LED back on every view, while the Agb costume (SameBoy's newer-than-CGB-E suppression) reads its own light only when a HuC1/HuC3 cartridge is present; `IrLinkSession` mirrors the serial link's deterministic interleave with its own credit-preserving resume token. A Tier A infrared-exchange stage covers self-sensing (unpaired CGB via RP and HuC1, unpaired Agb suppression, cross-view consistency), both-direction pattern exchange, replay, and churn identity. Analog IR warmup/decay and a Mystery Gift consumer (menu-drive timing scale) remain open. |
| GBA multiplayer-ready signal | 🔶 | Cable traffic and multiplayer transfers work, but a partner-presence ready-line required by some commercial lobbies is not yet packed into `SIOCNT`. |
| Fleet measurement | ✅ | The Humble battery measures independent and shared-stream fleets, burst catch-up, lifecycle operations, snapshots, forks, mailbox checks, and memory footprint with determinism guards, including a repeatable mixed-mapper fleet mode for megamorphic dispatch-site measurement. |
| Machine-neutral time-travel | ✅ | `MachineTimeTravel` in `Puck.Hosting` provides keyframe-every-120, delta-compressed rewind, a persistent pooled lookahead fork for runahead, and a cycle-budget fast-forward multiplier through `ITimeTravelMachine` on both hosts. Overworld cabinets and the AGB debug scene share neutral `rewind`/`rewind.status`/`runahead`/`fastforward` verbs; unreplayable mutations (link, device swap, cart swap, win pokes, unpark) drop history, and sensor-input or linked carts refuse the ring honestly. Verified by demo run over the console plane; the underlying snapshot/restore path is Tier A battery-gated on both cores. |
| Console introspection plane | 🔶 | Full-bus peek and poke on the machine contract back an `hgb.*` debug verb family (regs/pause/step/frame/until/status/snap/restore) mirroring the existing `agb.*` suite. Read/write watchpoints are a dormant, zero-cost-when-unarmed bus decorator on the Humble core (proven free by the zero-alloc gate and unmoved throughput); `Sm83Disassembler` and `ArmDisassembler` feed `hgb.dis`/`agb.dis` on both cores. Watchpoints do not yet exist on the Advanced core. |
| Peripheral feedback axis | ✅ | Rumble (Humble MBC5 motor-bit latch; Advanced GPIO rumble pin keyed by `AgbGameOverrides`), solar sensing (Boktai-keyed GPIO counter, light entering as recorded per-segment input), and tilt (Humble `ITiltSensor` DI seam behind MBC7's latch; Advanced address-mapped tilt keyed per game code) are latched deterministic device state on both cores, each covered by a Tier A device-proof stage. Cabinet motor output forwards through `IGamepadOutput` resolved via seat routing; controller accelerometer feeds the per-segment tilt sample. AGB gyro (WarioWare) is out of scope. |
| SM83 evidence tooling | 🔶 | A skip-on-missing SingleStepTests/sm83 Tier B stage runs per-instruction vectors across 498 families on a flat test bus through the shared SM83 core, with STOP and EI as documented, cited oracle-conflict skips. BESS export/import diagnostics round-trip Humble state with SameBoy/mGBA. Evidence for the one-shared-SM83-core doctrine, not a gate. |

## Bare metal

| Capability | Status | Current behavior |
|---|---|---|
| Freestanding C# runtime | ✅ | A Native AOT system module runs without the managed BCL or GC and uses mimalloc for allocation. |
| Freestanding Vulkan window | ✅ | A small Win32 executable creates a Vulkan device, swapchain, and presentation loop without the desktop host stack. |
| UEFI Steam Deck boot | ✅ | The UEFI path boots on Steam Deck hardware and writes the GOP framebuffer with the required memory attributes and display rotation. |
| Direct boot game host | 🧪 | The process/runtime pieces exist, but Puck is not yet a complete standalone operating environment on the device. |

## Supporting libraries

| Library | Responsibility |
|---|---|
| `Puck.Assets` | Content-addressed byte sources and caching. |
| `Puck.Storage` | Local and Azure object blobs plus JSON document helpers. |
| `Puck.Hosting` | Render-node tree, capability propagation, fixed-step context, timing, and publication. |
| `Puck.Compositing` | Backend-neutral draw-command replay. |
| `Puck.Text` | Atlas models, text layout, and coverage distance generation. |
| `Puck.Capture` | Frame observers, hashing, and PNG encoding. |
| `Puck.Recording` | The `puck.recording.v1` capture graph: overlay compositor, encoder-ladder contracts, Opus audio lane, hand-rolled Matroska/WebM muxer, and the `ICaptureSink` recording session. |

Statuses describe the current code and available evidence, not a release
timeline. Update this catalog when a capability or limitation changes.
