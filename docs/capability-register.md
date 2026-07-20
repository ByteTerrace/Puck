# Puck Capability Register

This document settles what Puck can and cannot do, as of branch tip `8f8c738` (Arcs 1–7 of the Demo → World port landed). It is written to be read once.

**On trusting this file.** Two kinds of fact live here. **Decisions** — what was built, what was deliberately not built, and why — do not expire; they are the reason this file exists. **Numbers** — enum members, verb lists, ceilings, stage counts — are regenerable and drift with the code. When a number matters to a change, read the cited `file:line` rather than trusting the value printed here. A number that disagrees with code is stale; a decision that disagrees with code is a change someone made without recording it.

Status tokens: **SHIPPED** (works in the live composition root), **PARTIAL** (works with a named gap), **SEAM-READY** (built and proven, no live consumer), **DESIGNED** (specified, not built), **REFUSED** (decided against).

---

## Questions this document closes

- Does Puck have 4-player split screen? Local couch multiplayer? Network multiplayer?
- Can Puck run an emulator inside the game world? Which consoles? Can I trade Pokémon across two cabinets? Do cartridge saves persist? Can I rewind an embedded emulator?
- Can Puck host another engine? Can a Puck world contain another Puck world?
- Does Puck have networking, sockets, matchmaking, authentication?
- Does Puck have audio? Positional audio? HRTF? Music? Sampled clips?
- Does Puck have a live in-engine editor? Can I sculpt, animate, rig, and place things while the game runs?
- Can Puck record video? Screenshots? Deterministic replays?
- Does Puck support scripting or mods? In what language? What can a script actually do?
- Which GPU backends? Can I switch backends at runtime? Is ray tracing used?
- How many players, entities, screens, cameras, views, audio voices, SDF instances?
- What is the frame-rate contract, and what measures it?
- Which documents does the running game actually boot from?

---

## Rendering and GPU backends

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Vulkan + Direct3D 12, one engine | SHIPPED | The same `SdfWorldEngine` renders on Vulkan (SPIR-V) and a LUID-matched D3D12 device (DXIL) from one HLSL source tree, gated for parity. | 18 matched `.spv`/`.dxil` kernel pairs; 40+ `world-*` parity stages | Post Tier C stage `world` — `src/Puck.Post/Stages/WorldStage.cs:13` |
| Backend selection | SHIPPED | Boot-time categorical choice via `--backend` or `host.backend`; one backend descriptor per process. | 1 | `src/Puck.World/Program.cs:93`; `src/Puck.World/WorldHost.cs:60` |
| Live backend hot-switch | SEAM-READY | `BackendSwitcher` swaps Vulkan↔D3D12 mid-session with content re-targeted and rollback; Puck.World registers one descriptor, so `Switch()` is a no-op there. | — | `src/Puck.Launcher/BackendSwitcher.cs`; Post Tier D `hot-switch` |
| Parity posture | SHIPPED | Relaxed by default (mean-abs-error ≤ 0.35, ≤ 20% pixels differing); `PUCK_PARITY_STRICT=1` opts into 6 calibrated-strict families. Ratified 2026-07-03. | 0.35 / 20% | `src/Puck.Post/ParityCheck.cs:183-315` |
| Present modes | SHIPPED | Vsync / Mailbox / Immediate / Adaptive on both backends, with documented per-backend fallback. | 4 | `src/Puck.Abstractions/Presentation/PresentMode.cs:9-24` |
| VRR + adaptive pacing | SHIPPED | EDID parsing (DisplayID Adaptive-Sync, HDMI Forum VRR, FreeSync) feeds `PresentPacingPolicy`; closed loop, fixture-tested. | — | Post Tier A `display-timing` — `src/Puck.Post/Stages/DisplayTimingStage.cs:162-277` |
| Render scale / upscaling | SHIPPED | Presentation-only, bit-exact at native; 5 tiers plus continuous sharpness blend. | q ∈ {255, 221, 181, 128, 90} | `src/Puck.Scene/WorldRenderScaleTier.cs:43` |
| Hardware ray tracing | PARTIAL | Inline ray-query is dual-backend and parity-gated, but exists as a normal-comparison debug probe; the shipped march is compute sphere tracing. | TLAS 64 instances | `src/Puck.Post/Stages/RtStage.cs`; `sdf-world-rt-debug.rq.comp.hlsl` |
| Cross-backend document composition | REFUSED | A `world.produce` request mixing backends fails preflight with attributed errors. Surface import/export exists; document-driven mixed-backend assembly is a stated capability rejection. | — | `docs/capability-catalog.md:48,61` |
| Device-loss recovery | SHIPPED | Per-backend `IDeviceLostRecoverable`; the switcher forwards to the active backend. | — | Post Tier D `device-lost` |
| GPU frame budget | PARTIAL | 100 ms is a catastrophic-regression tripwire on one 960×600 hero scene, not a calibrated budget. | 100.0 ms | `src/Puck.Post/Stages/GpuBudgetStage.cs:21` |

---

## SDF VM and world renderer

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| ISA breadth | SHIPPED | 22 live ops: point transforms, field ops, domain folds (including 17 wallpaper IUC groups), a scoped accumulator, shape and blend instructions. | 22 ops | `src/Puck.SdfVm/SdfOp.cs:1-150` |
| Shapes | SHIPPED | 17 primitives including a font-atlas Glyph (real marchable geometry) and SampledRegion (baked carve brick). | 17 | `src/Puck.SdfVm/SdfShapeType.cs:1-46` |
| Blends | SHIPPED | Union/Subtraction/Intersection/Xor plus smooth and chamfer variants. | 10 | `src/Puck.SdfVm/SdfBlendOp.cs:15-41` |
| Instances per program | SHIPPED | Mask-first uniform-grid cull measured at cap (beam 187.8 ms → 6.6 ms @ 4096 scattered carves). | 16384 | `src/Puck.SdfVm/SdfProgramBuilder.cs:19` |
| Diegetic screen surfaces | SHIPPED | Bound by a single-uint `screenMask`; a program declaring more throws. | 32 | `SdfProgramBuilder.cs:24`; `SdfWorldEngine.cs:174` |
| Composited viewports | SHIPPED | The composite kernel's `sources[5]` array is the absolute ceiling; Puck.World provisions 4. | 5 | `SdfWorldEngine.cs:163` |
| Field-scope nesting | SHIPPED | Depth 1. Raising it requires converting a scalar pair in `mapCore` into an indexed stack. | 1 | `SdfProgramBuilder.cs:33` |
| Shadow / AO tiers | SHIPPED | Grid-cull soft shadows (bit-identical to flat march) with 3-way fallback and a fast-march fleet tier; 3-tap or 1-sample AO. All runtime A/B levers. | 1024 instances per shadow gather | `sdf-world.hlsli:988-1200` |
| Debug view modes | SHIPPED | Index is the wire value. | 11 | `src/Puck.SdfVm/DebugViewModes.cs:13-25` |
| Carve/bake to voxel bricks | SEAM-READY | `SdfCarveBakePlanner` + brick pool are Post-gated; Puck.World has zero consumers — in-game sculpt carves stay analytic. | 8 slots × 128³ = 64 MB; region 1023/axis | `SdfBrickPoolLayout.cs`; no `Puck.World` reference |
| Screen source is engine-agnostic | SHIPPED | A screen slab samples an opaque `nint` image view; the VM has no knowledge of what produced it. | — | `src/Puck.SdfVm/Views/GuestSurfaceView.cs:6-16` |

---

## Viewports, cameras, split screen

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Local split screen | SHIPPED | Four local seats render in split screen; the engine is constructed at that capacity and throws above it. | `LocalSeatCount` = 4 | `src/Puck.World/Server/WorldPopulation.cs:60`; `SdfWorldEngine.cs:1662` |
| Built-in layout ladder | SHIPPED | 1 = fullscreen, 2 = side-by-side, 3 = big-top + two bottom, 4 = 2×2 quad, eased by `WorldViewComposer`. | 4 | `src/Puck.World/Client/WorldFrameSource.cs:849-860` |
| Custom layouts as data | SHIPPED | Named layouts keyed by seat count, each a list of normalized rects binding a seat or an authored camera; live override via `view.layout`. | — | `WorldViews.cs:5-47`; `WorldViewCommandModule.cs` |
| Cameras as data | SHIPPED | Orthogonal anchor + rig: 5 authored rig kinds compile to 6 engine rigs through one seam. | 64 cameras; 4096 px/axis | `WorldRigCompiler.cs:19`; `WorldDefinitionValidator.cs:30,37` |
| Offscreen "jumbotron" views | SHIPPED | Registration is cheap; refresh is hard-budgeted round-robin, with a further World-side frame divisor. | 64 registered, 4 rendered/frame, divisor 1–8 | `src/Puck.SdfVm/Views/ViewStack.cs:121,128`; `WorldScreenBinder.cs:111` |
| Jumbotron content kinds in World | PARTIAL | Only `SdfCameraView` (this world filmed by an authored camera) is wired. `NestedWorldView` and `GuestSurfaceView` exist and are proven, with zero Puck.World references. | 1 of 3 | grep: no `NestedWorldView`/`GuestSurfaceView` under `src/Puck.World` |
| Overlay compositing | SHIPPED | Split-screen compositing is native to the SDF compute kernel; `Puck.Compositing.GpuCompositor` is an unrelated 2D overlay recorder with no Puck.World usage. | — | `sdf-world-composite.comp`; `GpuCompositor.cs` |

**Known validator gap.** `WorldDefinitionValidator.ValidateViews` (`WorldDefinitionValidator.cs:1458-1519`) checks each layout slot's rect and camera reference but never caps a layout's total slot count against the engine's constructed viewport capacity of 4. A layout mixing 4 seat slots with a camera slot validates, loads, and then throws at first submit (`SdfWorldEngine.cs:1662`). Which of validator-check, composer-clamp, or raising capacity to 5 is the intended contract is undecided.

---

## Input and bindings

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Controller families | PARTIAL | Switch Pro, Xbox One/Series, DualSense: hardware-verified (DualSense also over Bluetooth). Steam Controller Triton: implemented and hardware-verified, lizard-mode suppression unchecked. Steam Controller classic: never run on real silicon. | 5 families | `src/Puck.Input/Devices/GamepadType.cs:4-19`; `README.md:463-543` |
| Keyboard | SHIPPED | Device id `default`, permanently seat 0, riding the identical `InputSignal` vocabulary as pads — a peer, not a fallback. | — | `PlayerRoster.cs:112-115` |
| Xbox pad count | SHIPPED | XInput's own API limit, independent of the 4-seat roster cap. HID-path pads have no per-family cap. | 4 | `Win32XboxAcquisitionSource.cs:16` |
| Binding document | SHIPPED | `puck.bindings.v1`: modifiers with press/release hysteresis, and chord rows that resolve to a page or a command. Chord order is significant; chord depth is unbounded by data. | 23 tracked button edges/pad | `BindingProfileDocument.cs:20`; `HeldOrderTracker.cs` |
| Live remapping | SHIPPED | Three layers: session (`player.bind`), durable profile (`profile.save`), world overlay (`world.bindings.*`), composed base-first with per-entry page merge. | — | `WorldBindingCommandModule.cs:38-250`; `proof.cs bindings` |
| Mode switching | SHIPPED | A seat's active page group flips a pointer on an already-compiled profile; latches survive the flip. | — | `WorldSeatBindings.cs:113` |
| Binding-bar overlay | SHIPPED | Per-seat glyph-family binding cluster, ported into Puck.World. | — | `src/Puck.World/WorldOverlayFeed.cs:221-252` |
| Guided binding sessions | SEAM-READY | Determinism Post-gated; zero hosts anywhere. Nobody has chosen between a diegetic tutorial and a `bind.session` verb. | — | Post Tier A `binding-session`; grep: no consumers |
| Per-key RGB bind legend | SEAM-READY | Hardware-verified on a Logitech G915; no host builds the per-tick legend state. | 115 lamps, ≤ 30 Hz | `src/Puck.Input/README.md:445-461` |
| Non-Windows HID transport | DESIGNED | Parsers are OS-agnostic; only Win32 HID transport exists. | 0 | `src/Puck.Input/README.md:481-483` |

---

## Networking and multiplayer

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Client/server split | SHIPPED | `WorldServer` is authoritative; `WorldClient` only interpolates snapshots and submits intents — it never advances sim state. | — | `Server/WorldServer.cs:50`; `Client/WorldClient.cs:175,300` |
| Socket / HTTP / RPC transport | REFUSED (absent) | None exists. A repo-wide grep for Socket/Tcp/Udp/WebSocket/Kestrel/gRPC/QUIC/NamedPipe returns zero implementation hits. Loopback in-process is the only `IServerLink`. | 1 transport | `Protocol/LoopbackTransport.cs:11` |
| Network multiplayer | REFUSED (unscheduled) | Local couch play for 4 seats is real and ships. Nothing crosses a process boundary. No arc of the 12-arc port plan targets a transport. The seam is deliberate; building it is on nobody's roadmap. | — | `docs/demo-to-world-port-plan.md` Arc listing |
| Transport-agnostic protocol | SHIPPED | Local seats submit per-tick intents over the identical `IServerLink` path a remote client would use; `WorldProtocol.Version` is checked at Join. | Version 1 | `Protocol/WorldProtocol.cs:8` |
| Entity table | SHIPPED | 4 reserved local seats plus simulated/inhabited bodies; peers allocate up from slot 4, inhabitants down from 127. | 128 total, 124 simulated | `WorldPopulation.cs:57,60,65` |
| "Network peers" | SHIPPED (named forward-looking) | Entities 4..127 are the deterministic simulated census driven by Wander/Attend producers. No remote human sits behind one. | 124 | `PlayerIntent.cs:102-110` |
| Correction / latency masking | SHIPPED | Three continuity kinds (Continuous / Teleport / Correction); errors above the ceiling snap instead of easing. | 3 world units | `Protocol/WorldSnapshot.cs:29` |
| Capability grants | SHIPPED | One table of (principal, capability, subject) across 4 capabilities (Drive/Control/Mutate/Edit) and 4 principal kinds, with exclusive holds. Seeded permissive locally. | 4 × 4 | `Server/WorldGrants.cs:51-146` |
| Authentication | REFUSED (absent) | The grant table authorizes established identities. `WorldPrincipal` is a bare value struct any in-process caller constructs; no credential, token, or handshake exists anywhere. | — | `Protocol/WorldPrincipal.cs:34` |

---

## Embedded emulators (GamingBricks)

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Which machines | SHIPPED | Two deterministic cores as `IScreenMachineEngine`: `gaming-brick` (one SM83 core across DMG/CGB/AGB costumes) and `advanced-gaming-brick` (native ARM7TDMI GBA). | 2 | `src/Puck.World/Program.cs:372-373` |
| Accuracy | SHIPPED | SM83: 498,000/498,000 SST vectors across 498/500 opcode families (2 pinned oracle-conflict skips), mooneye-ppu 19/19. GBA: ARM fuzz + accuracy suite + real TCHK10 aging-cartridge hardware. | — | `Puck.HumbleGamingBrick.Post/PostStages.cs`; `Puck.AdvancedGamingBrick.Post/PostStages.cs` |
| Embedding in the world | SHIPPED | A machine is one of five interchangeable producers on a screen slot; bootable at declare time or live via `screen.insert` with no engine rebuild. The default world runs a BIOS-backed `.gba` cartridge on screen 4 continuously. | 32 screens | `WorldScreenBinder.cs:53-220` |
| Player → machine input | PARTIAL | `player.engage` grants an exclusive route and merges multiplayer pads per host tick. The avatar route wires move + one action button; East/West/North, D-pad, shoulders, Start, Back, triggers, tilt are unmapped. | 1 of 12 buttons | `WorldEngagement.cs:202-221` |
| Machine audio | SHIPPED | Every booted machine synthesizes from boot unconditionally and binds into the world's positional speaker plan; boot/eject/swap self-heals by reference diff. | ~192 KB + low-single-digit % CPU each | `WorldAudioDirector.cs:343-389` |
| Live dmg↔cgb↔agb swap | SHIPPED | `screen.options` reconfigures with no reboot and no lost progress. The GBA-native core does not implement it (nothing to swap to). | — | `ScreenCommandModule.cs:340` |
| Cable linking in the world | PARTIAL | `screen.link` exists; every registered engine reports the link dormant. The queued per-machine worker model conflicts with the single-thread instruction-atomic interleave the link session needs. Core-level linking is real and Post-gated. | 0 live links | `GamingBrickEngine.cs:29-54` |
| Cartridge SRAM saves | PARTIAL | Both boot sites pass `savePath: null`, so a battery save never round-trips to disk across a World session. Core-level save round-trips are Post-gated. | — | `WorldScreenBinder.cs:400,1964` |
| Rewind / runahead / fast-forward | SEAM-READY | Both hosts implement `ITimeTravelMachine` and both batteries gate it; Puck.World has zero references and no verb. | Fork ≈ 62 µs / 42 µs | grep: no `ITimeTravelMachine` under `src/Puck.World` |
| Machine rumble to a real pad | SEAM-READY | `IFeedbackMachine` implemented and gated; no World consumer. | — | grep: no consumers |
| Memory peek | PARTIAL | `screen.peek` works on the SM83 core; the GBA core declines with a distinct message. | — | `WorldScreenBinder.cs:341` |
| GBA multiplayer lobby | PARTIAL | The link cable moves bytes correctly, but `PackSioControl` does not pack the Multiplayer SIOCNT SD/SI ready-line bits, so a commercial lobby never completes a round. | — | gaming-bricks skill, pinned frontier |

---

## Hosting, recursion, nested worlds

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Hosting another engine's pixels | SHIPPED | `IScreenMachine` is the live answer: any deterministic machine hosted as a frame + input endpoint, resolved by DI, never named concretely by the host. | 32 screens | `src/Puck.Abstractions/Machines/IScreenMachine.cs:6-12` |
| Generic host tree | SHIPPED | `IRenderNode.ProduceFrame` + `OnDeviceLost` forwarding, with an inherited/held capability seam (`IHostContext`) and a terminal-control baton. Real and load-bearing for backend/decorator composition. | — | `src/Puck.Hosting/IRenderNode.cs:11-26`; `IHostContext.cs` |
| Per-viewport child render nodes | REFUSED (dead) | `SdfWorldRenderSpec.Children` and `GamingBrickChildNode` have zero producers and zero construction sites; their named caller `OverworldRenderNode` no longer exists. This is orphaned residue, not a path to extend. | 0 producers | grep: no `Children =` initializer, no `new GamingBrickChildNode(` |
| A world inside a world | DESIGNED | `NestedWorldView` works and was proven in the retired Demo (Museum wallpaper + Droste door). No document field and no verb reach it in Puck.World. The committed structural claim: a nested world is a second world document with its own server — a capability, not a camera kind. | 160×144; brick capacity 0 | `NestedWorldView.cs`; `docs/demo-to-world-port-plan.md:7055-7145` (Arc 11) |
| Self-referential recursion | SHIPPED | The default world's screen 1 shows a live capture of its own window. `ViewStack` scopes a view's own screens to unbound during its render, so feedback cannot compound. | — | `ViewStack.cs:253-259` |
| Cross-backend surface interop | PARTIAL | Import/export paths carry GPU surfaces without a CPU copy in both directions; document-driven cross-backend composition remains refused (see Rendering). | — | `docs/capability-catalog.md:46,48` |

---

## Audio

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Mixing core | SHIPPED | Synchronous, allocation-free, fixed-point end to end (s16 × Q16 → int32 → cubic soft-clip → s16), never a libm call. The offline proof and the live pump call identical code. | 48000 Hz; 256 frames/block | `Audio/WorldAudioMixer.cs:44-60,237` |
| Spatialization | SHIPPED (this is the ceiling) | Equal-power stereo pan from listener azimuth plus finite-support distance attenuation. Elevation is ignored. HRTF, occlusion, directional cones, and Doppler are tracked deferrals, not partial implementations. | — | `WorldAudioMixer.cs:357-449` |
| Synth | SHIPPED | 32 voices, zero steady-state allocation; sine/pulse/saw/triangle/noise, ADSR, control-rate sweep and vibrato, one Chamberlin SVF per voice, steal-quietest. | 32 voices, 32 patches | `Audio/WorldVoiceSynth.cs:106-481` |
| Sampled clip playback | REFUSED (v1 scope) | An emission facet references a patch or a tune, never an authored PCM clip. Tracked as closeout-11. | — | `WorldSpeaker.cs` |
| Audio as data | SHIPPED | Speakers, tunes, patches, cue table, and per-row emission facets are ordinary document sections mutated through the same upsert vocabulary as everything else. | 32 emitters, 16 triggers, 16 sources | `WorldSpeaker.cs:1-289` |
| Determinism | SHIPPED | Two fresh runs of a scripted timeline hash-match over raw s16 PCM; the hash is self-referential and re-goldens on a deliberate mix-law change. | — | `scripts/audio-mix.cs` batteries (a),(h),(i) |
| Device stack | PARTIAL | Windows/WASAPI only: event-driven shared-mode s16 stereo 48 kHz on a dedicated MTA thread. Off Windows the service parks "unsupported" and never starts a thread. The mixer itself is platform-neutral. | 1 platform | `WasapiAudioRenderDevice.cs`; `AudioRenderPlatform` |
| Failure posture | SHIPPED | Plays silent, never crashes: any failing HRESULT parks the pump; the governor retries the default endpoint on a fixed cadence; a fill-callback exception degrades to one silent quantum. | 1000 ms retry | `WorldAudioRenderService.cs:22-27,104-160` |
| Cue table | SHIPPED | A closed event-token vocabulary binds to a patch and placement; each cue takes one of a reserved transient pool. `player.jump`/`player.land` are reserved with no producer. A cartridge declared in the document (as opposed to `screen.insert`ed) fires no `screen.boot` cue — a known, tracked defect. | 4 transient slots; 2 s loop cap | `WorldAudioDirector.cs:646-824`; `src/Puck.World/README.md:1607` |

---

## Capture, recording, replay

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| A/V capture | SHIPPED (Windows) | `capture.start`/`stop`/`status` arm a present-tap through the render decorator; hardware-MFT-first video, WASAPI loopback + microphone audio, hand-rolled Matroska/WebM muxing. Off Windows the factories decline honestly. | codecs `av1`,`h264`; 160 kbps Opus; queue 8 | `WorldRecordingCommandModule.cs`; `MediaFoundationVideoEncoderFactory.cs:39` |
| Backend-agnostic capture | SHIPPED | `CapturingRenderNode` touches only the neutral `Surface`/CPU readback; the OS, not the GPU backend, is the gate. | — | `src/Puck.Hosting/CapturingRenderNode.cs:6-14` |
| Capture overlays | SHIPPED | Text, Rect, Timecode; present only in the recording, never in the live window. | 3 kinds | `RecordingDocument.cs:41`; `RecordingEnums.cs:28-35` |
| Screenshots | SHIPPED | `world.screenshot <path.png>` arms a one-shot PNG of the next composed frame, overlay included. | — | `WorldUiCommandModule.cs:14-40` |
| Muxer determinism | SHIPPED | Byte-identical on repeat mux, proven by a standalone script — not a Post stage. Puck.Recording has zero Post coverage. | — | `dotnet run src/Puck.Recording/scripts/mux-check.cs` |
| Command-snapshot replay | SEAM-READY | Record → repeat → pack → round-trip → replay hash equality is Post-gated at Tier A, but only against `NeutralSim`, a synthetic stand-in. Puck.World has zero references and no `--record`/`--replay` surface. `InputRouter` already implements `ISnapshotSource`, so the integration point is a decorator swap away. | 600 ticks | `DeterminismHarness.cs:46-74`; `Replay/NeutralSim.cs` |
| Deterministic offline render | DESIGNED | `RecordingClock.Sim` is specified, validated, and never assigned anywhere in the tree. It would additionally need a tick-driven pump and a timestamp that excludes `AccumulatorTicks`' wall-clock remainder. | — | `RecordingEnums.cs:8`; `FrameContext.cs:37` |

---

## Authoring and the in-engine editor

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Editor mode | SHIPPED | Any joined, confirmed seat toggles in and out with no prerequisite state; intent diverts to Idle, the binding group flips, the camera seeds at the current framing. | 4 concurrent seats | `Client/WorldEditorSession.cs:166-223` |
| Editor cameras | SHIPPED | Free-fly and orbit with vantage-preserving handoff. | 0.5–64 u/s; 1.5–60 u | `WorldEditorSession.cs:40-51` |
| Selection | PARTIAL | Deterministic fixed-point SDF look-ray picking plus bounded proximity cycling, across 6 of the document's 23 sections (Scene, Screens, Spawns, Cameras, Placements, Speakers). The rest are edited by typed console verbs; they are not spatially pickable objects. | 6 of 23; radius 32 u, 16 candidates | `EditorSelectionCommandModule.cs:757`; `WorldEditorPicker.cs` |
| Drag authoring | SHIPPED | Grab → move → commit as one whole-row mutation on release, or cancel with no mutation ever existing. The drag never crosses the wire. | 12-frame preview deadline | `Client/WorldEditorDrag.cs` |
| Position snap | SHIPPED | Planar/uniform lattice snap with magnetize and release hysteresis via `editor.snap`. | — | `src/Puck.Authoring/GridSnap.cs:82-141` |
| Rotation + align-to-shape snap | SEAM-READY | 90°/45° rotation snapping (a real 24-element octahedral group) and face/inner-flush/center reference snapping are complete pure math with zero Puck.World callers. | 0 callers | `GridSnap.cs:8-64,143-174` |
| Sculpt workbench | SHIPPED | New/edit/commit lifecycle whose live preview composes through the identical stamper a committed stamp uses — what you sculpt is what stamps, byte-for-byte. | 7 primitives; 48 shapes/stamp; 16 palette slots | `Client/WorldWorkbench.cs`; `WorldPlacementPolicy.cs:20` |
| Sculpt style ops | SHIPPED | 7 blend ops, smooth radius, per-slot material editing, mirror, twist, bend, dilate, onion, grouping. | smooth ≤ 0.5; twist ±3.0 rad/u | `EditorSculptStyleCommandModule.cs`; `CreationDocument.cs:47-59` |
| Animation timeline | SHIPPED | Hold-style frame timeline; the same cadence constant drives the workbench preview and the shipped runtime replay. | 1–60 ticks/frame, default 8 | `EditorSculptRigCommandModule.cs:39-201` |
| IK rigs | SHIPPED | Analytic 2-bone limb solver and closed-form N-link drag spine — real math, no stubs. | 16 chains | `src/Puck.Authoring/ChainSolver.cs` |
| Undo | SHIPPED | Two independent mechanisms: a bounded per-bench sculpt ring discarded on exit, and world-level `world.undo` that replays the append-only journal minus its tail through the same apply path. | 64 sculpt; journal unbounded | `EditHistory.cs`; `WorldServer.cs:583-620` |
| Inhabitation | SHIPPED | `world.placement.inhabit` molds a placement's inhabit facet; the body renders as a body-rooted creation stamp riding the live interpolated pose, so an authored walk cycle plays while the body moves. | 8 stamp registrations world-wide | `WorldPlacementCommandModule.cs`; `Client/WorldStampPool.cs:146` |
| Screens on creatures | PARTIAL | A creation's declared faces derive into screen rows lit by a 4-token feed grammar; a 5th simultaneous face world-wide drops with a warning rather than a rejection. | 4, reserved at slots [24,28) | `Client/WorldCreationFacets.cs:17-99` |
| Engraved text on creations | DESIGNED | `CreationDocument.TextRuns` is fully modeled, budget-charged, and canonicalization-validated; World deliberately binds no world-space glyph atlas, so nothing emits. | — | `CreationDocument.cs:62-115`; `WorldPlacementStamper.cs:15-18` |
| Capacity honesty | SHIPPED | Every render-affecting authoring act is checked against a render envelope probed once at boot; a miss is rejected loudly naming the exact ceiling, never crashed or silently clamped. Concurrent client-local previews are composed and checked together. | probed per world | `WorldRenderEnvelope.cs:37-61` |

---

## World document and data model

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| The live document | SHIPPED | `puck.world.def.v1` (`WorldDefinition`) is what the running game boots from, loaded via `--world` or the checked-in default. | 23 sections | `WorldDefinition.cs:1548-1590` |
| Mutation vocabulary | SHIPPED | Whole-row upsert / whole-section replace addressed by stable id — never a field poke. Every mutation composes a full candidate document and revalidates the whole thing before swap-in. | 32 kinds | `Protocol/WorldMutation.cs:22-270` |
| Validation | SHIPPED | One thick gate on every load and every mutation candidate: id uniqueness, forward-reference id sets, and a documented sim-affecting vs presentation-only field taxonomy. | — | `WorldDefinitionValidator.cs:58-263` |
| Journal and undo | SHIPPED | A base document plus an append-only mutation list; undo is deterministic re-derivation, not a stored-state stack. `world.save` compacts it. | unbounded | `Server/WorldServer.cs:47-141` |
| Save round-trip | SHIPPED | A canonical writer (stable order, invariant numbers, LF, one trailing newline); `world.save` folds live session dimensions into their document homes without mutating live state, proven idempotent by an ouroboros gate. | — | `WorldSessionCapture.cs:23-55` |
| Player profiles | SHIPPED | `puck.world.player.v1` is a separate family by design — a profile travels with a person, a world document describes a world. Split blob layout under local AppData with a non-roaming machine sidecar. | — | `WorldPlayerDocument.cs:33`; `Server/WorldProfileStore.cs` |
| Cloud storage | REFUSED (default) | `WorldStorageDefaults.None` ships; `storage.status` reports "tier local (authoritative); cloud unwired" truthfully. | — | `WorldDefinition.cs:1330-1332` |
| `puck.run.v1` | SEAM-READY | The engine-tier run document is parse/validate/round-trip/schema-gated at Post Tier A and boots nothing. Its frame-materialization path (`JsonSdfFrameSource`) has zero consumers outside its own file, and `GraphBuilder` no longer exists. It is not what Puck.World renders from. | — | `src/Puck.Post/Stages/RunDocumentStage.cs` |

---

## Scripting and addons

| Capability | Status | Settled answer | Ceiling | Evidence |
|---|---|---|---|---|
| Substrate | SHIPPED | `puck.addon.v1`: deterministic WASM/WAT on Wasmtime, one store per addon, driven once per sim tick. Fuel on, threads off, SIMD off, NaN canonicalization on — every knob explicit. | Wasmtime pinned exactly `[44.0.0]` | `src/Puck.Scripting/ScriptingEngine.cs:24-41` |
| ABI | SHIPPED | A byte contract, not a function-call ABI: a 40-byte fixed-point snapshot in, up to 64 fixed-stride 24-byte command records out. No float crosses the boundary anywhere. | 40 B / 24 B × 64 | `AddonAbi.cs:11-23` |
| Sandbox | SHIPPED | Zero host imports are supplied. A guest is a pure poll target: no callbacks, no I/O, nothing but its own linear memory. A module declaring an import fails to link. | 16 MiB, 512 KiB stack | `AddonInstance.cs:19,281` |
| Fault model | SHIPPED | Faults are sticky and terminal; recovery is an explicit `Enable()` that disposes and reinstantiates from the cached module — never a mid-tick retry. | 8 fault kinds; 1,000,000 fuel/tick | `AddonInstance.cs:118-200` |
| Pad vocabulary | SHIPPED (frozen) | Exactly 10 abstract pad ids. An 11th is an ABI-version event. | 10 | `PadCommandId.cs` |
| What an addon can do in World | PARTIAL | An addon is a principal that must be granted Drive over a body by console command; it never self-authorizes. The translator wires 4 of 10 pad ids and always writes zero buttons into the snapshot. No path exists for an addon to submit a mutation, engage a screen, or edit a profile. | Drive only; 4 of 10 lanes | `Client/WorldAddonDriver.cs:96-204` |
| Mount timing | SHIPPED (deliberate) | Addons mount once at process start from the boot document. `world.addon.set` edits the document section only; the running host is untouched. The two-session pattern (mutate, save, reboot) is the intended flow, gate-verified. | — | `Program.cs:317-324`; `scripts/proof.cs:4420-4481` |
| Addon operator verbs | REFUSED (unported) | The retired Demo had `addon list/enable/disable/reload`. Puck.World has none: no way from a running session to list loaded addons, disable a misbehaving one, or hot-reload an edited module. `AddonHost.Reload` exists and works. | 0 verbs | grep: no `AddonHost.Reload` caller in `src/Puck.World` |
| Authoring path | SHIPPED | A hand-written `.wat` or any `.wasm` referenced by a document row with optional hash pin. No SDK, no toolchain, no build step. | LRU 64 modules | `Assets/addons/autopilot.wat`; `WasmModuleLoader.cs` |
| Networked addons | REFUSED (scope) | Loopback only — an addon runs in-process in the session that hosts it. | — | `WorldAddonDriver.cs:27-29` |

---

## Scale and performance contract

| Fact | Value | Evidence |
|---|---|---|
| Local seats / split viewports | 4 (engine kernel ceiling 5) | `WorldPopulation.cs:60`; `SdfWorldEngine.cs:163` |
| Total simulated bodies | 128 (4 seats + 124) | `WorldPopulation.cs:57,65` |
| SDF instances per program | 16384 (~41 MB mask buffer at cap) | `SdfProgramBuilder.cs:19` |
| Diegetic screens | 32 | `SdfProgramBuilder.cs:24` |
| Authored cameras | 64 | `WorldDefinitionValidator.cs:37` |
| Registered offscreen views / refreshed per frame | 64 / 4 | `ViewStack.cs:121,128` |
| Surface dimension | 4096 px/axis | `WorldDefinitionValidator.cs:30` |
| Audio voices / sources / patches | 32 / 16 / 32 | `WorldVoiceSynth.cs:108`; `WorldAudioMixer.cs:62,64` |
| Per-avatar SDF instructions | 60–100 | `WorldAvatarCatalog.cs:17-19` |
| Frame-rate contract | 60 FPS proof floor (rolling average **and** worst frame over a 2 s window) at 2560×1440, "low" quality, up to 124 bodies; 120 FPS is the separate reference desktop target under VRR | `scripts/proof.cs:1752`; `FrameRateMonitor.cs:11` |
| Live GPU timing | `world.timing on` + `world.gpu` echo whole-frame and 5 named passes; no env var | `WorldCommandModule.cs:305-329` |
| Puck.Bench per-scene scores | Unavailable for Puck.World — suite registration lived in the deleted Demo root and ports at Arc 10 | `docs/engine-bench-plan.md:3-10` |

The effective per-world instance ceiling is not a constant: `WorldRenderEnvelope` probes program-word and instance capacity once at boot for the boot definition and rejects mutations past it, so the practical number is a function of authored content up to the 16384 backstop.

---

## The only open questions

### ACCURACY
- Whether the relaxed default parity envelope catches real cross-backend divergence in daily use, or only the strict posture does. Settled by a deliberately-injected-bug drill.
- IMU axis/scale calibration on Switch Pro, DualSense, and Steam devices is nominal, not factory. Good enough for gravity-anchored pitch/roll; gyro-only yaw is the fragile axis.
- Steam Controller (classic) has never touched real hardware.
- The mealybug `m3_*` sub-dot PPU register signatures remain the named emulator frontier.
- Whether the Wasmtime fuel-boundary constant (5998) reproduces across both dev machines, or was only ever derived on one.

### PERFORMANCE
- No calibrated per-machine GPU frame budget exists at any player-count or scene-richness tier; the 100 ms tripwire times one fixed hero scene.
- The soft-shadow gather beats the camera-tile fallback on spread scenes and loses on dense clustering (1024 stacked carves: 46 → 101 ms). The density-adaptive gate is named and unbuilt; the lever ships hard-on.
- `ViewStack.RefreshBudget = 4` is flat regardless of scene cost or GPU headroom; unprofiled across the two reference machines.
- Capture's synchronous per-frame GPU readback on the render thread is unmeasured, so whether `capture.start` is safe to leave armed during play is unknown.
- SDF contact-field walking at 128 bodies is explicitly unmeasured.
- No measured concurrency budget for N simultaneously-booted machines, N addon-driven bodies, or the 8-slot animated/inhabited stamp pool against real world content.
- Full-entity-table snapshots every tick have no delta compression; unmeasurable until a transport exists.

### EXPRESSIVENESS
- Field-scope depth is 1; raising it needs an indexed shader stack. Nobody has hit the ceiling.
- Spatialization is pan + distance. Whether that is the permanent v1 ceiling or gets HRTF/occlusion/cones/Doppler is undecided (closeout-10, audio-02, audio-06).
- No sampled-clip audio source alongside synth patches and tunes (closeout-11).
- An engaged player reaches one machine button through the avatar route; the action-lane vocabulary is undecided.
- Rotation and align-to-shape snapping are complete math with no editor wiring — a wiring decision, not a design one.
- Text runs on creations need a world-space glyph atlas nobody has bound.
- Addons are locomotion plus two buttons; widening the World-side translator (the ABI is already frozen and generic) is unscoped.
- Nested worlds: does a nested world get a full server or a reduced one, how do the ticks relate, what does it cost? Arc 11's own first deliverable is the survey that answers this.
- An unanchored Orbit rig silently orbits the world origin; whether the validator should reject it is unconfirmed.
- Binding overlays replace all of a source's entries per layer; appending one half of a HoldRelease pair requires re-declaring both.

### GENUINELY UNDECIDED
1. **Is a socket transport wanted, and when?** Stop asking whether it is possible — it is, cleanly. It is absent from every roadmap. Also undecided: what authenticates a peer claim, and whether the model is lockstep-deterministic or server-authoritative-snapshot (today's shape implies the latter, never stated).
2. **Should Puck.World expose the proven live backend hot-switch?** Product scope, not capability.
3. **Should carve-bake be wired into Puck.World's sculpt carves,** or is the destructible-terrain story permanently analytic?
4. **Should `NestedWorldView` and `GuestSurfaceView` be wired into World's ViewStack, or retired?** Both are built and proven; deletion is cheap under greenfield doctrine.
5. **Should the dead `SdfWorldRenderSpec.Children` path be deleted or kept as a second hosting primitive for Arc 11?** Its caller is already gone.
6. **Is `IRenderNode` or `IScreenMachine` the seam for hosting a genuinely foreign engine?** Both exist; neither is designated.
7. **Where do cartridge saves live** — a screen data row that `world.save` round-trips, or a sibling file beside the ROM?
8. **Should live cable-linking get the "quiesce both workers and lend their cores" bridge?** This blocks the most obvious product question in the building.
9. **Should the replay stack become a live World capability** (`InputRouter` already implements `ISnapshotSource`) or stay a Post-only proof?
10. **Should `puck.run.v1`/Puck.Scene be retired** now that its only consumer is its own gate, or kept as a product-independent engine composition contract?
11. **Should the guided binding session, LampArray legend, time-travel verbs, and addon operator verbs be wired or explicitly dropped?** Each contradicts the unification contract's "reachable from a single running session" while unwired.
12. **Does the editor owe an in-fiction unlock beat,** or is the reveal-ladder language describing a future player-facing layer over an always-on dev surface? Zero gating code exists.