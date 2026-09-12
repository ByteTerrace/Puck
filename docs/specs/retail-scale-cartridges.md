# Retail-scale cartridges

Whether Puck data can carry a whole retail-scale game. Pokémon Gold/Silver is
the north star: roughly 250 species, 100 maps, a text engine, a battle engine, a
save the size of a small filesystem, and a day/night clock.

The target is **reimplementation, not byte identity** — a document that plays the
same, not one that assembles to the same bytes. That decision settles the rest of
the design, because it says the compiler owns encoding and the document owns
structure.

This document records the decisions and the work they imply. It states no status:
what is built, the code answers, and what has been verified with the check that
produced it belongs to [campaign.md](../campaign.md). If this plan and the code
disagree, the code wins and a line here is wrong.

## What the document can say, and what it cannot

`puck.cartridge.v1` is a frame-shaped template with slots, not a program model,
and three absences are the whole of why.

| Absent | Consequence |
|---|---|
| Any notion of a **procedure** | Rules are a flat list evaluated once per frame in declaration order. There is no call, so no behavior can be named, parameterized or reused, and there is no unit for a bank to hold. |
| Any **typed region** | State is named byte slots and named byte runs. An element is addressable by a computed index, but there is no record layout and no pointer, so a party, an NPC list and a table of pointers have no shape to be declared in — only a flat run and an author-maintained stride. |
| Any **interrupt body** | One implicit frame, with a fixed-capacity vblank write queue and `raster` as the two hard-coded escapes. Nothing else on the machine's timeline is addressable. |

Two facts change what the work costs.

**The backends are ready.** `Sm83Emitter` emits `Call`, `Return` and
`ReturnFromInterrupt`; `ThumbEmitter` emits `Call`. Procedures are a
document-and-middle problem, not an emitter problem.

**`Puck.State` holds vocabulary a cartridge should consume rather than restate**,
and the argument is the same each time: a cartridge's rules ask the same questions
of the same shapes a world's rules do. The comparison and opcode enums, the
expression (`ValueExpression`, admitted through `CartridgeExpressions.Reads`) and
the gate (`ActionPredicate`, composing through all/any/not) are consumed from
there. What a cartridge still restates, and the shape that would replace it:

| Cartridge | `Puck.State` |
|---|---|
| `variables` (named bytes) and `arrays` (named byte runs) | `StateRow`, a slot when it holds one cell and a **table** when it holds author-keyed cells, over typed `CellKind` (Int, Fixed, Bool, Text) |
| Nothing | `StateDomain.Ring`, `CellsOf`, `KeysOf` — ring buffers and lattice-shaped rows |
| The backends recognise the reserved button operand themselves | `RuleVocabulary`'s `OperandFamily`/`KeyFamily`, the seam a document project registers its reserved spellings through |
| Hand-written `CartridgeCost` | `RuleCost`, `RuleDataflow`, `RuleHazards`, `RuleWorkBudget`, `StateFrameHash` |

## What retail games do, and what it implies here

| Where | The idea | What it implies |
|---|---|---|
| Pokémon G/S, the Oracle games | A game-mode byte partitions the frame; each mode dispatches into its own handlers, and only one runs. | `scene` is exactly this, and is the one retail shape the document already has. Keep it unchanged. |
| Every GB game past 32 KB | Code is banked; the call graph decides placement and a trampoline makes a far call look like a near one. | A procedure must be a named document unit, because banking needs something to place and the author needs to say what is far. |
| Pokémon's overworld objects | An array of fixed-size records walked by a dispatcher, each carrying position, sprite, a state machine and a script pointer. | Typed regions. An entity primitive would be a second implementation of what a memory model gives. |
| Pokémon's text | A ROM string table reached through a far pointer, drawn a character at a time across frames, with control codes for pauses, names and breaks. | ROM residency, `Text` cells, and a draw that spans frames — not a `print` step. |
| Battle damage | 16×16 products with 32-bit intermediates and divides; the PRNG needs a multiply of its own. | Arithmetic completion is load-bearing, not a nicety. |
| Every GBA game, and most GB ones | Assets ship compressed; on GBA the BIOS decompressors are the de facto codecs. | A codec is a document concept: the document holds the decompressed asset and the compiler encodes it. |
| Sappy/M4A, and the per-game GB drivers | Audio is a driver — ROM tables walked by a per-frame update with its own note format and envelopes. | Falls out of procedures over ROM tables. The standing ruling against a tracker primitive survives. |
| Raster splits, HDMA streaming, mid-scanline writes | Writes placed at known scanlines within a known cycle budget. | Interrupt bodies with a verified cycle budget, generalizing `raster` and the vblank queue rather than adding a third escape. |

## Decisions

**D1 — The gate is behavioral determinism, not a byte diff.** Reimplementation
gives up any oracle against a retail image, so "this game works" is carried by
machinery already here: a forged image replayed from a recorded input script must
produce identical machine-state hashes on repeated runs of each backend. Cross-target parity compares normalized
authored variables and arrays at the same completed game-frame boundary, using each compilation's symbol map;
native addresses, registers, timing state and ROM bytes differ between machines. Independent expected-value
tests establish correctness: deterministic replay alone can repeat the same wrong answer. `HashDivergenceProbe`,
`CosimDiagnostic` and the snapshot paths provide per-target evidence, not interchangeable hardware hashes.

**D2 — A cartridge consumes `Puck.State`'s vocabulary rather than restating it,
and the forges are code-generating backends for it.** No second language, and no
arm on one side that the other lacks. The expression and the gate come from there;
the row vocabulary is what the memory model will take next. The step tree stays
the cartridge's own, because a machine verb is not a state effect and the tree is
what `if`, `repeat` and `break` need — the cartridge-only verbs belong in this
vocabulary, not as derived arms of the engine's effect list.

**D3 — A procedure is a document row, not a compiler outlining pass.** Outlining
recovers code size but leaves the author no way to say what lives far and the
linker no name to place. Procedures take parameters, declare their stack bound,
and are placed by a call-graph partition.

**D4 — `template` and a procedure are different tools, and both stay.**
`template` is authoring reuse expanded at compile time; a procedure is ROM-size
reuse called at run time. Macro expansion at retail scale explodes the image, and
a call at authoring scale costs a name for nothing.

**D5 — Payload leaves the source.** A `.puck` holds structure and references
assets; tiles, maps, audio and bulk tables are ingested from asset files rather
than spelled as scalars in the document text.

**D6 — The frame-shaped ceilings are re-derived, not raised.** `RuleCount`,
`StatementCount`, `MapWriteCount`, `BlitCellCount` and their neighbours bound the
shape of one frame. Under a program model the real resources are code bytes per
bank, cycles per interrupt window, and RAM bytes; raising the old numbers keeps
measuring the wrong thing.

**D7 — The content engines are a library, not engine features.** Given D2, D3 and
a memory model, a text engine, an entity dispatcher, a script interpreter and an
audio driver are authored — procedures over typed memory, composed through
`import`. That is the payoff, and it is what keeps the forge from growing a
Pokémon-shaped arm.

**D8 — Cost stays advice.** Validation refuses what makes an image wrong — a
shape with no room, a map write that would be dropped — never what makes it slow.
Nothing here reintroduces a cost refusal.

## Ceilings, and what replaces each

| Constant | Bounds today | Replaced by |
|---|---|---|
| `VariableCount`, and the variable window in bytes | named byte slots in a fixed window | typed regions; a named slot becomes a view into one |
| `ArrayCount`, `ArrayLength`, `ArrayByteCount` | WRAM-resident vectors seeded from a ROM table at boot | regions for mutable state, ROM residency for read-only tables; the byte index becomes an addressed offset |
| `RuleCount` | a bound to stop a runaway document | code bytes per bank, refused by `CartridgeCapacityException` |
| `MapWriteCount`, `RasterRowCount` | two hard-coded escapes onto the machine's timeline | interrupt bodies with verified cycle budgets |
| `SaveByteCount` | a work-RAM mirror written through to the save window | the cartridge's own RAM banks |
| `TileCount` | one tile bank | paged tile sets, placed by the linker |
| `WideMaximum`, with only assignment, `Add` and `Subtract` widened | two-byte slots | 16×16 multiply, divide, and 32-bit intermediates |

## Order of work

Dependency-ordered. Each stage is authorable and verifiable on both machines
before the next begins.

**1. The memory model.** Declared regions with typed layouts — records, arrays of
records — and an indirect operand form of base, offset and field. Named slots
become views. Dissolves four ceilings at once and is what every content engine is
written against. Proved by a record-walking document reading and writing the same
cells on both machines.

**2. Procedures and banked code.** Named parameterized bodies with a declared
stack bound; the rule list becomes the frame procedure. Bank assignment is a
call-graph partition plus a trampoline. Proved by a document whose code exceeds
one bank executing correctly across a far call on both machines.

**3. Interrupts and cycle budgets.** Declared vblank, hblank, timer, serial and
joypad bodies, each with a compiler-verified cycle budget; `raster` and the vblank
queue become instances of the general mechanism. Proved by reproducing the current
raster behavior through a declared hblank body, with the old path deleted in the
same change.

**4. ROM residency, codecs, and the real save.** Bank-and-offset addressable
tables with far reads and a linker that places them; codecs so the document
carries decompressed assets; the save becomes the cartridge's banked RAM window.
Proved by a table larger than work RAM read at run time, and a save larger than
the current mirror surviving a power cycle.

**5. Arithmetic completion.** 16×16 multiply, divide and 32-bit intermediates on
both backends, total under the rules the current helpers already follow — a zero
divisor yields zero. There is now one expression evaluator per machine to widen
rather than an operation's worth of scattered arms, which is most of what made
this stage expensive. Proved by a damage-formula document agreeing with a
reference implementation across a swept input space on both machines.

**6. Asset ingestion.** The document references assets; `puck` verbs ingest
images, maps and audio into that referenced form. A capability nobody can feed is
theoretical, and this is what makes the content volume tractable.

**7. The content library.** Text, camera-driven tilemap streaming, entity
dispatch, battle math, an audio driver — `.puck` modules composed through
`import`. Authored, not engineered; nothing in it should require touching a forge
project.

## Authoring and ergonomics

The authoring surface is the binding constraint at scale. Measure the committed
Tetris source against its document and two things show: the source is barely
smaller than the JSON it generates, and the overwhelming majority of it is
payload rather than logic — a few dozen rules against thousands of lines of
tiles, maps, arrays and audio, laid out one scalar per line by the canonical
formatter.

```bash
puck search '^(tiles|map|arrays|variables|sprites|sounds|rule) ' src/Puck.World/Assets/cartridges/tetris.cgb.puck -M 0
```

A retail-scale game carries orders of magnitude more logic than that, and on
these proportions its committed source would be unreviewable. Hence D5: payload
is referenced, not spelled.

Owed beyond the numbered stages:

- **`for` inside a rule body.** The language's compile-time loop stops at the rule
  boundary: the effect dispatcher has no `for` production, so a repetitive step
  sequence has no generator.
- **Formatter layout for bulk data.** One scalar per line is right for logic and
  wrong for a thousand-entry table.
- **Diagnostics at depth.** Source spans and refusals are good; untested is a
  diagnostic pointing into a template expanded inside an imported module inside a
  `for`.
- **Two transpiler constraints** that block DSL cartridge authoring: `let`-array
  re-lowering is cubic in element count, and an array-valued `let` used as a
  vector property lowers non-finite.

## Deliberately excluded

- **Byte-identical output**, by D1. The compiler owns instruction selection and
  layout.
- **Decompiling a retail ROM into `.puck`.** A document expressive enough to hold
  an arbitrary instruction stream is a hex dump with syntax. The document holds
  structure; that is the point, and it is why the direction is authoring rather
  than lifting.
- **Emulator changes.** Both machines are the oracle; work here never edits them
  to make a forged image agree.
- **A DMG target**, unclaimed today and unclaimed here.
- **A cost refusal**, by D8.
- **Entity, text, battle or tracker primitives**, by D7 — library content, and
  adding any of them to the schema would be a second implementation of a
  mechanism the memory model already provides.

## Verification

Every stage carries native execution on both real machines, because that is the
forge's standing bar and the only thing separating a compiling document from a
running one. Two gates are shared across all of them.

- **The round-trip gate** compiles the committed source and compares bytes
  against the committed document, so source and JSON cannot drift as either
  grows.
- **The determinism gate** of D1: repeated runs match on each backend; the two backends agree on normalized
  authored state at matched game-frame boundaries. Independent expected-value tests cover arithmetic, guards,
  storage and persistence. A reproducible mistake must still fail the correctness gate.

Capacity remains the one hard refusal: every way an image can outgrow its machine
raises `CartridgeCapacityException` naming what overran, and nothing here converts
a capacity failure into a truncated ROM.
