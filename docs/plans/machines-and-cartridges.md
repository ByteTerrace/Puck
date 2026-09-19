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
[the decisions register](../decisions/machines-and-cartridges.md).

## Implementation status

Checked against `state/rebuild` at `1d0d910c9`.

- **Landed:** named machine identities, neutral hosting seams, ordered
  operations, and the machine host described by
  [its README](../../src/Puck.World.Machines/README.md); the mirror source
  (`hgb-mirror.puck`) driving five machine-owned bindings and reading a sixth
  back; the arcade module declaring three named producers. On the cartridge
  side, `Sm83Emitter` emits `Call`, `Return`, and `ReturnFromInterrupt`,
  `ThumbEmitter` emits `Call`, `CartridgeExpressions.Reads` consumes
  `Puck.State`'s expression, and `scene` partitions the frame.
- **Not started:** every package below. Screens are still index-addressed; no
  generic machine-operation effect, execution receipt, or reusable cabinet
  module exists; `Puck.World` references both bricks and both forges directly;
  the cartridge document declares no procedure, typed region, or interrupt
  body, and every frame-shaped ceiling stands.

## The forcing artifact

The cabinet module of [the forcing world](state-and-language.md#the-forcing-world):
one parameterized module taking a placement, a facing, an engine, and a
cartridge asset, declaring a machine and a screen whose source is that
machine's video output named as a sibling, exporting the display as a read and
a binding. The arcade uses it twice under two aliases: a left cabinet running a
Humble brick and a right cabinet running an Advanced one, each with its own
cartridge, engage route, and controls. The right cabinet's cartridge is the
retail-scale one: procedures over typed regions, banked code, ROM-resident
tables, and a real save. Placing the cabinet is one statement, and its three
open UX details are settled by writing and playing it.

**Check:** the world boots and runs in `Puck.World`; the two aliased cabinets
share no names, render slots, inputs, memory bindings, link groups, or machine
state; a feed change on one preserves the other's execution; a passive monitor
of either grants nothing; the retail cartridge's forged image, replayed from a
recorded input script, produces identical machine-state hashes on repeated
runs of each backend, and the two backends agree on normalized authored state
at matched game-frame boundaries.

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
   rebinding. Cable endpoints move to the machine row and ordered groups derive
   from machine names; a coupled group advances with no display.
2. **Hardware consumers.** One machine-operation payload carries instance,
   expected generation, provider operation id, and a descriptor-validated
   payload; rules invoke it through one generic, schema-validated effect whose
   dynamic arguments use the state-expression compiler and declared parameter
   types. Direct predicates, addon watches, and availability reporting move to
   named instances. Bindings declare address space, width, byte order, access
   mode (inspect, patch, bus or device operation), update semantics (on change
   or held every tick), and conversion (checked by default); addresses cover the
   provider's declared range past `0xFFFF`; a failed read is distinct from a
   valid zero and leaves the last accepted cell unchanged with its validity
   exposed; multiple writes to one range compose explicitly or refuse; a
   replaced machine cannot suppress a first write the old instance saw. The
   phase order stands: world rules, binding synchronization in authored order,
   machine advance with the tick's folded input. Budgets count instances, guest
   memory, queued work, watches, links, and outputs independently of displays,
   and capacity refuses before allocation or commit.
3. **Boot-anchored reproduction.** A neutral receipt identifies the provider
   artifact closure, canonical configuration, firmware and content, compiler
   and output identity (source and compiler identity for a forged cartridge),
   and any snapshot format, hashed over reproducible content rather than
   paths, timestamps, or renderer indices; a missing or mismatching receipt
   refuses by name before advancement. External operations are recorded once;
   deterministic rule and binding work is re-executed once. The checkpoint
   refusal for machine-bearing history stays until full restoration
   round-trips; until then a tape can only arm before any cabinet has stepped,
   which is why the quilt canaries arm at tick 0.
4. **Cabinets are priced.** A colocated quilt runs every cabinet of every
   instance it starts — fifteen emulators for the four corners and the island —
   and the tick waits on their workers, so the headless quilt ticks at about a
   ninth of its authored rate. A cabinet's emulation is a line of the tick's
   budget (the ceilings-as-prices rule of
   [C1](state-and-language.md#c1--ceilings-as-prices)): a document that cannot
   afford its cabinets refuses at the door, and an instance nobody observes —
   no seat, no viewer, no binding read this tick — parks its cabinets rather
   than stepping them.

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
and stopped remain distinguishable) are settled here.

**Check:** placing the cabinet is one statement; two aliases work; insert,
reset, eject, and source selection have explicit effects; failed content, empty
content, stopped execution, missing signal, and faults are distinguishable in
the real app, including multi-seat control and live save and reload.

### Optional distribution

**Owns:** `src/Puck.World/Puck.World.csproj`'s brick and forge references, the
machine extension entry contract, provider registration, the retired
screen-owned lifecycle protocol arms, `ForgeCommandModule`'s index branches.

**Delivers:** the extension entry contract leaves the cartridge-forge package
for a host-scoped registration set shared by the executable, CLI, validators,
replay hosts, and instances, with no module-initializer registration or
process-global validation state; provider configuration, cartridge compilation,
symbol maps, commands, and Tune-to-Humble hosting live with their extensions;
core persistence carries a generic receipt. A distribution without either brick
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

**Delivers:** the packaged images rebuilt independently; cold and fast handoff,
IRQ and services, model transitions, graphics and audio exercised with
independent expected values; HGB's boot-mapping and model-change guard kept;
AGB service coverage including stateful sequences, sound, and MultiBoot; the
recorded `A.gba` render failure investigated before any compatibility claim.
The policy is enforced below every loading path (boot, insert, replacement,
reload, restore, replay) after trusted preparation and before runtime creation,
over exact source and executable bytes and the provider-verified format, with
selection under separate operator authority.

**Check:** raw ROM rejection, relabeled input, changed source or output pins,
valid new user-authored content, allowlist acceptance and rejection, and a
denied replacement retaining the running device; missing licensed assets
produce explicit skips, never a claim the path passed.

### The program model

**Owns:** `CartridgeDocument`, `CartridgeLimits`, both native emitters, the
cartridge vocabulary in `Puck.GamingBricks.Transpiler`. Stage 1 adopts the
rebuilt state row vocabulary once WP13 trues its names up.

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
dispatch, battle math, and an audio driver, composed through `import`.

**Delivers:** the engines a retail game needs, authored as procedures over
typed memory; nothing in it touches a forge project. Two language items it
needs are the language programme's: a compile-time `for` inside a rule body,
and diagnostics proven at depth through a module expanded inside an import
inside a `for`; two transpiler constraints it needs fixed are the cubic
`let`-array re-lowering and an array-valued `let` used as a vector property
lowering non-finite.

**Check:** the right cabinet's cartridge plays a retail-shaped game through the
library on both machines under the determinism gate.

## Sequencing

| Step | Packages, in parallel | Why here |
|---|---|---|
| 1 — today | Asset ingestion; firmware and content policy | Neither touches the state substrate or the language. |
| 2 — after the rebuild lands | The cabinet module | Its hardware consumers ride the rebuilt rule vocabulary, and its bindings name shapes only after [WP13](state-rebuild.md#wp13--documentation-and-skills) trues them up. |
| 3 — after the language's modules | Cabinet authoring and player experience | Needs `module`, `use` with an alias, and re-export from [S6](state-and-language.md#s6--modules-and-the-forcing-world), and the arcade's pool from [S7](state-and-language.md#s7--records-and-pools). |
| 4 | The program model, its five stages in order | Stage 1 adopts the rebuilt row vocabulary. |
| 5 | The content library; optional distribution | The library makes the cartridge retail-scale; the second provider makes the distribution optional. Together they close the artifact. |

## Verification summary

```bash
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2
```

```bash
dotnet test tests/Puck.World.Tests -c Release
```

```bash
dotnet test tests/Puck.GamingBricks.Transpiler.Tests -c Release
```

```bash
dotnet test tests/Puck.HumbleGamingBrick.Forge.Tests -c Release
```

```bash
dotnet test tests/Puck.AdvancedGamingBrick.Forge.Tests -c Release
```

```bash
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --lane gate --artifacts artifacts/hgb-post
```

```bash
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --bios <GBA_bios.rom>
```

---

[Plans](README.md) · [Decisions](../decisions/machines-and-cartridges.md)
