# Wind meadow

`debug.go meadow` stands the Moth in a patch of 1,994 two-part blades, centered
at `(119.5, 0, -55.3)`. It is east of the hero studio, about 72 m from the hero
spawn. The studio ends at x = 72; the grove's westernmost bank begins at x = 75.
The gallery, camera course and rate range remain clear.

[meadow.puck](meadow.puck) places sixteen independently bounded animated tiles,
six broadleaf trees, seven mossy rocks, five banks, eight sand caps and thirteen
clusters of six grass tufts. A low lawn cap meets the existing debug floor;
the port does not replace that floor or import the courtyard's pose stations.

The meadow is the densest patch of the floor's ground cover; [the lawn](lawn.md)
carries its families west across the south lawn to the spawn, imports this
module, and shares its wind cells, which this module exports.

The reusable designs live in [foliage.puck](../standard/foliage.puck), beside
the standard terrain library. `broadleafTree`, `mossyRock`, `grassTuft`,
`sceneryPatch`, `sceneryScatter` and `windMeadowTile` emit prototypes or placements
only where an importing author expands them. The debug module owns its position
tables. Banks, sand, moss, roots and crowns use `landscapeExponent = 2.05`.
Relieved solid scenery uses the engine's existing deterministic field contact.

Each blade has a planted stem and a tip parented to it. Gust phase reads the
placed world X/Z, so it crosses tile boundaries continuously. Tips add their
own flutter. The fringe, varied heights, orientations and boulder clearing
come from the courtyard source. The three live state cells control presentation:

```text
world.state.cell.set meadowWindSpeed $value 1.15
world.state.cell.set meadowFlutterSpeed $value 4.3
world.state.cell.set meadowWindPose $value 0
```

Set both speeds to zero to hold their current phases. `meadowWindPose` adds
a shared bend. Reload before a comparison requiring identical starting phases.
For a low inspection view:

```text
view.override camera look 124 2 -60 119.5 0.3 -55.3 55
```

`view.override camera auto` returns to the Moth's chase camera.

[meadow-tests.puck](meadow-tests.puck) reproduces a walk over the lawn and
support on a relieved bank through the real debug world. The
[job-system packet](../../docs/plans/open-items.md#engine-job-system) owns
the remaining cost of packing animated placements every frame.
