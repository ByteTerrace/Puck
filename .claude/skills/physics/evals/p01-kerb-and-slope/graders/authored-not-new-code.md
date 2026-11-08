---
type: llm
weight: 1
---

The response makes the fix authored kit data over the existing step-up and snap kernel, not new body code.

PASS if it says both behaviours already exist as the kit's motion settings (a step height, zero by default, and a ground-snap reach) executed by the existing step-up and snap kernel through the body's own contact sweep, so the change is setting those values in the Moth's kit or the standard's document rather than writing a new routine or raycast in `WorldBody`. Example: "Set `motion.stepHeight` to about 0.25 m in her kit and check `motion.groundSnap`; `FixedStepUp.TryStep` and `TrySnap` already run through the same sweep. No new C#."

FAIL if it proposes writing the step-up retry or the slope raycast as new code in `WorldBody` or another host type. Example: "Add a `TryStepUp` method to `WorldBody` that lifts the capsule by 0.2 m and re-runs the sweep, and cast a ray down each tick to clamp her to the ground."
