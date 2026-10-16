# Instance bounds and ownership

Every instance the program packs carries a finite bound where its operations
allow one, and the instance directory says which segments it owns. The bake's
field walk (`mapCore`) skips an instance whose bound the sample lies outside,
the bake's mask selects a group's instances, and the CPU evaluator culls a pure
union instance by the same bound, so a bound decides which work a sample does,
never what the field is.

## Bound requirements

An instance bound must include every operation that can move or expand its
surface:

- authored transforms and repeated copies;
- smooth-composition halos;
- scoped `Onion`, `Dilate`, and `Displace` reach;
- sampled-region extents; and
- any material or shape behavior that changes visibility.

An undersized bound clips the instance at the bound in the bake and in the CPU
queries. An oversized bound is safe but evaluates the instance where it cannot
matter.

## Instance ranges as an ownership partition

The packed instance directory expresses "this instance owns segments `[a, b)`",
so the segment walk attributes each segment to exactly one owner. Two ranges
claiming one instruction therefore leave the loser packing the empty segment
range behind a live bound: its geometry evaluates only where the winner's mask
bit happens to be set, and vanishes wherever the bake's mask selects the loser
alone.

Overlap is refused at the program constructor rather than resolved. The
first-match owner resolve stays in place as defence in depth—it keeps the
packed words a total, deterministic function of any range set that reaches it —
but a deterministic answer to an incoherent declaration is still the wrong
image, so the door never admits one.
