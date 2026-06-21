# CLAUDE.md

## `src/Puck/Puck.csproj` is INSPIRATION ONLY

Never reference `Puck.csproj` from any project. All functionality is being split
out into the separate `Puck.*` projects which you can freely use. Read the
old `Puck` and `Puck.Avatars` projects for reference only; build the real thing
in the split projects.

Our main focus right now is putting together a new minimal showcase in Puck.Demo.

## Controller input

The controller-input subsystem (Switch Pro / Xbox Series / PS5 DualSense, all flowing through
`Puck.Commands`) lives in `src/Puck.Input`. See [`src/Puck.Input/README.md`](src/Puck.Input/README.md)
for its architecture, the cross-family feature matrix, hardware-verified status, deferred work,
and debugging notes — it is the handoff doc for picking the work up on another machine.
