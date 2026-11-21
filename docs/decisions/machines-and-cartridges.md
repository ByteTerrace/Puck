# Machines and cartridges: decisions

The choices behind [the machines and cartridges programme](../plans/machines-and-cartridges.md),
each with the problem it answers and what follows from it. Current contracts
live in [the machine host README](../../src/Puck.World.Machines/README.md),
[the cartridge vocabulary](../emulation/shared/cartridge-dsl.md), and
[the forge guide](../emulation/shared/cartridge-forge.md).

## Machines

**Keep screens, and give a running machine its own named identity and
lifetime.** A screen is a useful authored object: a display surface with a
position, dimensions, a source, and an optional route. Three approaches were
weighed: moving the existing assemblies behind extension registration while
screens still own machines (better packaging, but audio, memory, lifetime, and
identity stay tied to a display); replacing screens and machines with a
universal ports-and-nodes language (expressive, but every ordinary cabinet
becomes an infrastructure exercise); or keeping screen and cabinet authoring
with named machine instances and optional capabilities underneath, which
preserves the physical vocabulary and permits shared displays, invisible
machines, and direct hardware interaction. The third is the decision. The
architectural test is whether installing another machine changes the host's
hardware vocabulary: neutral hosting is engine infrastructure, and cartridge
formats, console models, memory maps, cable protocols, and forges belong to
providers.

**An emulator core is machinery (the owner's ruling).** The C# emulator cores
are virtual machines, as the SDF VM is: engine machinery that runs authored
programs. The cartridges, and whatever else runs on a machine, are the authored
programs. A core written in C# is therefore not a second implementation of
something an author programs: "Puck code over engine code" (AGENTS.md) governs
what runs on the machine, which authors build and own, and the core is the
machinery that runs, schedules and costs it. The emulator plans stand as
written ([machines and cartridges](../plans/machines-and-cartridges.md),
[the Humble Gaming Deck](../plans/humble-gaming-deck.md)).

**Settled implementation contracts.**

| Contract | Decision |
|---|---|
| Authored machine | `WorldMachine(Name, Engine, Configuration, Running, Memory)` with its generation and lifetime semantics; cable endpoints on the machine row; ordered groups derived from machine names; no universal connection graph. |
| Display and speaker source | `WorldScreenSource.Machine(Instance, Output)` and `WorldSpeakerSource.Machine(Instance, Output)` reference an existing producer and never create, reset, advance, or dispose it. |
| Screen identity | Authored `Name` replaces `Index`; one derived catalog allocates explicit screens, creation faces, and headroom; commands and authority keep names; GPU slots stay inside presentation lookup. |
| Controls | A route may name an explicit instance and input port; omission resolves only a sole compatible port; feed changes release held input before rebinding; passive observation grants no control. |
| Operations | One payload carries instance, expected generation, provider operation id, and descriptor-validated payload; actor, ordering, and receipts use the existing authority envelope. |
| Hardware observations | Status plus value or reason, unsigned addresses; a failed read never produces a valid zero; direct predicates and mirrored values keep their distinct phases. |
| Provider assets | The existing field descriptor and walker own configuration path and reference semantics; asset preparation yields exact source, firmware, and compiled-image identities; core code contains no cartridge field-name tests. |
| Replay | A neutral receipt identifies provider artifact closure, canonical configuration, firmware and content, compiler and output, and any snapshot format; verified before advancing; external operations recorded once, deterministic work re-executed once. |

**Operation preparation is pure.** Preparation takes the current creation
request and an operation, reads no files and mutates no runtime, and returns a
replacement configuration, a prepared runtime action, or a refusal; the host
checks authority and generation, validates the candidate world, applies, and
only then reports applied and publishes the prepared definition. `content.insert`,
`content.eject`, and `machine.reset` use replacement preparation and reset
forces a new incarnation; `device.model` may preserve live state and keeps the
firmware and model guard. A live provider refusal leaves the declaration and
the world unchanged.

**Three access semantics stay distinct:** inspect (coherent, side-effect-free),
patch (deliberate state edits), and bus or device operation (hardware-visible,
modeled by the provider). A register write is never a relabeled RAM poke.

**Timing.** The phase order is world rules, binding synchronization in authored
order, then machine advance with the tick's folded input; moving the mirror
earlier is a separate decision with its own proof. Link groups advance once as
a coupled group with their integer-clock interleave; neither visibility nor
presentation cadence controls advancement; a guest worker never blocks on the
tick that consumes its events. Controller input is positioned inside the tick:
a press reaches the machine at the offset its event was captured at (below).
Cycle-positioned watches and register operations are implemented when a
gameplay scenario names its timing precision, and never advertised before that
scenario passes.

**A machine's video and controller input run at the machine's rate; the world's
tick rate bounds neither.** A world ticks at the rate its simulation needs (the
debug world at 30 Hz) and a machine completes frames at its own (about 60 Hz).
The machine still advances by the world's segments, the same cycles per
segment, so what follows changes what a player sees and when a press lands,
never the machine's mapping from segments and input to state.

| Option | What it gives | What it costs or leaves open |
|---|---|---|
| Show one frame per tick, hold input per tick | Nothing to build | Half of a 60 Hz machine's frames are emulated and never shown, and a press is sampled once per two frames: the largest defect on any in-world screen |
| Tick the world at the machine's rate | Every frame shown | The world's rate becomes a property of whichever machine is fastest, every world pays double simulation for a cabinet, and machines of different rates cannot share a world |
| Let the display drive the machine (advance it per presented frame) | Smooth video | Visibility and presentation rate would control advancement, which this decision forbids: two viewers at different display rates would need two machines, and an unseen cabinet would stop |
| **Queue the machine's frames and time its input inside the segment** | Every native frame shown at any display rate; a press lands on the frame it happened in; the machine's mapping is untouched | The segment runs in parts, so a core must say where its frame ends and how it treats an overrun budget, and a check must hold the parts to the whole |

The fourth is built. A host runs each segment in parts that end where a seat's
image changes and where the machine completes a frame (`MachineSegment.Run`),
stages each completed frame in a short queue stamped with the host clock
position it completed at, and a display takes the frame due at the position it
presents, kept in sequence while it is within half a frame of the machine
(`IMachineVideoOutput.FrameDue`). The authority builds a pad's image
over the tick from the timed events its input owner admitted
(`WorldEngagement.FoldTick`, [sub-tick time](input-channels.md#sub-tick-time-and-clocks)),
which are already taped and checkpointed, so replay reproduces it.

- Invariant: dividing a segment never changes the machine's state at its end.
  `MachineSegment.Run` is the only place a segment is divided, and the shared
  Post stage `queued-host-segment-parts` enforces it on every core by running
  the same segments divided and whole and requiring byte-identical checkpoints.
  The guarantee stops at the cores a Post battery runs: a new core joins the
  shared stage table to be covered.
- What it closes: a display cannot starve, slow, skip or desynchronize a
  machine by its rate or by not looking, because the queue is written by the
  machine and only read by displays; two displays at different rates read the
  same queue.
- What it leaves: no display shows every frame exactly once at every rate (a
  60 Hz machine repeats on a 120 Hz display and skips on a 45 Hz one), so the
  contract is the latest frame due. A runahead lookahead advances once per
  segment and is shown at the tick rate.

**A remote viewer gets a machine's picture as inputs or as frames, by the
author's choice of what a viewer may hold, and neither crosses the wire at the
machine's rate.** Today a world delivered from another authority runs its
machines there and they show nothing here
([worlds](../architecture/worlds.md#observation-and-display)), so the wire
carries no machine video at all. The stamped queue above is what makes either
path below show native-rate video from tick-rate messages, and neither is
built yet ([open item](../plans/machines-and-cartridges.md#open-items)).

| Option | What crosses, per machine | What a viewer learns | Cost |
|---|---|---|---|
| Replicate by input | The segment as the worker runs it: its ticks, the seats' images and their timed changes, about a tick's worth of bytes; a checkpoint to join | The machine's whole state: its RAM, its random numbers, a hidden hand | The viewer's processor runs the machine; the cartridge and firmware must be on the viewer |
| Stream frames | Each segment's completed frames with their stamps, sent once the worker finishes the segment, compressed once and fanned out to every viewer | The pixels a spectator at the cabinet sees | The authority's encode and the bandwidth: raw, a 160 by 144 RGBA frame is 92,160 bytes, about 5.5 MB a second at the machine's rate, before compression |
| Send frames at the machine's rate on their own channel | The same pixels | The same | A second, faster transport per machine, whose only gain is that a frame arrives when it completes rather than with its segment |

Replication by input rests on what the engine already holds: a machine is
recomputed from pinned content and pad input on replay
(`IWorldMachineHost`'s replay policy), a queued host captures and restores a
durable checkpoint (`IMachineCheckpointRuntime`), and the segment-parts stage
proves that the same segments and input give the same state, hence the same
frames, on any host. It is the cheapest wire by far and it is wrong for any
machine whose state is worth hiding: a viewer running a card game's cartridge
reads every hand, and one running a casino machine's reads the outcomes it has
not shown yet. So the choice is authored per machine, as every disclosure is,
and a machine that hides anything streams frames.

Neither path needs frames at the machine's rate on the wire. A viewer
interpolates toward the latest delivered tick, one step behind, and a
segment's frames are due across the step after it; frames that travel with
their stamps, once per segment, are due after they arrive, and the viewer's own
queue shows them at the machine's rate exactly as the local one does. The third
option's gain is therefore only the few milliseconds between a frame's
completion and its segment's, and it is not worth a second transport.

- What it closes: a viewer's bandwidth or display rate cannot change the
  machine (frames and inputs flow one way, authority to viewer); a streamed
  machine discloses only its pixels; a replicated one is exactly as trusted as
  a replay, since it is one.
- What it leaves: the viewer of a replicated machine can read its state, which
  is why hiding is the author's choice and not the engine's guess; a streamed
  machine's picture is one segment later than a local viewer's; and audio must
  follow the same stamps, which the open item carries.

**Identities stay distinct:** the document shape, the provider configuration
and operation schema, the host capability contract, the implementation and
content receipt, and the provider snapshot format. World stays v1 during
development: change the shape, regenerate, re-record, add no readers, aliases,
or fallbacks. A resume save needs provider state, timing remainders, binding
and watch history, pending operations, generations, and link state; the
checkpoint refusal for machine-bearing history stays until all of it
round-trips, and boot-anchored replay is the initial strategy. Authoritative
instances are scoped to their world authority, never to a process or content
hash.

**Bundled firmware is permissively licensed with retained provenance;** GPL
emibios is not the foundation, and branding is not a cartridge authentication
mechanism. Qualification separates source reproducibility, service
compatibility, and hardware timing; replacement firmware passing service tests
does not establish retail timing parity. User-supplied retail images stay
outside source control and reports, and a missing licensed asset is an explicit
skip.

**The local host default is Open/Allow.** A server operator may select an
immutable restricted policy that admits provider-validated authored cartridges
without publisher approval, or require exact hashes; an allowlist is an option,
never the authoring requirement. This controls what runs on a server and
promises nothing about piracy: headers, extensions, signatures from an
untrusted author, or branding prove no ownership.

**Kept out:** a universal object graph, a graph editor, an extension
marketplace, unrelated performance work, and full cycle-level event capture
without a selected gameplay requirement; general mid-run snapshots beyond
boot-anchored replay are optional.

## Cartridges

**D1 — The gate is behavioral determinism, not a byte diff.** Reimplementation
gives up any oracle against a retail image, so a forged image replayed from a
recorded input script must produce identical machine-state hashes on repeated
runs of each backend, and the backends agree on normalized authored state at
the same completed frame boundary through each compilation's symbol map.
Independent expected-value tests establish correctness, because replay alone
can repeat the same wrong answer.

**D2 — A cartridge consumes `Puck.State`'s vocabulary rather than restating
it**, and the forges are code-generating backends for it: the expression and
the gate come from there, and the row vocabulary is what the memory model
takes next. The step tree stays the cartridge's own, because a machine verb is
not a state effect.

**D3 — A procedure is a document row, not a compiler outlining pass.**
Outlining leaves the author no way to say what lives far and the linker no
name to place.

**D4 — `template` and a procedure are different tools, and both stay.** One is
authoring reuse expanded at compile time; the other is ROM-size reuse called
at run time. Macro expansion at retail scale explodes the image; a call at
authoring scale costs a name for nothing.

**D5 — Payload leaves the source.** The committed tetromino source is barely
smaller than the JSON it generates and is overwhelmingly tiles, maps, arrays,
and audio at one scalar per line; a retail game on those proportions would be
unreviewable.

**D6 — The frame-shaped ceilings are re-derived, not raised.** Under a program
model the real resources are code bytes per bank, cycles per interrupt window,
and RAM bytes.

**D7 — The content engines are a library, not engine features.** A text engine,
an entity dispatcher, a script interpreter, and an audio driver are procedures
over typed memory composed through `import`, which is what keeps the forge
from growing an arm shaped like one retail game.

**D8 — Cost stays advice.** Validation refuses what makes an image wrong, never
what makes it slow.

**Kept out:** byte-identical output; decompiling a retail ROM (a document that
holds an arbitrary instruction stream is a hex dump with syntax); emulator
edits to make a forged image agree; a DMG target; a cost refusal; entity,
text, battle, or tracker primitives.

## The Humble Gaming Deck

The choices behind [the Deck's plan](../plans/humble-gaming-deck.md).

- **The accuracy matrix.** The first milestone, through NTSC accuracy, targets
  the RP2A03G CPU and RP2C02G PPU, the revisions AccuracyCoin is written for.
  Regional hardware adds the RP2A07 and RP2C07 (PAL) and the UA6527P and UA6538
  (Dendy). The boards are the common set (mappers 0 to 4, MMC6 as MMC3
  submapper 1, and 7) and the plan's long-tail table. The peripherals are the
  standard controller, the Four Score and the Hori four-player adapter, the
  Zapper, the Famicom microphone, the Arkanoid controller, the Power Pad, and
  the Family BASIC keyboard. Anything outside the matrix is refused by name.
- **Deterministic defaults.** What hardware leaves to chance is a named
  power-on profile in the machine's configuration and identity, never a random
  draw. The defaults are CPU and PPU alignment phase 0, work RAM filled with
  zero, and an open-bus decay deadline chosen as the value AccuracyCoin's
  open-bus tests accept.
- **Corpora.** Every external corpus is fetched at run time into the per-user
  cache by pinned URL and SHA-256, as the Humble battery fetches its Game Boy
  collection, and never committed, whatever its licence. The loader resolves a
  cartridge from its header alone, so no game database is used; an ambiguous
  iNES header gets an explicit diagnostic instead of a lookup.
- **The co-simulation oracle** is an instrumented Mesen build the user supplies,
  as the Humble battery's SameBoy build is.
- **FDS firmware** is a user-supplied BIOS only, a local and by-hand input no
  check stage reads. An original Puck FDS BIOS is not planned: games call its
  services throughout play, so it would need verification on the scale of a
  second core.
- **Performance** is judged by counted work, as the bricks' is: zero
  steady-state allocation per frame is the battery's reading, and throughput is
  reported. Wall-clock realtime on the Steam Deck is measured only when the
  owner asks for a timing run.
- **Licence.** The Deck projects take the engine's Apache-2.0 licence. The
  GamingBricks dual licence names the brick projects, and widening it is a
  licensing change of its own.

---

[Decisions](README.md) · [The programme](../plans/machines-and-cartridges.md)
