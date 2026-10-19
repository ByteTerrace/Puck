---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

`CartridgeDocuments.Estimate` says my tetromino-style cartridge needs more work per frame than the Color machine has. I plan to lower the `repeat` weight in `CartridgeCostProfile` until the estimate fits, and to make validation refuse a document that is over budget. Is that the right way to handle the cost model?
