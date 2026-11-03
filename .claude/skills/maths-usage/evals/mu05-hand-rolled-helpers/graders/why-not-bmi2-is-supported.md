---
type: llm
weight: 1
---

The response says `Bmi2.IsSupported` is not a safe gate for the fast path because PDEP/PEXT run in slow microcode on some AMD parts (before Zen 3, Hygon Zen 1).

PASS if it says PDEP/PEXT are slow (microcoded) on some AMD CPUs, so an `IsSupported` check alone can pick the slower path, and the Puck helper gates on a different property. Example: "`Bmi2.IsSupported` is true on AMD parts before Zen 3 where PDEP is microcoded and slow, so use `HasFastParallelBits`."

FAIL if it recommends `Bmi2.IsSupported` as the gate, or gives no reason against it. Example: "Keep the `Bmi2.X64.IsSupported` branch; it is the standard check."
