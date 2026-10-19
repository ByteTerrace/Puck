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

- law cases executed: 708
- last run: 2026-10-19

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2525
- waived: 106
- uncovered: 634
- total public members: 3265
- last run: 2026-10-19

## Legs

| leg kind | legs |
| --- | --- |
| classical | 878 |
| in-tree-independent | 34 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 33 |
| shared-substrate:intra-presented | 80 |
| shared-substrate:shared-exact-kernel | 21 |
| shared-substrate:shared-upstream | 23 |
| shared-substrate:transcription | 27 |
| structural | 1233 |
| **total** | **2402** |

- statements: 826
- statements with no independent leg: 223
- last run: 2026-10-19

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10683 |
| binary-field | 256 | 516 |
| binary-field-axioms | 256 | 516 |
| binary-field-group | 256 | 611 |
| binary-polynomial | 256 | 614 |
| binary-polynomial-division | 256 | 614 |
| binary-polynomial-gcd | 256 | 614 |
| clifford-motor | 512 | 433 |
| clifford-multivector | 512 | 433 |
| clifford-planar-complex | 512 | 433 |
| clifford-planar-dual | 512 | 433 |
| clifford-planar-split | 512 | 433 |
| clifford-quaternion-even | 512 | 433 |
| clifford-reverse | 512 | 433 |
| closed-unit | 512 | 667 |
| complex | 512 | 10684 |
| complex-direction | 512 | 561 |
| complex-divide | 512 | 674 |
| complex-rotate | 512 | 674 |
| contribution-fold-analog | 512 | 352 |
| contribution-fold-formula | 512 | 352 |
| contribution-fold-no-pool | 512 | 352 |
| contribution-fold-order | 512 | 352 |
| contribution-fold-quantization | 512 | 352 |
| core-word-modular | 512 | 58 |
| cost-bound-arithmetic | 512 | 99 |
| cost-model-budgets | 512 | 99 |
| cost-model-conversions | 512 | 99 |
| directed-magnitude | 512 | 284 |
| directed-product | 512 | 284 |
| directed-product-sum | 512 | 284 |
| directed-quotient | 512 | 284 |
| directed-root | 512 | 284 |
| dual | 512 | 8451 |
| dual-divide | 512 | 561 |
| dual-generic | 512 | 561 |
| dual-quaternion | 512 | 674 |
| dynamics | 512 | 169 |
| extension-field | 256 | 600 |
| extension-field-inverse | 256 | 509 |
| extension-field-norm | 256 | 600 |
| extension-field-power | 256 | 509 |
| extension-field-product | 256 | 600 |
| fixed-saturate | 512 | 99 |
| integer-bit-align | 256 | 73 |
| integer-bit-morton | 128 | 73 |
| integer-bit-scatter | 128 | 73 |
| integer-bit-smear | 256 | 73 |
| integer-hexagonal-index | 512 | 138 |
| integer-magic-constants | 512 | 130 |
| integer-try-arithmetic | 512 | 63 |
| integer-try-multiplication | 512 | 58 |
| interval-arc-functions | 512 | 43 |
| interval-arctangent | 512 | 43 |
| interval-arithmetic | 512 | 43 |
| interval-circular | 512 | 43 |
| interval-isotonic | 512 | 43 |
| interval-power | 512 | 43 |
| mass-box | 256 | 284 |
| mass-capsule | 256 | 284 |
| mass-compound | 256 | 284 |
| mass-cylinder | 256 | 284 |
| mass-parallel-axis | 256 | 284 |
| mass-sphere | 256 | 284 |
| mass-volume | 256 | 284 |
| meet-associative | 512 | 343 |
| meet-bottom-absorption | 512 | 343 |
| meet-commutative | 512 | 343 |
| meet-idempotent | 512 | 343 |
| meet-monotonicity | 512 | 343 |
| meet-order-coherence | 512 | 343 |
| meet-product-composition | 512 | 343 |
| meet-top-identity | 512 | 343 |
| mixed-scale | 512 | 284 |
| mixed-scale-triple | 512 | 284 |
| mobius | 512 | 8451 |
| monogenic-exact | 512 | 433 |
| monogenic-fusion | 512 | 433 |
| position | 512 | 548 |
| position-delta | 512 | 657 |
| position-translate | 512 | 657 |
| presented | 512 | 955 |
| prime-exploration-primality | 512 | 39 |
| prime-field | 256 | 605 |
| prime-field-chain | 256 | 605 |
| prime-field-lucas | 256 | 605 |
| prime-field-primality | 256 | 605 |
| prime-field-root | 256 | 605 |
| q1648-scalar | 512 | 334 |
| q1648-scalar-division | 512 | 332 |
| q3232-scalar | 512 | 297 |
| q3232-scalar-division | 512 | 296 |
| quaternion | 512 | 674 |
| quaternion-antiparallel | 512 | 48 |
| quaternion-arc | 512 | 48 |
| quaternion-direction | 512 | 561 |
| quaternion-rotate | 512 | 561 |
| quaternion-sublattice | 256 | 561 |
| rate | 512 | 658 |
| rigid | 512 | 697 |
| rigid-direction | 512 | 572 |
| rigid-exp-series | 512 | 46 |
| rigid-log-series | 512 | 46 |
| rigid-point | 512 | 697 |
| scalar | 512 | 8455 |
| scalar-division | 512 | 692 |
| scalar-smoothstep | 512 | 50 |
| scalar-text | 512 | 692 |
| scalar-transcendental | 512 | 694 |
| smoke | 64 | 2178 |
| split | 512 | 10684 |
| split-divide | 512 | 674 |
| split-transform | 512 | 674 |
| square-grid | 512 | 127 |
| sublattice | 256 | 4611 |
| symmetric-apply2 | 512 | 284 |
| symmetric-apply3 | 512 | 284 |
| symmetric-invert2 | 256 | 327 |
| symmetric-invert3 | 256 | 327 |
| symmetric-solve2 | 512 | 327 |
| symmetric-solve3 | 512 | 328 |
| unit-fraction16 | 512 | 581 |
| unit-fraction32 | 512 | 699 |
| unsigned-scalar | 512 | 685 |
| vector | 512 | 669 |
| vector-compare-length | 512 | 44 |
| vector-componentwise-helpers | 512 | 63 |
| vector-direction | 512 | 669 |
| vector-lattice | 512 | 557 |
| vector-narrow | 512 | 669 |
| vector-norm | 512 | 557 |
| vector-orthonormal-basis | 512 | 220 |
| vector-ray-plane | 512 | 61 |
| vector-within | 512 | 49 |

- last run: 2026-10-19
