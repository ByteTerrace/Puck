# puck-addon-queryspam

This verification guest deliberately issues more pose queries than its declared
input capacity can accommodate. It exercises the host's query-budget refusal
path, which the default addon's single-query behavior does not reach. It is a
test fixture, not a shipped addon or a recommended application workload.

The source module documents the reduced capacity and query sequence. The
[host ABI reference](../../src/Puck.Scripting/README.md) owns cell layouts,
budgets, and verdicts; the [workspace guide](../README.md) owns build and
generated-source procedures. Building this crate alone does not establish that
an end-to-end refusal check passed on the current engine.

## Documentation

📚 [WebAssembly addons](../README.md) · 🛠️ [Development](../../docs/development/README.md)
