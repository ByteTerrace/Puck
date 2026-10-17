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

From the spawn the debug frame shows the garden, and inside it the far frame
shows the debug world from the far frame's mapped eye, two levels deep. The
axis lines, the reference cubes and the test room stand behind the spawn on +Z
and -X, out of the frames' way.

## What to try

- Look at the frame from the spawn: the sunset sky and the green floor fill the
  glass, with the far frame and the obelisk inside it, and the far frame's glass
  shows the debug world's blue sky.
- Walk forward through it and keep walking: you cross into the garden, then
  through the far frame back into the debug world, turned about and facing the
  spawn.
- Step sideways in front of the frame: the window reframes as your eye moves,
  as a real window would.
- `world.row.set views.nestingDepth 1` (or edit `nestingDepth` in
  `views` and `world.reload`): the far frame's glass shows its fallback magenta
  instead of the debug world, since a face past the depth shows its session's
  `fallback` colour. Set it back to 3 to restore the nested view.
- Edit [garden.puck](garden.puck), then `world.instance.stop 6~global6~garden`:
  the garden retires, and the next crossing or window resolves a fresh garden
  from the edited source. `world.reload` recompiles only the debug world.

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
