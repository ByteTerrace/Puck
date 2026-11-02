---
type: llm
weight: 1
---

The response confirms the cause by observing which JIT tier the kernel's code runs at, before and after the change, rather than inferring it from the latency alone.

PASS if it lists the kernel's tier with the runtime's JIT reporting, such as `DOTNET_JitDisasmSummary=1` (with `DOTNET_JitStdOutFile`) or JIT events in a trace. Example: "In a scratch process, set `DOTNET_JitDisasmSummary=1` to see each hot method's tier, and record first-call cost beside steady state."

FAIL if it gives no way to see the tier and treats the latency numbers as the diagnosis. Example: "It's tier-0 code; add the attribute and re-run `puck bench startup`."
