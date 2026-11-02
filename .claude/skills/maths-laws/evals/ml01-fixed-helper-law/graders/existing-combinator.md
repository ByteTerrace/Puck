---
type: llm
weight: 1
---

The response binds the law through an existing `Laws` combinator (named, such as `ScalarBinaryMatchesOracle`, or described as "the existing `Laws.cs` combinators and shared domains") instead of a new hand-written loop or a bare `[Fact]`.

PASS example: "Add exactly one binding in `LawRegistry`, built from the existing `Laws.cs` combinators and shared domains; don't write new loops."

FAIL if it writes its own sampling loop or a standalone `[Fact]`/`[Theory]` as the law. Example: "Write a test that loops over 10,000 random operands and asserts the helper equals the oracle."
