# The authoring loop

## Naming: `prototypes` on disk, `creations` on the console

A creation lives at `prototypes[].document` in the world file. The console row
door addresses those same rows as section `creations`, keyed by prototype id.
Both names are correct in their own place; neither works in the other's.

## Editing live

```
world.row.set creations <id> document.shapes[name=<shape>].<field> <json>
world.row.add creations <id> document.shapes[name=<shape>].swings <json> [after=<selector>]
world.row.remove creations <id> document.shapes <selector>
world.row.step creations.<id>.<field>
world.row     creations <id> document.shapes[name=<shape>]
```

Selectors are `[3]`, `[name=…]`, or `[id=…]`. A whole-row payload starts with
`{` or `[`; that is what tells the keyed and keyless forms apart. JSON `null`
clears a nullable field, and a `"state.row.key"` string keeps a binding.

Use `world.row.step <path> <delta>` for numeric scalars such as rounding,
exponent, smooth, or taper. It refuses vectors and nested objects; set the full
`[x,y,z]` for scale/position, or `[x,y,z,w]` quaternion for rotation through
`world.row.set`. A state-bound transform keeps its binding by editing the
underlying state row.

A shape's `parent` carries its animated rigid delta, not its static rest pose
or scale. Changing a parent's base scale does not reposition children; edit
their authored base poses too.

`world.row creations <id> document.shapes` lists the elements;
`world.row creations <id> hash` gives the row digest.

Mutation verbs return no synchronous echo — the verdict lands at the tick
boundary on stderr. A second edit to the same row inside one tick is refused:
`row '<identity>' already has an edit buffered this tick — fence with world.wait`.

`world.reload` re-reads the current world from disk, which is the external-editor
loop. `world.load <path> [force]` switches files and is refused while the journal
is dirty without `force`. `world.save` persists live edits.

## Looking

```
view.override camera <name>      # or: layout <name>, or auto / - to clear
world.screenshot <abs-path.png>
world.wait <ticks>
```

`world.screenshot` **arms** a capture. stdout carries
`[world.screenshot: pending <path> …]`; the file exists only once stderr carries
`[capture] … -> <path>` or `[debug] captured frame N -> <path>`. Fence with
`world.wait` and confirm that line before reading bytes. A second arm while one
is pending is refused by name. It is refused headless.

Two windowed captures are never byte-identical. Compare by changed-pixel count
against a threshold, never by bytes.

## Driving the game from a script

```bash
dotnet run --project src/Puck.World -c Release -- \
  --world <path> --exit-after-seconds N --state-dir <tmp> \
  < script.txt > out.log 2> err.log
```

Capture both streams: read-backs on stdout, refusals, mutation verdicts, and
capture confirmations on stderr. Blank lines and `#` lines are skipped.

A look-at-one-part script:

```
view.override layout <layout>
world.row.set creations moth document.shapes[name=torso].scale [0.17,0.30,0.13]
world.wait 4
world.row creations moth document.shapes[name=torso]
world.screenshot out/moth.png
world.wait 4
```

## Offline verbs

```bash
dotnet run --project src/Puck.Cli -- creation stats --world <path> [--prototype <id>]
dotnet run --project src/Puck.Cli -- creation sculpts
dotnet run --project src/Puck.Cli -- creation sculpt <name> --world <path>
dotnet run --project src/Puck.Cli -- schema --check
```

`creation stats` loads and validates the whole document, then prints
`[<id>] shapes: N, stamp budget: M/367`, histograms by primitive and blend,
counts by facet, and palette slot usage. It then emits text-free prototypes at
unit placement scale through the static path (when applicable) and pooled
rest-pose path, reporting global and scoped/shared clamps. Exit 1 covers
validation refusal **or emission inspection failure**; 2 is a usage error.
Text-bearing prototypes explicitly report that a resolved font atlas is needed
and defer clamp inspection to live `world.budget`.

`Morph`, `StairsUnion`, and `StairsSubtraction` are defined enum values:
raw document validation accepts them, but static shape emission rejects them
as PopField-only composition. Text-free stats catches that failure when the
selected path forwards the blend. Animated ungrouped shapes take pooled Union
defaults, so their unsupported authored blend can still appear in a histogram
with exit 0. Stats proves neither authored-blend preservation across paths,
live motion, nor rendered appearance.

`creation sculpt` applies a registered sculpt's patch to a file, validating
before writing and leaving the file untouched on refusal. The live twins are
`creation.sculpts` and `creation.sculpt <name>`, which patch the live document
all-or-nothing.

`schema --check` exits 1 on drift between the code and the generated section
schemas.

## Authoring in C# instead of JSON

`src/Puck.World.Authoring/Sculpting/` carries a builder for programmatic
sculpting. The registry ships empty — a sculpt is registered by a composition
root or a test, and `puck creation sculpts` prints `none registered` until one is.

| Type | Use |
|---|---|
| `CreationBuilder` | `Shape(...)`, `Chain(...)`, `Mirror(side => …)` with `Left`/`Right` sides that suffix names `L`/`R`, `Palette`/`PaletteSlot`, `Frame`, `SymmetryX`, `FixedId`, `Find`/`TryFind`, `TryValidateTopology`, `Build()`. Rotations must come from the interned helpers (`AxisAngleDegrees`, `Multiply`, `Identity`); a non-interned quaternion is refused. Auto ids start at 100 |
| `StateHoisting` | lifts literal rotations, scales, joints, and tuning values out into state rows so they become live-editable. Idempotent |
| `SculptPatch` | `UpsertRow`, `RemoveRow`, `SetMember`, `RemoveMember`, `Apply(JsonObject)` — JSON only, no schema dependency |
| `PatchPath` | dotted segments with one `[field=value]` selector each, e.g. `looks.rows[name=moth].motion.poses` |
| `ICreationSculpt` / `CreationSculptRegistry` | `Sculpt(SculptContext) → SculptPatch`; the context exposes the whole world as a `JsonObject` plus `TryGetCreationDocument(prototypeId)` |

The builder refuses a parent that is not yet declared:
`shape '<name>' names parent '<parent>', which is not yet declared — declare parents before children.`

## Verifying a change

1. `creation stats` — distinguish whole-document validation and budget from
   the separate static/pooled emission inspection; inspect clamp diagnostics.
2. Run the game and capture. A claim about how something reads is not verified
   until you have looked at the image.
3. If the placement carries a `solid` row, distinguish named refusals from
   accepted facets omitted by contact emission. Check contact explicitly;
   successful validation alone does not prove the picture matches collision.
