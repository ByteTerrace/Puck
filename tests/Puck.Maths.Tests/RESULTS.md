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
- last run: 2026-10-07

## Default

- law cases executed: 679
- last run: 2026-10-07

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2274
- waived: 58
- uncovered: 634
- total public members: 2966
- last run: 2026-10-07

## Legs

| leg kind | legs |
| --- | --- |
| classical | 890 |
| in-tree-independent | 32 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 21 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1225 |
| **total** | **2409** |

- statements: 835
- statements with no independent leg: 221
- last run: 2026-10-07

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10658 |
| binary-field | 256 | 491 |
| binary-field-axioms | 256 | 491 |
| binary-field-group | 256 | 586 |
| binary-polynomial | 256 | 589 |
| binary-polynomial-division | 256 | 589 |
| binary-polynomial-gcd | 256 | 589 |
| clifford-motor | 512 | 408 |
| clifford-multivector | 512 | 408 |
| clifford-planar-complex | 512 | 408 |
| clifford-planar-dual | 512 | 408 |
| clifford-planar-split | 512 | 408 |
| clifford-quaternion-even | 512 | 408 |
| clifford-reverse | 512 | 408 |
| closed-unit | 512 | 642 |
| complex | 512 | 10659 |
| complex-direction | 512 | 536 |
| complex-divide | 512 | 649 |
| complex-rotate | 512 | 649 |
| contribution-fold-analog | 512 | 327 |
| contribution-fold-formula | 512 | 327 |
| contribution-fold-no-pool | 512 | 327 |
| contribution-fold-order | 512 | 327 |
| contribution-fold-quantization | 512 | 327 |
| core-word-modular | 512 | 33 |
| cost-bound-arithmetic | 512 | 74 |
| cost-model-budgets | 512 | 74 |
| cost-model-conversions | 512 | 74 |
| directed-magnitude | 512 | 259 |
| directed-product | 512 | 259 |
| directed-product-sum | 512 | 259 |
| directed-quotient | 512 | 259 |
| directed-root | 512 | 259 |
| dual | 512 | 8426 |
| dual-divide | 512 | 536 |
| dual-generic | 512 | 536 |
| dual-quaternion | 512 | 649 |
| dynamics | 512 | 144 |
| extension-field | 256 | 575 |
| extension-field-inverse | 256 | 484 |
| extension-field-norm | 256 | 575 |
| extension-field-power | 256 | 484 |
| extension-field-product | 256 | 575 |
| fixed-saturate | 512 | 74 |
| integer-bit-align | 256 | 48 |
| integer-bit-morton | 128 | 48 |
| integer-bit-scatter | 128 | 48 |
| integer-bit-smear | 256 | 48 |
| integer-hexagonal-index | 512 | 113 |
| integer-magic-constants | 512 | 105 |
| integer-try-arithmetic | 512 | 38 |
| integer-try-multiplication | 512 | 33 |
| interval-arc-functions | 512 | 18 |
| interval-arctangent | 512 | 18 |
| interval-arithmetic | 512 | 18 |
| interval-circular | 512 | 18 |
| interval-isotonic | 512 | 18 |
| interval-power | 512 | 18 |
| mass-box | 256 | 259 |
| mass-capsule | 256 | 259 |
| mass-compound | 256 | 259 |
| mass-cylinder | 256 | 259 |
| mass-parallel-axis | 256 | 259 |
| mass-sphere | 256 | 259 |
| mass-volume | 256 | 259 |
| meet-associative | 512 | 318 |
| meet-bottom-absorption | 512 | 318 |
| meet-commutative | 512 | 318 |
| meet-idempotent | 512 | 318 |
| meet-monotonicity | 512 | 318 |
| meet-order-coherence | 512 | 318 |
| meet-product-composition | 512 | 318 |
| meet-top-identity | 512 | 318 |
| mixed-scale | 512 | 259 |
| mixed-scale-triple | 512 | 259 |
| mobius | 512 | 8426 |
| monogenic-exact | 512 | 408 |
| monogenic-fusion | 512 | 408 |
| position | 512 | 523 |
| position-delta | 512 | 632 |
| position-translate | 512 | 632 |
| presented | 512 | 930 |
| prime-exploration-primality | 512 | 14 |
| prime-field | 256 | 580 |
| prime-field-chain | 256 | 580 |
| prime-field-lucas | 256 | 580 |
| prime-field-primality | 256 | 580 |
| prime-field-root | 256 | 580 |
| q1648-scalar | 512 | 309 |
| q1648-scalar-division | 512 | 307 |
| q3232-scalar | 512 | 272 |
| q3232-scalar-division | 512 | 271 |
| quaternion | 512 | 649 |
| quaternion-antiparallel | 512 | 23 |
| quaternion-arc | 512 | 23 |
| quaternion-direction | 512 | 536 |
| quaternion-rotate | 512 | 536 |
| quaternion-sublattice | 256 | 536 |
| rate | 512 | 633 |
| rigid | 512 | 672 |
| rigid-direction | 512 | 547 |
| rigid-exp-series | 512 | 21 |
| rigid-log-series | 512 | 21 |
| rigid-point | 512 | 672 |
| scalar | 512 | 8430 |
| scalar-division | 512 | 667 |
| scalar-smoothstep | 512 | 25 |
| scalar-text | 512 | 667 |
| scalar-transcendental | 512 | 669 |
| smoke | 64 | 2156 |
| split | 512 | 10659 |
| split-divide | 512 | 649 |
| split-transform | 512 | 649 |
| square-grid | 512 | 102 |
| sublattice | 256 | 4586 |
| symmetric-apply2 | 512 | 259 |
| symmetric-apply3 | 512 | 259 |
| symmetric-invert2 | 256 | 302 |
| symmetric-invert3 | 256 | 302 |
| symmetric-solve2 | 512 | 302 |
| symmetric-solve3 | 512 | 303 |
| unit-fraction16 | 512 | 556 |
| unit-fraction32 | 512 | 674 |
| unsigned-scalar | 512 | 660 |
| vector | 512 | 644 |
| vector-compare-length | 512 | 19 |
| vector-componentwise-helpers | 512 | 38 |
| vector-direction | 512 | 644 |
| vector-lattice | 512 | 532 |
| vector-narrow | 512 | 644 |
| vector-norm | 512 | 532 |
| vector-orthonormal-basis | 512 | 195 |
| vector-ray-plane | 512 | 36 |
| vector-within | 512 | 24 |

- last run: 2026-10-07
