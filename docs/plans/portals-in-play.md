# Portals in play

Players open doors during play: in combat to send an attack through or move
somewhere clever, and anywhere a game's rules allow
([the ruling](../decisions/engine-design.md#a-portal-is-a-plane-a-body-passes-through)).
A door a player places may pair two points in one world or open onto another
world or zone, and a door may ride something that moves.

## What exists

A door is a portal facet on a placement's face (`WorldPlacementPortal`). The
authority scans every placement's doors each tick (`WorldInstanceHost`'s
`ScanInstancePortals`), so a door a rule installs with `upsertPlacement` works
at once. A pair in one world carries a body through its isometry in place,
swept so nothing tunnels, keeping its state and momentum, drawn on both sides
while it straddles; a scaled pair resizes the traveller. A door onto another
world goes through the destination and federation path.

## What is not known to work yet

- **Placing a door where a player aims.** `upsertPlacement` installs a literal
  placement whose pose is fixed when the world is written; whether a
  placement's pose can follow a state cell (written from an aim's hit point
  and normal) is unchecked.
- **Queries through a door.** A projectile body crosses like any body, but
  `Puck.Physics` names no portal, so rays, hit tests and lines of sight
  probably stop at the glass.
- **A moving door.** Crossing is swept against the body's motion; a door that
  moves sweeps too.
- **A placed door onto another world**, opened mid-play rather than authored.
- **Where a door may open.** No query answers whether a surface admits a door.
- **Cost.** Each visible door is a nested view; a fight with many live doors
  is a rendering and memory load the nested-view quality table must carry.

## Design

To be written by the design lane.
