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
tick that consumes its events. Cycle-positioned watches, input, and register
operations are implemented when a gameplay scenario names its timing
precision, and never advertised before that scenario passes.

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
