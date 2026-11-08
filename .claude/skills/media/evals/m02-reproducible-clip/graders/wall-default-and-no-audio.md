---
type: llm
weight: 1
---

The response explains why the plan is not reproducible and what the deterministic clock costs.

PASS if it says the default document stamps from the wall clock, so frame timing varies between runs, and that the deterministic option stamps frames from the simulation tick clock but does not allow audio rows, so the microphone narration must be dropped or recorded separately. Example: "The default `Wall` clock stamps at consume time, so two runs differ; set the clock to `Sim` for tick-stamped frames, but the validator refuses audio rows under `Sim`, so narrate in a separate pass."

FAIL if it says the plan as given is reproducible, or proposes the deterministic clock while keeping the microphone row. Example: "Switch to the sim clock and keep the mic row; the timestamps will line up."
