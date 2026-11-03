---
type: llm
weight: 1
---

The response states STOP's pad-byte rule: STOP consumes its pad byte (PC+2) only when no interrupt is pending, tested as `IE & IF` independent of IME, and leaves the byte when an interrupt is already latched.

PASS if it states that rule, in any words, including that the pending test ignores IME. Example: "STOP skips its pad byte only if `IE & IF` is zero, whatever IME says; with an interrupt already pending the byte is left in place."

FAIL if it says it does not know the rule, gives a different rule (for example, always consumes the byte, or tests IME), or only describes the timing bug. Example: "STOP is always two bytes; the fix is in how many cycles it takes."
