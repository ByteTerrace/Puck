---
type: llm
weight: 1
---

The response takes highlighting and diagnostics from the engine running in the browser, not from a portal-side grammar or checker.

PASS if it rejects the JavaScript grammar and checker as a second implementation of the language and says the studio gets highlighting (semantic tokens), completion and diagnostics from Puck's own language server running inside the in-browser engine (`Puck.World.Browser`), with the deeper compile and composition check also run by that engine. Example: "Don't write a Lezer grammar: the studio has no grammar of its own. It colours from the language server's semantic tokens and shows its diagnostics, both served by the WebAssembly engine over `Lsp`/`LspIdle`."

FAIL if it endorses writing a grammar, tokenizer or checker in the portal, even as a fallback for speed. Example: "A Lezer grammar is fine for colouring; keep the language server for errors only."
