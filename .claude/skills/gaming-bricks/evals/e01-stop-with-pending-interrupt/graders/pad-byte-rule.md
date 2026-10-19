---
type: llm
weight: 1
---

The response states that STOP consumes its pad byte (PC+2) only when no interrupt is pending, tested as IE & IF independent of IME, and leaves the byte when an interrupt is already latched.
