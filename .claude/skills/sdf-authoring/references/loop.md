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

`world.load <path> [force]`, `world.reload`, and a bare `world.save` are all
JSON-only, and none of them know `.puck` exists — only the process's own boot
(`--world <file>.puck`, once, via `PuckWorldLoader`) transpiles it. This is
today's behavior, not a design choice a workaround can fix:

- `world.load <path>` and `world.reload` both read through
  `WorldDefinitionFileSource.TryLoad`, the plain JSON loader, with no
  `.puck` special case. Pointed at a `.puck` file (explicitly for
  `world.load`, or implicitly for `world.reload`'s re-read of the running
  world's own origin) the read fails to parse
  (`is not a valid puck.world.def.v1 document`) instead of picking up the
  source.
- `world.save` with **no path argument** writes canonical JSON back over the
  running world's origin path. Against a `.puck`-sourced world this
  overwrites the `.puck` file with JSON, discarding every `let`, `template`,
  `for`, comment, and shape/palette sugar spelling in it.

Against a world booted `--world <file>.puck`: `world.row.set`/`world.row`
mutations are fine (they act on the in-memory document, not the file). To
persist one, `world.save <explicit-path>.world.json` — never bare
`world.save`, `world.reload`, or `world.load` naming the `.puck` path itself —
then hand-port the change back into the `.puck` source (or decompile a
throwaway copy to diff against; decompiling is one-way, see below). Restart
with `--world <file>.puck` to pick up an edited `.puck` source; there is no
live re-transpile.

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

`<path>` may be a `.world.json` file or a `.puck` source — `PuckWorldLoader`
transpiles a `.puck` path in memory at this one boot, transparently to
everything below it. There is no separate compile step for a quick look; run
`puck compile <file>.puck --validate` first only when you want the named
diagnostics before spending a boot.

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

A `.puck` source compiles and validates first — `creation stats` and
`schema --check` both take JSON, never `.puck`, directly:

```bash
puck compile <file>.puck --output <file>.world.json --validate
puck lint <file>.puck --strict
```

`compile --validate` runs document-level validation: the stamp-budget count
and the panel/trim/cells creation-scope refusal both fire here (they walk
every `prototypes[]` row unconditionally, whether or not a placement uses
it). It does **not** run emission or contact construction — `Morph` and
similar PopField-only blends validate here and only fail at the next step;
a Polygon/Ellipse prism profile under a `solid` placement, and a residual
nonuniform `Scale`, likewise surface only there.

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

## Authoring in C# instead of JSON — a live but unused path

`src/Puck.World.Authoring/Sculpting/` carries a builder for programmatic
sculpting. It is real infrastructure — `CreationBuilder`'s topological
invariants (parents declared before children, ids unique, `Mirror` always
emitting `Left` before `Right`) are enforced and covered by
`tests/Puck.World.Tests/CreationBuilderLawTests.cs`, and `puck creation
sculpts`/`sculpt <name>` and the live `creation.sculpts`/`creation.sculpt`
console twins are wired and working. But **no shipped creation is authored
through it today**: `CreationSculptRegistry` ships with zero sculpts
registered (a sculpt is registered by a composition root or a test, and
`puck creation sculpts` prints `none registered` until one is), and nothing
in the tree calls `new CreationBuilder(...)` outside that law-test suite.
`moth.puck`'s 230 hand-authored `shape` statements are `.puck` DSL source,
not `CreationBuilder` output.

Prefer `.puck` for hand-authoring a character, armor, or prop — it is the
checked-in convention every shipped creation follows, and the loop above
already covers it. Reach for `CreationBuilder` only for programmatic or
parametric generation (mirrored rigs from one authoring expression, a
patch applied by a tool or test) where the DSL's `for`/lambda builtins
would not fit, and register the result through `ICreationSculpt` so
`puck creation sculpt` can apply and validate it.

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

1. For a `.puck` source, `puck compile --validate` first — it catches
   structure, budget, and scope refusals before spending a `creation stats`
   or a boot on them.
2. `creation stats` — distinguish whole-document validation and budget from
   the separate static/pooled emission inspection; inspect clamp diagnostics.
3. Run the game and capture. A claim about how something reads is not verified
   until you have looked at the image.
4. If the placement carries a `solid` row, distinguish named refusals from
   accepted facets omitted by contact emission. Check contact explicitly;
   successful validation alone does not prove the picture matches collision.
