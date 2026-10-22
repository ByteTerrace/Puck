# Wonderland

[wonderland.puck](wonderland.puck) pairs two portals of two sizes. A gilt door
stands on the gallery lobby's west wall, and its counterpart is the same door a
quarter the size, a mouse hole at the foot of the east wall of the room next
door. Walk into the big door and you come out of the mouse hole a quarter your
size; walk back into the mouse hole and you come out of the big door at full
size again. The room holds the gaming devices ([devices.md](devices.md)) at
their real size, so at a quarter your size the television towers over you and
the Advanced Brick's plinth stands twice your height.

| Where | What |
|---|---|
| (10.9, 0.05, 30.5) | `wonderland-door`, the big door, 2.5 by 3.5 m, facing +X into the lobby, a sign over it |
| (9.2, 0.05, 30.5) | `wonderland-mouse-hole`, the mouse hole, 0.625 by 0.875 m, facing -X into the room |
| x 0.7 .. 9.7, z 23.7 .. 34.3 | `wonderland-room`: walls 4.5 m tall, open to the sky, a doorway in the south wall at x 3.5 .. 5.5, a 12 cm skirting inside |
| x 1.15 .. 1.65, z 28.6 .. 29.9 | `wonderland-stairs`: ten game cases climbing to the deck cabinet's top, 6.5 cm a step |
| From the cabinet to (4.8, 31) | `wonderland-cable`: the pad's extension cable across the floor, not solid |

## How the scale works

A mapped portal pair's map is a similarity
(`WorldFrameIsometry`): its scale is the counterpart face's width over the
source face's. The mouse hole is the big door at a quarter, so the pair's scale
is a quarter one way and four the other. A crossing multiplies the body's cell
of `bodyScale` (`bodies.scaleRow`, in `debug.puck`) by it, and maps her
position, velocity and heading through the pair. Her collider, walk speed,
gravity, jump, step height, engage reach, drawn form and chase camera all
follow the cell. A crossing that would take the cell outside the row's 0.25 to
1 is refused by name, on `world.transfer`. The two faces must be one rectangle
at two sizes a power of two apart, so a round trip returns her exactly to the
size she left with.

Each glass is a window as well as a door. The big door shows the room as it
looks from the mouse hole at a quarter size, the devices giant; the mouse hole
shows the lobby as it looks from the big door at full size.

At full size she cannot fit the mouse hole: the room's east wall stops her
before her feet reach its glass. To loom over the devices, walk in through the
room's own doorway (`debug.go wonderland-room`).

## Get in

- `debug.go wonderland` stands you 2.5 m before the big door, facing it. Walk
  ahead.
- `debug.go devices` stands you in the room before the television, at whatever
  size you are.
- Headless or from the console: `body.pose 12.5 0.05 30.5 90 0 0`, then
  `body.fly 0 -1 0 0 0 0 1.5` (the strafe runs -X while the camera faces -Z).
  Back out: `body.pose 8.4 0.05 30.5 -90 0 0`, then
  `body.fly 0 1 0 0 0 0 1.5`.

## What to watch

- Each crossing narrates on stderr, scale last:
  `[world.transfer: transfer=0 'boot' seat 1 crossed wonderland-door/glass -> wonderland-mouse-hole/glass in place, at (9.16, 0.06, 30.50) moving (-1.25, 0.00, 0.00) m/s, scale 0.25]`.
- `body.where 0` reports `scale=0.25` after the big door and `scale=1.00` after
  the mouse hole; `world.state bodyScale` shows the cell.
- At a quarter size the context button (E, X on a pad) engages a device from
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
