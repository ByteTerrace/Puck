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
  `follows` and its derived radius; a gravity orbit's `phaseBy`, read at the
  clock row's exact turn (`StateAdvance.TurnAt`); the projection carries a
  source's clocks. A palette slot that casts no shadow (`castsShadow: false`,
  `SdfMaterial.CastsNoShadow`), which no shadow drawing selects, so a star's
  ball lights what it stands over. A view stores its light at a scale near its
  exposure (`SdfPhotometry.StoredLightScale`), so a star's surface and the pad
  beside it fit the frame's half-float images, and an exposure reaches EV 30.
- **Standard.** `worlds/standard/stars.puck`: `sunlight`, `star`, `dayTurn`,
  `pairTurn`, `starLight`, `starDisc`, `starRail`, `starTide`, `starPull`,
  `starSurface`, `starHeat`. `dayCycle` is built on it.
- **The debug system.** A binary (`moon-frame.puck`'s `sunStar` and
  `companionStar`, the companion on `companionTurn` about the sun, in the day's
  plane) lights the valley (`day.puck`), the moon (`moon.puck`) and its own
  world (`sun.puck`), which owns the pair's clock (`pair`) that the valley
  (`sun-watch.puck`) and the moon follow. The gallery hall authors no sun. The
  worlds that stand alone (garden, hearth, night, galaxy, the Moth basis) take
  `sunlight`; the city's sun is its own day cycle's.
- **The proving place.** Perihelion, a rock in `sun.puck` the star passes once
  a day, reached through the `PERIHELION` painting in the painting wing, or by
  the launch field's ship through the seam over the moon (`sunSeamHeight`);
  `world.benchmark run sun` flies that trip until the star's heat burns the
  ship back onto the pad (`sun-route.puck`).

## What is left

In the order it matters. Each names the check that closes it.

1. **The light derived per view.** The sourced light is placed once per
   world a frame, from the frame's first view, so a second view far from the
   first (another player, a window's camera) is lit and shadowed from the
   first's direction. Deriving it per view means a light record per view
   (each view's light mask selecting its own) and, for the shadows, a light
   direction per shadow anchor (`SdfWorldShadows`), since a world's cascades
   are drawn once for every view. *Check:* two seats on either side of the
   star at the pass are each lit from the star.
2. **Indirect light beside a star.** The irradiance volume's shared-exponent
   cubes top out near 65,000 nits, so the bounce light beside the star
   saturates; the direct light, stored at the view's scale, does not.
3. **A moving seam.** A star-centred system, where planets orbit, needs an
   adjacency whose frame relation rides a clock; `WorldFrameIsometry` is
   fixed. The debug system avoids it by letting the star circle the planet.
4. **The planet from the star.** A sky view's draw distance is capped below
   the 15 km between Perihelion and the valley, so the outpost's sky shows no
   planet.
5. **The valley's sun dims with the switch.**
   [Cross-world, left open 1](cross-world.md#left-open): the valley follows
   `sunLight` and its key light does not read it.
