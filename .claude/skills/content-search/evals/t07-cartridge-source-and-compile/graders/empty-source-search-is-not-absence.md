---
type: llm
weight: 1
---

The response says an empty search of the authored sources does not by itself show what the compiled cartridge holds.

PASS if it says no hits in the `.puck` sources does not prove the compiled cartridge lacks the value, and names a reason: a lowered default, a template, `for` or `map` expansion, a different spelling, or an import that was not searched; and it sends the reader to compile and look. Example: "No hits in the sources doesn't prove the cartridge lacks `litColumns`: a template or default can produce it, so compile and search the output."

FAIL if it treats an empty source search as proof about the compiled cartridge in either direction. Example: "If the search comes back empty, `litColumns` isn't used by the cartridge."
