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
want `world.reload` to recompile your edits to the source. It opens on the
Moth basis's fixed three-quarter camera, which faces the avatar from the -Z
side and so has the frame behind it. Press 5, or run `view.override layout
play`, for the chase camera: it follows the avatar from behind, shows the frame
ahead and to the right with the far frame inside it, and follows the avatar
through every crossing. `--control` opens the
Operator attachment an MCP client drives
([setup](../../../Puck.Mcp/README.md)). Where several Worlds run, start the
adapter with `--attach <file>`, naming the file the World prints on stderr
(`[world.control: operator attachment <file>]`); without it, the adapter keeps
whichever World it attached to first.

## The layout

| Where | What | Portal |
|---|---|---|
| Debug world, `portal` at (3, 0, -8), facing +Z, ahead and right of the spawn | The gilt frame, glass showing the garden | Walk -Z through it: you arrive in the garden at the same point, still heading -Z. The pair (`portal`, `arrival`) faces each other, so the crossing maps as the identity. |
| Garden, `arrival` at (3, 0, -8), facing -Z | The frame you came out of, glass showing the debug world | Turn round and walk +Z into it: you arrive back out of the debug frame, walking +Z towards the spawn. |
| Garden, `far` at (5.5, 0, -20), facing +Z | The frame ahead of you, glass showing the debug world turned about | Walk -Z into it: the pair (`far`, `portal`) shares a yaw, so the crossing turns you about and you come out of the debug frame walking +Z towards the spawn, the view turned with you. |
| Garden, `obelisk` at (9, 0, -30) | The landmark | |

The far frame stands on the line the spawn camera looks along through the debug
frame, so the window from the spawn holds the far frame's window back onto the
debug world, two levels deep (`world.nesting` lists `session$24`, then
`session$24$24` and `session$24$25`, then the third level). The debug world
and the garden are each resident once, however many windows show them: the
debug world seen through the far frame is a view of the World's own residency
(`world.nesting` says `via world`), and every window onto the garden a view of
the garden's scene (`via endpoint:6~global6~garden`), so `world.counters` shows
the two residencies `world` and `routed$6~global6~garden`. Each window renders
at the power-of-two share of the screen its glass covers, as clear glass
sampled linearly (the frames author `filter: linear`), without shadows. The
axis lines, the reference cubes and the test room stand behind the spawn on +Z
and -X, out of the frames' way.

## Where a crossing lands

A door fires on the edge of its band, a slab one band depth thick in front of
the glass (the frame's half-depth, or the distance one step of the fastest
authored travel covers, whichever is larger). The crossing maps the traveller
through the isometry between the two frames, which carries the distance from the
face through unchanged, so a traveller who fired the band lands that far behind
the counterpart's face, still walking out through it. That is why the crossings
arrive at z -6.7 in the garden, behind the garden's `arrival` frame at z -8, and
at z -9.3 behind the debug frame on the way back: the traveller is placed where
it would have been had the glass been a plain opening, so the view through the
glass and the view after the crossing agree.

The landing never re-fires a door. The band is one-sided, so the landing behind
the counterpart is outside its band, and walking on out through the counterpart's
glass moves against nothing it triggers on. The arrival also latches the
traveller as already inside any band it lands in, so a mapped pair that sets the
traveller down on its counterpart's threshold does not bounce it back. Only
turning round and walking back into the counterpart's front fires the way back.

## What to try

The crossings below work in every boot shape, headless included; a presenting
boot draws each of them at the display rate on the RTX 2060, Direct3D 12 and
Vulkan alike, with the chase camera on (`view.override layout play`), and a
body that crosses keeps its baked mesh in the world it returns to. A crossing
still costs a frame 70 to 140 ms on the RTX 2060 (the first crossing of a boot
the most), and a window's growth across an octave on the approach 15 to 35 ms,
while the render graph rebuilds the views whose reads or extent moved ([open
item](../../../../docs/plans/open-items.md#rendering)).

- Walk forward from the spawn and keep walking (`body.pose 3 - 0 0 0 0`, then
  `body.fly 1 0 0 0 0 0 4`): the debug frame's band mints a transfer, and you
  arrive in the garden at the same point. A `body.fly` tape belongs to the
  instance that holds the body, so it stops at the crossing; issue it again in
  the garden to walk on (a held key or stick keeps going). Walk on through the far frame
  (`body.pose 5.5 - -12 0 0 0`, `body.fly 1 0 0 0 0 0 5`) and you come back out
  of the debug frame turned about.
- From inside the garden, stand past the arrival frame and walk +Z into it
  (`body.pose 3 - -12 180 0 0`, `body.fly -1 0 0 0 0 0 2`): you come back out of
  the debug frame walking +Z.
- Run `world.row.set views nestingDepth 1`: the depth changes live, with no
  boot, `world.nesting` names no level two deep, and the garden's frames show
  their fallback magenta. `world.row.set views nestingDepth 3` restores it; a
  value past 8 is refused.
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
