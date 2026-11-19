# The valley

The caldera the debug world's floor lies in: eight wall sectors round a clear
floor, an overlook on the south-west face, the falls and lake on the north-west
and the glacier above them ([valley.puck](valley.puck), its macro form in
[valley-form.puck](valley-form.puck)). The plan it follows is
[the valley and the moons](../../docs/plans/valley-and-moons.md), and the
walls' engine work is [the terrain pass](../../docs/plans/terrain-pass.md).

## The walls' look

The walls are Study B's warm stratified canyon
([valley and gallery](../../docs/game/art/valley-and-gallery.md), the
[Canyon workshop pack](../../docs/game/art/canyon-workshop-pack/README.md)'s
valley overview): terracotta and peach faces built from large planes, darker
strata every few storeys, pale sunlit ridges, dark gullies, and turf on every
ledge and top flat enough to hold it. Read from the floor, the walls are warm
mid-values against the green floor; from the rim, the far walls are lighter and
cooler than the near ones, and the strata still read as horizontal lines.

The treatment is the standard's terrain program
([terrain.puck](../standard/terrain.puck), `terrainMaterial`), laid on every
sector's rock slot as `valleyRockBands`: a face that deepens toward the foot, a
dark and a pale stratum repeating every 23 m of altitude, and turf by slope, all
broken by the same noise so the strata wander and no edge runs ruler-straight
round the ring. The cel treatment composes with it: the terrain program writes
the surface, cel the light response.

## Outside references

Real places, for what the rock does:

- Zion National Park's Navajo Sandstone walls (US National Park Service, Zion
  geology pages): cross-bedded red and cream sandstone in tall planes, with
  vegetation holding to every ledge.
- The Grand Canyon's exposed strata (US National Park Service, Grand Canyon
  geology pages): horizontal bands of differing hardness, the hard ones standing
  out as ledges and the soft ones as slopes between them.
- Canyon de Chelly (US National Park Service): sheer red walls whose faces
  darken in vertical streaks where water runs.

Stylized games, for the quality the bar asks:

- The Legend of Zelda: Breath of the Wild (Nintendo, 2017), Gerudo Canyon:
  banded red cliffs painted as a few flat values that read from kilometres away.
- Sable (Shedworks, 2021): canyon strata drawn as flat colour bands under a
  hard light, with no surface noise.

The traits to hit, in order: the walls read as rock and as warm, at first
sight; strata read as horizontal and continuous across a sector; ledges carry
green; ridges catch light and gullies hold shadow; nothing shows a triangle's
facet or a grain finer than the treatment's brush.

## What still limits them

- The relief bakes at a coarse cell (the terrain pass's
  [what set a sector's cell](../../docs/plans/terrain-pass.md#what-set-a-sectors-cell)),
  so curvature, which the program reads from the baked normals, changes per
  triangle: a ridge or gully band scattered into flecks of the 2 to 4 m
  triangles, so the walls read no curvature until the source tiles lift the
  cell. Close in, the wall's foot shows the same triangles as flat facets.
- The walls are smooth superellipsoid domes under their relief, where Study B
  has vertical cliff planes and stepped ledges; the bands follow altitude, so
  they read as strata on any form, but the form is the terrain pass's relief
  and sets.
- Nothing past the shadow reach is shadowed (the terrain pass's far shadows),
  so the far walls' gullies hold no cast shadow.
