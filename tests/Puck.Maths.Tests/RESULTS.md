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
- last run: 2026-10-06

## Default

- law cases executed: 663
- last run: 2026-10-06

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2239
- waived: 58
- uncovered: 634
- total public members: 2931
- last run: 2026-10-06

## Legs

| leg kind | legs |
| --- | --- |
| classical | 864 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1211 |
| **total** | **2368** |

- statements: 805
- statements with no independent leg: 217
- last run: 2026-10-06

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10652 |
| binary-field | 256 | 485 |
| binary-field-axioms | 256 | 485 |
| binary-field-group | 256 | 580 |
| binary-polynomial | 256 | 583 |
| binary-polynomial-division | 256 | 583 |
| binary-polynomial-gcd | 256 | 583 |
| clifford-motor | 512 | 402 |
| clifford-multivector | 512 | 402 |
| clifford-planar-complex | 512 | 402 |
| clifford-planar-dual | 512 | 402 |
| clifford-planar-split | 512 | 402 |
| clifford-quaternion-even | 512 | 402 |
| clifford-reverse | 512 | 402 |
| closed-unit | 512 | 636 |
| complex | 512 | 10653 |
| complex-direction | 512 | 530 |
| complex-divide | 512 | 643 |
| complex-rotate | 512 | 643 |
| contribution-fold-analog | 512 | 321 |
| contribution-fold-formula | 512 | 321 |
| contribution-fold-no-pool | 512 | 321 |
| contribution-fold-order | 512 | 321 |
| contribution-fold-quantization | 512 | 321 |
| core-word-modular | 512 | 27 |
| cost-bound-arithmetic | 512 | 68 |
| cost-model-budgets | 512 | 68 |
| cost-model-conversions | 512 | 68 |
| directed-magnitude | 512 | 253 |
| directed-product | 512 | 253 |
| directed-product-sum | 512 | 253 |
| directed-quotient | 512 | 253 |
| directed-root | 512 | 253 |
| dual | 512 | 8420 |
| dual-divide | 512 | 530 |
| dual-generic | 512 | 530 |
| dual-quaternion | 512 | 643 |
| dynamics | 512 | 138 |
| extension-field | 256 | 569 |
| extension-field-inverse | 256 | 478 |
| extension-field-norm | 256 | 569 |
| extension-field-power | 256 | 478 |
| extension-field-product | 256 | 569 |
| fixed-saturate | 512 | 68 |
| integer-bit-align | 256 | 42 |
| integer-bit-morton | 128 | 42 |
| integer-bit-scatter | 128 | 42 |
| integer-bit-smear | 256 | 42 |
| integer-hexagonal-index | 512 | 107 |
| integer-magic-constants | 512 | 99 |
| integer-try-arithmetic | 512 | 32 |
| integer-try-multiplication | 512 | 27 |
| interval-arc-functions | 512 | 12 |
| interval-arctangent | 512 | 12 |
| interval-arithmetic | 512 | 12 |
| interval-circular | 512 | 12 |
| interval-isotonic | 512 | 12 |
| interval-power | 512 | 12 |
| mass-box | 256 | 253 |
| mass-capsule | 256 | 253 |
| mass-compound | 256 | 253 |
| mass-cylinder | 256 | 253 |
| mass-parallel-axis | 256 | 253 |
| mass-sphere | 256 | 253 |
| mass-volume | 256 | 253 |
| meet-associative | 512 | 312 |
| meet-bottom-absorption | 512 | 312 |
| meet-commutative | 512 | 312 |
| meet-idempotent | 512 | 312 |
| meet-monotonicity | 512 | 312 |
| meet-order-coherence | 512 | 312 |
| meet-product-composition | 512 | 312 |
| meet-top-identity | 512 | 312 |
| mixed-scale | 512 | 253 |
| mixed-scale-triple | 512 | 253 |
| mobius | 512 | 8420 |
| monogenic-exact | 512 | 402 |
| monogenic-fusion | 512 | 402 |
| position | 512 | 517 |
| position-delta | 512 | 626 |
| position-translate | 512 | 626 |
| presented | 512 | 924 |
| prime-exploration-primality | 512 | 8 |
| prime-field | 256 | 574 |
| prime-field-chain | 256 | 574 |
| prime-field-lucas | 256 | 574 |
| prime-field-primality | 256 | 574 |
| prime-field-root | 256 | 574 |
| q1648-scalar | 512 | 303 |
| q1648-scalar-division | 512 | 301 |
| q3232-scalar | 512 | 266 |
| q3232-scalar-division | 512 | 265 |
| quaternion | 512 | 643 |
| quaternion-antiparallel | 512 | 17 |
| quaternion-arc | 512 | 17 |
| quaternion-direction | 512 | 530 |
| quaternion-rotate | 512 | 530 |
| quaternion-sublattice | 256 | 530 |
| rate | 512 | 627 |
| rigid | 512 | 666 |
| rigid-direction | 512 | 541 |
| rigid-exp-series | 512 | 15 |
| rigid-log-series | 512 | 15 |
| rigid-point | 512 | 666 |
| scalar | 512 | 8424 |
| scalar-division | 512 | 661 |
| scalar-smoothstep | 512 | 19 |
| scalar-text | 512 | 661 |
| scalar-transcendental | 512 | 663 |
| smoke | 64 | 2150 |
| split | 512 | 10653 |
| split-divide | 512 | 643 |
| split-transform | 512 | 643 |
| square-grid | 512 | 96 |
| sublattice | 256 | 4580 |
| symmetric-apply2 | 512 | 253 |
| symmetric-apply3 | 512 | 253 |
| symmetric-invert2 | 256 | 296 |
| symmetric-invert3 | 256 | 296 |
| symmetric-solve2 | 512 | 296 |
| symmetric-solve3 | 512 | 297 |
| unit-fraction16 | 512 | 550 |
| unit-fraction32 | 512 | 668 |
| unsigned-scalar | 512 | 654 |
| vector | 512 | 638 |
| vector-compare-length | 512 | 13 |
| vector-componentwise-helpers | 512 | 32 |
| vector-direction | 512 | 638 |
| vector-lattice | 512 | 526 |
| vector-narrow | 512 | 638 |
| vector-norm | 512 | 526 |
| vector-orthonormal-basis | 512 | 189 |
| vector-ray-plane | 512 | 30 |
| vector-within | 512 | 18 |

- last run: 2026-10-06
