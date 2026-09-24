# puck-dashboard

This package implements World Studio and the authenticated portal pages. Its
studio loads verified official content and edits a document's `.puck` source
with Puck.World.Browser's own language server, checks, and previews. The
[Puck.Dashboard reference](../../README.md) owns the studio model, page
behavior, setup, and verification workflow.

The local [package scripts](package.json) provide development serving, a
TypeScript and Vite build, and regression tests. Engine-backed tests need the
browser engine and official content artifacts described in the parent
reference; the studio's own tests run over a fixture workspace of their own.

## Documentation

📚 [Dashboard web workspace](../README.md) · 🛠️ [Development](../../../../docs/development/README.md)
