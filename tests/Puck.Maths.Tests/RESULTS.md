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

- law cases executed: 726
- last run: 2026-10-23

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
- last run: 2026-10-23

## Legs

| leg kind | legs |
| --- | --- |
| classical | 891 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 15 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 33 |
| shared-substrate:intra-presented | 80 |
| shared-substrate:shared-exact-kernel | 24 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 31 |
| structural | 1231 |
| **total** | **2415** |

- statements: 855
- statements with no independent leg: 233
- last run: 2026-10-23

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10689 |
| angle-wrap | 512 | 6 |
| binary-field | 256 | 522 |
| binary-field-axioms | 256 | 522 |
| binary-field-group | 256 | 617 |
| binary-polynomial | 256 | 620 |
| binary-polynomial-division | 256 | 620 |
| binary-polynomial-gcd | 256 | 620 |
| bit-mix-64 | 512 | 6 |
| clifford-motor | 512 | 439 |
| clifford-multivector | 512 | 439 |
| clifford-planar-complex | 512 | 439 |
| clifford-planar-dual | 512 | 439 |
| clifford-planar-split | 512 | 439 |
| clifford-quaternion-even | 512 | 439 |
| clifford-reverse | 512 | 439 |
| closed-unit | 512 | 673 |
| complex | 512 | 10690 |
| complex-direction | 512 | 567 |
| complex-divide | 512 | 680 |
| complex-rotate | 512 | 680 |
| contribution-fold-analog | 512 | 358 |
| contribution-fold-formula | 512 | 358 |
| contribution-fold-no-pool | 512 | 358 |
| contribution-fold-order | 512 | 358 |
| contribution-fold-quantization | 512 | 358 |
| core-word-modular | 512 | 64 |
| cost-bound-arithmetic | 512 | 105 |
| cost-model-budgets | 512 | 105 |
| cost-model-conversions | 512 | 105 |
| degree-conversion | 512 | 6 |
| directed-magnitude | 512 | 290 |
| directed-product | 512 | 290 |
| directed-product-sum | 512 | 290 |
| directed-quotient | 512 | 290 |
| directed-root | 512 | 290 |
| double-seams | 512 | 6 |
| dual | 512 | 8457 |
| dual-divide | 512 | 567 |
| dual-generic | 512 | 567 |
| dual-quaternion | 512 | 680 |
| dynamics | 512 | 175 |
| extension-field | 256 | 606 |
| extension-field-inverse | 256 | 515 |
| extension-field-norm | 256 | 606 |
| extension-field-power | 256 | 515 |
| extension-field-product | 256 | 606 |
| fixed-saturate | 512 | 105 |
| follower-lanes | 512 | 6 |
| integer-bit-align | 256 | 79 |
| integer-bit-morton | 128 | 79 |
| integer-bit-scatter | 128 | 79 |
| integer-bit-smear | 256 | 79 |
| integer-hexagonal-index | 512 | 144 |
| integer-magic-constants | 512 | 136 |
| integer-try-arithmetic | 512 | 69 |
| integer-try-multiplication | 512 | 64 |
| interval-arc-functions | 512 | 49 |
| interval-arctangent | 512 | 49 |
| interval-arithmetic | 512 | 49 |
| interval-circular | 512 | 49 |
| interval-isotonic | 512 | 49 |
| interval-power | 512 | 49 |
| mass-box | 256 | 290 |
| mass-capsule | 256 | 290 |
| mass-compound | 256 | 290 |
| mass-cylinder | 256 | 290 |
| mass-parallel-axis | 256 | 290 |
| mass-sphere | 256 | 290 |
| mass-volume | 256 | 290 |
| meet-associative | 512 | 349 |
| meet-bottom-absorption | 512 | 349 |
| meet-commutative | 512 | 349 |
| meet-idempotent | 512 | 349 |
| meet-monotonicity | 512 | 349 |
| meet-order-coherence | 512 | 349 |
| meet-product-composition | 512 | 349 |
| meet-top-identity | 512 | 349 |
| mixed-scale | 512 | 290 |
| mixed-scale-triple | 512 | 290 |
| mobius | 512 | 8457 |
| monogenic-exact | 512 | 439 |
| monogenic-fusion | 512 | 439 |
| plane-doors-and-clamp | 512 | 6 |
| position | 512 | 554 |
| position-delta | 512 | 663 |
| position-translate | 512 | 663 |
| presented | 512 | 961 |
| prime-exploration-primality | 512 | 45 |
| prime-field | 256 | 611 |
| prime-field-chain | 256 | 611 |
| prime-field-lucas | 256 | 611 |
| prime-field-primality | 256 | 611 |
| prime-field-root | 256 | 611 |
| q3232-scalar | 512 | 303 |
| q3232-scalar-division | 512 | 302 |
| quaternion | 512 | 680 |
| quaternion-antiparallel | 512 | 54 |
| quaternion-arc | 512 | 54 |
| quaternion-direction | 512 | 567 |
| quaternion-rotate | 512 | 567 |
| quaternion-sublattice | 256 | 567 |
| rate | 512 | 664 |
| rational-doors | 512 | 6 |
| rational-rate | 512 | 6 |
| rigid | 512 | 703 |
| rigid-direction | 512 | 578 |
| rigid-exp-series | 512 | 52 |
| rigid-log-series | 512 | 52 |
| rigid-point | 512 | 703 |
| saturate-multiply | 512 | 6 |
| saturating-integers | 512 | 6 |
| scalar | 512 | 8461 |
| scalar-division | 512 | 698 |
| scalar-smoothstep | 512 | 56 |
| scalar-text | 512 | 698 |
| scalar-transcendental | 512 | 700 |
| scatter-point | 512 | 6 |
| seconds-of-ticks | 512 | 6 |
| signed-quotients | 512 | 6 |
| split | 512 | 10690 |
| split-divide | 512 | 680 |
| split-transform | 512 | 680 |
| square-grid | 512 | 133 |
| sublattice | 256 | 4617 |
| symmetric-apply2 | 512 | 290 |
| symmetric-apply3 | 512 | 290 |
| symmetric-invert2 | 256 | 333 |
| symmetric-invert3 | 256 | 333 |
| symmetric-solve2 | 512 | 333 |
| symmetric-solve3 | 512 | 334 |
| unit-fraction16 | 512 | 587 |
| unit-fraction32 | 512 | 705 |
| unsigned-scalar | 512 | 691 |
| vector | 512 | 675 |
| vector-compare-length | 512 | 50 |
| vector-componentwise-helpers | 512 | 69 |
| vector-cone | 512 | 6 |
| vector-direction | 512 | 675 |
| vector-lattice | 512 | 563 |
| vector-narrow | 512 | 675 |
| vector-norm | 512 | 563 |
| vector-orthonormal-basis | 512 | 226 |
| vector-ray-plane | 512 | 67 |
| vector-reject | 512 | 6 |
| vector-rescale | 512 | 6 |
| vector-try-arithmetic | 512 | 6 |
| vector-within-exact | 512 | 6 |
| vector-yaw | 512 | 6 |
| yaw-pitch-roll | 512 | 6 |
| zigzag | 512 | 6 |

- last run: 2026-10-23
