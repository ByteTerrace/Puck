---
type: llm
weight: 1
---

The response says the zero-allocation assertion is a deterministic count and that a timing is never asserted in a law.

PASS if it says to assert an allocation count (zero or a ceiling) and not a timing, or that no law gates a timing. Example: "Assert the allocated-byte count equals zero; don't assert on elapsed time, because no law gates a timing."

FAIL if it asserts or relies on a time threshold for the proof, or says nothing about the assertion being a count. Example: "Add a stopwatch assertion that the tick stays under a millisecond."
