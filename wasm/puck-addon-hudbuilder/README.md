# puck-addon-hudbuilder

This verification guest exercises addon-driven HUD mutations. Its main variant
requests a mutation handle, creates a panel, and chains an element update after
the first operation is applied. Other variants provoke specific refusals and
report the verdict observed by the guest. It is a test fixture and is not shipped
as the default addon.

The crate requires one of its feature-selected variants; see
[Cargo.toml](Cargo.toml) and the module comments for each case. The
[host ABI reference](../../src/Puck.Scripting/README.md) owns the mutation
protocol, and the [World addon reference](../../src/Puck.World.Addons/README.md)
explains the host integration. The fixture's historical proof claims remain
scoped to their recorded runs.

## Documentation

📚 [WebAssembly addons](../README.md) · 🛠️ [Development](../../docs/development/README.md)
