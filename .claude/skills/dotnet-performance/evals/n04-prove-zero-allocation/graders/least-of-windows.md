---
type: llm
weight: 1
---

The response says `AllocationWindow.Least` runs the body in several windows and passes when some window allocates nothing, and fails (naming the sampled types) when every window allocated.

PASS if it says the assertion takes the least over repeated windows (up to 16), so a stray one-off allocation does not fail it but a real per-tick allocation fails every window. Example: "`Least` takes the least of up to 16 windows; if every window allocated it throws and names the sampled types."

FAIL if it asserts on a single run's allocated bytes, or does not say how a stray one-off allocation is kept from failing the law. Example: "Read the allocated bytes once before and once after the tick and assert zero."
