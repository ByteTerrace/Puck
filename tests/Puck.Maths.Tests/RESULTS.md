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

- law cases executed: 723
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
| classical | 890 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 15 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 33 |
| shared-substrate:intra-presented | 80 |
| shared-substrate:shared-exact-kernel | 24 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 31 |
| structural | 1229 |
| **total** | **2412** |

- statements: 852
- statements with no independent leg: 231
- last run: 2026-10-23

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10688 |
| angle-wrap | 512 | 5 |
| binary-field | 256 | 521 |
| binary-field-axioms | 256 | 521 |
| binary-field-group | 256 | 616 |
| binary-polynomial | 256 | 619 |
| binary-polynomial-division | 256 | 619 |
| binary-polynomial-gcd | 256 | 619 |
| bit-mix-64 | 512 | 5 |
| clifford-motor | 512 | 438 |
| clifford-multivector | 512 | 438 |
| clifford-planar-complex | 512 | 438 |
| clifford-planar-dual | 512 | 438 |
| clifford-planar-split | 512 | 438 |
| clifford-quaternion-even | 512 | 438 |
| clifford-reverse | 512 | 438 |
| closed-unit | 512 | 672 |
| complex | 512 | 10689 |
| complex-direction | 512 | 566 |
| complex-divide | 512 | 679 |
| complex-rotate | 512 | 679 |
| contribution-fold-analog | 512 | 357 |
| contribution-fold-formula | 512 | 357 |
| contribution-fold-no-pool | 512 | 357 |
| contribution-fold-order | 512 | 357 |
| contribution-fold-quantization | 512 | 357 |
| core-word-modular | 512 | 63 |
| cost-bound-arithmetic | 512 | 104 |
| cost-model-budgets | 512 | 104 |
| cost-model-conversions | 512 | 104 |
| degree-conversion | 512 | 5 |
| directed-magnitude | 512 | 289 |
| directed-product | 512 | 289 |
| directed-product-sum | 512 | 289 |
| directed-quotient | 512 | 289 |
| directed-root | 512 | 289 |
| double-seams | 512 | 5 |
| dual | 512 | 8456 |
| dual-divide | 512 | 566 |
| dual-generic | 512 | 566 |
| dual-quaternion | 512 | 679 |
| dynamics | 512 | 174 |
| extension-field | 256 | 605 |
| extension-field-inverse | 256 | 514 |
| extension-field-norm | 256 | 605 |
| extension-field-power | 256 | 514 |
| extension-field-product | 256 | 605 |
| fixed-saturate | 512 | 104 |
| float-projection | 512 | 4 |
| follower-lanes | 512 | 5 |
| integer-bit-align | 256 | 78 |
| integer-bit-morton | 128 | 78 |
| integer-bit-scatter | 128 | 78 |
| integer-bit-smear | 256 | 78 |
| integer-hexagonal-index | 512 | 143 |
| integer-magic-constants | 512 | 135 |
| integer-try-arithmetic | 512 | 68 |
| integer-try-multiplication | 512 | 63 |
| interval-arc-functions | 512 | 48 |
| interval-arctangent | 512 | 48 |
| interval-arithmetic | 512 | 48 |
| interval-circular | 512 | 48 |
| interval-isotonic | 512 | 48 |
| interval-power | 512 | 48 |
| mass-box | 256 | 289 |
| mass-capsule | 256 | 289 |
| mass-compound | 256 | 289 |
| mass-cylinder | 256 | 289 |
| mass-parallel-axis | 256 | 289 |
| mass-sphere | 256 | 289 |
| mass-volume | 256 | 289 |
| meet-associative | 512 | 348 |
| meet-bottom-absorption | 512 | 348 |
| meet-commutative | 512 | 348 |
| meet-idempotent | 512 | 348 |
| meet-monotonicity | 512 | 348 |
| meet-order-coherence | 512 | 348 |
| meet-product-composition | 512 | 348 |
| meet-top-identity | 512 | 348 |
| mixed-scale | 512 | 289 |
| mixed-scale-triple | 512 | 289 |
| mobius | 512 | 8456 |
| monogenic-exact | 512 | 438 |
| monogenic-fusion | 512 | 438 |
| plane-doors-and-clamp | 512 | 5 |
| position | 512 | 553 |
| position-delta | 512 | 662 |
| position-translate | 512 | 662 |
| presented | 512 | 960 |
| prime-exploration-primality | 512 | 44 |
| prime-field | 256 | 610 |
| prime-field-chain | 256 | 610 |
| prime-field-lucas | 256 | 610 |
| prime-field-primality | 256 | 610 |
| prime-field-root | 256 | 610 |
| q1648-scalar | 512 | 335 |
| q1648-scalar-division | 512 | 333 |
| q3232-scalar | 512 | 302 |
| q3232-scalar-division | 512 | 301 |
| quaternion | 512 | 679 |
| quaternion-antiparallel | 512 | 53 |
| quaternion-arc | 512 | 53 |
| quaternion-direction | 512 | 566 |
| quaternion-rotate | 512 | 566 |
| quaternion-sublattice | 256 | 566 |
| rate | 512 | 663 |
| rational-doors | 512 | 5 |
| rational-rate | 512 | 5 |
| rigid | 512 | 702 |
| rigid-direction | 512 | 577 |
| rigid-exp-series | 512 | 51 |
| rigid-log-series | 512 | 51 |
| rigid-point | 512 | 702 |
| saturate-multiply | 512 | 5 |
| saturating-integers | 512 | 5 |
| scalar | 512 | 8460 |
| scalar-division | 512 | 697 |
| scalar-smoothstep | 512 | 55 |
| scalar-text | 512 | 697 |
| scalar-transcendental | 512 | 699 |
| scatter-point | 512 | 5 |
| seconds-of-ticks | 512 | 5 |
| signed-quotients | 512 | 5 |
| smoke | 64 | 2178 |
| split | 512 | 10689 |
| split-divide | 512 | 679 |
| split-transform | 512 | 679 |
| square-grid | 512 | 132 |
| sublattice | 256 | 4616 |
| symmetric-apply2 | 512 | 289 |
| symmetric-apply3 | 512 | 289 |
| symmetric-invert2 | 256 | 332 |
| symmetric-invert3 | 256 | 332 |
| symmetric-solve2 | 512 | 332 |
| symmetric-solve3 | 512 | 333 |
| unit-fraction16 | 512 | 586 |
| unit-fraction32 | 512 | 704 |
| unsigned-scalar | 512 | 690 |
| vector | 512 | 674 |
| vector-compare-length | 512 | 49 |
| vector-componentwise-helpers | 512 | 68 |
| vector-cone | 512 | 5 |
| vector-direction | 512 | 674 |
| vector-lattice | 512 | 562 |
| vector-narrow | 512 | 674 |
| vector-norm | 512 | 562 |
| vector-orthonormal-basis | 512 | 225 |
| vector-ray-plane | 512 | 66 |
| vector-reject | 512 | 5 |
| vector-rescale | 512 | 5 |
| vector-try-arithmetic | 512 | 5 |
| vector-within | 512 | 49 |
| vector-within-exact | 512 | 5 |
| vector-yaw | 512 | 5 |
| yaw-pitch-roll | 512 | 5 |
| zigzag | 512 | 5 |

- last run: 2026-10-23
