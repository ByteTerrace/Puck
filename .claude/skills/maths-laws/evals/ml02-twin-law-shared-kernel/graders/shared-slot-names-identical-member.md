---
type: llm
weight: 1
---

The response says the leg's shared slot names the kernel precisely and states it is the identical member on both sides.

PASS if it says the shared slot names `BinaryFieldKernels.CarrylessMultiply64` (the kernel) and that both sides call that identical member. Example: "The shared slot names `BinaryFieldKernels.CarrylessMultiply64` and says both operators call this identical member."

FAIL if it leaves the shared slot vague ('the multiply kernel'), or does not say the member is the same on both sides. Example: "Mark the leg as shared with a note that the code is similar."
