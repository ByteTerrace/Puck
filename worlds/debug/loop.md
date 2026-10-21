# Loop shaft

[loop.puck](loop.puck) stands a portal in the floor and its counterpart in the
ceiling directly above it, both in the debug world itself. A body that steps
onto the floor glass drops out of the ceiling glass still falling, falls the six
metres back to the floor glass, and goes through it again, forever. It tests
portals within one world: the destination `loop` references the debug world's
own document, so it resolves to the running World (`world.destinations` reads
`active=global:gen0@boot`), and each crossing moves the body in place rather
than transferring it to another world.

| Where | What |
|---|---|
| (-20, 0, -20) | `loop-floor`, the floor glass, facing up |
| (-20, 6, -20) | `loop-ceiling`, the ceiling glass, facing down, turned a half about up so a crossing keeps the body's heading |
| Corners at ±1.4 m | `loop-posts`, four posts, not solid |

The shaft stands south-west of the spawn, clear of the portal frame at
(3, 0, -8).

## Get in

Issue each line through the console or the World's MCP attachment.

- Drop in from above: `body.pose -20 3 -20 0 0 0`. The body falls onto the
  floor glass, comes out of the ceiling and keeps falling.
- Walk on: `body.pose -20 - -17.5 0 0 0`, then `body.fly 1 0 0 0 0 0 0.5`. The
  ground under the floor glass holds nothing back while the body stands in the
  glass's opening, so the body drops through as it steps on, comes out of the
  ceiling still drifting forward, slows in the air over the shaft and settles
  into the loop. A held walk carries it out past the posts on its first fall,
  onto the ground beside the shaft.

A door fires once the body's origin, at its feet, passes through the glass, so
each crossing sets the body down just inside the shaft below the ceiling glass,
as far through it as its feet had gone through the floor glass.

## What to watch

- Each crossing narrates on stderr:
  `[world.transfer: transfer=24 'boot' seat 1 crossed loop-floor/glass -> loop-ceiling/glass in place, at (-20.00, 5.81, -19.93) moving (0.00, -40.00, 0.00) m/s]`.
  The fall speeds up on each lap until it holds at the Moth's 40 m/s sink
  speed (its `fall` hold's `envelope.sinkSpeed`); from there the crossings
  alternate between two arrival heights, because six metres is not a whole
  number of 1.33 m steps.
- `body.where 0` gives the pose and `vel=(x, y, z)`, the authoritative velocity
  in metres per second; between crossings `y` stays negative and never passes
  -40.
- `world.portals` lists both faces; `inside=1` on `loop-floor/glass` means the
  seat stands latched just through its glass.
- `world.destinations` shows `loop` resolved to `boot`, the World itself.

The floor glass shows the shaft as seen from above the ceiling glass, so the
shaft repeats down through it to `views.nestingDepth` levels, the deepest
showing the fallback magenta.

## Stop the loop

Press Backspace (or L3 on a pad) to respawn at the stage spawn. From the
console, pose the body out of the shaft: `body.pose spawn:stage` returns it to the
origin, or `body.pose -24 0 -20 0 0 0` stands it beside the shaft. Flying out
sideways (`body.fly 0 1 0 0 0 0 1`) also works: a crossing keeps planar
velocity, so the body leaves the glass's footprint.
