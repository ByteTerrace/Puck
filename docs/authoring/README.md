# Authoring content

Choose the kind of content you want to change. Puck's authoring tools produce
ordinary documents that the runtime validates, so editing through a tool does
not bypass the document's rules.

| Task | Guide and example |
|---|---|
| Author a world in the Puck DSL | [World vocabulary](../../src/Puck.World.Transpiler/README.md) and its [generated construct table](../reference/world-vocabulary.md), with [language syntax](../../src/Puck.Transpiler/README.md) for expressions, templates and collections. |
| Test a world's behaviour in its own language | [Testing a world](testing-a-world.md) — `test { given when expect }` and `puck test`. |
| Inspect or edit a running world | [World console](../../src/Puck.World/README.md), including document mutations, reload and saved state. |
| Understand document fields | [World schema](../../src/Puck.World.Schema/README.md). |
| Write one shader or a multi-pass effect | [Live shader workflow](../../src/Puck.World/README.md#shader-pipelines), then the [pipeline contract](../../src/Puck.Shaders/README.md#shader-pipelines-and-live-development). |
| Build a cartridge | [Cartridge DSL](../../src/Puck.GamingBricks.Transpiler/README.md) and [forge workflow](../../src/Puck.GamingBricks.Forge/README.md). |
| Author shapes or audio | [World authoring library](../../src/Puck.World.Authoring/README.md) and [Example documents](../examples/README.md). |

## Source, document and running state

The DSL is an authoring language that lowers to JSON. Templates and generated
collections make the source easier to maintain; the runtime receives the
resulting document. Decompilation can recover an equivalent document source,
but cannot reconstruct the author's original templates or comments.

The document's **vocabulary** defines what its fields mean. A world and a
cartridge share language syntax while having different validation rules.
Compilation checks the source; vocabulary validation checks whether the
resulting content is admitted. Native cartridge compilation and shader
compilation add their own target requirements.

## Pinning file assets

Use `asset "path"` when a world value names bytes that live beside the source,
for example a machine cartridge:

```puck
machines [
  {
    name: "cabinet"
    engine: "gaming-brick"
    configuration {
      schema: "puck.gaming-brick.configuration.v1"
      model: "cgb"
      content { path: asset "content/game.gb" }
    }
    running: false
  }
]
```

The path uses forward slashes and resolves from the file that wrote it. An
imported module therefore owns paths relative to that module, even when a root
source instantiates it. Compilation writes the equivalent path relative to the
root source so the emitted world can still find the same bytes.

The root source has one sibling `<stem>.assets.json` lock. It records the full
SHA-256 digest of every `asset` reference. An ordinary compile refuses a missing
lock entry, changed bytes, an unreadable file, or a path or asset set over the
language's bounds. It never accepts a byte change on its own.

Refresh the complete lock only when the asset change is intentional:

```powershell
puck compile path/to/world.puck --validate --update-assets
```

The refresh happens only after compilation and validation succeed. It replaces
the lock's asset set, so pins no longer referenced by the source are removed.
Sources with asset references currently compile beside their source directory;
`--output` may choose another filename there, but not another directory, because
the compiled paths remain relative to that location. Each output and the lock
is replaced atomically as its own file. Publication spans several files, so an
I/O failure partway through cannot make the whole set atomic.

## Composing neighbouring worlds

One `.puck` source can emit several sibling documents and connect them without repeating topology rows:

```puck
module patch(origin: Point) {
  ground floor { center: origin  size [12m, 12m] }
  spawn arrival { at: origin + [0, 0, 4m]  yaw: 180deg }
}

world west = patch(origin: [0m, 0m, 0m])
world east = patch(origin: [12m, 0m, 0m])
border west.east, east.west { height: 6m  hysteresis: 2m }
```

Each `world` becomes `<name>.world.json`. A `border` writes reciprocal references, persisted global destinations,
and adjacency rows. A bare cardinal endpoint selects the side of the world's only ground; with several grounds,
write the ground name too, such as `west.floor.east`. The selected edges must have equal size and meet at the same
composition coordinate. `width` may narrow both edges around their centres; it cannot exceed either edge.

A floor or ceiling boundary writes its frame instead of deriving it from ground geometry:

```puck
border island.under, cavern.sky {
  center [0m, 80m, 0m]  yaw: 0deg  pitch: -90deg
  width: 90m  height: 90m  hysteresis: 2m
}
```

The reciprocal frame reverses yaw and pitch. `hysteresis` is a minimum ownership deadband in world units; the
runtime uses the larger of this value and its collider/motion-derived safety threshold, and reciprocal rows must
agree.

`door island.arch1, parlor.arrival` connects an authored placement face (`arch1/portal` by default) to the named
spawn and creates a return copy of that arch in the destination. Travel maps through the two face frames in both
directions, so the spawn pose locates and turns the return arch. The source arch must have a self-contained world
transform, prototype, and face source; a parented arch or a face that depends on another camera or machine is
refused rather than copied incompletely.

Keep a runnable example small while learning. Make one change, validate it,
then inspect the result in the actual host. A successful source compilation
alone does not prove that a shader renders correctly or that a cartridge plays
correctly. The linked guides provide the appropriate run and verification steps.

For upcoming authoring features and release work, see the
[plans](../plans/README.md). Current command syntax belongs in the project
guides above, so it has one place to stay synchronized with the implementation.
