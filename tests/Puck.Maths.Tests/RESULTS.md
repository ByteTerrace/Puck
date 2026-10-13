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

- covered: 2522
- waived: 106
- uncovered: 634
- total public members: 3262
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
| algebra-fractional | 512 | 10676 |
| binary-field | 256 | 509 |
| binary-field-axioms | 256 | 509 |
| binary-field-group | 256 | 604 |
| binary-polynomial | 256 | 607 |
| binary-polynomial-division | 256 | 607 |
| binary-polynomial-gcd | 256 | 607 |
| clifford-motor | 512 | 426 |
| clifford-multivector | 512 | 426 |
| clifford-planar-complex | 512 | 426 |
| clifford-planar-dual | 512 | 426 |
| clifford-planar-split | 512 | 426 |
| clifford-quaternion-even | 512 | 426 |
| clifford-reverse | 512 | 426 |
| closed-unit | 512 | 660 |
| complex | 512 | 10677 |
| complex-direction | 512 | 554 |
| complex-divide | 512 | 667 |
| complex-rotate | 512 | 667 |
| contribution-fold-analog | 512 | 345 |
| contribution-fold-formula | 512 | 345 |
| contribution-fold-no-pool | 512 | 345 |
| contribution-fold-order | 512 | 345 |
| contribution-fold-quantization | 512 | 345 |
| core-word-modular | 512 | 51 |
| cost-bound-arithmetic | 512 | 92 |
| cost-model-budgets | 512 | 92 |
| cost-model-conversions | 512 | 92 |
| directed-magnitude | 512 | 277 |
| directed-product | 512 | 277 |
| directed-product-sum | 512 | 277 |
| directed-quotient | 512 | 277 |
| directed-root | 512 | 277 |
| dual | 512 | 8444 |
| dual-divide | 512 | 554 |
| dual-generic | 512 | 554 |
| dual-quaternion | 512 | 667 |
| dynamics | 512 | 162 |
| extension-field | 256 | 593 |
| extension-field-inverse | 256 | 502 |
| extension-field-norm | 256 | 593 |
| extension-field-power | 256 | 502 |
| extension-field-product | 256 | 593 |
| fixed-saturate | 512 | 92 |
| integer-bit-align | 256 | 66 |
| integer-bit-morton | 128 | 66 |
| integer-bit-scatter | 128 | 66 |
| integer-bit-smear | 256 | 66 |
| integer-hexagonal-index | 512 | 131 |
| integer-magic-constants | 512 | 123 |
| integer-try-arithmetic | 512 | 56 |
| integer-try-multiplication | 512 | 51 |
| interval-arc-functions | 512 | 36 |
| interval-arctangent | 512 | 36 |
| interval-arithmetic | 512 | 36 |
| interval-circular | 512 | 36 |
| interval-isotonic | 512 | 36 |
| interval-power | 512 | 36 |
| mass-box | 256 | 277 |
| mass-capsule | 256 | 277 |
| mass-compound | 256 | 277 |
| mass-cylinder | 256 | 277 |
| mass-parallel-axis | 256 | 277 |
| mass-sphere | 256 | 277 |
| mass-volume | 256 | 277 |
| meet-associative | 512 | 336 |
| meet-bottom-absorption | 512 | 336 |
| meet-commutative | 512 | 336 |
| meet-idempotent | 512 | 336 |
| meet-monotonicity | 512 | 336 |
| meet-order-coherence | 512 | 336 |
| meet-product-composition | 512 | 336 |
| meet-top-identity | 512 | 336 |
| mixed-scale | 512 | 277 |
| mixed-scale-triple | 512 | 277 |
| mobius | 512 | 8444 |
| monogenic-exact | 512 | 426 |
| monogenic-fusion | 512 | 426 |
| position | 512 | 541 |
| position-delta | 512 | 650 |
| position-translate | 512 | 650 |
| presented | 512 | 948 |
| prime-exploration-primality | 512 | 32 |
| prime-field | 256 | 598 |
| prime-field-chain | 256 | 598 |
| prime-field-lucas | 256 | 598 |
| prime-field-primality | 256 | 598 |
| prime-field-root | 256 | 598 |
| q1648-scalar | 512 | 327 |
| q1648-scalar-division | 512 | 325 |
| q3232-scalar | 512 | 290 |
| q3232-scalar-division | 512 | 289 |
| quaternion | 512 | 667 |
| quaternion-antiparallel | 512 | 41 |
| quaternion-arc | 512 | 41 |
| quaternion-direction | 512 | 554 |
| quaternion-rotate | 512 | 554 |
| quaternion-sublattice | 256 | 554 |
| rate | 512 | 651 |
| rigid | 512 | 690 |
| rigid-direction | 512 | 565 |
| rigid-exp-series | 512 | 39 |
| rigid-log-series | 512 | 39 |
| rigid-point | 512 | 690 |
| scalar | 512 | 8448 |
| scalar-division | 512 | 685 |
| scalar-smoothstep | 512 | 43 |
| scalar-text | 512 | 685 |
| scalar-transcendental | 512 | 687 |
| smoke | 64 | 2174 |
| split | 512 | 10677 |
| split-divide | 512 | 667 |
| split-transform | 512 | 667 |
| square-grid | 512 | 120 |
| sublattice | 256 | 4604 |
| symmetric-apply2 | 512 | 277 |
| symmetric-apply3 | 512 | 277 |
| symmetric-invert2 | 256 | 320 |
| symmetric-invert3 | 256 | 320 |
| symmetric-solve2 | 512 | 320 |
| symmetric-solve3 | 512 | 321 |
| unit-fraction16 | 512 | 574 |
| unit-fraction32 | 512 | 692 |
| unsigned-scalar | 512 | 678 |
| vector | 512 | 662 |
| vector-compare-length | 512 | 37 |
| vector-componentwise-helpers | 512 | 56 |
| vector-direction | 512 | 662 |
| vector-lattice | 512 | 550 |
| vector-narrow | 512 | 662 |
| vector-norm | 512 | 550 |
| vector-orthonormal-basis | 512 | 213 |
| vector-ray-plane | 512 | 54 |
| vector-within | 512 | 42 |

- last run: 2026-10-13
