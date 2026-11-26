# Gallery kit: real-world studies

The gallery's kit (`worlds/standard/architecture.puck`) is modelled from the
selected [canyon workshop direction](canyon-workshop-pack/README.md) and the
gallery kit pack (`gallery-kit-pack/`, being drawn). The paintings fix the look but
not the measurements. This page records the real buildings and practice that
give the kit believable sizes and behaviour, and what each one decides. Each
fact is cited to the page it came from. Where sources disagree, both figures
are given.

## Structure

**Branched columns.** The teal branched pier is a known structural type. Stuttgart
Airport's Terminal 3 carries its roof on 18 steel tree columns, which give it a
light, open interior ([SBP](https://www.sbp.de/en/project/airport-stuttgart-terminal-3/)).
Its typical trunk is about 2.6 m tall (13 m along the landside façade), and the
branches spread up to 32 m ([Modern Steel Construction](https://www.aisc.org/globalassets/modern-steel/archives/2000/08/2000v08_airport_roof.pdf)).
Jewel Changi's glass gridshell rests on an inner ring of 14 tubular-steel tree
columns ([Architectural Record](https://architecturalrecord.com/articles/14153-jewel-changi-airport-by-safdie-architects)).
What it decides: a short trunk that branches low reads as structure, not
ornament. `branchedPier` therefore splits about halfway up its 4.2 m storey and
spans 4 m between piers. The paintings' piers are stockier than the steel
originals, as the cartoon style asks.

**Moving sunshades.** The orange fan shades on dark hubs have a real model in
the Milwaukee Art Museum's Burke Brise Soleil. It is a moveable sunscreen with a
66 m (217 ft) wingspan and 72 steel fins from 8 to 32 m long
([MAM](https://mam.org/visit/burke-brise-soleil/); one article gives 36 fins in
all ([Inhabitat](https://inhabitat.com/amazing-calatrava-shade-pavilion-for-the-milwaukee-art-museum/2/))).
It takes 3.5 minutes to open or close ([Google Arts & Culture](https://artsandculture.google.com/story/bwWhaCk9A4WRJg))
and closes itself when the wind reaches 23 mph ([MAM](https://mam.org/visit/burke-brise-soleil/)).
What it decides: the gallery's fan shades are moving parts driven by world state
(the day's sun, a wind cell), not static props. That drives the engine's
authored motion and its rebake cost for real, as AGENTS.md asks of polish.

**Planted terraces around a void.** Jewel Changi stacks four levels of
landscaped terraces around a central garden, in an amphitheatre section
([Dezeen](https://www.dezeen.com/2019/04/12/jewel-changi-airport-singapore-safdie-architects-waterfall/)),
with a 40 m indoor waterfall falling from an oculus
([Dezeen](https://www.dezeen.com/2019/03/12/moshe-safdie-worlds-tallest-indoor-waterfall-changi-airport/)).
What it decides: the atrium's balconies carry planters along their edges, as B's
planted terraces do outside. A water feature in the atrium is a candidate for a
place that loads the water system indoors.

## Human scale

**Plinth heights.** Display plinths come in sets of different heights, such as
30, 36 and 42 inches (0.76, 0.91 and 1.07 m), to give a room depth and contrast
([Displays2go](https://www.displays2go.com/P-52174/Hexagon-Museum-Plinth-Set-Scratch-Resistant-Rubber-Feet)).
A full display pedestal is about 91 cm tall ([Slimline Warehouse](https://slimlinewarehouse.com.au/ds14s36wt/museum-exhibit-cases)).
What it decides: the plinth's cap stands at 0.7 m, so a subject's centre sits
near 1.2 m, eye height for a 1.75 m visitor. Varying the cap height between rows
is a lever for depth in a long hall.

**Guarding.** A barrier in a public building is at least 1,100 mm to the top of
its handrail; 900 mm applies only inside a single-family dwelling
([Balconette, citing BS 6180 and Approved Document K](https://balconette.co.uk/glass-balustrade/articles/glass-balustrade-and-juliet-height-regulations);
[NBS Source](https://source.thenbs.com/product/asf-pro-channel-fixed-glass-balustrade/fViPQXuwR7RiLH1ZxreHTQ)).
These are product and guidance pages, not the code itself. What it decides: the
balustrade's brass handrail stands 1.1 m above the slab. The Moth is 2.06 m
suited ([the pack](canyon-workshop-pack/README.md#modeller-handoff)), so the rail
reaches about her hip.

## Painterly treatment

Practitioners building stylised, painterly environments describe the same order
of work. Block out, then check value and contrast in greyscale, then set the
palette ([80.lv, mountain cabin](https://80.lv/articles/building-a-stylized-mountain-cabin-with-modular-assets-and-custom-shaders/)).
Tint occluded areas toward a deep cool colour so the shadows read as painted
([same](https://80.lv/articles/building-a-stylized-mountain-cabin-with-modular-assets-and-custom-shaders/)).
Judge the scene as one illustration rather than polishing assets one by one
([80.lv, impressionist scene](https://80.lv/articles/how-to-build-a-stylized-and-painterly-scene-inspired-by-impressionist-art)).
Elsinore painted each interior in a light version and a shadow version, and let
the day's directional light choose between them, so the painted look held
through the day ([Game Developer](https://gamedeveloper.com/programming/walking-in-a-painting-interior-daytime-lighting-in-elsinore)).

What it decides for the gallery:

- A value check comes before colour. A capture of the hall in greyscale (the
  bloom and tone settled, the day pinned) must already separate the floor,
  walls, ceiling, structure and exhibits before any palette tuning.
- Shadows are coloured, not black. The cel style's `shadowTint` (in
  `worlds/standard/styles.puck`) carries the teal toward which the gallery's
  shadows lean, as B's undercrofts do.
- The room is judged whole, against the eye-level hall in the kit pack, never
  one piece at a time.
