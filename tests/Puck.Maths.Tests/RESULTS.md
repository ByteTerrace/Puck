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
- last run: 2026-10-13

## Default

- law cases executed: 707
- last run: 2026-10-13

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2521
- waived: 106
- uncovered: 634
- total public members: 3261
- last run: 2026-10-13

## Legs

| leg kind | legs |
| --- | --- |
| classical | 925 |
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
| **total** | **2472** |

- statements: 868
- statements with no independent leg: 224
- last run: 2026-10-13

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10675 |
| binary-field | 256 | 508 |
| binary-field-axioms | 256 | 508 |
| binary-field-group | 256 | 603 |
| binary-polynomial | 256 | 606 |
| binary-polynomial-division | 256 | 606 |
| binary-polynomial-gcd | 256 | 606 |
| clifford-motor | 512 | 425 |
| clifford-multivector | 512 | 425 |
| clifford-planar-complex | 512 | 425 |
| clifford-planar-dual | 512 | 425 |
| clifford-planar-split | 512 | 425 |
| clifford-quaternion-even | 512 | 425 |
| clifford-reverse | 512 | 425 |
| closed-unit | 512 | 659 |
| complex | 512 | 10676 |
| complex-direction | 512 | 553 |
| complex-divide | 512 | 666 |
| complex-rotate | 512 | 666 |
| contribution-fold-analog | 512 | 344 |
| contribution-fold-formula | 512 | 344 |
| contribution-fold-no-pool | 512 | 344 |
| contribution-fold-order | 512 | 344 |
| contribution-fold-quantization | 512 | 344 |
| core-word-modular | 512 | 50 |
| cost-bound-arithmetic | 512 | 91 |
| cost-model-budgets | 512 | 91 |
| cost-model-conversions | 512 | 91 |
| directed-magnitude | 512 | 276 |
| directed-product | 512 | 276 |
| directed-product-sum | 512 | 276 |
| directed-quotient | 512 | 276 |
| directed-root | 512 | 276 |
| dual | 512 | 8443 |
| dual-divide | 512 | 553 |
| dual-generic | 512 | 553 |
| dual-quaternion | 512 | 666 |
| dynamics | 512 | 161 |
| extension-field | 256 | 592 |
| extension-field-inverse | 256 | 501 |
| extension-field-norm | 256 | 592 |
| extension-field-power | 256 | 501 |
| extension-field-product | 256 | 592 |
| fixed-saturate | 512 | 91 |
| integer-bit-align | 256 | 65 |
| integer-bit-morton | 128 | 65 |
| integer-bit-scatter | 128 | 65 |
| integer-bit-smear | 256 | 65 |
| integer-hexagonal-index | 512 | 130 |
| integer-magic-constants | 512 | 122 |
| integer-try-arithmetic | 512 | 55 |
| integer-try-multiplication | 512 | 50 |
| interval-arc-functions | 512 | 35 |
| interval-arctangent | 512 | 35 |
| interval-arithmetic | 512 | 35 |
| interval-circular | 512 | 35 |
| interval-isotonic | 512 | 35 |
| interval-power | 512 | 35 |
| mass-box | 256 | 276 |
| mass-capsule | 256 | 276 |
| mass-compound | 256 | 276 |
| mass-cylinder | 256 | 276 |
| mass-parallel-axis | 256 | 276 |
| mass-sphere | 256 | 276 |
| mass-volume | 256 | 276 |
| meet-associative | 512 | 335 |
| meet-bottom-absorption | 512 | 335 |
| meet-commutative | 512 | 335 |
| meet-idempotent | 512 | 335 |
| meet-monotonicity | 512 | 335 |
| meet-order-coherence | 512 | 335 |
| meet-product-composition | 512 | 335 |
| meet-top-identity | 512 | 335 |
| mixed-scale | 512 | 276 |
| mixed-scale-triple | 512 | 276 |
| mobius | 512 | 8443 |
| monogenic-exact | 512 | 425 |
| monogenic-fusion | 512 | 425 |
| position | 512 | 540 |
| position-delta | 512 | 649 |
| position-translate | 512 | 649 |
| presented | 512 | 947 |
| prime-exploration-primality | 512 | 31 |
| prime-field | 256 | 597 |
| prime-field-chain | 256 | 597 |
| prime-field-lucas | 256 | 597 |
| prime-field-primality | 256 | 597 |
| prime-field-root | 256 | 597 |
| q1648-scalar | 512 | 326 |
| q1648-scalar-division | 512 | 324 |
| q3232-scalar | 512 | 289 |
| q3232-scalar-division | 512 | 288 |
| quaternion | 512 | 666 |
| quaternion-antiparallel | 512 | 40 |
| quaternion-arc | 512 | 40 |
| quaternion-direction | 512 | 553 |
| quaternion-rotate | 512 | 553 |
| quaternion-sublattice | 256 | 553 |
| rate | 512 | 650 |
| rigid | 512 | 689 |
| rigid-direction | 512 | 564 |
| rigid-exp-series | 512 | 38 |
| rigid-log-series | 512 | 38 |
| rigid-point | 512 | 689 |
| scalar | 512 | 8447 |
| scalar-division | 512 | 684 |
| scalar-smoothstep | 512 | 42 |
| scalar-text | 512 | 684 |
| scalar-transcendental | 512 | 686 |
| smoke | 64 | 2173 |
| split | 512 | 10676 |
| split-divide | 512 | 666 |
| split-transform | 512 | 666 |
| square-grid | 512 | 119 |
| sublattice | 256 | 4603 |
| symmetric-apply2 | 512 | 276 |
| symmetric-apply3 | 512 | 276 |
| symmetric-invert2 | 256 | 319 |
| symmetric-invert3 | 256 | 319 |
| symmetric-solve2 | 512 | 319 |
| symmetric-solve3 | 512 | 320 |
| unit-fraction16 | 512 | 573 |
| unit-fraction32 | 512 | 691 |
| unsigned-scalar | 512 | 677 |
| vector | 512 | 661 |
| vector-compare-length | 512 | 36 |
| vector-componentwise-helpers | 512 | 55 |
| vector-direction | 512 | 661 |
| vector-lattice | 512 | 549 |
| vector-narrow | 512 | 661 |
| vector-norm | 512 | 549 |
| vector-orthonormal-basis | 512 | 212 |
| vector-ray-plane | 512 | 53 |
| vector-within | 512 | 41 |

- last run: 2026-10-13
