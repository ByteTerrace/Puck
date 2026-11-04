# Southern woodland

[forest.puck](forest.puck) composes a continuous belt around the south valley, with a dense path-side core and three glades. It follows the [selected painterly valley treatment](../../docs/game/art/valley-and-gallery.md): grouped crown silhouettes, restrained greens, warm wood and sparse cream/gold flowers.

The composition has mature overlapping crowns, saplings along the irregular edge, patches of scrub and broad fern fronds, grass in lighter openings, warm fallen-leaf patches, mossy rocks and fallen wood. Glades provide breathing room; the bent paved route stays legible. Bulk trees reuse the lawn's prototype variants. Shared standard templates define the fern and log assets.

[forest-layout.puck](forest-layout.puck) owns the belt, glade and density masks. The canopy, understory, floor and clearing fragments use the existing seeded `scatterPoints` sampler. Each layer can be edited independently. Paving exclusions read the same [paths.puck](paths.puck) controls as the lawn.

Use `body.pose spawn:forest 0`, `spawn:forest-inside`, `spawn:forest-glade` and `spawn:forest-west` for visual inspection. `spawn:forest-overview` is elevated; pause simulation when inspecting it. Capture presented frames at the entrance, beneath the canopy and across a glade after every composition pass.

Most planting remains on the level valley floor. A bounded solid noise-relief patch in the eastern clearing proves geometric surface detail. Wider terrain banks and surface-bound planting are deferred while the new surface-placement validator's null-reference failure is resolved. The [landscape milestone](../../docs/plans/dense-landscapes.md) owns performance qualification; an attractive still does not establish the frame or memory budget.

The standard's `landscapeSurface` applies active static weathering: vertically
stretched bark grain, softened tonal dabs on matte foliage and broader ground variation.
The `noise`, `patternScale` and `softness` controls operate on colour/roughness;
the clearing's creation-level `noise` separately displaces its actual field. Its `floor: 1`
matters because static placements have no render lanes. The line mask makes random scratches; it is not the standing bark recipe.
[Surface authoring](../../.claude/skills/sdf-authoring/references/surface.md#static-landscape-surfaces)
explains the masks, frames and cost.

For repeatable composition captures, select `view.override layout three-quarter`
then `view.override camera forest-overview` or `forest-clearing`. The play layout's
seat slot deliberately keeps the player's camera under a named-camera override.
The design review used temporary exposure compensation 1.5; default exposure was
not changed. The bark comparison additionally pinned EV100 to 9 and disabled local
adaptation through `render.exposure.hold`.

The fixed `forest-bark`, `forest-foliage`, `forest-ground` and `forest-ground-low`
cameras live in [forest-surface-views.puck](forest-surface-views.puck). Select them
under `three-quarter`. The low view judges silhouette and relief; a material
close-up alone cannot prove either. `groundSurface` supplies both the floor and
relieved patch, keeping one material recipe and a common pattern phase.

`forest-ground-relief` uses the existing `sceneryPatch` with a shallow mass and
seeded two-octave displacement, sunk into the floor inside the tree-free glade.
Its solid has zero margin. The surrounding roots and flower drifts stay outside
this bounded proving patch. The native per-tick crossing followed the displaced
surface and returned to the original floor; broader tree grounding is still open.
