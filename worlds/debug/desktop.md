# The desktop pane

[desktop.puck](desktop.puck) captures the Claude desktop app's window: the
first visible top-level window whose title contains `Claude`, compared without
regard to case. It shows the window in two places: the `views.graphs` row
`claude`, which the `desktop` layout places as a pane over the bottom two
fifths of the display below the play view, and screen 16, the monitor of a
desktop computer in the games corner of the gallery hall's lobby (below). Every
line below is typed at the World's console, or sent through the local operator
attachment (`world.control`, the `puck` MCP server), which is the same console.

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
the capture's 2560 by 1392 pixels at a 27-inch 2560 by 1440 panel's pixel
pitch, 0.2331 mm, make a 596.7 by 324.5 mm glass (`halfWidth` 0.29837 and
`halfHeight` 0.16224), the window's own shape. The panel's black
bezel lies outside the glass, 7 mm at the sides and top and 18 mm at the chin.
The glass is a 14 mm slab sunk 4 mm into the panel, its face 10 mm proud of
the panel and 3 mm proud of the bezel, thick enough for the mesh bake to
resolve it in front of the panel.

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
ring) run the same two lines. `view.override layout auto` also returns to the play view. `world.view.panes`
lists the shown panes; with the desktop layout active it lists `claude` over
the bottom two fifths, with its mapping.

To move or resize the pane, change `paneHeight` in `desktop.puck` (the pane's
share of the display, measured from the bottom edge) or the layout's slots. A
slot's `x`, `y`, `width` and `height` are fractions of the display, origin top
left. To capture another window, change `claudeTitle`. The `claude` row's
`fit: Contain` shows the window at its own aspect ratio, centred in the slot
with dark bars beside it, whatever the slot's shape, the display's size or the
capture's `profile` extent (which sets only how many pixels the capture keeps;
the pane and the monitor share `claudeCapture`, at the window's own size, and
the monitor's glass is cut to it).
`world.view.panes` lists the pane's source at the window's own size (`source
2560x1392` for a maximized window) with `fit Contain`. The display follows the
World's window, so maximizing it re-lays the pane over the larger display.

## Use the window from inside the World

The pane draws the window but sends it nothing until you open it:

```text
source.passthrough open claude Claude
```

`claude` is the pane's instance and `Claude` the title you expect the captured
window to carry; the verb refuses if the pane is not shown or the window's title
does not contain it. Once open, `world.view.panes` ends the pane's mapping
with `destination Passthrough` in place of `destination Presentation`. A click on the pane gives the window the keyboard;
pointer events over the pane reach the window at the matching point, and keys
and text go to it instead of the game. Over the window the World shows the
system arrow in place of its own drawn cursor; on the bars beside it and
everywhere else the pointer is the game's.

- **Control+Alt+Escape** returns the keyboard to the game. The source stays
  open, and another click on the pane gives the window the keyboard again.
- `source.passthrough` echoes where the keyboard is and each open source's
  window, captured frame, client area and DPI scale.
- `source.passthrough close claude` closes it, releasing every key and button
  the window holds.

Only the World's own console, or your own click or pad press on the engaged
monitor below, opens a passthrough source. No seat, peer, addon, schedule,
script or world document can: `desktop.puck`'s pad page only says which pad
control means which pointer act, and the engine acts on your own pad's press
alone.

## Use the window by engaging the monitor

Screen 16 engages like a device: walk within 3 m of the monitor and press the
context button (E, or X on a pad). The camera eases onto the monitor's picture
until it fills the window as large as fits, centred on black, with the room
faded out around it, and the system arrow shows while the pointer is on the
picture. Nothing reaches the window yet: click the picture (or press A on a
pad), and that press, mapped through the screen to the window's own pixel,
opens it as a passthrough source, reaches the window, and gives it the keyboard
(`source.passthrough` lists it as `<instance> engaged window 'Claude …'`). Only
a press of the local user's own mouse or pad counts; `body.engage 16` at the
console, `player.signal` or anything scripted engages the screen and shows the
window without opening it.

**Leave in one step:** Control+Alt+Escape, whoever has the keyboard, or X on a
pad. Both close the passthrough source, releasing every key and button the
window holds, and ease the camera back to the Moth; `source.passthrough` then
reads `none opened`. E leaves too until the window takes the keyboard; after
that E types into the window. Clicking the black fill beside the window returns
the keyboard to the game without leaving. Hiding the pane keeps the source
open, and the window then receives nothing; a click after the pane is shown
again reaches it. A headless or offscreen boot has no window and no
`source.passthrough` verb.

### With a pad

While the Moth is engaged on the monitor, the `desktop-pad` page in
`desktop.puck` gives a pad the desktop, the way a controller's desktop mode
does:

| Control | Does |
|---|---|
| Left stick | Moves the pointer across the picture (the system cursor follows), faster the further it tilts and the longer it is held |
| Right stick | Scrolls, up and down and side to side |
| A | Left click; hold it to drag. The first press opens the window |
| Y | Right click |
| B | Middle click |
| LB / RB | Back / Forward (the mouse's fourth and fifth buttons) |
| D-pad | Arrow keys, once the window is open |
| X | Leaves |
| Start | Nothing yet: the engine has no on-screen keyboard |

The pointer stays on the picture. Its feel is the screen route's `padPointer`
in `desktop.puck`: `speed` (picture heights a second at full tilt), `curve`
(how much finer a small tilt is), `boost` reached after `rampSeconds` of held
tilt, and `scrollSpeed` (notches a second); `world.screens` echoes it. The
verbs the page names (`source.passthrough.pointer`, `.scroll`,
`.button.<left|right|middle|back|forward>`, `.arrow.<up|down|left|right>`) act
only for a local pad's own control: typed, or driven by `player.signal`, they
are refused by name. `source.passthrough` ends with the pad's state:

```text
[source.passthrough: keyboard claude; claude engaged window 'Claude' …; pad p1 screen 16 'claude' stick (0.4, 0) scroll (0, 0) buttons none keys none feel speed 0.5 curve 2 boost 3 ramp 0.6s scroll 12/s]
```

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
diagnostic. A changed `claudeCapture` names a new source, so the pane and the
screen each open a fresh capture, and an open passthrough source on the old one
closes; open it again.

## What a capture shows

External pixels never reach a capture: while a `world.screenshot` or
`puck_capture_frame` is armed, the pane and the screen show their capture fill,
a flat `#202020`, in place of the window. To confirm the window is drawn, look
at the World's own window, or read `world.view.panes` and `source.passthrough`.

A window that delivers no frame shows the same fill outside a capture: one
minimized since the World opened its capture (a minimized window sends the
compositor nothing), or one that is closed, until it returns. The World stays
ready and captures proceed meanwhile, and `screen.state 16` ends with
`fault=window 'Claude' awaiting a compositor frame; it shows its capture fill`. A window minimized after its first frame keeps showing that frame.
