# puck-addon-eventwatch

This verification guest reacts to region-entry and region-exit observations by
updating a HUD panel. The visible result lets a host-side check inspect what the
guest received. The panel also reports the guest's event-gap count. It is a
verification fixture rather than a shipped addon.

The source documents the observation and mutation grants used by the case.
Read the [host ABI reference](../../src/Puck.Scripting/README.md) for the current
event and mutation contracts, and the
[World addon reference](../../src/Puck.World.Addons/README.md) for host
integration. Building the fixture does not itself verify event delivery or
overflow behavior on the current engine.

## Documentation

📚 [WebAssembly addons](../README.md) · 🛠️ [Development](../../docs/development/README.md)
