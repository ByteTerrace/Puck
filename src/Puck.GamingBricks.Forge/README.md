# Puck.GamingBricks.Forge

Players author ROMs as `puck.cartridge.v1` JSON documents. This package owns
the source model, validation, canonical source hash, JSON Pointer editor, and
compiler contract. It has no World or platform dependency. The
[HGB compiler](../Puck.HumbleGamingBrick.Forge/README.md) emits CGB cartridges;
the [AGB compiler](../Puck.AdvancedGamingBrick.Forge/README.md) emits AGB
cartridges. The exported bytes run on their native emulators without a managed
game interpreter. There are no embedded sample games.

## Author inside the engine

Open Puck's console. The same commands work through process stdin, including
a headless host. `help forge.set` describes an individual command.

```text
forge.new cgb PLAYER
forge.set /variables/- {"name":"x","initial":32}
forge.set /tiles/- {"name":"block","pixels":["11111111","11111111","11111111","11111111","11111111","11111111","11111111","11111111"]}
forge.set /sprites/- {"name":"player","tile":{"constant":1},"x":{"variable":"x"},"y":{"constant":40},"visible":{"constant":1}}
forge.set /rules/- {"name":"move","when":[{"kind":"key","key":"right","mode":"held"}],"body":[{"kind":"set","target":{"variable":"x"},"operation":"add","value":{"constant":1}}]}
forge.show /rules
forge.check
forge.build
forge.save player.cartridge.json
forge.export player.gbc
forge.play 0 player.gbc
```

This creates a movable tile sprite. The blank document starts with one blank
tile, an empty map, explicit palettes, and no behavior. Authors supply their
own rules and art. Screen 0 must already be declared in the world, and the
acting author must hold Control over it. `forge.play` writes the ROM and
submits the ordinary screen insertion; the server reports acceptance or
refusal. Existing engagement and pad mappings provide player input.

`forge.new agb PLAYER` starts an AGB source instead. To change an existing
CGB source to AGB, set `/target` to `"agb"` and replace `/palette` with 16
RGB555 integers. Check the result before building.

| Command | Effect |
|---|---|
| `forge.new <cgb|agb> <title>` | Replace this author's draft with explicit blank source. |
| `forge.open <source-path>` | Replace the draft with validated JSON from disk. |
| `forge.show [pointer]` | Read the whole draft or a subtree as JSON. |
| `forge.set <pointer> <json>` | Set a field/element; a final `/-` appends an array element. |
| `forge.remove <pointer>` | Remove an existing field/element. |
| `forge.undo` | Swap with the previous edit; invoking again redoes it. |
| `forge.check` | Validate source and report its canonical hash. |
| `forge.build` | Compile in memory; report target, ROM size, hash and variable addresses. |
| `forge.save <source-path>` | Atomically write validated canonical JSON. |
| `forge.export <rom-path>` | Compile and atomically write ROM bytes. |
| `forge.play <screen-index> <rom-path>` | Export and submit a normal authoritative screen insert. |

Paths are explicit, relative to the host working directory unless absolute.
Their parent directories must exist. Save/export replace an existing file.
Drafts belong to each acting local console or seat; they are local editor state,
not a replicated world mutation. Save before starting another draft or leaving
the host. A failed edit/load preserves the previous draft. Drafts may be
temporarily invalid; check, save, build, export and play all validate. No
environment variable controls authoring or compilation.

Pointers use RFC 6901 (`/tiles/1/pixels/0`, with `~0` for `~` and `~1` for `/`).
JSON strings keep their quotes, for example `forge.set /title "MY GAME"`.
Array indices must exist unless appending; unknown fields and references are
refused at validation. Source files are bounded to 1 MiB and nesting to 32.

## Boot a document from a world screen

A world's `screens[].source` of `$type: machine` may name a source document
directly: a `contentPath` ending in `.cartridge.json` is parsed and compiled at
bind through the engine's own compiler (`gaming-brick` uses
`HgbCartridgeCompiler`, `advanced-gaming-brick` uses `AgbCartridgeCompiler`)
and the compiled bytes boot exactly as an exported ROM does. `screen.state
<index>` echoes `cartridge <path> hash <canonical source hash> rom <image
hash>`. A document this package's validator or a compiler refuses faults the
slot with the same message `forge.check` would print; an engine with no
compiler refuses the path when the world document validates. The shipped
cartridges under `src/Puck.World/Assets/cartridges/` are the worked examples.

## Source contract

All top-level fields are required. `forge.show` prints a complete starting
document; applications can obtain the same data with `CartridgeDocuments.Create`.

| Field | Meaning and limits |
|---|---|
| `schema` | Exactly `puck.cartridge.v1`. |
| `target` | `cgb` or `agb`. CGB uses the Color hardware, not DMG compatibility. |
| `title` | 1–12 printable ASCII characters, canonicalized to uppercase. |
| `gameCode` | Four ASCII characters; used by the AGB header. |
| `palettes` | `{background,object}`, each 1–8 palettes on CGB or 1–16 on AGB. A palette is 4 RGB555 integers on CGB, 16 on AGB. Object color zero is transparent. |
| `tiles` | 1–256 named 8×8 tiles. Each `pixels` array has eight strings of eight hexadecimal palette indices. Sprite color zero is transparent. |
| `map` | Exactly 1024 tile indices, row-major over a 32×32 background. |
| `mapPalettes` | Optional 1024 background palette indices, one per cell. Absent means every cell is on palette zero. |
| `bitmap` | Optional `{clear}` per-pixel drawing surface, 240 by 160, one byte per pixel indexing the background palette bank read as one flat run of 256 colours. It replaces the tile background entirely, so it cannot share a document with a panel, a turning background or extra layers; sprites still draw over it. `clear` is the colour it is filled with each frame, or absent to leave it as drawn. AGB only. |
| `layers` | Up to 2 `{map,mapPalettes,scrollX,scrollY,priority,visible}` scrolling backgrounds behind the document's own, each with its own 32×32 map and scroll. `priority` is 0 (nearest) through 3. A turning background occupies the nearer of the two hardware surfaces, so declaring one leaves room for a single layer. AGB only. |
| `window` | Optional `{map,mapPalettes,x,y,visible}` panel drawn over the background from its corner down and right, out of its own 32×32 map. |
| `raster` | Up to 8 `{line,scrollX,scrollY}` rows in ascending scanline order, each setting the scroll for the band from its line to the picture's foot. `line` is 1–143; line zero is the document's own scroll. |
| `clock` | Optional naming of state slots a `clock` step fills from the cartridge's real-time clock. `seconds`, `minutes` and `hours` work on both targets. The rest do not, because the two machines carry different devices: `days` is a count of days since the cartridge started and is cgb only, while `day`, `month` and `year` are a calendar date and are agb only. |
| `affine` | Optional `{map,angle,scale,centreX,centreY,visible}` background that rotates and scales. Angle is a turn in 256 steps, scale is sixteenths (16 = life size). AGB only. |
| `tallSprites` | Draws sprites 8×16; a sprite's tile index then names a pair and its low bit is ignored. |
| `variables` | Up to 64 `{name,initial}` unsigned bytes. |
| `arrays` | Up to 32 named `{name,initial}` byte runs, 7168 bytes in total. `initial` fixes the length at 1–256; a byte index cannot address more. |
| `screens` | Up to 16 named `{name,width,tiles}` rectangles, at most 120 tiles, painted by a blit step. |
| `sounds` | Up to 8 named sounds, each exactly one of `music` (a looping audio document), `effect` (a one-shot on the pulse-1, noise or wave voice, with `frames` per row, and a 32-entry `waveform` for the wave voice), or `sample` (signed 8-bit recorded audio; AGB only). |
| `save` | Optional `{version,variables,arrays}` battery-backed state, at most 72 bytes. |
| `rules` | Up to 64 `{name,when,body}` rules; at most 8 conditions per rule and 64 steps anywhere in one body, nested at most 8 deep. |
| `sprites` | Up to 40 `{name,tile,x,y,visible,palette}` 8×8 sprites. Every value field accepts constants or variables; `palette` is optional and selects an object palette. |
| `scrollX`, `scrollY` | Constant/variable background offsets in pixels. |

Names are unique within each collection, case-sensitive, 1–64 ASCII letters,
digits, underscores or hyphens, and an array may not reuse a variable's name.
Constants and initial values are in 0–255.

A value reads exactly one of a literal, a state slot, or an array element:

```json
{"constant":42}
{"variable":"score"}
{"array":"field","index":{"variable":"cursor"}}
```

An index is itself a value, so `field[pointers[cursor]]` nests. An index at or
beyond the array's length reads zero and discards a write, which keeps every
access total rather than trapping. A write destination is the same shape without
the literal: `{"variable":"score"}` or `{"array":"field","index":{...}}`.

A rule body is a tree of steps, not a flat list. Each step is one of:

```json
{"kind":"set","target":{"variable":"score"},"operation":"add","value":{"constant":1}}
{"kind":"if","when":[...],"then":[...],"else":[...]}
{"kind":"repeat","count":18,"index":"row","body":[...]}
{"kind":"break"}
{"kind":"map","row":{"variable":"y"},"column":{"variable":"x"},"tile":{"constant":3}}
{"kind":"blit","screen":"panel","row":0,"column":0}
{"kind":"play","sound":"theme"}
{"kind":"stop"}
{"kind":"fade","amount":{"variable":"dim"},"toward":"black"}
{"kind":"blend","surface":"panel","weight":{"variable":"alpha"}}
{"kind":"play","sound":"pluck","rate":{"array":"notes","index":{"variable":"row"}}}
{"kind":"plot","row":{"variable":"y"},"column":{"variable":"x"},"colour":{"constant":3}}
{"kind":"save"}
{"kind":"load"}
```

A `map` step writes one background cell at run time. Writes are queued and land
together in the next frame's vertical blank, so a cell changed this frame appears
the frame after. The queue holds 24 entries, and validation bounds a frame's map
writes to that rather than letting the queue drop one: loops multiply, and branch
arms count once because only one runs. A blit repaints a whole named screen with
the display off; its row and column are literals. Because no vertical blank
arrives while the display is off, a blit suspends frame production rather than
merely costing work, which is why a screen is capped at 120 tiles — measured, the
frame counter stalls entirely near 168.

A `raster` row changes the scroll part way down the picture, which is what puts a
fixed status panel over a scrolling world or drives layers at different speeds.
The Color machine takes a scanline-match interrupt and writes the registers in
the horizontal blank before the band's first line; the advanced machine has no
interrupt handler when it direct-boots, so it feeds the same registers from a
table through a horizontal-blank transfer instead. Rows republish in the
vertical blank on the advanced machine, so a change there shows on the following
frame exactly as a map write does.

A `play` of a recorded sound takes an optional `rate`: the playback rate in
sixty-fourths of the recording's own, so 64 plays it as recorded, 128 an octave
up and 32 an octave down. One recording therefore serves a whole instrument's
range, and a rate read from an array makes an ordinary rule into a sequencer —
there is no tracker primitive, and none is needed. A rate of zero sounds
nothing rather than holding one sample forever. Rates are refused on music and
effect sounds, which take their pitch from their own rows.

`play` starts a named sound. Music replaces whatever the melodic voice was
playing; an effect runs on one of the two voices reserved for one-shots, so it
never interrupts the music under it and ends on its own terminator; a sample
takes one of four mixer voices. `stop` silences the music voice only.

A `sample` needs the AGB target: that machine streams recorded audio through a
timer-clocked transfer into a mixer the cartridge runs every frame, and the CGB
target has no digital sound hardware to stream into. Every other sound kind runs
on both. `save` writes the declared state to the cartridge's
battery-backed window behind a magic, version and checksum header, and `load`
restores it — a block that fails any of those checks leaves the state at its
authored initial values, so a fresh cartridge and a corrupted one behave alike.

Both run on either target. One compiled track drives both machines: the advanced
machine's legacy programmable-sound channel exposes the same four registers the
humble machine's does, so a document's audio does not change with its target. The
save block carries the same magic, version and checksum on both, though the stored
bytes live in each machine's own save window and a saved game does not travel
between them.

Compilation refuses a document only for what makes the image wrong — a shape the
machine has no room for — never for what makes it slow. A cartridge that misses
frames still runs, and the machine absorbs that already, so the per-frame work is
reported rather than refused: `CartridgeDocuments.Estimate` returns the estimate
and the target's reservation, and an author over it gets a slower cartridge, not
a wall. An image that outgrows its machine's windows raises
`CartridgeCapacityException` naming what overran.

A `plot` sets one pixel of the declared `bitmap` to a palette entry. Coordinates
run from the top left, and a plot outside the surface is dropped rather than
wrapping onto another row. The surface's memory takes no single-byte write, so
each plot reads and rebuilds the halfword holding its pixel; two plots on the
same halfword therefore do not disturb each other.

A `blend` makes one surface translucent over whatever is drawn beneath it.
`surface` names `background`, `panel`, `middle`, `far`, `sprites` or `backdrop`
— `middle` and `far` being the two surfaces behind the document's own background,
which a turning background and the declared layers fill nearest first, and the
last two being every sprite and the colour behind everything — and `weight` is
the translucent surface's share in sixteenths, so 16 is opaque and 0 leaves only
what is below. Weights past 16 are held at 16 rather than wrapping. It is an
`agb` step: the Color machine has no blend unit, and its `fade` works by rebaking
palettes, which cannot mix two surfaces because a palette entry knows nothing
about what is drawn beneath it. A `blend` and a `fade` share one hardware
register, so the later step in a frame is the one that takes effect.

A `repeat` writes `index` with the iteration number and runs `body` `count`
times. The count is a literal in 1..255, never a variable, so the work a frame
can do stays bounded by inspection. A `break` leaves the innermost `repeat` and
is refused outside one; because it skips the increment, the index is left at the
iteration that broke, while a loop that finishes leaves it at `count`. A step
carrying a field belonging to another kind is refused rather than ignored.

Each frame samples input, evaluates rules in array order, then updates
presentation. All conditions in `when` must match; an empty array always
matches. Steps execute immediately in order, so later steps and rules see
earlier writes. There are no hidden states or game-type switches.

Conditions have either of these shapes:

```json
{"kind":"key","key":"a","mode":"pressed"}
{"kind":"compare","left":{"variable":"score"},"comparison":"ge","right":{"constant":10}}
```

Keys are `a`, `b`, `start`, `select`, `up`, `down`, `left`, `right`. Modes are
`held`, `pressed`, `released`. Comparisons are unsigned `eq`, `ne`, `lt`, `le`,
`gt`, `ge`. An action is
`{"target":{"variable":"score"},"operation":"add","value":{"constant":1}}`.
Operations are `set`, `add`, `subtract`, `and`, `or`, `xor`, `mul`, `div`,
`mod`, `shl` and `shr`; arithmetic is unsigned and wraps modulo 256 on both
targets. A runtime zero divisor yields zero and a runtime shift of eight or more
yields zero, so no operand can trap; the literal forms of both are refused at
validation instead. A sprite with zero visibility, an invalid runtime
tile index, or a top-left position outside the native viewport is hidden.
The viewports are 160×144 and 240×160 pixels respectively.

The compiler enforces code/data capacity, and validation bounds per-frame work
with `CartridgeCost`. That model counts abstract work units — one unit is an
eleventh of a `set` step writing a literal to a variable — and compares the
total against a per-frame reservation. The weights are measured, not estimated:
`CartridgeCostMeasurement` boots documents on both real machines and reports the
largest per-frame iteration count each sustains at full frame rate, and cost per
iteration is inversely proportional to that. Each weight is the worse of the two
targets, so a document that fits also fits either target alone and flipping
`/target` cannot change whether it holds frame cadence.

A primitive with no measured weight prices as `Unmodeled` and the document is
refused, rather than admitted against an invented number. Adding a primitive
therefore means measuring it. To re-measure, raise the reservation so the
harness can probe past it, run
`PUCK_FORGE_MEASURE=1 dotnet test tests/Puck.AdvancedGamingBrick.Forge.Tests`,
fold the reported capacities into the weights, and restore the reservation. CGB exports are 32 KiB; AGB exports are
64 KiB. Source hashes identify canonical source; they are distinct from the
engine's hash of exported ROM bytes. `CartridgeCompilation.Variables` and `.Arrays` map source names to native
memory addresses for debugging and memory watches.

This version supports background maps written at run time, byte-state rules,
addressable byte arrays, sprites, cartridge audio and battery-backed state on both
targets. ROM banking, persistent variable saves, dynamic map
banked large games and visual editing are not yet part of this document schema. A blit compiles and runs on both targets but carries no
measured per-frame weight, so a document using one is refused until that cost is
characterized; build screens from the initial `map` and runtime `map` writes
instead. Existing Tune audio
documents retain their separate compiler. These boundaries are explicit so
an editor cannot silently discard unsupported authored data.

## Embed an editor or compiler

Reference `ByteTerrace.Puck.GamingBricks.Forge` and the desired target package.
Keep a `CartridgeDraft`, edit it, then call `Check()` and
`ICartridgeCompiler.Compile(document)`. Both compilers are stateless; input data
alone determines emitted bytes. The caller owns filesystem I/O and machine
lifetime. No World service registration, process launch, BIOS download or
external compiler is required.

`LabelTable` is the shared machine-code label bookkeeping — allocate an id,
bind it to a byte offset, resolve it at fixup time, and refuse a branch naming
a label nothing bound. Both `Sm83Emitter` and `ThumbEmitter` hold one; the
fixup lists and patch encodings stay per-instruction-set.

AGB output uses direct boot without BIOS calls. `forge.play` explicitly selects
the engine's `stub` option. A retail BIOS/hardware boot needs a valid supplied
logo; the lower-level AGB cartridge builder accepts it, but this source version
targets direct boot. CGB may use the emulator's seeded post-boot state. Neither
document compiler bundles a BIOS.

Run the native compiler/editor and emitter tests with:

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Forge.Tests -c Release
dotnet test tests/Puck.HumbleGamingBrick.Forge.Tests -c Release
```
