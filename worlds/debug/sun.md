# The sun

The debug world's sun is a place. [sun.puck](sun.puck) is a federated world
of its own in which the system's star is a body: a ball of fire that circles
the valley floor once a day. The valley, the moon and this world each place
the star where the shared `day` clock puts it on one orbit
([moon-frame.puck](moon-frame.puck)'s `sunStar` and `sunTurn`), so the sun in
the valley's sky, the sun over the moon and the star that passes this world's
outpost are one thing. The design is in [the stars plan](../../docs/plans/stars.md).

## Getting there

The painting `PERIHELION` hangs on the east wall of the painting wing
(`debug.go portal-sun` stands a body before it). Walk into the canvas and you
step out of an iron frame on Perihelion, a rock 520 m inside the star's path.
Walk back into the frame to return. The launch field's ship flies there too:
past the moon, a seam over it hands the ship to this world.

## What to look at

| Do this | And see |
|---|---|
| Stand on the pad | The star as a disc across the void, as wide as it looks from there: it grows as it comes. The frame's posts shadow the pad, hard while the star is far and blurred as it nears; the star's own ball casts no shadow. |
| `world.state.cell.set day $value 0.59` in the valley, then wait | The star arriving: a white ball in its own glow, 3 km out. It passes the rock at three in the afternoon (`day` 0.625), 380 m of void between its surface and the pad, and the camera stops down as it comes, so the pad stays a lit grey under it. |
| Hold the day near the pass (`world.row.set state day advance {"perSecondNumerator":0,"perSecondDenominator":1}` in the valley) | The star standing beside the rock, its surface churning. |
| Step off the rock's far side during the pass | The star's pull takes whatever the rock's does not hold. |
| Fly toward it | Within 300 m of its centre a body is burned and wakes on the pad (`world.state sunBurned` counts them, `world.state sunHeat` reads how near the first seat is to that). |
| Fly the ship there: board it on the launch field, climb through the sky seam, on past the moon's reach and through the seam over the moon (`sunSeamHeight`, [moon-frame.puck](moon-frame.puck)) | The sun's world takes the ship and its pilot over the moon, and the star ahead is the one the valley and the moon saw. Fly at it and its heat burns the ship back onto the pad; `world.benchmark run sun` flies the whole trip ([sun-route.puck](sun-route.puck)). |
| Watch the sky here, from the moon or from the valley floor | The star's red companion circling it every ten minutes, 2 km out ([moon-frame.puck](moon-frame.puck)'s `companionStar` and `companionTurn`): a second disc and a second, fainter shadow beside the star's. From the valley it swings across the star's disc and behind it twice a turn, since it circles in the day's plane. This world owns the pair's clock (`world.state pair`); the valley and the moon follow it. |
| `sun.switch 0` in the valley | The star goes dark here six seconds later, its surface with it; the moon and the valley read the dark after their own delays ([moon.md](moon.md#the-planet-the-sun-and-the-seam)). |
| Look through the painting from the wing during the pass | The star's light through the glass on the wing's floor. |

The valley's day levers ([debug.md](debug.md)) move the star in all three
worlds, since the moon's and the sun's `day` rows follow the valley's.

## How it is made

- **The star's place.** `starRail` (the standard's
  [stars.puck](../standard/stars.puck)) is a `gravity.orbits` row turned by the
  `day` row, with a body riding it. The ball and the pull are placements
  attached to that body.
- **Its light.** `starLight` is a directional light with a `source`: the
  engine derives its direction, its strength and its shadows' softness from
  where the star stands and where the viewer does. The same row lights the
  valley ([day.puck](day.puck)) and the moon ([moon.puck](moon.puck)), about
  the valley floor's place in each world's coordinates.
- **Its disc.** `starDisc` draws a sun disc as wide as the star looks, so the
  disc is the ball's own outline wherever the ball is too far to draw.
- **Its dangers.** `starHeat` and `starPull` are rules and a gravity area of
  this world, not engine code.
