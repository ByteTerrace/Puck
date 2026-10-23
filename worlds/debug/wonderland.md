# Wonderland

[wonderland.puck](wonderland.puck) pairs two portals of two sizes. A painting in
the painting wing's ornate gilt frame ([paintings.md](paintings.md)) hangs on the
gallery lobby's west wall, and its counterpart is the same painting a quarter
the size, a mouse hole at the foot of the east wall of the room next door. Walk
into the big painting and you come out of the mouse hole a quarter your size;
walk back into the mouse hole and you come out of the big painting at full size
again. The room holds the gaming devices ([devices.md](devices.md)) at their
real size, so at a quarter your size the television towers over you and the
Advanced Brick's plinth stands twice your height.

| Where | What |
|---|---|
| (10.3, 0.05, 30.5) | `wonderland-door`, the big painting on the lobby west wall's face, its canvas 2.25 by 2.875 m from just below the floor, facing +X into the lobby, its plaque on the wall to its right |
| (9.4, 0.05, 30.5) | `wonderland-mouse-hole`, the mouse hole on the room east wall's face, its canvas 0.5625 by 0.71875 m, facing -X into the room |
| x 0.7 .. 9.7, z 23.7 .. 34.3 | `wonderland-room`: walls 4.5 m tall under a roof, a doorway in the south wall at x 3.5 .. 5.5, a 12 cm skirting inside, and one dim 2,700 K lamp (`wonderland-lamp`, hall.puck) so the device screens glow |
| x 1.15 .. 1.65, z 28.6 .. 29.9 | `wonderland-stairs`: ten game cases climbing to the deck cabinet's top, 6.5 cm a step |
| From the cabinet to (4.8, 31) | `wonderland-cable`: the pad's extension cable across the floor, not solid |

## How the scale works

A mapped portal pair's map is a similarity
(`WorldFrameIsometry`): its scale is the counterpart face's width over the
source face's. The mouse hole is the big painting at a quarter, so the pair's scale
is a quarter one way and four the other. A crossing multiplies the body's `size`
register (`bodies.scale`, declared in `size.puck`) by it, and maps her
position, velocity and heading through the pair. Her collider, walk speed,
gravity, jump, step height, engage reach, drawn form and chase camera all
follow the register. A crossing that would take it outside the register's 0.25
to 1 is refused by name, on `world.transfer`. The two faces must be one
rectangle at two sizes a power of two apart, so a round trip returns her
exactly to the size she left with. The register is her own state, so it goes
with her through the painting wing's paintings: she arrives in the garden or
the night field the size she left in, and comes home that size.

Each canvas is a window as well as a door, rippling as the painting wing's do.
The big painting shows the room as it looks from the mouse hole at a quarter
size, the devices giant; the mouse hole shows the lobby as it looks from the
big painting at full size.

Both paintings hang on their wall's face, and the wall gives way while a body
stands in the opening and fits inside it
(`Puck.World.Server.WorldPortalHoleContactField`). At full size she does not
fit the mouse hole, so the wall round it stops her before her feet reach its
glass. To loom over the devices, walk in through the room's own doorway
(`debug.go wonderland-room`).

## Get in

- `debug.go wonderland` stands you 3 m before the big painting, facing it.
  Walk ahead.
- `debug.go devices` stands you in the room before the television, at whatever
  size you are.
- Headless or from the console: `body.pose 12.5 0.05 30.5 90 0 0`, then
  `body.fly 0 -1 0 0 0 0 1.5` (the strafe runs -X while the camera faces -Z).
  Back out: `body.pose 8.8 0.05 30.5 -90 0 0`, then
  `body.fly 0 1 0 0 0 0 1.5`.

## What to watch

- Each crossing narrates on stderr, scale last:
  `[world.transfer: transfer=0 'boot' seat 1 crossed wonderland-door/glass -> wonderland-mouse-hole/glass in place, at (9.38, 0.06, 30.50) moving (-1.25, 0.00, 0.00) m/s, scale 0.25]`.
- `body.where 0` reports `scale=0.25` after the big painting and `scale=1.00` after
  the mouse hole; `body.state 0` shows the `size` register.
- At a quarter size the context button (E, RB on a pad) engages a device from
  within 0.75 m, on the ground plane; `body.engage <screen>` from farther says
  how near she must stand at her size.
- A jump at a quarter size rises about 0.3 m, a quarter of her full jump.

## Things to do small

- Step up into the deck cabinet's open right-hand bay, where the spare
  cartridges lie: its floor is one quarter-size step up.
- Climb the game cases at the cabinet's south end to its top and stand beside
  the console's cartridge, the television's back towering over you.
- Walk up to the Humble Brick's face where it leans on its rest on the floor.
- Follow the cable across the floor.
