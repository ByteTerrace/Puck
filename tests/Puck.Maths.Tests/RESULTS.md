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
- last run: 2026-10-10

## Default

- law cases executed: 697
- last run: 2026-10-10

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2478
- waived: 103
- uncovered: 634
- total public members: 3215
- last run: 2026-10-10

## Legs

| leg kind | legs |
| --- | --- |
| classical | 911 |
| in-tree-independent | 34 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 21 |
| shared-substrate:shared-upstream | 23 |
| shared-substrate:transcription | 28 |
| structural | 1236 |
| **total** | **2444** |

- statements: 855
- statements with no independent leg: 223
- last run: 2026-10-10

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10672 |
| binary-field | 256 | 505 |
| binary-field-axioms | 256 | 505 |
| binary-field-group | 256 | 600 |
| binary-polynomial | 256 | 603 |
| binary-polynomial-division | 256 | 603 |
| binary-polynomial-gcd | 256 | 603 |
| clifford-motor | 512 | 422 |
| clifford-multivector | 512 | 422 |
| clifford-planar-complex | 512 | 422 |
| clifford-planar-dual | 512 | 422 |
| clifford-planar-split | 512 | 422 |
| clifford-quaternion-even | 512 | 422 |
| clifford-reverse | 512 | 422 |
| closed-unit | 512 | 656 |
| complex | 512 | 10673 |
| complex-direction | 512 | 550 |
| complex-divide | 512 | 663 |
| complex-rotate | 512 | 663 |
| contribution-fold-analog | 512 | 341 |
| contribution-fold-formula | 512 | 341 |
| contribution-fold-no-pool | 512 | 341 |
| contribution-fold-order | 512 | 341 |
| contribution-fold-quantization | 512 | 341 |
| core-word-modular | 512 | 47 |
| cost-bound-arithmetic | 512 | 88 |
| cost-model-budgets | 512 | 88 |
| cost-model-conversions | 512 | 88 |
| directed-magnitude | 512 | 273 |
| directed-product | 512 | 273 |
| directed-product-sum | 512 | 273 |
| directed-quotient | 512 | 273 |
| directed-root | 512 | 273 |
| dual | 512 | 8440 |
| dual-divide | 512 | 550 |
| dual-generic | 512 | 550 |
| dual-quaternion | 512 | 663 |
| dynamics | 512 | 158 |
| extension-field | 256 | 589 |
| extension-field-inverse | 256 | 498 |
| extension-field-norm | 256 | 589 |
| extension-field-power | 256 | 498 |
| extension-field-product | 256 | 589 |
| fixed-saturate | 512 | 88 |
| integer-bit-align | 256 | 62 |
| integer-bit-morton | 128 | 62 |
| integer-bit-scatter | 128 | 62 |
| integer-bit-smear | 256 | 62 |
| integer-hexagonal-index | 512 | 127 |
| integer-magic-constants | 512 | 119 |
| integer-try-arithmetic | 512 | 52 |
| integer-try-multiplication | 512 | 47 |
| interval-arc-functions | 512 | 32 |
| interval-arctangent | 512 | 32 |
| interval-arithmetic | 512 | 32 |
| interval-circular | 512 | 32 |
| interval-isotonic | 512 | 32 |
| interval-power | 512 | 32 |
| mass-box | 256 | 273 |
| mass-capsule | 256 | 273 |
| mass-compound | 256 | 273 |
| mass-cylinder | 256 | 273 |
| mass-parallel-axis | 256 | 273 |
| mass-sphere | 256 | 273 |
| mass-volume | 256 | 273 |
| meet-associative | 512 | 332 |
| meet-bottom-absorption | 512 | 332 |
| meet-commutative | 512 | 332 |
| meet-idempotent | 512 | 332 |
| meet-monotonicity | 512 | 332 |
| meet-order-coherence | 512 | 332 |
| meet-product-composition | 512 | 332 |
| meet-top-identity | 512 | 332 |
| mixed-scale | 512 | 273 |
| mixed-scale-triple | 512 | 273 |
| mobius | 512 | 8440 |
| monogenic-exact | 512 | 422 |
| monogenic-fusion | 512 | 422 |
| position | 512 | 537 |
| position-delta | 512 | 646 |
| position-translate | 512 | 646 |
| presented | 512 | 944 |
| prime-exploration-primality | 512 | 28 |
| prime-field | 256 | 594 |
| prime-field-chain | 256 | 594 |
| prime-field-lucas | 256 | 594 |
| prime-field-primality | 256 | 594 |
| prime-field-root | 256 | 594 |
| q1648-scalar | 512 | 323 |
| q1648-scalar-division | 512 | 321 |
| q3232-scalar | 512 | 286 |
| q3232-scalar-division | 512 | 285 |
| quaternion | 512 | 663 |
| quaternion-antiparallel | 512 | 37 |
| quaternion-arc | 512 | 37 |
| quaternion-direction | 512 | 550 |
| quaternion-rotate | 512 | 550 |
| quaternion-sublattice | 256 | 550 |
| rate | 512 | 647 |
| rigid | 512 | 686 |
| rigid-direction | 512 | 561 |
| rigid-exp-series | 512 | 35 |
| rigid-log-series | 512 | 35 |
| rigid-point | 512 | 686 |
| scalar | 512 | 8444 |
| scalar-division | 512 | 681 |
| scalar-smoothstep | 512 | 39 |
| scalar-text | 512 | 681 |
| scalar-transcendental | 512 | 683 |
| smoke | 64 | 2170 |
| split | 512 | 10673 |
| split-divide | 512 | 663 |
| split-transform | 512 | 663 |
| square-grid | 512 | 116 |
| sublattice | 256 | 4600 |
| symmetric-apply2 | 512 | 273 |
| symmetric-apply3 | 512 | 273 |
| symmetric-invert2 | 256 | 316 |
| symmetric-invert3 | 256 | 316 |
| symmetric-solve2 | 512 | 316 |
| symmetric-solve3 | 512 | 317 |
| unit-fraction16 | 512 | 570 |
| unit-fraction32 | 512 | 688 |
| unsigned-scalar | 512 | 674 |
| vector | 512 | 658 |
| vector-compare-length | 512 | 33 |
| vector-componentwise-helpers | 512 | 52 |
| vector-direction | 512 | 658 |
| vector-lattice | 512 | 546 |
| vector-narrow | 512 | 658 |
| vector-norm | 512 | 546 |
| vector-orthonormal-basis | 512 | 209 |
| vector-ray-plane | 512 | 50 |
| vector-within | 512 | 38 |

- last run: 2026-10-10
