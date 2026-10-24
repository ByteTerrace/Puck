# The rate range

The rate range is a lane of open ground on the debug world's west side for
judging the `world.rates` levers. [range.puck](range.puck) holds it, and
`debug.puck` imports it last. Six Moths walk loops at marked distances down the
lane, so a body's pose shows at every tier the bodies lever has. Three screens
play a bouncing ball, and three windows look into the garden. Each one stands at
a distance that puts its lever in the 30 Hz or the 15 Hz tier. Signs stand on
both sides of every body threshold. Each sign gives its distance and the tier a
body standing there takes at 1280x900 and at 3840x2160.

## Get there and compare

```text
debug.go range               # stand at the lane's head, facing down it (-Z)
world.debug-view rates       # tint each rated item by its tier (F8 toggles it)
world.debug-view rate-flash  # tint each one only on the frames it updates
world.debug-view off         # the final image again
world.rates toggle           # every lever off, then on again (F7)
world.rates                  # each lever's longest interval, and each rated item's size and tier
```

| Key | Does |
|---|---|
| F7 | `world.rates toggle`: every lever off, or every lever on again. Compare the same scene with and without the levers |
| F8 | `world.debug-view toggle rates`: the tier view, or the final image again |
| RT + View on a pad | `world.rates toggle` from the Debug page |
| RT + North (or X + I) | `world.debug-view next`: steps through the debug views, `rates` and `rate-flash` last |

`range.tiers rates|rate-flash|off` runs the same `world.debug-view` lines.

In the tier view, an item held to 30 Hz is tinted amber and one held to 15 Hz
magenta. An item updating every frame is drawn untinted, and so is every item
while its lever is off. The view covers bodies (their pose), screens (their
source's upload) and windows (their nested view, through the glass that shows
it).

`rate-flash` tints an item only on the frames it updates, so its cadence shows
as a flicker. A presented capture (`puck_capture_frame` with `mode:
"presented"`, or `world.screenshot <file> presented 8`) shows that flicker
frame by frame. A screen's or a window's flash follows the latest schedule, a
frame behind its upload.

The tier views keep the final shading but render without temporal
reconstruction, as every debug view does. Use them to find what is rated, and
judge the levers in the final image with F7. A settled capture rates nothing,
since a still of a tick owes nothing to earlier frames, so a tier view shows no
tint in one.

The far shadow cascade has no item to tint. `world.rates` reports it
(`shadow-far.30hz` against the 33.3 ms period), and it refreshes at least 30
times a second whenever its lever is on.

`world.rates` lists every item each lever rated on the latest frame, with its
size on screen and its tier. A body is listed as `body:<n>=<px/m>@<hz>`, a
window as `session$<screen>=<display px>@<hz>`, and a screen as
`source$machine$…=<share of its own height>@<hz>`. At 1280x900, from the spawn
with the camera at rest, it read:

```text
bodies: body:2=5.5px/m@15hz body:3=7.9px/m@15hz body:4=10.0px/m@15hz body:5=14.1px/m@15hz body:6=21.4px/m@30hz body:7=101.6px/m@60hz
views: session$28=85px@30hz session$29=36px@15hz session$30=21px@15hz
screens: …=0.59x@30hz …=0.28x@15hz …=0.15x@15hz
```

The walkers move, so their numbers move by a few percent.

## The lane

D is the distance down the lane from the spawn, at x = -100, z = 118 - D.

| What | Where | Tier at 1280x900 | Tier at 3840x2160 |
|---|---|---|---|
| Walker, body 7 | D 10 | every frame | every frame |
| Walker, body 6 | D 42 | 30 Hz | every frame |
| Walker, body 5 | D 64 | 15 Hz | every frame |
| Walker, body 4 | D 90 | 15 Hz | 30 Hz |
| Walker, body 3 | D 115 | 15 Hz | 30 Hz |
| Walker, body 2 | D 165 | 15 Hz | 15 Hz |
| Screen `range-deck-9` | D 9, 8 m left | 30 Hz | every frame |
| Screen `range-deck-24` | D 24, 8 m left | 15 Hz | 30 Hz |
| Screen `range-deck-50` | D 50, 8 m left | 15 Hz | 15 Hz |
| Window `range-window-27` | D 27, 12 m left | 30 Hz | every frame |
| Window `range-window-69` | D 69, 12 m left | 15 Hz | 30 Hz |
| Window `range-window-121` | D 121, 12 m left | 15 Hz | 15 Hz |
| Signs | D 25, 30, 55, 60, 67, 72, 138 and 146, on the right | on each sign | on each sign |

Each walker loops within about 3 m of its home at 1.5 m/s, a walk, so its
limbs swing the whole time. The tiers hold with the chase camera at rest at the
spawn. Turning the camera, walking down the lane or resizing the window moves
them, and the tier view shows where. A window larger than the monitor is
clamped to it: asked for 3840x2160 on a 2560x1440 monitor, the World presents
2564x1421, and every tier follows those 1421 rows.

## How the distances follow

A body switches to 30 Hz at or below 30 pixels per metre, and to 15 Hz at or
below 15 (`SdfUpdateRateTiers`). Pixels per metre are the display's height over
`2 tan(0.48)` = 1.0432 times the body's depth. The depth is measured to the
nearest point of the sphere the body's look reaches. The chase camera stands
4.2 m behind her at a 0.3 rad pitch with a 0.96 rad field of view
(`avatars/moth.puck`), so a body D down the lane stands about 0.9553 D + 2.39 m
deep. The thresholds fall at D = 27.6 and 57.7 m at 1280x900, and at 69.7 and
142 m at 3840x2160. The readings above match that model.

A screen's source uploads at 30 Hz once its picture shows at or below its own
240 rows on the display, and at 15 Hz at or below 120. Each screen's picture is
2 m high. A window renders at 30 Hz at or below 120 display pixels high and
15 Hz at or below 60. Each window's glass is the garden painting's, 2.84 m
high. All of these sizes are display pixels, after upscaling, so the render
grid never changes a tier.

A body seen through a window or a camera screen is rated by that view's share
of the display (`WorldViewGraphHost.DisplayShareOf`). A window no view shows
rates no body.

## Inside

- The walkers are population bodies 2 to 7, each a Moth on the `range-walker`
  kit, steered by the `rangeLoop` producer. It walks forward at three tenths of
  the kit's 5 m/s, weaving, and turns back toward its home once it is 2.5 m
  away. The debug world imports range.puck last, so the walkers take the
  population's lowest free slots, below the chess pieces, and no other body is
  renumbered.
- Each screen's deck runs `cartridges/deck/moving-sprite.nes` at an outdoor LED
  wall's 10,000 nits, so the picture reads in full sun.
- Each window is a door into the garden, as the garden painting is
  (`counterpart: "arrival/glass"`). Walking into one crosses to the garden, and
  the arrival frame there leads back to the garden painting in the wing. The
  three windows raise the debug world's derived face screens to eleven.
