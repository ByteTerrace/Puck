# Tape pruning and inclusion

The SDF instruction stream is ordered: transforms, folds, shapes, field
operations, and material decisions can affect every later instruction. Pruning
is valid only when it preserves that ordered semantics.

## Current applicability

Per-region tape specialization is not useful for ordinary flat room programs;
the bookkeeping and specialized dispatch cost outweigh the saved interpreter
work. It may become useful inside large, multi-segment placed creations where a
small subset of segments influences each tile.

## Exact skip rule

A range can be skipped only when its candidate cannot change the current
accumulator. The proof depends on:

- a conservative spatial bound;
- the range's composition operator;
- its smooth-blend and scoped-field reach;
- whether point or field state escapes the range; and
- the material winner rule.

Union-like ranges are the simplest case. Subtraction, intersection-family
composition, and unscoped field operations often make a range unmaskable. `Xor`
has union-like exterior influence, but its bound still needs the same influence
margin as a union candidate.

## Design guidance

Prefer compact segment metadata produced during program analysis over a second
general-purpose compiler. Keep an unspecialized interpreter path and compare
images bit-for-bit where the contract promises exactness.

Before implementing a pruner, measure instructions evaluated per hit, segment
count per instance, mask density, metadata bandwidth, and specialization cost.
Track any renewed work in [the SDF backlog](../sdf-backlog.md).
