---
name: rom-forge
description: Guides Puck's data-first cartridge authoring in Puck.GamingBricks.Forge, its CGB and AGB native document compilers, and the in-engine forge console. Covers authoring, compiling, and decompiling `.puck` cartridge sources (`puck.cartridge.v1`) and their diagnostics, plus SM83/Thumb emitters, hardware kernels, Tune audio documents, boot-ROM tools and emulator-driven verification. Use for ROM source schemas, `.puck` cartridge authoring or compile/decompile errors, cartridge editing/build/play/export, native rule or graphics generation, and cartridge/boot/save seams. The `.puck` language core (grammar, CLI verbs) belongs to the puck-dsl skill; emulator core behavior belongs to gaming-bricks.
---

# ROM documents and native compilers

The user's current instructions outrank this file. Update stale instructions
in the same change rather than preserving an obsolete workflow.

## Authoring a cartridge

`.puck` is the cartridge's authorial source; `puck.cartridge.v1` JSON is the derived, engine-consumed
artifact and stays committed beside it (`tetris.cgb.puck` / `tetris.cgb.cartridge.json`, `hgb-mirror.cgb.puck`
/ `hgb-mirror.cgb.cartridge.json`). The regeneration gate in `tests/Puck.GamingBricks.Transpiler.Tests`
(`CartridgeRoundTripTests.TestCommittedSourceCompilesToTheCommittedDocument`) compiles every committed
source and byte-compares it against its committed document, so the two can never drift apart quietly.
Never hand-edit the generated JSON: regenerate it from the source. `src/Puck.World/Assets/cartridges/pip.agb.cartridge.json`
is the one exception today — no `.puck` source exists for it yet, so it is still authored and edited
directly as JSON, by hand or through the `forge.set` JSON-Pointer editor below.

`Puck.GamingBricks.Transpiler` is the `puck.cartridge.v1` vocabulary: it knows what a cartridge's sections
mean. The language itself — the one-spelling grammar, `let`/`template`/`for`, units, the compile-time
collection builtins and lambdas (`range`/`length`/`concat`/`map`/`filter`/`reduce`/`distinct`/`sort`/
`groupBy`), the `puck compile`/`decompile`/`lint`/`fmt`/`lsp` verbs and their flags, and the shared
diagnostics catalog belong to the puck-dsl skill; read that rather than re-deriving the grammar here. This
skill owns the cartridge vocabulary's own shape and the loop that turns a `.puck` edit into a running,
observable cartridge.

**The rule-body asymmetry.** A cartridge rule admits control flow a world rule refuses outright: `if …
{ } else { }`/chained `else if`, `repeat <count> as <name> { }` (count a compile-time literal 1..255) with
`break`, call-form gates (`key(<button>, held|pressed|released)`), and every compound-assignment operator
(`+= -= *= /= %= &= |= ^= <<= >>=`). Writing any of these into a `puck.world.def.v1` document instead trips
**PUCK037** (control flow where the vocabulary's rule shape is straight-line), **PUCK038** (a call-form gate
where only comparisons are legal), or **PUCK039** (a compound assignment the vocabulary's effects carry no
operator for) — `src/Puck.Transpiler/Diagnostics/PuckDiagnosticCodes.cs`. Porting a cartridge idiom into a
world document, or a world rule's straight-line habit into a cartridge, is the concrete way this bites.

A rule step or gate lowers to JSON as:

| DSL form | Lowers to |
|---|---|
| `a = b`, and `+= -= *= /= %= &= \|= ^= <<= >>=` | `set`, operation absent (assignment) or `Add`/`Subtract`/`Multiply`/`Divide`/`Modulo`/`BitAnd`/`BitOr`/`BitXor`/`ShiftLeft`/`ShiftRight` |
| `if <gate> { … } else { … }`, chained `else if` | `if` with `when`/`then`/`else` |
| `repeat <count> as <name> { … }` | `repeat` (a play-time loop; distinct from the document-only, compile-time `for`) |
| `break` | `break` |
| `map(row:, column:, tile:, palette:)`, `play(sound:)`, `fade(amount:, toward:)`, `blit`, `plot`, `blend`, `clock`, `save`, `load`, `stop` | the like-named step |
| `key(<button>, held\|pressed\|released)` | a `key` condition reading `$key:<button>:<mode>` |
| `==` `!=` `<` `<=` `>` `>=`, composed with `and`/`or`/`not` | a `compare` condition, composed as `all`/`any`/`not` |

Verified against the shipped `tetris.cgb.puck` (compiled with `puck compile --validate`), an input gate and
control flow read:

```
rule "steer" {
    when ph == 0
    ...
    if key(left, held) {
        want = 1
    }
    if key(right, held) {
        want = 2
    }
    ...
}
```

(`src/Puck.World/Assets/cartridges/tetris.cgb.puck:7603-7699`, elided to the gate usage — `key(...)` is a
gate condition only; it is not a readable operand inside an expression, unlike a plain state name.) A nested, guarded loop:

```
rule "scan" {
    when ph == 2
    repeat 5 as k {
        if rcur >= 18 {
            break
        }
        ...
    }
}
```

(`tetris.cgb.puck:7773-7778`; a `repeat`'s index name — `k` above — must be a declared variable, the same as
any other read.) A `map` step writing a background cell reads `map(column: "c2", palette: "pcol", row: "r2",
tile: "1")` (`tetris.cgb.puck:7765`). The compile-time collection builtins keep big data
sections short and reach a document field, never a rule body:

```
arrays [
    { name: "field", initial: map(range(0, 180), i => 0) }
]
```

## Verifying a cartridge edit

The loop from source to a running, observable cartridge:

1. Edit the `.puck` source.
2. `puck compile <name>.cgb.puck --output <name>.cgb.cartridge.json --validate` — `--validate` runs the
   forge's own `CartridgeDocuments.Validate` over the lowered JSON; lowering alone only proves the source was
   well formed, never that the cartridge is legal. (`-w`/`--watch`, `--strict`, and `--bundle` are the verb's
   other real flags.)
3. In a running `Puck.World`, load the compiled document: `forge.open <name>.cgb.cartridge.json` for a local
   draft, or `forge.play <screen-index> <name>.cgb.cartridge.json` to hot-load straight into a screen (an
   arcade cabinet's, among others). **Both verbs are JSON-only** — `forge.open` calls `CartridgeDocuments.Parse`
   on raw bytes and `forge.play` refuses any output path that does not end `.cartridge.json`
   (`src/Puck.World/ForgeCommandModule.cs`) — so the `.puck` step always happens before the console is
   involved; there is no transparent in-engine compile for cartridges the way `PuckWorldLoader` gives worlds.
4. `capture.start` to LOOK at the result. Cartridges are verified IN a world, never a bespoke driver harness.

## Player authoring

`src/Puck.GamingBricks.Forge` owns `puck.cartridge.v1`, validation,
canonicalization, the `CartridgeDraft` JSON Pointer editor, graphics encoding,
and `ICartridgeCompiler`. It depends only on Assets and is packable.
Read its README for the source contract and runnable console walkthrough.

`HgbCartridgeCompiler` in `Puck.HumbleGamingBrick.Forge` emits native CGB
SM83 code, 2bpp tiles, maps and palettes. `AgbCartridgeCompiler` in
`Puck.AdvancedGamingBrick.Forge` emits Thumb code and mode-0 4bpp graphics.
Both consume the same rules, variables, sprites and input conditions. Source
data determines behavior; never add a game-name switch or copy gameplay into
a C# template. Authored sample sources and generated cartridge JSON live under `src/Puck.World/Assets/cartridges`.

`src/Puck.World/ForgeCommandModule.cs` provides local console/seat drafts and
`forge.new/open/show/set/remove/undo/check/build/save/export/play` in every
boot shape. JSON tails retain quotes through `WorldCommandArguments`.
Draft edits can temporarily violate the schema; compile/save/play always
validate. Failed edits leave the draft intact. Source paths are explicit;
there are no environment-variable settings. `forge.play` saves canonical
cartridge source, then submits `WorldScreenOp.Insert` with the acting principal
through `IServerLink`; the registered content provider compiles that source.
Never bypass the server's screen authority or replay/content hashing. Both verbs are JSON-only (see
"Verifying a cartridge edit" above for the `.puck` step that precedes them).

The current schema has explicit limits and omissions: cgb/agb targets,
byte and two-byte state, addressable byte arrays, 8x8 or tall sprites, runtime background writes,
cartridge audio, battery-backed state, and rule bodies that are step TREES
(`set`/`if`/`repeat`/`break`/`map`/`blit`/`play`/`stop`/`save`/`load`), not flat
action lists. An operand is a `Puck.State.ValueExpression` in its infix spelling
and a gate is a `Puck.State.ActionPredicate`, so `(a + b) * 2`, `field[i + 1]`,
`any` and `not` are all authorable; `CartridgeExpressions.Reads` is the admitted
operation subset and everything outside it is refused at its own spelling. A
button reads through `$key:<button>:<mode>`, which is why input composes under
`not`. A write target is `{state, key}`, the key being a bare number, a bare cell
read, or `$expr:` plus the index's infix spelling.

The schema does not claim DMG or ROM banking. Common primitives preserve their authored semantics on both
targets; hardware-specific features have explicit validation gates. Consult the validator before describing
a feature as portable. Wide slots precede byte slots in the shared layout, and saves persist every byte.
Byte expressions reject wide literals and reads instead of truncating them differently across targets.

Colour is per-palette, not global: `palettes.background` / `palettes.object` hold
up to 8 palettes on cgb and 16 on agb, `mapPalettes` picks one per background
cell, and a sprite picks an object palette. cgb carries the cell attributes in
video bank one (low three bits) and agb packs them into the screen entry's top
four bits; a runtime `map` write carries its own optional `palette`, and the cgb
queue drains twice — once a bank — to land the tile and the attribute.

A sound is EITHER a music track or a one-shot effect. A track is 1-4 voice
parts, one to a channel; validation refuses an effect on any voice a track
occupies. The four sequencers are identical and a loop start is the only thing
that separates a track from a one-shot: a voice carrying one rewinds at the
terminator, a voice without one stops and mutes. Stream step widths are
`duration` + FIVE register bytes for pulse one (NR10-NR14) and for wave
(NR30-NR34), FOUR for pulse two (NR21-NR24) and noise (NR41-NR44) — keep the
encoder and both drivers' register counts in sync. A music part's hold SUSTAINS
where a one-shot's mutes, which is why the two cannot share a row appender.

`AudioDocumentCompiler` and `ApuNotePeriod` live in the SHARED forge package, not
the humble one: the advanced machine's legacy sound channel takes the same four
registers, so one compiled track drives both. `AgbSoundDriver` remaps them to the
halfword pair at 0x04000068 / 0x0400006C — KEEP IN SYNC with the humble driver's
port order. `AgbSaveModule` writes the byte-wide window at 0x0E000000 and the
image must carry its identifier text, which is how a host recognizes the backup
kind; without it the window reads open bus and nothing persists. Add those by extending data, validation, both relevant backends and
native execution tests, not by a hidden special case.

## Backend contracts

Read [references/backend-contracts.md](references/backend-contracts.md) for memory addresses, register
bits, the cost model, and native-compiler instruction-level behavior — none of it has a DSL spelling, so
it stays out of the authoring loop above. It is dense and hard-won; rewrite only to correct a fact, never
to shorten.

## Other maintained tools

The existing `Framework/` low-level APIs remain for Tune/native tooling.
`GameFramework`, `GameManifest`, `AssetLinker`, sound/save/PRNG helpers and
PBAK readers are not the player editor. PBAK has no live producer; do not
present that legacy route as in-engine art authoring.

`TuneRom.Build(AudioDocument, string)` remains the live world-audio compiler.
Audio/synth documents live in Assets; keep the forge packages independent of
World. `HgbImage` contains native image encoders; `AudioDocumentCompiler`
shares the integer note-period calculations with the audio subsystem.

## The authored boot ROMs (`BootRomBuilder`)

`BootRomBuilder.Build(ConsoleModel)` emits the boot ROM a revision executes from
reset: 256 bytes for a monochrome revision, 2304 for a Color one (mapped
0x0000-0x00FF and 0x0200-0x08FF, the cartridge header showing through the gap).
It is a real boot program — header-checksum verification with a wedge on mismatch
(the companion-console revisions check neither logo nor checksum), an original
PUCK wordmark scrolled in, a rising-fifth pulse chime (silent on companion revisions),
the compatibility-mode selector and compatibility-palette load for
a cartridge without the color flag, the revision's register handoff, and the unmap
at 0x00FE so the program counter falls into 0x0100.

- **Presentation and admission are independent.** `BootRomBuilder.Build(model, mark)` defaults to
  `BootRomMark.Compatible`, which displays the original four-tile PUCK wordmark and accepts ordinary, Puck and
  independently authored header logos. Explicit `Era` and `House` remain strict diagnostic policies with a compact
  monogram and a 48-byte comparison table. A header bitmap is editable data, not authentication or an enforceable
  Puck-only distribution. Calibration boots the matching emitted policy; never calibrate one instruction layout
  against another. No policy expands cartridge artwork into the framebuffer.
- **The handoff counter is the contract.** `BootDivPrediction`'s tables are the
  budget; the Color image carries those same tables and computes its target from
  the cartridge header, plus the revision's own offset from those shared tables
  (`BootRomLayout.HandoffCounterExtra`, which carries `Cgb0Extra`/`AgbExtra`).
  The program resets the divider and then consumes exactly the predicted count,
  so everything before the reset is free and everything after it is straight
  line.
- **The tables are pinned from outside; the solve is not evidence.** The same
  `Compute(model, header)` call seeds the post-boot state, sets the emitted
  image's target, and steers the builder's solve, so a wrong table produces a
  matching boot. The `boot-rom-handoff` stage breaks that by booting the named
  mooneye `boot_div-*` reference cartridges and checking both the prediction and
  the authored image against the counter each cartridge asserts. That covers one
  header on every revision; every other table row is unpinned outside this
  repository, and the stage's pass detail says so. Never move a table value to
  make a boot agree with it.
- **The straight line is SOLVED, not counted.** `BootRomBuilder` boots the image
  it just emitted against `BootRomProbeCartridge` headers and adjusts two
  machine-cycle constants until the handoff counter and scanline land. Never
  hand-count instruction timings into these constants, and never let the solved
  tail absorb a per-revision offset — that is `HandoffCounterExtra`'s job, added
  by the emitted table walk.
- **The compatibility palettes are the index registers' cause.** The image carries
  `CompatibilityPalette`'s selection tables and loads the chosen background and two
  object palettes eight bytes at a time through BCPD/OCPD auto-increment, which is
  the only thing that lands BCPS/OCPS on 0x88/0x90 (read back 0xC8/0xD0). The data
  ports read sealed in compatibility mode, so palette RAM contents are not compared
  there (the capture is gated on the compatibility authority) and the seeded path
  carries no palette-RAM seed. Space for those tables comes from the low window
  (`BootRomLowWindow`, between the entry jump and the unmap) and from carrying the
  divider's checksum contributions as one common value plus a row per checksum
  that differs (`BootRomChecksumTable`).
- **Equivalence surface.** `BootRomHandoff.Compare` has ONE runner, the POST's
  `boot-rom-handoff` stage; the forge tests pin each revision's image to a
  recorded hash instead. The compared surface is the processor register file, the
  divider counter, every readable high-page register, high RAM through 0xFFFE,
  the interrupt-enable register, and Color palette RAM where the compatibility
  authority (`DmgCompatibilityState.IsActive`) says the silicon runs natively —
  never on the model alone. `Capture` mutates nothing: it snapshots the machine
  around the palette walk and restores it. Only the picture-pipeline and
  audio-generator sub-register phase is outside the surface — it is not reachable
  from an executing program. The LY-comparison bit is IN it and is not masked:
  a revision whose seeded handoff parks at LY==LYC==0 carries the latch set, so
  its status register reads 0x84.
- Runtime packages embed explicitly generated images; they never reference Forge or calibrate while loading a game.
  `HgbFirmware.GetImage(model)` returns a private copy. Regenerate with the owning `puck firmware` verb and verify
  its bytes before packaging.
- `MachineBootMode` selects cold or fast startup independently of image selection. Fast startup retains the selected
  image's `MachineIdentity` fingerprint; cold and fast instances of that same image can restore complete snapshots.
  A firmware-free seeded diagnostic remains a different identity. Component power-on initialization uses
  `MachineConfiguration.ExecutesBootRom`, not a null-image test.

The native ARM7TDMI firmware is separate from `BootRomBuilder`'s SM83 `Agb`/`Ags`
compatibility images. Its source and build contract live in
[`Puck.AdvancedGamingBrick/Firmware`](../../../src/Puck.AdvancedGamingBrick/Firmware/README.md).
Use `puck firmware agb --verify` with that document's explicit LLVM paths to
check the generated image; never substitute a runtime Forge dependency or a
managed SWI shortcut. Keep its permissive provenance and package-local notices
with the image. Route native service, music-driver and download changes through
the [Advanced Post firmware stages](../../../src/Puck.AdvancedGamingBrick.Post/README.md#tiers),
using local retail firmware only as a black-box functional oracle and never as
source or a bundled asset.

## Per-frame cost is measured, never estimated

`CartridgeCost` prices a document in abstract work units and
`CartridgeDocuments.Estimate` reports the frame's bound beside the target's
reservation; nothing refuses on it. The units name no processor,
clock or instruction count; one unit is an eleventh of a `set` step writing a
literal to a variable. Weights come from `CartridgeCostMeasurement`, which boots
documents on both real machines and bisects the largest per-frame iteration count
each sustains at full frame rate; cost per iteration is inversely proportional to
that capacity. Never hand-count an emitter's instruction sequence into a weight,
and never adjust a weight to make a document fit.

`CartridgeCostProfile` carries separate weights and reservations for each target. A multiply has different
relative cost on SM83 and Thumb, so changing `/target` can change the estimate and cadence. Never compare raw
unit totals across targets or describe the advisory estimate as a proof of frame timing.

A blit SUSPENDS frame production rather than costing work: with the display off no
vertical blank arrives, so the frame counter does not advance. Measured, a
rectangle degrades cadence steeply past 120 tiles and stalls the document entirely
near 168, which is what `CartridgeLimits.BlitCellCount` pins. Never raise it
without re-measuring.

`map` writes queue and drain in the next frame's vertical blank on BOTH targets —
AGB stages and drains at its frame sync deliberately, so a cell changed this frame
appears the frame after on either machine. Validation bounds a frame's map writes
to the queue's 24 entries so a write can never be silently dropped; the push
subroutine's drop path must stay unreachable. `blit` is implemented and validated
on both targets but is deliberately UNMODELED: a small one measures near a fixed
cost while a full-screen one exceeds a whole frame on the Color machine and barely
registers on AGB, so it is both uncharacterized and a cadence-divergence hazard.
Do not give it a weight without a model that spans both observations.

A primitive with no measured weight prices as `CostBound.Unmodeled`, which
poisons the whole estimate rather than contributing an invented number, so the
document builds with no usable advice. Adding a primitive means measuring it. To re-measure, raise the reservation so the harness
can probe past it, run the harness with `PUCK_FORGE_MEASURE=1`, fold the reported
capacities in, and restore the reservation. `CostBound` and `CostModelProfile`
live in `Puck.Maths` and are shared with `Puck.State`'s rule cost; each subsystem
owns its own coefficients.

## Verification

Run both existing forge test projects. `CartridgeCompilerTests` builds JSON
source, exercises editing/refusals, pins deterministic output after round-trip,
and runs inputs, comparisons, arithmetic and sprite/graphics observations on
both real emulators. Emitter probes and boot-image hash tests remain separate.
Compiler validation cannot prove an arbitrary player's game correct; no
hidden demo verifier runs during document compilation.

Every SM83 verifier MUST call
`VerifyMachineSettle.SettleOutOfOamDma` after stepping frames before bus reads.
A fixed cycle boundary can land inside DMA and otherwise falsely read gated
WRAM as 0xFF. `Framework.VerifyMachineDriver` already applies this rule.

For World integration, run the actual host through stdin using the puck-world
skill. Verify source editing, canonical save/open, ROM export and accepted
screen insertion for both targets; a build alone is not a play test.

Keep XML API docs, package READMEs, the shared source guide, World's command
entry, project map and this skill synchronized. Named arguments, m_ fields,
and the repository analyzers apply; do not raise complexity ceilings.
