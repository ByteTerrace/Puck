# The lawn

The valley floor's ground cover, from the spawn (`debug.go stage`) and the
approach to the hall's entrance across the south lawn to the
[wind meadow](meadow.md). [lawn.puck](lawn.puck) places the standard's foliage
families ([foliage.puck](../standard/foliage.puck)) in the art direction's
treatment ([valley and gallery](../../docs/game/art/valley-and-gallery.md)):
grouped shapes in flat values, props clustered along paths and banks, and the
middle of the lawn left open. It is lane C1 of the
[composition plan](../../docs/plans/debug-world-composition.md#the-order).

## The ground and its paths

The floor (`floor`, placed by the Moth basis) is the standard's
`paintedGround`: the 740 m disc in one flat turf value carrying eight paths as
paints, quadratic Béziers in world X/Z (`lawnPaths`). A paint is colour drawn
at the hit, so the paths cost no draw, bake or contact and keep the sharp edge
the art direction asks of paths alone. They run from the spawn to the
entrance, north of the studio past the climate wing's reservation into the
meadow between its two western banks, south past the studio, and west to the
edge of the stream and pool corridor, which [C3](../../docs/plans/debug-world-composition.md#the-order)
carries on over its bridge.

## The families

Every member is computed in `.puck`: `lawnGroups` names where each group stands
(its centre, kind, count, radius and seed), and a golden-angle sequence places
its members with a seeded scale, yaw and variant. Flowers and tufts stand along
each path's edge on alternating sides, sampled along the curve, and a sample
whose flower would land on a neighbouring path is dropped.

| Kind | Template | Variants |
|---|---|---|
| Rounded oak | `broadleafTree` | `lawn-oak-0..2` |
| Flat spreading crown | `flatCrownTree` | `lawn-flat-0..2` |
| Tall column | `columnTree` | `lawn-column-0..2` |
| Weeping | `weepingTree` | `lawn-weeping-0..1` |
| Shrub | `scrubClump` | `lawn-scrub-0..2` (two to four crowns) |
| Boulder | `mossyRock` in `canyonRock` and a peach set | `lawn-boulder-0..1` |
| Flowers | `flowerCluster` | `lawn-flowers-0` yellow, `-1` white |
| Grass | `grassTuft`; a four-tile `windMeadowTile` patch beside the spawn | `lawn-tuft`, `lawn-grass-0..3` |
| Turf masses | `sceneryPatch` | `lawn-patch-0..9`, shade under the groves and lighter sweeps |

Each tree family is a `groupedTree`: a trunk, roots and branches in bark and
overlapping `Superellipsoid` crowns in three flat leaf values, under a faint
2.5 cm relief that keeps each creation in one field scope (a solid tree
without it broke the chase camera's clearance across the valley). Every family
takes `height`, `spread`, `lean` (radians, tipping everything
above the roots) and `leaves`, so the floor mixes species and no two trees of one
species read as a clone. To change the floor's mix, change a group's `kind` in
`lawnGroups`; to change a species everywhere, change its variant rows in the
`prototypes` block.

The wind-grass patch shares the meadow's gust phase through the placed world
X/Z and its three wind cells, which `meadow.puck` exports for it.

## Kept clear

The hall (x 10 to 70, z 12 to 40), the studio (x 24 to 72, z -76 to -20), the
stream and pool corridor west of x -38, the camera course, the combat yard and
the climate wing hold nothing from the lawn.

## Cost

The lawn's bake, draw, frame and memory measurements before and after it
landed are in the composition plan's
[C1 as built](../../docs/plans/debug-world-composition.md#c1-as-built). Each
static row bakes on its own, so the tufts and flowers are most of the added
bakes; the wind-grass patch beside the spawn adds 748 dynamic parts and their
draws, the forcing load for one draw per shared geometry. `lawnGrassTiles = []`
removes it.

[Debug world](debug.md) · [Wind meadow](meadow.md)
