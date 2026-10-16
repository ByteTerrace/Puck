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
| Default (Smoke+Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` |
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --filter-trait tier=Smoke` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --explicit on --filter-trait tier=Deep` |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --explicit on --filter-trait tier=Exhaustive` |

## Smoke

- law cases executed: 22
- last run: 2026-10-16

## Default

- law cases executed: 707
- last run: 2026-10-16

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
- last run: 2026-10-16

## Legs

| leg kind | legs |
| --- | --- |
| classical | 926 |
| in-tree-independent | 34 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 21 |
| shared-substrate:shared-upstream | 23 |
| shared-substrate:transcription | 28 |
| structural | 1250 |
| **total** | **2473** |

- statements: 868
- statements with no independent leg: 224
- last run: 2026-10-16

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10679 |
| binary-field | 256 | 512 |
| binary-field-axioms | 256 | 512 |
| binary-field-group | 256 | 607 |
| binary-polynomial | 256 | 610 |
| binary-polynomial-division | 256 | 610 |
| binary-polynomial-gcd | 256 | 610 |
| clifford-motor | 512 | 429 |
| clifford-multivector | 512 | 429 |
| clifford-planar-complex | 512 | 429 |
| clifford-planar-dual | 512 | 429 |
| clifford-planar-split | 512 | 429 |
| clifford-quaternion-even | 512 | 429 |
| clifford-reverse | 512 | 429 |
| closed-unit | 512 | 663 |
| complex | 512 | 10680 |
| complex-direction | 512 | 557 |
| complex-divide | 512 | 670 |
| complex-rotate | 512 | 670 |
| contribution-fold-analog | 512 | 348 |
| contribution-fold-formula | 512 | 348 |
| contribution-fold-no-pool | 512 | 348 |
| contribution-fold-order | 512 | 348 |
| contribution-fold-quantization | 512 | 348 |
| core-word-modular | 512 | 54 |
| cost-bound-arithmetic | 512 | 95 |
| cost-model-budgets | 512 | 95 |
| cost-model-conversions | 512 | 95 |
| directed-magnitude | 512 | 280 |
| directed-product | 512 | 280 |
| directed-product-sum | 512 | 280 |
| directed-quotient | 512 | 280 |
| directed-root | 512 | 280 |
| dual | 512 | 8447 |
| dual-divide | 512 | 557 |
| dual-generic | 512 | 557 |
| dual-quaternion | 512 | 670 |
| dynamics | 512 | 165 |
| extension-field | 256 | 596 |
| extension-field-inverse | 256 | 505 |
| extension-field-norm | 256 | 596 |
| extension-field-power | 256 | 505 |
| extension-field-product | 256 | 596 |
| fixed-saturate | 512 | 95 |
| integer-bit-align | 256 | 69 |
| integer-bit-morton | 128 | 69 |
| integer-bit-scatter | 128 | 69 |
| integer-bit-smear | 256 | 69 |
| integer-hexagonal-index | 512 | 134 |
| integer-magic-constants | 512 | 126 |
| integer-try-arithmetic | 512 | 59 |
| integer-try-multiplication | 512 | 54 |
| interval-arc-functions | 512 | 39 |
| interval-arctangent | 512 | 39 |
| interval-arithmetic | 512 | 39 |
| interval-circular | 512 | 39 |
| interval-isotonic | 512 | 39 |
| interval-power | 512 | 39 |
| mass-box | 256 | 280 |
| mass-capsule | 256 | 280 |
| mass-compound | 256 | 280 |
| mass-cylinder | 256 | 280 |
| mass-parallel-axis | 256 | 280 |
| mass-sphere | 256 | 280 |
| mass-volume | 256 | 280 |
| meet-associative | 512 | 339 |
| meet-bottom-absorption | 512 | 339 |
| meet-commutative | 512 | 339 |
| meet-idempotent | 512 | 339 |
| meet-monotonicity | 512 | 339 |
| meet-order-coherence | 512 | 339 |
| meet-product-composition | 512 | 339 |
| meet-top-identity | 512 | 339 |
| mixed-scale | 512 | 280 |
| mixed-scale-triple | 512 | 280 |
| mobius | 512 | 8447 |
| monogenic-exact | 512 | 429 |
| monogenic-fusion | 512 | 429 |
| position | 512 | 544 |
| position-delta | 512 | 653 |
| position-translate | 512 | 653 |
| presented | 512 | 951 |
| prime-exploration-primality | 512 | 35 |
| prime-field | 256 | 601 |
| prime-field-chain | 256 | 601 |
| prime-field-lucas | 256 | 601 |
| prime-field-primality | 256 | 601 |
| prime-field-root | 256 | 601 |
| q1648-scalar | 512 | 330 |
| q1648-scalar-division | 512 | 328 |
| q3232-scalar | 512 | 293 |
| q3232-scalar-division | 512 | 292 |
| quaternion | 512 | 670 |
| quaternion-antiparallel | 512 | 44 |
| quaternion-arc | 512 | 44 |
| quaternion-direction | 512 | 557 |
| quaternion-rotate | 512 | 557 |
| quaternion-sublattice | 256 | 557 |
| rate | 512 | 654 |
| rigid | 512 | 693 |
| rigid-direction | 512 | 568 |
| rigid-exp-series | 512 | 42 |
| rigid-log-series | 512 | 42 |
| rigid-point | 512 | 693 |
| scalar | 512 | 8451 |
| scalar-division | 512 | 688 |
| scalar-smoothstep | 512 | 46 |
| scalar-text | 512 | 688 |
| scalar-transcendental | 512 | 690 |
| smoke | 64 | 2177 |
| split | 512 | 10680 |
| split-divide | 512 | 670 |
| split-transform | 512 | 670 |
| square-grid | 512 | 123 |
| sublattice | 256 | 4607 |
| symmetric-apply2 | 512 | 280 |
| symmetric-apply3 | 512 | 280 |
| symmetric-invert2 | 256 | 323 |
| symmetric-invert3 | 256 | 323 |
| symmetric-solve2 | 512 | 323 |
| symmetric-solve3 | 512 | 324 |
| unit-fraction16 | 512 | 577 |
| unit-fraction32 | 512 | 695 |
| unsigned-scalar | 512 | 681 |
| vector | 512 | 665 |
| vector-compare-length | 512 | 40 |
| vector-componentwise-helpers | 512 | 59 |
| vector-direction | 512 | 665 |
| vector-lattice | 512 | 553 |
| vector-narrow | 512 | 665 |
| vector-norm | 512 | 553 |
| vector-orthonormal-basis | 512 | 216 |
| vector-ray-plane | 512 | 57 |
| vector-within | 512 | 45 |

- last run: 2026-10-16
