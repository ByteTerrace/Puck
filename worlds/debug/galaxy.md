# The planetoid field

Six small bodies in open space, each with gravity of its own shape, behind the
galaxy painting in the painting wing ([galaxy.puck](galaxy.puck)). It is the
proving ground for gravity that comes from shapes; the design it follows is
[physics across worlds](../../docs/plans/cross-world.md#physics-across-worlds).

`debug.go portal-galaxy` stands a body before the painting on the wing's south
wall; walk ahead into the canvas. You arrive on home's pad, and the frame behind
you leads back.

## The tour

A glowing disc is a launch pad: stand on it and you are carried to the next
body along an arc. The pads are the standard's `launchPad`
([gravity.puck](../standard/gravity.puck)), a rule over the timed move that
travels to a placement.

| Stop | Shape | Its gravity | Where the next pad is |
|---|---|---|---|
| Home | A ball | A `point` area: the pull falls with distance as a ball's does | On the pad the frame stands on, 3.5 m east of the middle |
| Ring | A torus | A `surface` area: toward the tube's nearest surface, so you walk round the tube and through the hole | On top of the tube, on the far side from where you land |
| Cube | A cube | A `surface` area: each face is a floor, and an edge turns you over it | On the top face, toward a corner |
| Coin | A slab | A `planar` area: both faces are floors | On the underside: walk over the rim |
| Turnover | A corridor with a floor and a ceiling | A `directional` area whose `scale` row a pad writes | On the floor, toward the west edge |
| Pipe | A tube walked on the inside | An `axial` area that pushes outward | On the tube's ceiling: walk round the inside |

In the turnover, the pad at the far end of the floor writes `turnoverPull` to
-1, which turns the pull up; the pad on the ceiling writes it back. A body
standing on the floor rises straight to the ceiling, turning over on the way,
and stands on it.

A pebble moon circles home on rails (`gravity.orbits`): its position is the
orbit's closed form at every tick, and it carries a small pull of its own, so
you can stand on it as it comes round. `world.state.cell.set pebbleRiding
$value 0` takes it off the rails and `1` puts it back; it closes on the orbit
at six metres a second and is then where the orbit stands at that tick.

Open space pulls faintly toward home, so a body that walks off a planetoid
drifts back to it.

## Headless

`world.gravity` on the galaxy's instance lists each area's form and bindings,
the pebble's orbit with its position, and the last solve's counts
(`surfaceQueries` is the contact-field gradient queries the two surface areas
asked: none for a body standing where it stood).

```text
body.pose 3.5 0.3 0 0 0 0
world.wait 8
body.where
```

stands a body on home's first pad and reads it on the ring eight seconds later.
`puck test worlds/debug/galaxy-tests.puck --reproduce` plays a drop onto home's
side, two launches, the turnover and the pebble.
