---
type: llm
weight: 1
---

The response keeps floating-point conversions (such as `ToVector3`) at the presentation boundary only, and says nothing converted for presentation flows back into simulation state.

PASS if it says the renderer converts the fixed-point position to float for drawing, one way, and that nothing float flows back into simulation state. Example: "Convert to float only when drawing; the float position never feeds back into the simulation."

FAIL if it keeps the position or velocity as float in simulation state, or feeds a rendered or interpolated float back into state. Example: "Integrate in `Vector3` floats and snapshot the float position."
