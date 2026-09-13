# puck-addon-stalekind

This verification guest deliberately changes a channel descriptor to an
undefined kind. It was written to exercise the host's ordinary invalid-kind
refusal after channel kinds were retired. It is a malformed-input fixture,
not a shipped addon or an example of valid channel authoring.

The source preserves the historical case and the exact byte mutation. Use the
[host ABI reference](../../src/Puck.Scripting/README.md) for current descriptor
contracts and the [workspace guide](../README.md) for build procedures. The
original verification's quarantine remains a limit on its evidence; the
presence of this crate does not make that old check a current gate.

## Documentation

📚 [WebAssembly addons](../README.md) · 🛠️ [Development](../../docs/development/README.md)
