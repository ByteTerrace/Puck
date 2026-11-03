---
type: llm
weight: 1
---

The response says to check that the co-simulation's record count differs from ROM to ROM before believing a clean result.

PASS if it says to compare the record count across two different ROMs, and that equal counts mean the comparison is mostly seeing the shared boot sequence. Example: "Compare the record count between two different ROMs; if they match, the run only saw the boot sequence."

FAIL if it never mentions comparing record counts across ROMs. Example: "No divergence over 30 frames is a good sign; run a few more ROMs to be sure."
