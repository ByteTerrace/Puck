# Machines and cartridges

A machine is a named device the world hosts; a cartridge is the document that
machine runs. Neither finishes alone: a cabinet with nothing worth playing in
it proves no hosting, and a retail-scale cartridge with nowhere to run proves
no game. This programme builds both against one artifact, an arcade cabinet
authored once, placed twice, with a real game inside it. The host side gives a
running machine its own named identity, keeps screens as the physical
vocabulary, and puts provider knowledge behind extension contracts; the guest
side gives `puck.cartridge.v1` the program and memory structures a whole game
needs. The reasoning behind every decision is in
[the decisions register](../decisions/machines-and-cartridges.md). A third
machine family, the NES and Famicom core, has its own page:
[Humble Gaming Deck](humble-gaming-deck.md).

## Where it stands

The machine host is described by [its README](../../src/Puck.World.Machines/README.md).
Open across the packages below: screens are index-addressed
(`WorldScreen.Index`), and a cable group lists screen indices; no rule effect
invokes a machine operation, no execution or content receipt exists, and a tape
arms only before any cabinet has stepped; checkpoints still refuse pumped addon
guests, applied screen operations, live coupled links and enabled rewind
history; addon memory watches are keyed by screen; no reusable cabinet module
exists; `Puck.World` references both bricks and the three forges directly; the
`puck.cartridge.v1` content-admission preset (`GamingBrickContentPolicies`) has
no production consumer; the cartridge document declares no procedure, typed
region, or interrupt body, and every frame-shaped ceiling stands. Each package
below names its part.

## The forcing artifact

The device row and cabinets of the debug world, the
[forcing world](state-and-language.md#the-forcing-world) (`worlds/debug/devices.puck`
and its stands): one parameterized cabinet module taking a placement, a facing,
an engine, and a cartridge asset, declaring a machine and a screen whose source
is that machine's video output named as a sibling, exporting the display as a
read and a binding. The row uses it for each device the debug world shows (the
Humble Gaming Brick, the Advanced Gaming Brick and the Humble Gaming Deck with
its television), and a device placed twice under two aliases is the proof of
independence: one cabinet running a Humble brick and one running an Advanced
one, each with its own cartridge, engage route, and controls. The Advanced
cabinet's cartridge is the retail-scale one: procedures over typed regions,
banked code, ROM-resident tables, and a real save. Placing the cabinet is one
statement, and its three open UX details are settled by writing and playing it.

**Check:** `puck_world_start` boots `worlds/debug` and a `puck_exec` read of
`machine.state` shows every device running; two aliased cabinets share no names,
render slots, inputs, memory bindings, link groups, or machine state; a feed
change on one preserves the other's execution; a passive monitor of either
grants nothing; the retail cartridge's forged image, replayed from a recorded
input script, produces identical machine-state hashes on repeated runs of each
backend, and the two backends agree on normalized authored state at matched
game-frame boundaries.

## Packages

### The cabinet module

**Owns:** `WorldMachine`, `WorldScreen` and its catalog, `WorldScreenSource`,
`WorldSpeakerSource`, routes and control derivation, `WorldMachineCableGroup`,
the machine-operation envelope and its rule effect, memory bindings and
hardware observations, addon watches, the execution and content receipt,
`ScreenCommandModule`'s machine-facing arms, and the cabinet module itself.

**Delivers:**

1. **Display identity.** Authored screen `Name` replaces `Index`; one derived
   catalog allocates explicit screens, creation faces, and authoring headroom,
   and commands and authority keep names while GPU slots stay inside
   presentation lookup, so a recycled slot cannot retarget a command or a
   replay entry. A route names an explicit machine instance and input port, or
   omits them only when the source identifies exactly one compatible port;
   ambiguity is a validation error, derivation mints no authority, and a
   passive monitor gets no control. A feed change releases held input before
   rebinding. Cable endpoints are declared on the machine row and groups derive
   from those declarations. In place: cable endpoints on the machine row, a
   removed display leaving its machine and generation alone and releasing its
   input, named link members that advance once, and routes that name the
   input port they drive (`route.port`, required for an engageable display of a
   multi-port machine). Open: authored screen `Name` and the derived catalog,
   routes that name a machine instance, and cable-group membership by machine
   name in place of screen index.
2. **Hardware consumers.** One machine-operation payload carries instance,
   expected generation, provider operation id, and a descriptor-validated
   payload; rules invoke it through one generic, schema-validated effect whose
   dynamic arguments use the state-expression compiler and declared parameter
   types. Direct predicates and addon watches move to named instances. In
   place: the operation payload as `machine.operation`; availability reporting
   in `machine.state`; bindings declaring address space, width, access mode
   (inspect, patch, bus), update semantics (on change or held every tick) and
   conversion (checked by default), with addresses past `0xFFFF`, a failed read
   that leaves the last accepted cell unchanged with its availability exposed,
   overlapping writes refused, a replaced machine's first write never
   suppressed, and the phase order of world rules, binding synchronization in
   authored order, then machine advance with the tick's folded input. Open: the
   generic rule effect, named-instance direct predicates and addon watches
   (the watches still key by screen), byte order and device-operation access
   on bindings, and budgets that count instances, guest memory, queued work,
   watches, links, and outputs independently of displays, with capacity
   refusing before allocation or commit.
3. **Boot-anchored reproduction.** A neutral receipt identifies the provider
   artifact closure, canonical configuration, firmware and content, compiler
   and output identity (source and compiler identity for a forged cartridge),
   and any snapshot format, hashed over reproducible content rather than
   paths, timestamps, or renderer indices; a missing or mismatching receipt
   refuses by name before advancement. External operations are recorded once;
   deterministic rule and binding work is re-executed once. The world checkpoint
   carries named handheld cores, held input, pacing, instance generations and
   binding memos; its refusal stays, by name, for a pumped addon guest, an
   applied screen operation, a live coupled link and enabled rewind history until
   full restoration round-trips for them. Until the receipt exists and those
   round-trip, a tape can only arm before any cabinet has stepped, which is why
   recorded tapes arm at tick 0.
4. **Cabinets are priced, and the price is an input to the job-system memo.** A
   world that hosts many instances runs every cabinet of every instance it
   starts, and the tick waits on their workers; the debug world's three devices
   are the sizing case. A cabinet's emulation is a line of the tick's budget
   (the ceilings-as-prices rule of
   [C1](state-and-language.md#c1--ceilings-as-prices)): a document that cannot
   afford its cabinets refuses at the door. Machine stepping is scheduled work
   under the [engine job system](open-items.md#engine-job-system), whose memo
   decides how cabinets are stepped, parked and resumed. "Observed" there means
   authoritative state only: a seat, a binding read this tick, or an engagement.
   A viewer, a render or residency is never an observation, because who looks
   must not change simulation state (the determinism rule).

**Check:** two aliased cabinets with independent controls and state; a passive
monitor grants no control; slot reuse cannot retarget a command; a coupled
group advances with no display; raw and symbolic reads and writes, an Advanced
address above `0xFFFF`, a real device side effect, failed-read versus valid-zero,
generation replacement, and capacity refusal before allocation; a changed guest
register fails verification even when the world population matches; named
links and bindings replay; `MachineMemoryLawTests`, `MachineHostTransactionLawTests`,
and both GamingBrick Post batteries in Release.

### Cabinet authoring and player experience

**Owns:** schema generation, DSL lowering and decompilation, language-server
metadata, imports and exports for machine rows, the screen and machine
commands, cabinet inspection, source and cartridge magazines, `forge play`.

**Delivers:** the cabinet is one reusable module that emits machine, display,
route, and optional speaker together, with no second JSON form that constructs
a machine inside a screen source; importing it under two aliases produces two
independent devices with local memory-row and control references rewritten
together. The provider's one descriptor supplies configuration validation,
fields, ports, operations, hardware spaces, and descriptions to runtime
validation, JSON Schema tooling, the CLI, and completion, and identifies which
configuration fields hold references, paths, and declarations so import
rewriting and asset pinning can act on them; diagnostics name the instance and
authored field. Feed selection and cartridge changes have different owners:
moving, resizing, hiding, or removing a display changes the display; changing
the feed selects an output; insert, eject, reset, and reconfigure address a
named machine; a cabinet's removal action names its row set explicitly, since
proximity, matching ROM hashes, or a vanished display never imply ownership.
Machine rows have an explicit running or stopped initial state. Display
inspection shows source, signal, and bound controls; machine inspection shows
running, stopped, empty, or faulted, mounted content, configuration, bindings,
links, and backlog, so a stopped machine and a missing framebuffer stay
distinct. Saving a live world writes its canonical definition and never
overwrites a module-based source. The three UX details (no repetitive wiring;
a source switch releases or explicitly retargets held control; empty, failed,
and stopped remain distinguishable) are settled here. In place: each engine's
`MachineEngineDescriptor` supplies its versioned configuration, ports,
operations and hardware spaces to construction and validation, machine rows
carry an explicit `running` flag, `machine.state` reports a machine's execution
and binding outcomes, and `world.row.set machines` edits a declaration through
the ordinary mutation door. Open: the reusable cabinet module imported under
two aliases, and the three UX details.

**Check:** placing the cabinet is one statement; two aliases work; insert,
reset, eject, and source selection have explicit effects; failed content, empty
content, stopped execution, missing signal, and faults are distinguishable in
the real app, including multi-seat control and live save and reload.

### Optional distribution

**Owns:** `src/Puck.World/Puck.World.csproj`'s brick and forge references, the
machine extension entry contract, provider registration, `ForgeCommandModule`'s
index branches.

**Delivers:** the extension entry contract leaves the cartridge-forge package
for a host-scoped registration set shared by the executable, CLI, validators,
replay hosts, and instances, with no module-initializer registration or
process-global validation state; provider configuration, cartridge compilation,
symbol maps, commands, and Tune-to-Humble hosting live with their extensions;
core persistence carries a generic receipt. In place: the entry contract is
`AddMachineEngine` in `Puck.Abstractions` (an `IMachineEngine` and its
`IMachineContentProvider`), `WorldMachineCatalog.From` builds each host's
immutable catalog from the composed extensions, and `Puck.World.Machines`
references no Gaming Brick core or forge and registers nothing from a module
initializer. Open: `Puck.World.csproj` still references both bricks and the three
forges (the Humble brick, Advanced brick and Humble Gaming Deck forges), `TuneMachineSource` uses the Humble brick and `ForgeCommandModule` the shared forge directly, the
generic persistence receipt waits on the cabinet module's receipt, and the
distribution without either brick. A distribution without either brick
builds and runs camera, view, QR, text, and test-pattern screens and admits a
different provider through the same contract; static composition survives for
AOT and browser deployments; the loader shares neutral contract assemblies with
no special case for a forge package. The package-consumer proof is repeated
after the final contract change: pack the runtime and dependency closure,
restore from a fresh local feed into an application outside the repository,
cold-boot with bundled firmware, submit input, advance, read video and audio,
and exercise the save-state contract with no repository path, forge toolchain,
retail BIOS, or World application.

**Check:** a build with no bundled brick dependencies boots ordinary sources
and a non-brick provider; the resolved dependency closure is checked; compiler
references prove retired APIs have no callers; the package consumer proof
passes.

### Firmware and content policy

**Owns:** the bundled HGB boot ROMs and AGB BIOS with their qualification, the
immutable content-admission policy the machine host invokes.

**Delivers:** firmware split by origin: a hand-authored replacement image is
source and its bytes stay committed beside their license, while an image
generated from a program is a derived product. HGB startup images derive in
process on demand; the AGB BIOS remains packaged under the measured toolchain
[exception](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth).
Neither has a byte-compare verify mode. Remaining qualification: cold and fast
handoff, IRQ and services, model transitions, graphics and audio exercised with
independent expected values; HGB's boot-mapping and model-change guard kept;
AGB service coverage including stateful sequences, sound, and MultiBoot (the
bundled-firmware stage in `Puck.AdvancedGamingBrick.Post` is a functional smoke
of the services, not full sound or MultiBoot conformance); the recorded
`A.gba` render failure investigated before any compatibility claim.
The policy is enforced below every loading path (boot, insert, replacement,
reload, restore, replay) after trusted preparation and before runtime creation,
over exact source and executable bytes and the provider-verified format, with
selection under separate operator authority. In place: the policy contract
(`IMachineContentAdmissionPolicy`, `MachineContentAdmissionPolicy`) in
`Puck.Abstractions`, which the machine host evaluates when it prepares a
machine's content, at boot and at each replacement, with an explicit
disposition for auxiliary asset paths; the `puck.cartridge.v1` preset,
`GamingBrickContentPolicies`, beside the format it names in
`Puck.GamingBricks.Forge`, with `MachineContentAdmissionPolicyTests`. Open: no
production consumer selects that preset (the composition registers the open
policy, and a restricted host supplies its own at construction), selection
under separate operator authority, and a law for each of reload, restore and
replay.

**Check:** raw ROM rejection, relabeled input, changed source or output pins,
valid new user-authored content, allowlist acceptance and rejection, and a
denied replacement retaining the running device; missing licensed assets
produce explicit skips, never a claim the path passed.

### The program model

**Owns:** `CartridgeDocument`, `CartridgeLimits`, both native emitters, the
cartridge vocabulary in `Puck.GamingBricks.Transpiler`. Stage 1 adopts the
rebuilt state row vocabulary, whose names the
[state data model](../reference/state/data-model.md) records.

**Delivers, each stage authorable and verifiable on both machines before the
next:**

1. **The memory model.** Declared regions with typed layouts (records, arrays of
   records) and an indirect operand of base, offset, and field; named slots
   become views; `VariableCount`, `ArrayCount`, `ArrayLength`, and
   `ArrayByteCount` dissolve. Proved by a record-walking document reading and
   writing the same cells on both machines.
2. **Procedures and banked code.** Named parameterized bodies with a declared
   stack bound; the rule list becomes the frame procedure; bank assignment is a
   call-graph partition with a trampoline; `RuleCount` becomes code bytes per
   bank refused by `CartridgeCapacityException`. Proved by code exceeding one
   bank executing across a far call on both machines.
3. **Interrupts and cycle budgets.** Declared vblank, hblank, timer, serial, and
   joypad bodies, each with a compiler-verified cycle budget; `raster` and the
   vblank queue become instances of it and their paths are deleted. Proved by
   the current raster behavior reproduced through a declared hblank body.
4. **ROM residency, codecs, and the real save.** Bank-and-offset tables with far
   reads placed by the linker; codecs so the document carries decompressed
   assets; the save becomes the cartridge's banked RAM window and `SaveByteCount`
   goes; `TileCount` becomes paged tile sets. Proved by a table larger than work
   RAM read at run time and a save larger than the current mirror surviving a
   power cycle.
5. **Arithmetic completion.** 16×16 multiply, divide, and 32-bit intermediates
   on both backends, total (a zero divisor yields zero). Proved by a
   damage-formula document agreeing with a reference across a swept input space
   on both machines.

The re-derived numbers for the new ceilings (code bytes per bank, cycles per
interrupt window, RAM bytes) exist once there is a program model to measure.

**Check:** the stage proofs above; the round-trip gate over committed sources;
the determinism gate (repeated runs match per backend, the backends agree on
normalized authored state at matched frame boundaries, independent expected
values cover arithmetic, guards, storage, and persistence); every overrun
raises `CartridgeCapacityException` naming what overran.

### Asset ingestion

**Owns:** `puck` verbs that ingest images, maps, and audio into a referenced
form; the formatter's layout for bulk data.

**Delivers:** the document references assets and never spells tiles, maps,
audio, or bulk tables as scalars; the verbs produce the referenced form with
pinned hashes; the formatter lays a thousand-entry table out as a table.

**Check:** the retail cartridge's tiles, maps, and audio arrive by reference;
the committed source is logic, not payload.

### The content library

**Owns:** `.puck` modules for text, camera-driven tilemap streaming, entity
dispatch, battle math, and an audio driver, composed through `import`. They live
in the shared package `worlds/standard/` (beside `cards.puck`, the precedent for
a reusable fragment every package imports), so any package imports them; the
house cartridges that use them stay in the importing package's `cartridges/`
folder.

**Delivers:** the engines a retail game needs, authored as procedures over
typed memory; nothing in it touches a forge project. One language item it
needs is the language programme's: diagnostics proven at depth through a
module expanded inside an import inside a `for` (a compile-time `for` inside a
rule body is available); two transpiler constraints it needs fixed are the cubic
`let`-array re-lowering and an array-valued `let` used as a vector property
lowering non-finite.

**Check:** the right cabinet's cartridge plays a retail-shaped game through the
library on both machines under the determinism gate.

## Sequencing

| Step | Packages, in parallel | Why here |
|---|---|---|
| 1 — today | Asset ingestion; firmware and content policy | Neither touches the state substrate or the language. |
| 2 | The cabinet module | Its hardware consumers ride the rebuilt rule vocabulary, and its bindings name the shapes the [state reference](../reference/state.md) records. |
| 3 — after the language's modules | Cabinet authoring and player experience | Needs `module`, `use` with an alias, and re-export from [S6](state-and-language.md#s6--modules-and-the-forcing-world), and the arcade's pool from [S7](state-and-language.md#s7--records-and-pools). |
| 4 | The program model, its five stages in order | Stage 1 adopts the rebuilt row vocabulary. |
| 5 | The content library; optional distribution | The library makes the cartridge retail-scale; the second provider makes the distribution optional. Together they close the artifact. |

## Verification summary

A change is read by running it: boot `worlds/debug` through the `puck` server's
`puck_world_start` and read `machine.state` and the device screens with
`puck_exec` and `puck_capture_frame`. The emulator batteries are the
accuracy checks:

```bash
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --artifacts artifacts/hgb-post
```

```bash
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --bios <GBA_bios.rom>
```

```bash
puck sweep
```

## Open items

What the programme still owes. A line is deleted when its work lands, never
ticked; the [open items](open-items.md#next) rank the work packets drawn from it.
The Humble Gaming Deck's packages are in [its plan](humble-gaming-deck.md#open-items).

- [Asset ingestion](#asset-ingestion) (startable today).
- [Firmware and content policy](#firmware-and-content-policy) (startable today).
- [The cabinet module](#the-cabinet-module): display identity, hardware
  consumers, boot-anchored reproduction.
- [Cabinet authoring and player experience](#cabinet-authoring-and-player-experience),
  after the language's modules.
- [The program model](#the-program-model): memory model, procedures and banks,
  interrupts, ROM residency and the save, arithmetic.
- [The content library](#the-content-library); [optional distribution](#optional-distribution).
- Humble Gaming Brick corpus failures. No file records an expected outcome, so
  every corpus ROM that does not pass fails its stage in
  `Puck.HumbleGamingBrick.Post` on every run. A default run with every corpus
  fetched fails 14 stages and 1,999 cases: gambatte 1,655, GBMicrotest 109, AGE
  82, wilbertpol acceptance 70 and misc 5, mealybug 48, SameSuite 19, and a
  handful of one- or two-case suites (blargg's `oam_bug` screenshots and
  `7-timing_effect`, cgb-acid-hell, little-things-gb `firstwhite`, scribbltests
  `scxly`, strikethrough, bully). Each is an inconclusive probe or a screenshot,
  hex-pattern or register-signature mismatch; the report names a sample per
  stage and the JUnit file names every case. They are the Humble accuracy work
  list, closed by emulator corrections, never by recording them.
- Committed coverage for what the commercial-ROM stages checked. The batteries
  read no commercial ROM or retail BIOS (the owner's ruling), so the stages that
  needed one are gone: the Humble and Advanced commercial link-game replays, the
  Humble scripted cross-generation trade stages and their explorers, the
  Advanced solar-cartridge replay, the AGS aging cartridge and the retail-BIOS
  firmware oracle, plus the commercial render-hash floors. Committed stages
  already cover the cable itself: Humble `serial-link` (DMG-CGB, DMG-AGB,
  CGB-AGB), `link-churn`, `linked-host-cable`, `linked-host-time-travel`,
  `infrared-exchange` and the acceptance serial suites; Advanced `link-replay`,
  `link-churn`, `serial-clock` and `firmware-multiboot`; the solar sensor's GPIO
  protocol (`solar-device`). Gaps that remain, each closed by a committed
  homebrew or authored test cartridge:
  1. the GBA multiplayer SIOCNT SD/SI ready lines are unmodeled
     (`AgbSerialController.PackSioControl`), and no partner-polling micro-ROM
     exists to prove them;
  2. a game-shaped interrupt-driven link handshake with retry loops across a
     CGB-AGB pair, which the trade stages exercised end to end, has only the
     byte-exchange stages above;
  3. retail-hardware conformance of timers, DMA and the PPU, which the AGS
     cartridge graded, is covered only by the mGBA suite and the jsmolka corpus;
  4. the bundled firmware has no independent oracle for its functional results
     beyond documented contracts, since the retail-BIOS comparison ran only
     against a supplied image (a retail-BIOS control still runs inside several
     Advanced Tier A stages when `--bios` names one by hand).
- Low priority: shorten the two emulator batteries. `Puck.HumbleGamingBrick.Tests`
  runs the Humble battery with an empty corpus cache and
  `Puck.HumbleGamingDeck.Tests` runs the Deck's Tier A, each in a child process;
  each self-contained stage reaches lines no other stage does, so neither run
  narrows to a subset. Their cost is the longest stages
  (`queued-host-time-travel`, `bundled-firmware`, `boot-rom-handoff`,
  `queued-host-memory-access`), and under instrumentation the emulator loops
  slow by an order of magnitude. Fewer emulated frames in those stages, with the
  same checks, is what shortens both.
- `QueuedMachineWorker` frame-tracking locals are not reset on checkpoint
  restore or rewind, and `HgdMachine`'s reset keeps a latched bus write (read from
  the code in a pre-`main` review, not reproduced; confirm with a failing test
  before fixing).

---

[Plans](README.md) · [Decisions](../decisions/machines-and-cartridges.md)
