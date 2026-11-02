---
type: llm
weight: 1
---

The response says a co-simulation that short compares little more than the boot ROM's logo scroll, which every ROM shares, so a clean result says nothing about the ROM itself.

PASS example: "30 frames is too short: the boot logo scroll dominates a run under about 60 frames, so a clean result means little."

FAIL if it accepts the 30-frame result as evidence about the ROM, or objects to the run only for other reasons. Example: "No divergence over 30 frames is a good sign; run a few more ROMs to be sure."
