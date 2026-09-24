# puck-addon-stalekind

This verification guest lays out an ordinary channel descriptor table and then
overwrites the `Response` descriptor's kind byte with `4`, a value no
`AddonChannelKind` member carries. The mount must refuse it through the
ordinary undefined-kind refusal `AddonChannelTableReader` gives every
unrecognized byte (`descriptor 2: channel kind 4 is not defined`). It is a
malformed-input fixture, not a shipped addon or an example of valid channel
authoring.

The source module documents the exact byte mutation. Use the
[host ABI reference](../../docs/reference/scripting.md) for current descriptor
contracts and the [workspace guide](../README.md) for build procedures. No
automated gate runs this fixture.

## Documentation

📚 [WebAssembly addons](../README.md) · 🛠️ [Development](../../docs/development/README.md)
