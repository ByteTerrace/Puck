# Caldera water

Six implementation references for composition lane C3, extending the selected
[B, Canyon workshop](../valley-and-gallery.md#selected-art-treatment).
The water is a visual stress target: transparent shallows, coloured depth,
broken reflections, foam, falling sheets, spray, wet stone and ice must work
together in the same painterly valley.

## Image guide

| Panel | Builder handoff |
|---|---|
| [1. Spawn, full glacier](01-spawn-full-glacier.png) | First-read hierarchy: grounded blue-white ice above the north-west rim, a thin lake edge, one bright falls ribbon, stream and bridge leading toward the foreground pool. The cream/ochre gallery and glass lift anchor the right; retain open yellow-green lawn. |
| [2. Rim glacier and lake](02-rim-glacier-lake.png) | Grounded ice tongue, cobalt crevasses, ivory sun planes, wet scalloped toe and exposed warm moraine. Melt channels visibly join the lake. A flat reflective lake accelerates only at the rock outlet sill. Clear near-bank stones, turquoise middle depth and teal deep water need distinct responses. |
| [3. Falls and river head](03-falls-river-head.png) | Continuous falling sheet interrupted by rock, transparent turquoise core and selective white edges. Spray belongs at impact; a dark plunge pocket, broken expanding foam rings and downstream streaks connect it to the river. Wetness hugs the fall line and splash zone. The guarded landing stays beside the water. |
| [4. River, bridge and pool](04-river-bridge-pool.png) | Current passes under a cream stone arch into a broad quieter pool. Keep deep swimming water, transparent shallow margins, submerged fish, reflected warm cliffs and sky, bank contacts and upstream riffles readable together. The dark bank throat introduces underground drainage. |
| [5. Reservoir gauge](05-reservoir-gauge.png) | A cream-shell well pavilion, teal structure, orange pivoted shades, guarded dry terrace and connected stairs disguise a level instrument as civic architecture. The cutaway explains the rock chamber, descending inflow, air space, submerged passage and orange float touching the actual water surface beside pale tick marks. |
| [6. Spawn, melt complete](06-spawn-melt-complete.png) | Same camera and landmarks as panel 1. Bare ice-scoured cradle, empty melt grooves, a lake retained at its outlet sill, dry fall face with a residual dark wet streak, no spray, and a higher settled river/pool within the banks. |

The [exact prompts](prompts.md) include the rejected gauge draft and its edit.
The [written audit](review.md) records every verdict. Each PNG contains one
panel; only kept images are included. The before/after pair shares its framing
and export dimensions. The optional dusk panel is omitted because selected B
specifies warm clear afternoon; study C's blue-hour treatment is a different
direction.

## Place and scale

The [composition map](../../../plans/debug-world-composition.md#the-map)
governs placement. Spawn remains at the origin, the gallery north/east of it,
the falls on the north-west wall (compass 315), and the stream east along that
wall's foot before turning south around x -55. The terminal pool is near
(-55, -10), west of spawn. The bridge carries the spawn-to-range route; preserve
its connected paths, clear arch and dry approaches as the river rises.

The south-west overlook stays on its existing face at 345 m, reached from the
pool-side route. It is outside these north-facing views. The little rail at
the lake and the plunge-pool landing are local water-inspection architecture,
not replacement overlook decks. Keep the lawn available for its other places.

Use the map's 300–430 m walls, roughly 400 m floor and approximately 18 m
four-storey gallery when blocking out. These perspective paintings establish
silhouettes and material hierarchy; derive measured scale and sight lines from
the authored world. At spawn, the lake should read as a narrow edge behind
its sill. The rim close-up supplies the surface and channels hidden from below.

## The lake, the glacier and the cradle

The sheet the rim view is built and judged against. Every proportion is the
authored world's own number (`worlds/debug/water-site.puck`,
`worlds/debug/glacier-site.puck`); the real references say what each trait must
look like, and panel 2 sets the palette and the composition.

References opened for it: the Wikipedia pages on
[crevasses](https://en.wikipedia.org/wiki/Crevasse) (transverse crevasses form
where a valley steepens and stretch across the flow; splaying crevasses run out
from the margins and are concave up-glacier),
[moraines](https://en.wikipedia.org/wiki/Moraine) (lateral moraines are
parallel ridges of debris along the sides of a valley glacier, steepest next to
the ice; terminal moraines follow the snout) and
[Peyto Lake](https://en.wikipedia.org/wiki/Peyto_Lake) (a turquoise proglacial
lake coloured by glacial rock flour), with panel 2 for the palette.

| Part | Reads as | Targets, from the world |
|---|---|---|
| Lake | A clear shallow rim over stones, turquoise at middle depth, teal at the centre, calm and flat, moving only at the outlet sill | Water fills the bowl's own ellipsoid to the level, so depth runs from nothing at the bank to 5 to 6 m at the centre; no water side stands bare. Cobbles gather in clusters, a third sunk in the floor, with larger warm boulders along the waterline; the sill is where the bowl's floor and the outlet's cross. |
| Glacier | One convex tongue of blue-white ice, widest in its basin and narrowing to a lobed toe, built of bevelled blocks bent into arcs concave up-glacier and merged into one body: snow lenses on every up-facing plane that thin to nothing at their edges, blue risers, cobalt slots with pale lips between the icefall's rows that splay up-glacier at the margins, a snowfield névé that climbs and feathers into the head wall over a bergschrund, grit along both margins and a moraine stripe down the flow, and a scalloped wet toe in the lake with a portal and a melt channel that churn the water where they meet it | About 100 m up a 30 degree ramp (the ridge's own mean slope), 36 m across in the basin and 22 m at the toe; the crest stands 1 to 6 m proud of the trough's lips, the margins come down onto the trough's floor; the toe's lobes stand with their feet under the water. It melts by shrinking toward its head, so the toe retreats up the valley as the ice thins. |
| Cradle | A scoured U-shaped trough in warm rock, with grooves down its floor, banked by lateral moraine on both lips | The trough is 36 m across with a rounded floor; moraine of heaped debris runs along both lips, a crest of big heaps (3 to 5 m high) and smaller heaps on its inner slope, all one grit speckled dark; bare at the end of the melt, when the floor and its grooves are what is left. |

Views the sheet must hold: from the valley floor looking up at the rim
(`look -230 345 230 -311 324.5 311`), from the lake's near bank at the toe, and
along the trough from above the head.

## The visible melt

| Stage | Visible change |
|---|---|
| Initial, panel 1 with panel 2 detail | Full ice mass feeds channels and lake; overflow feeds the falls. Reservoir has capacity, so the river drains below ground. |
| Filling over hours, panel 5 explains the interior | Ice retreats within the same rock cradle; more bare stone and empty grooves appear. The physical reservoir level and float rise together. Show shrinking ice volume as well as a changing toe, without adding new source mountains. |
| Reservoir filled | The below-ground sink stops taking net storage; the river rises within its existing banks and settles to its steady level. Preserve bridge clearance and the quiet pool margin. This transition is builder direction, not a separate generated time sample. |
| Three hours, panel 6 | Ice is fully gone. The retained lake reaches its outlet level without spilling. Falling sheets, impact spray and directional foam disappear; the old fall line is exposed rock. The river and reservoir remain at the requested settled level. |

This is the owner's authored time course, not a claim of a perpetual source
after the glacier has gone. The feature lane owns the finite water budget,
reservoir capacity, timing and terminal equilibrium; these images specify
how those states should read. Do not infer volume or flow rates from pixels.

## Gauge and material construction

Panel 5 is an explanatory geological cutaway. In the playable bank, retain
the rock enclosure and inset observation window; opening the entire chamber
as an exposed pit would change its meaning. The protected gauge reveals the
same reservoir, with air above and water below its level. The orange float
runs on a teal guide anchored to the wall, touching the surface throughout its
travel. Pale broad ticks remain readable without a numerical HUD. Place the
inspection window on an accessible dry bank route; reserve a clear view of its
whole useful range. Size the playable gauge and tick spacing for that approach.

The descending inlet communicates filling; the submerged passage communicates
hydraulic connection. Their exact routed section and elevations are authored
from the feature lane's flow model, with no disconnected decorative tank or
uphill gravity return. When storage fills, the inlet's net flow must diminish;
at the end it must not continue drawing down the settled river.

Keep ice and water distinct: matte ivory/cyan ice planes with dark crevasses,
translucent wet edges, and a horizontal water surface that reflects the warm
rock. Use large reflection fragments and depth colour rather than a flat cyan
fill. Concentrate foam at collisions and fast shallows, spray at impact, and
caustic-like light on visible submerged stones. Let the quiet pool preserve
those stones and reflected architecture through gentle ripples. Wet rock is a
coloured darker material region, never a black strip. Broad strata, grouped
foliage and selective sharp edges keep this sophistication within B's cartoon
language.

These are concept targets for the feature lane; no World implementation or
rendering behavior is claimed by the pack. Art review establishes visible
coherence. Running the authored water through its stages remains the feature
lane's work.

[Art and visual design](../README.md)
