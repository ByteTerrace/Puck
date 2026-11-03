---
type: llm
weight: 1
---

The response puts `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on the compute kernel that is entered directly, and does not turn tiered compilation off for the process or mark every method.

PASS if it names `AggressiveOptimization` on the hot compute kernel (and may leave a cheap dispatcher above several kernels tiered). Example: "Mark the compute kernel a request enters directly with `AggressiveOptimization`; leave tiering on elsewhere."

FAIL if it sets `TieredCompilation=false` for the process, marks every method or only a dispatcher, or names the attribute without saying where it goes. Example: "Set `TieredCompilation=false` so everything compiles optimized on first call."
