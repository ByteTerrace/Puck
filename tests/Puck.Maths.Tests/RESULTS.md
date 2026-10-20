# Puck.Maths.Tests — RESULTS

Machine-written by the assembly ledger at run end. Each block records the last run that exercised it — a tier
block only when that tier ran law cases, coverage only when the ratchet gate ran, the frontier only when a run
consumed domains AND every law it ran passed — so every other block keeps the text its own last run left. The
last-run dates are the only volatile content; they do not by themselves trigger a rewrite.

Every figure below is MACHINE-INDEPENDENT by construction: the same commit produces the same counts and the same
frontier indices on every machine, so a difference here is a real difference and never a difference of hardware.
No duration is recorded, deliberately. One here would carry no machine identity, would span the whole session
rather than the block it sits under, and would be taken without a busy-machine guard — so it could not answer
any question asked of it. Cost is measured outside the suite, by `puck bench`.

## Invocations

| tier | command |
| --- | --- |
| Default | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --explicit on --filter-trait tier=Deep` |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --explicit on --filter-trait tier=Exhaustive` |

## Default

- law cases executed: 718
- last run: 2026-10-20

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2519
- waived: 106
- uncovered: 634
- total public members: 3259
- last run: 2026-10-20

## Legs

| leg kind | legs |
| --- | --- |
| classical | 887 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 15 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 33 |
| shared-substrate:intra-presented | 80 |
| shared-substrate:shared-exact-kernel | 24 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 32 |
| structural | 1226 |
| **total** | **2407** |

- statements: 847
- statements with no independent leg: 229
- last run: 2026-10-20

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10686 |
| angle-wrap | 512 | 3 |
| binary-field | 256 | 519 |
| binary-field-axioms | 256 | 519 |
| binary-field-group | 256 | 614 |
| binary-polynomial | 256 | 617 |
| binary-polynomial-division | 256 | 617 |
| binary-polynomial-gcd | 256 | 617 |
| bit-mix-64 | 512 | 3 |
| clifford-motor | 512 | 436 |
| clifford-multivector | 512 | 436 |
| clifford-planar-complex | 512 | 436 |
| clifford-planar-dual | 512 | 436 |
| clifford-planar-split | 512 | 436 |
| clifford-quaternion-even | 512 | 436 |
| clifford-reverse | 512 | 436 |
| closed-unit | 512 | 670 |
| complex | 512 | 10687 |
| complex-direction | 512 | 564 |
| complex-divide | 512 | 677 |
| complex-rotate | 512 | 677 |
| contribution-fold-analog | 512 | 355 |
| contribution-fold-formula | 512 | 355 |
| contribution-fold-no-pool | 512 | 355 |
| contribution-fold-order | 512 | 355 |
| contribution-fold-quantization | 512 | 355 |
| core-word-modular | 512 | 61 |
| cost-bound-arithmetic | 512 | 102 |
| cost-model-budgets | 512 | 102 |
| cost-model-conversions | 512 | 102 |
| degree-conversion | 512 | 3 |
| directed-magnitude | 512 | 287 |
| directed-product | 512 | 287 |
| directed-product-sum | 512 | 287 |
| directed-quotient | 512 | 287 |
| directed-root | 512 | 287 |
| double-seams | 512 | 3 |
| dual | 512 | 8454 |
| dual-divide | 512 | 564 |
| dual-generic | 512 | 564 |
| dual-quaternion | 512 | 677 |
| dynamics | 512 | 172 |
| extension-field | 256 | 603 |
| extension-field-inverse | 256 | 512 |
| extension-field-norm | 256 | 603 |
| extension-field-power | 256 | 512 |
| extension-field-product | 256 | 603 |
| fixed-saturate | 512 | 102 |
| float-projection | 512 | 3 |
| follower-lanes | 512 | 3 |
| integer-bit-align | 256 | 76 |
| integer-bit-morton | 128 | 76 |
| integer-bit-scatter | 128 | 76 |
| integer-bit-smear | 256 | 76 |
| integer-hexagonal-index | 512 | 141 |
| integer-magic-constants | 512 | 133 |
| integer-try-arithmetic | 512 | 66 |
| integer-try-multiplication | 512 | 61 |
| interval-arc-functions | 512 | 46 |
| interval-arctangent | 512 | 46 |
| interval-arithmetic | 512 | 46 |
| interval-circular | 512 | 46 |
| interval-isotonic | 512 | 46 |
| interval-power | 512 | 46 |
| mass-box | 256 | 287 |
| mass-capsule | 256 | 287 |
| mass-compound | 256 | 287 |
| mass-cylinder | 256 | 287 |
| mass-parallel-axis | 256 | 287 |
| mass-sphere | 256 | 287 |
| mass-volume | 256 | 287 |
| meet-associative | 512 | 346 |
| meet-bottom-absorption | 512 | 346 |
| meet-commutative | 512 | 346 |
| meet-idempotent | 512 | 346 |
| meet-monotonicity | 512 | 346 |
| meet-order-coherence | 512 | 346 |
| meet-product-composition | 512 | 346 |
| meet-top-identity | 512 | 346 |
| mixed-scale | 512 | 287 |
| mixed-scale-triple | 512 | 287 |
| mobius | 512 | 8454 |
| monogenic-exact | 512 | 436 |
| monogenic-fusion | 512 | 436 |
| plane-doors-and-clamp | 512 | 3 |
| position | 512 | 551 |
| position-delta | 512 | 660 |
| position-translate | 512 | 660 |
| presented | 512 | 958 |
| prime-exploration-primality | 512 | 42 |
| prime-field | 256 | 608 |
| prime-field-chain | 256 | 608 |
| prime-field-lucas | 256 | 608 |
| prime-field-primality | 256 | 608 |
| prime-field-root | 256 | 608 |
| q1648-scalar | 512 | 335 |
| q1648-scalar-division | 512 | 333 |
| q3232-scalar | 512 | 300 |
| q3232-scalar-division | 512 | 299 |
| quaternion | 512 | 677 |
| quaternion-antiparallel | 512 | 51 |
| quaternion-arc | 512 | 51 |
| quaternion-direction | 512 | 564 |
| quaternion-rotate | 512 | 564 |
| quaternion-sublattice | 256 | 564 |
| rate | 512 | 661 |
| rational-doors | 512 | 3 |
| rational-rate | 512 | 3 |
| rigid | 512 | 700 |
| rigid-direction | 512 | 575 |
| rigid-exp-series | 512 | 49 |
| rigid-log-series | 512 | 49 |
| rigid-point | 512 | 700 |
| saturate-multiply | 512 | 3 |
| saturating-integers | 512 | 3 |
| scalar | 512 | 8458 |
| scalar-division | 512 | 695 |
| scalar-smoothstep | 512 | 53 |
| scalar-text | 512 | 695 |
| scalar-transcendental | 512 | 697 |
| scatter-point | 512 | 3 |
| seconds-of-ticks | 512 | 3 |
| signed-quotients | 512 | 3 |
| smoke | 64 | 2178 |
| split | 512 | 10687 |
| split-divide | 512 | 677 |
| split-transform | 512 | 677 |
| square-grid | 512 | 130 |
| sublattice | 256 | 4614 |
| symmetric-apply2 | 512 | 287 |
| symmetric-apply3 | 512 | 287 |
| symmetric-invert2 | 256 | 330 |
| symmetric-invert3 | 256 | 330 |
| symmetric-solve2 | 512 | 330 |
| symmetric-solve3 | 512 | 331 |
| unit-fraction16 | 512 | 584 |
| unit-fraction32 | 512 | 702 |
| unsigned-scalar | 512 | 688 |
| vector | 512 | 672 |
| vector-compare-length | 512 | 47 |
| vector-componentwise-helpers | 512 | 66 |
| vector-cone | 512 | 3 |
| vector-direction | 512 | 672 |
| vector-lattice | 512 | 560 |
| vector-narrow | 512 | 672 |
| vector-norm | 512 | 560 |
| vector-orthonormal-basis | 512 | 223 |
| vector-ray-plane | 512 | 64 |
| vector-reject | 512 | 3 |
| vector-rescale | 512 | 3 |
| vector-try-arithmetic | 512 | 3 |
| vector-within | 512 | 49 |
| vector-within-exact | 512 | 3 |
| vector-yaw | 512 | 3 |
| yaw-pitch-roll | 512 | 3 |
| zigzag | 512 | 3 |

- last run: 2026-10-20
