# The desktop pane

[desktop.puck](desktop.puck) captures the Claude desktop app's window: the
first visible top-level window whose title contains `Claude`, compared without
regard to case. It shows the window in two places: the `views.graphs` row
`claude`, which the `desktop` layout places as a pane over the bottom two
fifths of the display below the play view, and screen 16, a 4 m by 1 m glass slab beside the
portal frame, eight metres ahead of the spawn and eight to the right. Every
line below is typed at the World's console, or sent through the local operator
attachment (`world.control`, the `puck` MCP server), which is the same console.

## Show and hide the pane

```text
view.override layout desktop
view.override layout play
```

The play wheel's Desktop and Play sectors (hold Tab or LB) run the same two
lines. `view.override layout auto` also returns to the play view. `world.view.panes`
lists the shown panes; with the desktop layout active it lists `claude` over
the bottom two fifths, with its mapping.

To move or resize the pane, change `paneHeight` in `desktop.puck` (the pane's
share of the display, measured from the bottom edge) or the layout's slots. A
slot's `x`, `y`, `width` and `height` are fractions of the display, origin top
left. To capture another window, change `claudeTitle`. The `claude` row's
`fit: Contain` shows the window at its own aspect ratio, centred in the slot
with dark bars beside it, whatever the slot's shape, the display's size or the
capture's `profile` extent (which sets only how many pixels the capture keeps).
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

Only the World's own console opens a passthrough source. No binding, seat,
peer, addon, schedule or world document can, so nothing in `desktop.puck` sends
the window input. Hiding the pane keeps the source open, and the window then
receives nothing; a click after the pane is shown again reaches it. A headless
or offscreen boot has no window and no `source.passthrough` verb.

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
