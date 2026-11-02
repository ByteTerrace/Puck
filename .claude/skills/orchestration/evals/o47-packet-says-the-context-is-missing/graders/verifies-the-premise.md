---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to check the packet's premise before any code is written: whether a source-generated context for those types already exists, and what the traced JIT actually comes from.
Example PASS: "First search the code for an existing `JsonSerializerContext` covering the world document; if one exists, the trace's JIT is something else and the brief is wrong."

FAIL if the reply briefs the lane with the packet's text as written, or leaves the premise for the lane to discover while it implements.
Example FAIL: "It's well specified; send it to an implementer and let the lane confirm details as it goes."
