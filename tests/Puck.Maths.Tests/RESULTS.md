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
| algebra-fractional | 512 | 10680 |
| binary-field | 256 | 513 |
| binary-field-axioms | 256 | 513 |
| binary-field-group | 256 | 608 |
| binary-polynomial | 256 | 611 |
| binary-polynomial-division | 256 | 611 |
| binary-polynomial-gcd | 256 | 611 |
| clifford-motor | 512 | 430 |
| clifford-multivector | 512 | 430 |
| clifford-planar-complex | 512 | 430 |
| clifford-planar-dual | 512 | 430 |
| clifford-planar-split | 512 | 430 |
| clifford-quaternion-even | 512 | 430 |
| clifford-reverse | 512 | 430 |
| closed-unit | 512 | 664 |
| complex | 512 | 10681 |
| complex-direction | 512 | 558 |
| complex-divide | 512 | 671 |
| complex-rotate | 512 | 671 |
| contribution-fold-analog | 512 | 349 |
| contribution-fold-formula | 512 | 349 |
| contribution-fold-no-pool | 512 | 349 |
| contribution-fold-order | 512 | 349 |
| contribution-fold-quantization | 512 | 349 |
| core-word-modular | 512 | 55 |
| cost-bound-arithmetic | 512 | 96 |
| cost-model-budgets | 512 | 96 |
| cost-model-conversions | 512 | 96 |
| directed-magnitude | 512 | 281 |
| directed-product | 512 | 281 |
| directed-product-sum | 512 | 281 |
| directed-quotient | 512 | 281 |
| directed-root | 512 | 281 |
| dual | 512 | 8448 |
| dual-divide | 512 | 558 |
| dual-generic | 512 | 558 |
| dual-quaternion | 512 | 671 |
| dynamics | 512 | 166 |
| extension-field | 256 | 597 |
| extension-field-inverse | 256 | 506 |
| extension-field-norm | 256 | 597 |
| extension-field-power | 256 | 506 |
| extension-field-product | 256 | 597 |
| fixed-saturate | 512 | 96 |
| integer-bit-align | 256 | 70 |
| integer-bit-morton | 128 | 70 |
| integer-bit-scatter | 128 | 70 |
| integer-bit-smear | 256 | 70 |
| integer-hexagonal-index | 512 | 135 |
| integer-magic-constants | 512 | 127 |
| integer-try-arithmetic | 512 | 60 |
| integer-try-multiplication | 512 | 55 |
| interval-arc-functions | 512 | 40 |
| interval-arctangent | 512 | 40 |
| interval-arithmetic | 512 | 40 |
| interval-circular | 512 | 40 |
| interval-isotonic | 512 | 40 |
| interval-power | 512 | 40 |
| mass-box | 256 | 281 |
| mass-capsule | 256 | 281 |
| mass-compound | 256 | 281 |
| mass-cylinder | 256 | 281 |
| mass-parallel-axis | 256 | 281 |
| mass-sphere | 256 | 281 |
| mass-volume | 256 | 281 |
| meet-associative | 512 | 340 |
| meet-bottom-absorption | 512 | 340 |
| meet-commutative | 512 | 340 |
| meet-idempotent | 512 | 340 |
| meet-monotonicity | 512 | 340 |
| meet-order-coherence | 512 | 340 |
| meet-product-composition | 512 | 340 |
| meet-top-identity | 512 | 340 |
| mixed-scale | 512 | 281 |
| mixed-scale-triple | 512 | 281 |
| mobius | 512 | 8448 |
| monogenic-exact | 512 | 430 |
| monogenic-fusion | 512 | 430 |
| position | 512 | 545 |
| position-delta | 512 | 654 |
| position-translate | 512 | 654 |
| presented | 512 | 952 |
| prime-exploration-primality | 512 | 36 |
| prime-field | 256 | 602 |
| prime-field-chain | 256 | 602 |
| prime-field-lucas | 256 | 602 |
| prime-field-primality | 256 | 602 |
| prime-field-root | 256 | 602 |
| q1648-scalar | 512 | 331 |
| q1648-scalar-division | 512 | 329 |
| q3232-scalar | 512 | 294 |
| q3232-scalar-division | 512 | 293 |
| quaternion | 512 | 671 |
| quaternion-antiparallel | 512 | 45 |
| quaternion-arc | 512 | 45 |
| quaternion-direction | 512 | 558 |
| quaternion-rotate | 512 | 558 |
| quaternion-sublattice | 256 | 558 |
| rate | 512 | 655 |
| rigid | 512 | 694 |
| rigid-direction | 512 | 569 |
| rigid-exp-series | 512 | 43 |
| rigid-log-series | 512 | 43 |
| rigid-point | 512 | 694 |
| scalar | 512 | 8452 |
| scalar-division | 512 | 689 |
| scalar-smoothstep | 512 | 47 |
| scalar-text | 512 | 689 |
| scalar-transcendental | 512 | 691 |
| smoke | 64 | 2178 |
| split | 512 | 10681 |
| split-divide | 512 | 671 |
| split-transform | 512 | 671 |
| square-grid | 512 | 124 |
| sublattice | 256 | 4608 |
| symmetric-apply2 | 512 | 281 |
| symmetric-apply3 | 512 | 281 |
| symmetric-invert2 | 256 | 324 |
| symmetric-invert3 | 256 | 324 |
| symmetric-solve2 | 512 | 324 |
| symmetric-solve3 | 512 | 325 |
| unit-fraction16 | 512 | 578 |
| unit-fraction32 | 512 | 696 |
| unsigned-scalar | 512 | 682 |
| vector | 512 | 666 |
| vector-compare-length | 512 | 41 |
| vector-componentwise-helpers | 512 | 60 |
| vector-direction | 512 | 666 |
| vector-lattice | 512 | 554 |
| vector-narrow | 512 | 666 |
| vector-norm | 512 | 554 |
| vector-orthonormal-basis | 512 | 217 |
| vector-ray-plane | 512 | 58 |
| vector-within | 512 | 46 |

- last run: 2026-10-16
