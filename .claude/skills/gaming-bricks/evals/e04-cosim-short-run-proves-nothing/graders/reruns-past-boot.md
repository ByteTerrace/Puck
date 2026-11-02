---
type: llm
weight: 1
---

The response reruns the comparison long enough to get past the boot sequence into the ROM's own code: at least 120 frames.

PASS if it gives a frame count of 120 or more, or says "at least 120 frames". Example: "Redo the co-simulation with `--frames 120` or more."

FAIL if it keeps the run under 120 frames, says the frame count does not matter, or gives no rerun length. Example: "Rerun with `--frames 60`."
