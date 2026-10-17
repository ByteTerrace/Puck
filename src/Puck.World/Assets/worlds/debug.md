# Debug world portals

[debug.puck](debug.puck) stands a gilt picture frame eight metres ahead of the
spawn. Its glass is a portal onto the garden, [garden.puck](garden.puck): a
small plot under a sunset sky, with a green floor and a golden obelisk. The
frame shows the garden as a window, and walking through it crosses there with
no load. The garden holds two frames of its own that lead back, and the far one
stands in view through the debug frame, so the spawn shows a portal seen through
a portal.

## Open it

Run from the repository root:

```powershell
dotnet run --project src/Puck.World -c Release -- --control --world src/Puck.World/Assets/worlds/debug.puck
```

The debug world is the World's default, so `--world` only matters when you
want `world.reload` to recompile your edits to the source. `--control` opens the
Operator attachment an MCP client drives
([setup](../../../Puck.Mcp/README.md)). Where several Worlds run, start the
adapter with `--attach <file>`, naming the file the World prints on stderr
(`[world.control: operator attachment <file>]`); without it, the adapter keeps
whichever World it attached to first.

## The layout

| Where | What | Portal |
|---|---|---|
| Debug world, `portal` at (3, 0, -8), facing +Z, ahead and right of the spawn | The gilt frame, glass showing the garden | Walk -Z through it: you arrive in the garden at the same point, still walking -Z. The pair (`portal`, `arrival`) faces each other, so the crossing maps as the identity. |
| Garden, `arrival` at (3, 0, -8), facing -Z | The frame you came out of, glass showing the debug world | Turn round and walk +Z into it: you arrive back out of the debug frame, walking +Z towards the spawn. |
| Garden, `far` at (5.5, 0, -20), facing +Z | The frame ahead of you, glass showing the debug world turned about | Walk -Z into it: the pair (`far`, `portal`) shares a yaw, so the crossing turns you about and you come out of the debug frame walking +Z towards the spawn, the view turned with you. |
| Garden, `obelisk` at (9, 0, -30) | The landmark | |

The far frame stands on the line the spawn camera looks along through the debug
frame, so the window from the spawn holds the far frame's window back onto the
debug world, two levels deep (`world.nesting` lists `session$24`, then
`session$24$24` and `session$24$25`, then the third level). The
axis lines, the reference cubes and the test room stand behind the spawn on +Z
and -X, out of the frames' way.

## What to try

A presenting boot (windowed or offscreen) currently draws nothing once the
frame's window follows the garden's scene: the display stays black and a
capture is one pixel, while the simulation runs on
([open item](../../../../docs/plans/open-items.md#rendering)). The crossings
below work in every boot shape, headless included.

- Walk forward from the spawn and keep walking (`body.pose 3 - 0 0 0 0`, then
  `body.fly 1 0 0 0 0 0 4`): the debug frame's band mints a transfer, and you
  arrive in the garden at the same point. Walk on through the far frame
  (`body.pose 5.5 - -12 0 0 0`, `body.fly 1 0 0 0 0 0 5`) and you come back out
  of the debug frame turned about.
- From inside the garden, stand past the arrival frame and walk +Z into it
  (`body.pose 3 - -12 180 0 0`, `body.fly -1 0 0 0 0 0 2`): you come back out of
  the debug frame walking +Z.
- Set `nestingDepth` in `views` to 1 and boot again: `world.nesting` names no
  level two deep, and the garden's frames show their fallback magenta.
- Edit [garden.puck](garden.puck), then `world.instance.stop 6~global6~garden`:
  the garden retires, and the next crossing resolves a fresh garden from the
  edited source. `world.reload` recompiles only the debug world.

`body.fly` moves along the seat's heading, which a headless boot keeps at -Z,
so there `body.fly -1` walks +Z whichever way the body faces.

## Inspecting a portal

Every verb below runs headless as well as windowed, except where noted.

| Verb | Shows |
|---|---|
| `world.portals` | Each portal face: its destination, durability, travel, arrival mode, counterpart and which seats stand latched inside its band (`inside=`). `world.portals instance:6~global6~garden` reads the garden's frames. |
| `world.destinations` | Each destinations row, its reference, the faces that consume it, and the resolver's live state: the instance it resolved to, its generation and its occupancy. Add `instance:<name>` for the garden's own rows. |
| `world.references`, `world.admission` | The authored references and the admission rows that let a traveller arrive and a window observe. |
| `world.instance.status`, `world.instance.seats` | The running worlds (`boot` and the garden, `6~global6~garden`) and which world holds each seat: after a crossing the seat moves to the garden. |
| `body.where` | The seat's body pose; after a crossing it names the world it is in (`instance:6~global6~garden`). |
| `world.faces` | Each derived face: its screen index, the session it shows, its generation, lease and projection. Headless boots report the glass unbound. |
| `world.nesting` | The nesting depth, and, in a presenting boot, each world a seat is presented in and each session view at every depth with what each of its screens shows. |
| `world.screens` | In a presenting boot, each face's source and the mapping it publishes. |
| `world.view.panes <x> <y>` | In a presenting boot, the walk from a display point through each window into the world it lands on. |

Crossings narrate on stdout as they happen: `world.portal: 'boot' portal/glass
seat 1 entered -> queued transfer…` and `world.transfer: … departed -> … arrived`.
