---
type: llm
weight: 1
---

The response puts `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on the compute kernel that is entered directly, and leaves the cheap dispatcher above several kernels tiered so first use compiles only the path taken.

PASS example: "Mark the compute kernel a request enters directly with `AggressiveOptimization`; leave the dispatcher above the kernels tiered."

FAIL if it puts the attribute on the dispatcher or on every method, turns tiered compilation off for the process, or names the attribute without saying where it goes. Example: "Set `TieredCompilation=false` so everything compiles optimized on first call."
