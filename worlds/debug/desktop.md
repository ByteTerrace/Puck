# The desktop pane

[desktop.puck](desktop.puck) captures the whole primary display (`monitorIndex:
0`, 2560 by 1440). The World's own window is on that display, so the picture
shows the World showing the picture, smaller each time, as a camera pointed at
its own screen does. It shows the display in two places: the `views.graphs` row
`desktop`, which the `desktop` layout places as a pane over the bottom two
fifths of the display below the play view, and screen 16, the monitor of a
desktop computer in the games corner of the gallery hall's lobby (below). Every
line below is typed at the World's console, or sent through the local operator
attachment (`world.control`, the `puck` MCP server), which is the same console.

The primary display rather than the one the World's window is on: the
monitor's glass is cut to the capture's extent when the world compiles, and the
primary does not change under a running World, while the World's window can
move to a display of another shape. To capture another display, change
`monitorIndex` (0 is the primary, the rest follow in the order Windows lists
them) and the profile's extent to its size.

## The desktop computer

A desk stands against the lobby's west wall between the chess table
([`chess.md`](chess.md)) and the big painting to Wonderland ([`wonderland.md`](wonderland.md)), on
the level 0 floor (y = 0.05), every piece at the size of the real thing and its
own prototype in `desktop.puck`:

| Piece | Where and how big |
|---|---|
| Desk | 1.5 m wide and 0.75 m deep, its light oak top 0.74 m up on charcoal panel legs: x 10.33 to 11.08, z 22.95 to 24.45 |
| Monitor | a 27-inch 16:9 monitor on an aluminium stand on the desk's back half, its glass's face centred at (10.75, 1.13, 23.7), facing +X into the lobby |
| Computer | a 210 by 480 by 480 mm graphite mid tower on the floor north of the desk, its mesh front and lit power button facing the lobby |
| Keyboard and mouse | a 440 by 140 mm keyboard of five rows of 14 mm keys before the monitor, the mouse on its right |
| Chair | an office chair on five casters, its seat 0.5 m up, before the desk at (11.55, 23.7) |

Whoever sits at the desk faces the wall, -X. `debug.go desktop` stands the
Moth behind the chair, facing the monitor and within its engage radius.

The monitor's glass is exactly the picture, the owner's ruling for every
device ([decision](../../docs/decisions/rendering.md#in-world-devices-and-screens)):
the capture's 2560 by 1440 pixels at a 27-inch 2560 by 1440 panel's pixel
pitch, 0.2331 mm, make a 596.7 by 335.7 mm glass (`halfWidth` 0.29837 and
`halfHeight` 0.16783), the display's own 16:9 shape. The panel's black bezel
lies outside the glass, 7 mm at the sides and top and 18 mm at the chin. The
glass is a 14 mm slab sunk 4 mm into the panel, its face 10 mm proud of the
panel and 3 mm proud of the bezel, thick enough for the mesh bake to resolve it
in front of the panel.

To frame the corner from behind the chair, or the monitor alone:

```text
view.override camera look 12.6 1.6 23.7 10.75 1.1 23.7
view.override camera look 11.6 1.25 23.7 10.75 1.13 23.7
view.override camera auto
```

## Show and hide the pane

```text
view.override layout desktop
view.override layout play
```

The play wheel's Desktop and Play sectors (hold Tab or LB, then step to the View
ring) run the same two lines. `view.override layout auto` also returns to the
play view. `world.view.panes` lists the shown panes; with the desktop layout
active it lists `desktop` over the bottom two fifths, with its mapping.

To move or resize the pane, change `paneHeight` in `desktop.puck` (the pane's
share of the display, measured from the bottom edge) or the layout's slots. A
slot's `x`, `y`, `width` and `height` are fractions of the display, origin top
left. The `desktop` row's `fit: Contain` shows the display at its own aspect
ratio, centred in the slot with dark bars beside it, whatever the slot's shape.
`world.view.panes` lists the pane's source at the display's size (`source
2560x1440`) with `fit Contain`.

## Use the desktop by engaging the monitor

Screen 16 engages like a device: walk within 3 m of the monitor and press the
context button (E, or X on a pad). The camera eases onto the monitor's picture
until it fills the window as large as fits, centred on black, with the room
faded out around it, and the system arrow shows while the pointer is on the
picture.

The picture is a remote control for the real desktop. A click on it (or A on a
pad) opens the desktop as a passthrough source (`source.passthrough` lists
`desktop engaged window 'Desktop' …`), and from then on each press on the
picture lands on the desktop at the point the picture shows: when the button
comes up, the system cursor goes there, presses, drags to where the release
was, releases, and comes back to where your hand left it. So a click on the
taskbar's picture clicks the taskbar, opens Start, or brings a window forward,
exactly as clicking the real one would. The wheel turns at the point under the
pointer the same way. Moving the pointer over the picture reaches nothing.

- **The World in the picture.** Where the picture shows the World's own window,
  a press reaches nothing: the World never sends input to itself, so a click on
  its mirror cannot repeat itself down the picture.
- **The keyboard is Windows'.** The desktop takes no keys from the World. A
  window a press brought forward has the keyboard as it would after a real
  click; while the World's window is in front the keys are the game's (so E
  leaves).
- **Windows draws over the World.** Start, a menu or a window a press brings
  forward opens on top of the World's window on the real screen, as over any
  window, and is used directly there; the picture shows it too.

Only a press of the local user's own mouse or pad counts; `body.engage 16` at
the console, `player.signal` or anything scripted engages the screen and shows
the desktop without opening it.

**Leave in one step:** Control+Alt+Escape, from any window (while a seat frames
the desktop the World claims the chord system-wide, and `source.passthrough`
says `chord claimed`), or X on a pad, or E while the World's window is in
front. Each closes the passthrough source, releasing every button and key the
pad holds, brings the World's window back to the front so the keyboard is the
game's, and eases the camera back to the Moth; `source.passthrough` then reads
`none opened`. A headless or offscreen boot has no window and no
`source.passthrough` verb.

### With a pad

While the Moth is engaged on the monitor, the `desktop-pad` page in
`desktop.puck` makes a pad the desktop's mouse, the way a controller's desktop
mode does. It moves the system cursor itself, across the whole display, and
presses at it, whichever window is in front:

| Control | Does |
|---|---|
| Left stick | Moves the cursor across the display, faster the further it tilts and the longer it is held |
| Right stick | Scrolls, up and down and side to side |
| A | Left click; hold it to drag. The first press opens the desktop |
| Y | Right click |
| B | Middle click |
| LB / RB | Back / Forward (the mouse's fourth and fifth buttons) |
| D-pad | Arrow keys, to whichever window has the keyboard |
| X | Leaves |
| Start | Nothing yet: the engine has no on-screen keyboard |

The cursor stays on the display. Where it rests on the World's window over the
picture, a press is the World's own press there and lands where the picture
shows, as a mouse click does. Its feel is the screen route's `padPointer` in
`desktop.puck`: `speed` (display heights a second at full tilt), `curve` (how
much finer a small tilt is), `boost` reached after `rampSeconds` of held tilt,
and `scrollSpeed` (notches a second); `world.screens` echoes it. The verbs the
page names (`source.passthrough.pointer`, `.scroll`,
`.button.<left|right|middle|back|forward>`, `.arrow.<up|down|left|right>`) act
only for a local pad's own control: typed, or driven by `player.signal`, they
are refused by name. `source.passthrough` ends with the pad's state:

```text
[source.passthrough: keyboard game; desktop engaged window 'Desktop' frame 2560x1440 client 2560x1440 at 0,0 scale 1 chord claimed; pad p1 screen 16 'source$capture$…' stick (0.4, 0) scroll (0, 0) buttons none keys none feel speed 0.5 curve 2 boost 3 ramp 0.6s scroll 12/s]
```

## Use the pane from the console

The pane draws the desktop but sends it nothing until you open it:

```text
source.passthrough open desktop Desktop
```

`desktop` is the pane's instance and `Desktop` the title the capture's target
carries; the verb refuses if the pane is not shown. Once open, a press on the
pane lands on the desktop where the pane shows it, as on the engaged monitor.
`source.passthrough close desktop` closes it.

Only the World's own console, or your own click or pad press on the engaged
monitor, opens a passthrough source. No seat, peer, addon, schedule, script or
world document can: `desktop.puck`'s pad page only says which pad control means
which pointer act, and the engine acts on your own pad's press alone.

## Edit while it runs

```text
world.watch on
```

Each save of `desktop.puck` (or any file the world imports) then reloads the
world after 150 ms of quiet. Without the watch, save and type:

```text
world.reload
```

A reload that refuses keeps the running world and prints the source
diagnostic. A changed `desktopCapture` names a new source, so the pane and the
screen each open a fresh capture, and an open passthrough source on the old one
closes; open it again.

## What a capture shows

External pixels never reach a capture: while a `world.screenshot` or
`puck_capture_frame` is armed, the pane and the screen show their capture fill,
a flat `#202020`, in place of the desktop. To confirm the desktop is drawn,
look at the World's own window (or a screenshot of the real screen), or read
`world.view.panes` and `source.passthrough`.
