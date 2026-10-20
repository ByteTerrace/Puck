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

- law cases executed: 740
- last run: 2026-10-20

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2616
- waived: 106
- uncovered: 634
- total public members: 3356
- last run: 2026-10-20

## Legs

| leg kind | legs |
| --- | --- |
| classical | 911 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 47 |
| shared-substrate:fused-substrate | 33 |
| shared-substrate:intra-presented | 80 |
| shared-substrate:shared-exact-kernel | 24 |
| shared-substrate:shared-upstream | 23 |
| shared-substrate:transcription | 32 |
| structural | 1254 |
| **total** | **2464** |

- statements: 870
- statements with no independent leg: 234
- last run: 2026-10-20

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10684 |
| angle-wrap | 512 | 1 |
| binary-field | 256 | 517 |
| binary-field-axioms | 256 | 517 |
| binary-field-group | 256 | 612 |
| binary-polynomial | 256 | 615 |
| binary-polynomial-division | 256 | 615 |
| binary-polynomial-gcd | 256 | 615 |
| bit-mix-64 | 512 | 1 |
| clifford-motor | 512 | 434 |
| clifford-multivector | 512 | 434 |
| clifford-planar-complex | 512 | 434 |
| clifford-planar-dual | 512 | 434 |
| clifford-planar-split | 512 | 434 |
| clifford-quaternion-even | 512 | 434 |
| clifford-reverse | 512 | 434 |
| closed-unit | 512 | 668 |
| complex | 512 | 10685 |
| complex-direction | 512 | 562 |
| complex-divide | 512 | 675 |
| complex-rotate | 512 | 675 |
| contribution-fold-analog | 512 | 353 |
| contribution-fold-formula | 512 | 353 |
| contribution-fold-no-pool | 512 | 353 |
| contribution-fold-order | 512 | 353 |
| contribution-fold-quantization | 512 | 353 |
| core-word-modular | 512 | 59 |
| cost-bound-arithmetic | 512 | 100 |
| cost-model-budgets | 512 | 100 |
| cost-model-conversions | 512 | 100 |
| degree-conversion | 512 | 1 |
| directed-magnitude | 512 | 285 |
| directed-product | 512 | 285 |
| directed-product-sum | 512 | 285 |
| directed-quotient | 512 | 285 |
| directed-root | 512 | 285 |
| double-seams | 512 | 1 |
| dual | 512 | 8452 |
| dual-divide | 512 | 562 |
| dual-generic | 512 | 562 |
| dual-quaternion | 512 | 675 |
| dynamics | 512 | 170 |
| extension-field | 256 | 601 |
| extension-field-inverse | 256 | 510 |
| extension-field-norm | 256 | 601 |
| extension-field-power | 256 | 510 |
| extension-field-product | 256 | 601 |
| fixed-saturate | 512 | 100 |
| float-projection | 512 | 1 |
| follower-lanes | 512 | 1 |
| integer-bit-align | 256 | 74 |
| integer-bit-morton | 128 | 74 |
| integer-bit-scatter | 128 | 74 |
| integer-bit-smear | 256 | 74 |
| integer-hexagonal-index | 512 | 139 |
| integer-magic-constants | 512 | 131 |
| integer-try-arithmetic | 512 | 64 |
| integer-try-multiplication | 512 | 59 |
| interval-arc-functions | 512 | 44 |
| interval-arctangent | 512 | 44 |
| interval-arithmetic | 512 | 44 |
| interval-circular | 512 | 44 |
| interval-isotonic | 512 | 44 |
| interval-power | 512 | 44 |
| mass-box | 256 | 285 |
| mass-capsule | 256 | 285 |
| mass-compound | 256 | 285 |
| mass-cylinder | 256 | 285 |
| mass-parallel-axis | 256 | 285 |
| mass-sphere | 256 | 285 |
| mass-volume | 256 | 285 |
| meet-associative | 512 | 344 |
| meet-bottom-absorption | 512 | 344 |
| meet-commutative | 512 | 344 |
| meet-idempotent | 512 | 344 |
| meet-monotonicity | 512 | 344 |
| meet-order-coherence | 512 | 344 |
| meet-product-composition | 512 | 344 |
| meet-top-identity | 512 | 344 |
| mixed-scale | 512 | 285 |
| mixed-scale-triple | 512 | 285 |
| mobius | 512 | 8452 |
| monogenic-exact | 512 | 434 |
| monogenic-fusion | 512 | 434 |
| plane-doors-and-clamp | 512 | 1 |
| position | 512 | 549 |
| position-delta | 512 | 658 |
| position-translate | 512 | 658 |
| presented | 512 | 956 |
| prime-exploration-primality | 512 | 40 |
| prime-field | 256 | 606 |
| prime-field-chain | 256 | 606 |
| prime-field-lucas | 256 | 606 |
| prime-field-primality | 256 | 606 |
| prime-field-root | 256 | 606 |
| q1648-scalar | 512 | 335 |
| q1648-scalar-division | 512 | 333 |
| q3232-scalar | 512 | 298 |
| q3232-scalar-division | 512 | 297 |
| quaternion | 512 | 675 |
| quaternion-antiparallel | 512 | 49 |
| quaternion-arc | 512 | 49 |
| quaternion-direction | 512 | 562 |
| quaternion-rotate | 512 | 562 |
| quaternion-sublattice | 256 | 562 |
| rate | 512 | 659 |
| rational-doors | 512 | 1 |
| rational-rate | 512 | 1 |
| rigid | 512 | 698 |
| rigid-direction | 512 | 573 |
| rigid-exp-series | 512 | 47 |
| rigid-log-series | 512 | 47 |
| rigid-point | 512 | 698 |
| saturate-multiply | 512 | 1 |
| saturating-integers | 512 | 1 |
| scalar | 512 | 8456 |
| scalar-division | 512 | 693 |
| scalar-smoothstep | 512 | 51 |
| scalar-text | 512 | 693 |
| scalar-transcendental | 512 | 695 |
| scatter-point | 512 | 1 |
| seconds-of-ticks | 512 | 1 |
| signed-quotients | 512 | 1 |
| smoke | 64 | 2178 |
| split | 512 | 10685 |
| split-divide | 512 | 675 |
| split-transform | 512 | 675 |
| square-grid | 512 | 128 |
| sublattice | 256 | 4612 |
| symmetric-apply2 | 512 | 285 |
| symmetric-apply3 | 512 | 285 |
| symmetric-invert2 | 256 | 328 |
| symmetric-invert3 | 256 | 328 |
| symmetric-solve2 | 512 | 328 |
| symmetric-solve3 | 512 | 329 |
| unit-fraction16 | 512 | 582 |
| unit-fraction32 | 512 | 700 |
| unsigned-scalar | 512 | 686 |
| vector | 512 | 670 |
| vector-compare-length | 512 | 45 |
| vector-componentwise-helpers | 512 | 64 |
| vector-cone | 512 | 1 |
| vector-direction | 512 | 670 |
| vector-lattice | 512 | 558 |
| vector-narrow | 512 | 670 |
| vector-norm | 512 | 558 |
| vector-orthonormal-basis | 512 | 221 |
| vector-ray-plane | 512 | 62 |
| vector-reject | 512 | 1 |
| vector-rescale | 512 | 1 |
| vector-try-arithmetic | 512 | 1 |
| vector-within | 512 | 49 |
| vector-within-exact | 512 | 1 |
| vector-yaw | 512 | 1 |
| yaw-pitch-roll | 512 | 1 |
| zigzag | 512 | 1 |

- last run: 2026-10-20
