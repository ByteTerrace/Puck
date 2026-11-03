# Forest pressure patch

[forest.puck](forest.puck) is the first connected canopy portion of the
[dense-landscape milestone](../../docs/plans/dense-landscapes.md). It uses the
standard's existing tree, shrub, tuft and authored wind templates.

Use `debug.go forest` for the entrance and `debug.go forest-inside` farther along
the road. Paving and vegetation exclusions share [paths.puck](paths.puck).
The standard's [path masks](../standard/paths.puck) use control/segment bounds,
segment distance and a quadratic chord-error allowance, without sampled-point gaps.

The source varies area, spacing and candidate populations with fixed prototype
families. The wider belt, density sweep, shared static copies, GPU instance
buckets, nested-view route and target-device qualification remain in the milestone.

`world.sdf.rig` lists registration indices; `world.sdf.rig <index> [shape]` reads
driver phases, weights and continuation. Pause both exported meadow cadence cells
with `world.state.cell.set`, let the pose settle, restore them and compare several
readbacks and presented grass-only frames. The wind bindings are shared with the
meadow/lawn. Temporal and lighting noise can change pixels while phases are held;
read phases and work beside image differences.
