# The moon

The pale moon over the valley is a world of its own ([moon.puck](moon.puck)): a
cratered sphere about its origin whose pull points at its centre. The debug
world shows it in its sky and reaches it two ways. The plan it follows is
[the valley and the moons](../../docs/plans/valley-and-moons.md#the-trip-to-the-moon).

The two worlds share one frame up to a shift ([moon-frame.puck](moon-frame.puck)):
a point at `p` on the moon is the point `p + moonCentre` of the debug world.
The moon in the valley's sky is drawn from that frame, and the sky seam hands a
ship across at the same point of it.

## Through the painting

The moon painting in the painting wing ([paintings.md](paintings.md)) is a door
opened in play ([moon-door.puck](moon-door.puck)). Its curtain stays drawn
until a seat presses the context button on the step before it: the press offers
the moon a pairing, the moon's own rules grant it by opening the socket on its
pad (a bronze frame on the pad's east rim), and the curtain draws aside while
both stand. Walking in arrives out of the socket; walking back into the socket
arrives before the painting. The offer lapses after 45 seconds and the moon's
grant after 60, and only the seat that pressed may pass. Headless, the lines
are:

```text
body.pose 66 0.1 13.6 0 0 0 0
player.signal keyboard.e press
player.signal keyboard.e release
world.wait 6
world.state moonDoorOpen
player.signal keyboard.w press
```

The same door stands in the open on the launch field: a gilt frame east of the
pad (`moon-door-valley`). A press on the ground before it makes the same offer,
and while the door stands open walking through it arrives out of the same
socket. Headless, `body.pose 100 0.3 78.9 0 0 0 0` stands a seat on its step.
The socket's way back still leads to the painting.

The frame at the moon's north pole is the moon's own way home: walking into it
arrives before the painting whether or not the door is open.

Beside the painting, the moon beacon switch ([moon-beacon.puck](moon-beacon.puck))
lights the beacon on the moon's pad. The debug world owns the switch; the moon
follows it and its own rule lights the lamp.

The moon's `day` follows the debug world's, so writing `day` in the valley
(`world.state.cell.set day $value 0.75`) moves the moon's sun too.

The valley is on the moon's other side from the pole. Walking or flying round
the moon brings a body to any other part of it: its feet stay toward the
centre everywhere.

## By ship

`debug.go launch` stands a body beside the launch field's pad
([launch.puck](launch.puck)), where the standard's ship
([ship.puck](../standard/ship.puck)) stands. The context button boards it
from within five metres and steps out beside it again;
`body.engage body:<n> capture:on` and `body.disengage` are the console's
forms, with the ship's body number from `world.population` (the highest
slot). While a seat drives the ship
its own body rides in the cockpit (`body.where` reads `anchor=body:<n>`), and
the ship flies as she walks: the movement channels fly it where the camera
looks, the rise channel climbs and sinks, and the run channel is the boost.

The seam is a level plane `skySeamAltitude` metres over the valley floor
(`world.adjacencies` reads it as `sky`). A ship that climbs through it is the
moon's from there, with its pilot. Both crossings narrate:

```text
[world.adjacency: 'boot/sky' body:574 crossed -> queued transfer=2 ...]
[world.transfer: transfer=3 'boot' seat 1 departed -> '<moon instance>' seat 1 arrived (anonymous), driving body:1 (kit 'ship') ...]
```

Inside the moon's reach (`moonReach` metres from its centre) the ship rolls to
stand against the moon's pull. Brought down within its flare of the ground and
let go, it settles and stands. `body.where <body> instance:<name>` reads a
body of the moon's instance by its zero-based index, as the boot form does:
the seat as 0 and the ship as the body the transfer line names.

Headless, a seat's forward is world -Z whatever the ship's heading, so a
scripted flight steers with `body.fly`'s forward and strafe as world axes and
its rise along the ship's own up, which is away from the moon inside its reach.

## The whole trip in one line

`world.benchmark run moon` flies the round trip ([moon-route.puck](moon-route.puck)):
the standard's `pilot` seat program works seat 1's own controls, so the seat
boards on the pad with the context button, climbs through the seam with the
boost held, crosses over the moon's pole above its reach, lands beside the
arrival frame, and comes home the same way and steps out. The controls are the
seat's, so they follow it across the seam. Each leg is a route `fly` step: a
point to fly to, the world it is in, and for a seam the world it enters; a
landing flies at the ground until the ship stands on it. `world.benchmark`
reads the chapter it is on and, at the end, each chapter's report.

While the seat rides the ship its camera is the ship's own
(`shipCamera` in [ship.puck](../standard/ship.puck)), on either side of the
seam. The sky thins as it climbs: the `altitude` clock
([launch.puck](launch.puck)) reads how high the viewer stands, and
[day.puck](day.puck) keys the air, the clouds and the stars of space on it.
Past the seam the seat sees the moon's own scene.

## What it does not do yet

The plan's [engine gaps](../../docs/plans/valley-and-moons.md#engine-gaps-on-the-path)
lists them: above about 1,500 m the valley's exposure opens up and the ship
blows out until the moon's exposure takes over at the seam, the valley is not
drawn in the moon's sky and so drops out of view below at the seam, the
arrival frame stands on the side away from the valley, and the crossing
stalls for a moment.
