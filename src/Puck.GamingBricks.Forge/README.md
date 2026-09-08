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
forge.set /rules/- {"name":"move","when":[{"kind":"key","key":"right","mode":"held"}],"actions":[{"variable":"x","operation":"add","value":{"constant":1}}]}
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
| `palette` | 4 CGB or 16 AGB RGB555 integers, 0–32767. Shared by background and sprites. |
| `tiles` | 1–256 named 8×8 tiles. Each `pixels` array has eight strings of eight hexadecimal palette indices. Sprite color zero is transparent. |
| `map` | Exactly 1024 tile indices, row-major over a 32×32 background. |
| `variables` | Up to 64 `{name,initial}` unsigned bytes. |
| `rules` | Up to 64 `{name,when,actions}` rules; at most 8 conditions and 16 actions per rule. |
| `sprites` | Up to 40 `{name,tile,x,y,visible}` 8×8 sprites. All four value fields accept constants or variables. |
| `scrollX`, `scrollY` | Constant/variable background offsets in pixels. |

Names are unique within each collection, case-sensitive, 1–64 ASCII letters,
digits, underscores or hyphens. A value is exactly `{"constant":42}` or
`{"variable":"score"}`. Constants and initial values are in 0–255.

Each frame samples input, evaluates rules in array order, then updates
presentation. All conditions in `when` must match; an empty array always
matches. Actions execute immediately in order, so later actions and rules see
earlier writes. There are no hidden states or game-type switches.

Conditions have either of these shapes:

```json
{"kind":"key","key":"a","mode":"pressed"}
{"kind":"compare","left":{"variable":"score"},"comparison":"ge","right":{"constant":10}}
```

Keys are `a`, `b`, `start`, `select`, `up`, `down`, `left`, `right`. Modes are
`held`, `pressed`, `released`. Comparisons are unsigned `eq`, `ne`, `lt`, `le`,
`gt`, `ge`. An action is `{"variable":"score","operation":"add","value":{"constant":1}}`.
Operations are `set`, `add`, `subtract`, `and`, `or`, `xor`; arithmetic wraps
modulo 256 on both targets. A sprite with zero visibility, an invalid runtime
tile index, or a top-left position outside the native viewport is hidden.
The viewports are 160×144 and 240×160 pixels respectively.

The compiler enforces code/data capacity, and validation conservatively bounds
per-frame work across both targets. CGB exports are 32 KiB; AGB exports are
64 KiB. Source hashes identify canonical source; they are distinct from the
engine's hash of exported ROM bytes. `CartridgeCompilation.Variables` maps
source names to native memory addresses for debugging and memory watches.

This version supports static tile maps, byte-state rules and sprites. Cartridge
sound, persistent variable saves, dynamic map writes, banked large games and
visual editing are not yet part of this document schema. Existing Tune audio
documents retain their separate compiler. These boundaries are explicit so
an editor cannot silently discard unsupported authored data.

## Embed an editor or compiler

Reference `ByteTerrace.Puck.GamingBricks.Forge` and the desired target package.
Keep a `CartridgeDraft`, edit it, then call `Check()` and
`ICartridgeCompiler.Compile(document)`. Both compilers are stateless; input data
alone determines emitted bytes. The caller owns filesystem I/O and machine
lifetime. No World service registration, process launch, BIOS download or
external compiler is required.

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
