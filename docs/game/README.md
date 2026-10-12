# Reference game

The reference game is Puck's concrete design target: a shared world that makes
the engine's world model useful to play, author, and verify. This section
collects the design requirements, proposed engineering work, and visual
direction. Engine concepts and current contracts remain in the
[manual](../README.md); these pages explain how the reference game uses them.

While the current engine campaign runs, the game's worlds (the island, its
districts, the tabletop games and the asset packages) are quarantined under
[`experimental/`](../../experimental/README.md) at their old repository paths,
and the debug world is the one live world
([decision](../decisions/engine-design.md#one-live-world-while-the-campaign-runs)).
These pages describe the design those worlds carry.

- [Reference game design](design.md)—gameplay requirements and project decisions.

- [Play](../plans/play.md)—the programme that sequences the game's remaining engineering, its waves, the federation remainder, the group finder, and MCP.

- [Art and visual design](art/README.md)—character briefs, concept packages, and visual references.
