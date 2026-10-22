# Painting wing

[paintings.puck](paintings.puck) hangs five paintings in the gallery hall's east
wing, on level 0: framed canvases that are doors, as in Mario 64. Walk or jump
into a canvas and you pass through it; until you do, its glass shows where it
leads. Each canvas ripples like an event horizon (see
[the ripple](#the-ripple)).

| Where | Painting | What it does |
|---|---|---|
| North wall, x = 57 | `painting-garden`, gilt frame | Opens onto the garden ([garden.puck](garden.puck)), a sunset plot. The garden's `arrival` frame is the way back. |
| North wall, x = 64 | `painting-night`, gallery frame (white mat in a black frame) | Opens onto the night field ([night.puck](night.puck)), a snowfield under stars. Its `arrival` frame is the way back. |
| West and east walls, z = 34.5 | `painting-link-west` and `painting-link-east`, brass frames | Two paintings facing each other 18 m apart across the room: step into either and step out of the other, facing out of it. |
| Floor and ceiling, (66, 22.6) | `painting-fall-floor` and `painting-fall-ceiling`, gilt squares | The endless fall: a body that steps onto the floor glass drops out of the ceiling glass still falling, and falls through the floor again, forever. |

The wing is x 51 to 69.7, z 12.3 to 39.7. A partition wall at x = 51 shuts it
off from the exhibit rows, with a doorway at z 24 to 28 under a "PAINTING WING"
board, and a lining over the south, north and east walls hides their windows.
The wing's own downlights and a track of wall washes over each wall painting
(`wing-down-*` and `wing-wash-*`, hall.puck) light it whatever the lobby's
switch says, and each canvas, which shows a live view, lights it further. Each painting has a plaque; the floor painting's lies on
the floor before it, and a rail rings the hole in level 1's floor over the
ceiling painting (below).

`debug.go portals` stands a body at the doorway, facing in. `debug.go
portal-garden`, `portal-night`, `portal-link` and `portal-fall` stand it before
each painting, facing it.

## Walk in

Issue each line through the console or the World's MCP attachment.

- The garden: `debug.go portal-garden`, then walk ahead. The body arrives at the
  garden's `arrival` frame walking -Z. Turn round and walk +Z into the frame
  and it comes back out of the painting. The garden's `far` frame, twelve metres
  on, is a second way back.
- The night field: the same from `debug.go portal-night`.
- Across the room: `debug.go portal-link`, then walk ahead (west). The body
  comes out of the east painting walking west, back across the room.
- The endless fall: `debug.go portal-fall`, then walk ahead onto the floor
  painting. A body posed above it (`body.pose 66 3 22.6 0 0 0`) falls onto the
  glass and in.

A headless seat keeps its heading at -Z, so there `body.fly` walks along the
heading and a sideways walk is a strafe: `body.pose 57 0.05 36.3 180 0 0`, then
`body.fly -1 0 0 0 0 0 2` walks +Z into the garden painting, and `body.pose
54.5 0.05 34.5 90 0 0`, then `body.fly 0 -1 0 0 0 0 2` walks -X into the west
painting.

A door fires once the body's origin, at its feet, passes through the glass. A
wall painting's canvas reaches the floor, so the feet cross it, and the wall
behind the glass holds nothing back while the body stands in the opening
(`WorldPortalHoleContactField`).

## The endless fall

The floor and ceiling paintings are a shaft in the debug world itself: both name
the destination `self`, which references the debug world's own document, so the
transfer resolves to the running World
(`world.destinations` reads `active=global:gen0@boot`), and each crossing moves
the body in place rather than transferring it to another world.

A body falling out of the ceiling painting needs 2.2 m of air over its head, so
level 1's floor is holed over the ceiling painting (`fallSlab` in
[hall.puck](hall.puck)) and railed on level 1. The hole is a real hole: a body
that falls into it from level 1 falls through the ceiling painting's back and
onto the floor painting, and from there it is in the loop.

- Each crossing narrates on stderr:
  `[world.transfer: transfer=24 'boot' seat 1 crossed painting-fall-floor/glass -> painting-fall-ceiling/glass in place, at (66.00, 4.11, 22.60) moving (0.00, -10.20, 0.00) m/s, scale 1.00]`.
  The fall speeds up on each lap until it holds at the Moth's 40 m/s sink
  speed (its `fall` hold's `envelope.sinkSpeed`).
- `body.where 0` gives the pose and `vel=(x, y, z)`, the authoritative velocity
  in metres per second.
- `world.portals` lists every face; `inside=1` on `painting-fall-floor/glass`
  means the seat stands latched just through its glass.

The floor glass shows the shaft as seen from just under the ceiling glass, so
the shaft repeats down through it to `views.nestingDepth` levels, the deepest
showing the fallback colour.

### Get out

Press Backspace (or L3 on a pad) to respawn at the stage spawn. From the console,
pose the body out of the shaft: `body.pose spawn:stage` returns it to the origin,
or `debug.go portal-fall` stands it before the floor painting. Flying out sideways
(`body.fly 0 1 0 0 0 0 1`) also works: a crossing keeps planar velocity, so the
body leaves the glass's footprint.

## The ripple

Each canvas is an opening whose source authors `ripple: true`
(`WorldScreenSource.Session.Ripple`, valid only under `projection: window`).
Presentation only; nothing in the simulation reads it.

- Idle: the canvas image is refracted by three slow interfering waves, with a
  faint caustic highlight riding them.
- Contact: a body whose middle (or, on the floor and ceiling glass, whose feet)
  comes within 0.45 m of the glass, inside its outline, sends concentric rings
  from the point it touched across the whole canvas, fading within about a
  second. A body that passes through sends stronger ones, and a body that
  arrives out of a painting rings it. Up to four contacts show at once and sum.
- The opening is reactive in a temporal view, so the moving refraction is not
  smeared by the history.

The contacts come from the bodies' render poses and the presentation clock
(`WorldOpeningRipples`); the shading is `sdfOpeningRipple` in
`shade/sdf-environment.hlsli`. A presented capture shows it:
`world.screenshot <path.png> presented 8`, then `world.wait captures 30`.

## Testing

A same-world pair cannot be booted by a `puck test` block: the generated world
carries a minted document id, so the destination `self` (which names the
`debug` document) resolves to another world instead of the running one. Show the
linked pair and the fall by running the debug world headless and driving it with
the lines above, reading each crossing's `world.transfer` echo.
