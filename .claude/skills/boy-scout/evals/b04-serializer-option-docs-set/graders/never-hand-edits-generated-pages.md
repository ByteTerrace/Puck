---
type: llm
weight: 1
---

The response says the generated docfx API pages are not edited by hand: they are build output, brought up to date by regenerating them from the corrected sources.

PASS if it says not to hand-edit the generated pages and to regenerate them (or report them stale until someone regenerates). Example: "`docs/api/_site/` is build output of `puck docs build`. Don't edit it; fixing the XML comments and rebuilding corrects it, and if you can't rebuild in this run, report the pages as stale."

FAIL if it tells the reader to edit the generated pages directly, or treats them like the hand-maintained surfaces. Example: "Update the README, the XML comments and the docfx API pages to describe the new `IndentSize`."
