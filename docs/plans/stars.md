# Stars

The owner's request: the sun is its own module that several worlds share,
binary systems are possible, and it is "a proper sun. A player might even be
silly enough to mistakenly open a portal near the sun or fly their ship to
it." A star is a place and a body in Puck's world model, seen as one thing
from a valley floor, from space, through a portal and from beside it.

This page records what was settled, what is built in the debug world, and
what is left. The mechanisms it rests on are
[influence between worlds](../decisions/worlds-and-federation.md#influence-between-worlds-is-a-delayed-value-and-a-tide)
and [reading and acting across worlds](../decisions/worlds-and-federation.md#reading-and-acting-across-worlds).
[sun.md](../../worlds/debug/sun.md) says how to visit the star.

## What was settled

Each row gives the options weighed, the one taken and why.

| Question | Options | Taken |
|---|---|---|
| What a star is | A sky object each planet authors; a body inside one system world; a world of its own | A world of its own, in which the star is a body. It owns what only it can know (whether it shines, its mass), which other worlds follow as delayed rows. Every world of the system authors its own view of the star from one shared description (`star` in `worlds/standard/stars.puck`), so no world replicates the star. |
| Who holds the orbit clock | Each world counts the engine tick; the star's world owns every clock; each clock has one owner and the rest follow | One owner per clock, followed. A day is its planet's clock, so the debug world owns `day` and the moon and the sun follow it. A pair's mutual orbit is the system's, so the star's world owns that row and planets follow it. A followed row is the only thing sent between worlds. |
| How a star's place reaches a world | Replicate a position; key directions by hand per world; a closed-form orbit each world evaluates from the clock it adopted | The closed form. A star rides conics (`FixedConicOrbit`): on the authority a `gravity.orbits` row turned by a state row (`WorldGravityOrbit.PhaseBy`), and on the presentation the same elements on the light (`WorldRenderLightOrbit`), read on the timeline clock over that row. A projection carries render and clocks and no gravity, so the light carries its own orbit. |
| One star at every distance | Three hand-crossfaded representations; a per-pixel sphere light; a directional light derived from where the body and the viewer stand, with a disc of the body's angular size and the body's geometry in front of it | The derived directional (`WorldRenderLightSource`). Direction, inverse-square illuminance and penumbra follow the viewer continuously, so there is no transition to tune. The disc is as wide as the body looks, so it is the body's own outline where the body is past the draw distance, and the geometry covers it exactly where it is not. |
| Scale | Real distances in one frame; scaled distances in one frame; a frame per world joined by seams and portals | A frame per world. `FixedQ4816` holds astronomical distances, but presentation narrows to float and the draw distance is capped (`WorldRenderDefaults.MaxFarDistance`), so each world stays near its origin and a crossing changes coordinates, as the moon's does. The debug system is small by authoring: its star is 15 km from the valley. |
| Danger | Engine rules for heat and radiation; authored rules and fields | Authored: `starHeat` is rules over `bodyPosition`, `starPull` a gravity area on the star's body. A portal beside a star needs nothing new: the painting's screen emission lights the near side, and the far world's exposure is its own. |
| Binaries | A second light keyed by hand; a source that rides several orbits | Several orbits. A source rides its orbits in order, each about the point the ones before reach (`WorldRenderLightSource.Orbits`): the pair's turn about the world, then the star's own about the pair's centre (`pairTurn`). Two stars are two lights and two discs; the renderer shadows two directionals (the primary and secondary shadow roles) and lights by any number. |
| The day cycle | Keep `dayCycle` as its own keyed sun; make it a preset over the star | A preset. `dayCycle` authors a `star` on a `dayTurn` and keys only what is a look (the horizon's dimming and colour). |

**Invariant.** A star stands where its clocks put it on its orbits, and
nothing else. On the authority that is `WorldOrbitField`, which holds no
state between steps; on the presentation it is
`WorldEnvironmentResolve`'s source step, which reads the clock's presented
phase every frame. The two read the same row, so they agree to the row's
resolution. The guarantee stops at the viewer: the light is derived once per
world a frame from the frame's first view (as a view clock is), so a second
view far from the first is lit from the first's direction.

**Attacks it closes.** A world cannot be told where the star is by another
world: it computes the place from a clock, and a hostile clock owner can only
turn the star, which the follower's rules may clamp. The star's world cannot
reach into another world's bodies; its light and mass arrive as followed rows
those worlds scale and bound.

**Rulings it rests on.** Cross-world interaction is a flagship feature; authors
rule (every size, colour, strength and danger is `.puck`); Puck code over
engine code (heat is rules); brute force is a defect where a closed form
answers.

## What is built

- **Engine.** A directional light's `source` and its orbits; a sun disc's
  `follows` and its derived radius; a gravity orbit's `phaseBy`; the
  projection carries a source's clocks.
- **Standard.** `worlds/standard/stars.puck`: `sunlight`, `star`, `dayTurn`,
  `pairTurn`, `starLight`, `starDisc`, `starRail`, `starTide`, `starPull`,
  `starSurface`, `starHeat`. `dayCycle` is built on it.
- **The debug system.** One star (`moon-frame.puck`'s `sunStar`) lights the
  valley (`day.puck`), the moon (`moon.puck`) and its own world (`sun.puck`).
  The gallery hall authors no sun. The worlds that stand alone (garden,
  hearth, night, galaxy, the Moth basis) take `sunlight`; the city's sun is
  its own day cycle's.
- **The proving place.** Perihelion, a rock in `sun.puck` the star passes once
  a day, reached through the `PERIHELION` painting in the painting wing.

## What is left

In the order it matters. Each names the check that closes it.

1. **A ship flies to the star.** The moon's world ends at its own reach; a
   seam above it into the sun's world carries the ship on, as the sky seam
   carries it to the moon. *Check:* `world.benchmark run` of a route that
   climbs from the launch field through both seams and is burned at the star.
2. **A second star in the debug sky.** A companion on a `pairTurn`, with the
   pair's row owned by `sun.puck` and followed by the valley and the moon.
   *Check:* a capture of two discs and two shadow directions in the valley,
   and an eclipse where one star's disc crosses the other's.
3. **A clock row's phase at full resolution.** `phaseBy` reads a `Fixed`
   row's sixteen fractional bits, so a body on a 15 km rail moves in steps of
   about 1.4 m. The exact phase is the row's base and epoch under its advance
   (`StateAdvance`), which the orbit can read once the reader exposes them.
   *Check:* the star's speed between ticks is constant in `body.where`.
4. **A body that does not shadow its own light.** A star's ball stands on the
   line from every surface toward its light, and it shadowed everything on
   the outpost at every distance tried, 2.4 km and 14 km. No draw can opt out
   of casting, so the star's light casts no shadows in `sun.puck`
   (`shadow: never`). The fix is a draw that the cascades' selection leaves
   out, authored on the creation or the placement. *Check:* with
   `shadow: always` on the star's light, the pad is lit and the frame's posts
   shadow it.
5. **Scene light past the frame's range.** Within about 3 km of the star
   the lit rock and the star's surface show as one flat grey: a surface 520 m
   from the star takes 83 million lux, and both it and the star pass what the
   frame's scene light holds, so the exposure meter reads them alike. Past
   3 km the star is white against a correctly exposed pad. The frame needs a
   range a star fits in (light stored against the view's exposure, or a wider
   format). *Check:* a capture at the pass shows a lit grey pad under a white
   star.
6. **A moving seam.** A star-centred system, where planets orbit, needs an
   adjacency whose frame relation rides a clock; `WorldFrameIsometry` is
   fixed. The debug system avoids it by letting the star circle the planet.
7. **The planet from the star.** A sky view's draw distance is capped below
   the 15 km between Perihelion and the valley, so the outpost's sky shows no
   planet.
8. **The valley's sun dims with the switch.**
   [Cross-world, left open 1](cross-world.md#left-open): the valley follows
   `sunLight` and its key light does not read it.
