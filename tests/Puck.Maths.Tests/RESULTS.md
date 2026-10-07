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

- law cases executed: 673
- last run: 2026-10-07

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2244
- waived: 58
- uncovered: 634
- total public members: 2936
- last run: 2026-10-07

## Legs

| leg kind | legs |
| --- | --- |
| classical | 875 |
| in-tree-independent | 32 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1216 |
| **total** | **2383** |

- statements: 818
- statements with no independent leg: 219
- last run: 2026-10-07

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10655 |
| binary-field | 256 | 488 |
| binary-field-axioms | 256 | 488 |
| binary-field-group | 256 | 583 |
| binary-polynomial | 256 | 586 |
| binary-polynomial-division | 256 | 586 |
| binary-polynomial-gcd | 256 | 586 |
| clifford-motor | 512 | 405 |
| clifford-multivector | 512 | 405 |
| clifford-planar-complex | 512 | 405 |
| clifford-planar-dual | 512 | 405 |
| clifford-planar-split | 512 | 405 |
| clifford-quaternion-even | 512 | 405 |
| clifford-reverse | 512 | 405 |
| closed-unit | 512 | 639 |
| complex | 512 | 10656 |
| complex-direction | 512 | 533 |
| complex-divide | 512 | 646 |
| complex-rotate | 512 | 646 |
| contribution-fold-analog | 512 | 324 |
| contribution-fold-formula | 512 | 324 |
| contribution-fold-no-pool | 512 | 324 |
| contribution-fold-order | 512 | 324 |
| contribution-fold-quantization | 512 | 324 |
| core-word-modular | 512 | 30 |
| cost-bound-arithmetic | 512 | 71 |
| cost-model-budgets | 512 | 71 |
| cost-model-conversions | 512 | 71 |
| directed-magnitude | 512 | 256 |
| directed-product | 512 | 256 |
| directed-product-sum | 512 | 256 |
| directed-quotient | 512 | 256 |
| directed-root | 512 | 256 |
| dual | 512 | 8423 |
| dual-divide | 512 | 533 |
| dual-generic | 512 | 533 |
| dual-quaternion | 512 | 646 |
| dynamics | 512 | 141 |
| extension-field | 256 | 572 |
| extension-field-inverse | 256 | 481 |
| extension-field-norm | 256 | 572 |
| extension-field-power | 256 | 481 |
| extension-field-product | 256 | 572 |
| fixed-saturate | 512 | 71 |
| integer-bit-align | 256 | 45 |
| integer-bit-morton | 128 | 45 |
| integer-bit-scatter | 128 | 45 |
| integer-bit-smear | 256 | 45 |
| integer-hexagonal-index | 512 | 110 |
| integer-magic-constants | 512 | 102 |
| integer-try-arithmetic | 512 | 35 |
| integer-try-multiplication | 512 | 30 |
| interval-arc-functions | 512 | 15 |
| interval-arctangent | 512 | 15 |
| interval-arithmetic | 512 | 15 |
| interval-circular | 512 | 15 |
| interval-isotonic | 512 | 15 |
| interval-power | 512 | 15 |
| mass-box | 256 | 256 |
| mass-capsule | 256 | 256 |
| mass-compound | 256 | 256 |
| mass-cylinder | 256 | 256 |
| mass-parallel-axis | 256 | 256 |
| mass-sphere | 256 | 256 |
| mass-volume | 256 | 256 |
| meet-associative | 512 | 315 |
| meet-bottom-absorption | 512 | 315 |
| meet-commutative | 512 | 315 |
| meet-idempotent | 512 | 315 |
| meet-monotonicity | 512 | 315 |
| meet-order-coherence | 512 | 315 |
| meet-product-composition | 512 | 315 |
| meet-top-identity | 512 | 315 |
| mixed-scale | 512 | 256 |
| mixed-scale-triple | 512 | 256 |
| mobius | 512 | 8423 |
| monogenic-exact | 512 | 405 |
| monogenic-fusion | 512 | 405 |
| position | 512 | 520 |
| position-delta | 512 | 629 |
| position-translate | 512 | 629 |
| presented | 512 | 927 |
| prime-exploration-primality | 512 | 11 |
| prime-field | 256 | 577 |
| prime-field-chain | 256 | 577 |
| prime-field-lucas | 256 | 577 |
| prime-field-primality | 256 | 577 |
| prime-field-root | 256 | 577 |
| q1648-scalar | 512 | 306 |
| q1648-scalar-division | 512 | 304 |
| q3232-scalar | 512 | 269 |
| q3232-scalar-division | 512 | 268 |
| quaternion | 512 | 646 |
| quaternion-antiparallel | 512 | 20 |
| quaternion-arc | 512 | 20 |
| quaternion-direction | 512 | 533 |
| quaternion-rotate | 512 | 533 |
| quaternion-sublattice | 256 | 533 |
| rate | 512 | 630 |
| rigid | 512 | 669 |
| rigid-direction | 512 | 544 |
| rigid-exp-series | 512 | 18 |
| rigid-log-series | 512 | 18 |
| rigid-point | 512 | 669 |
| scalar | 512 | 8427 |
| scalar-division | 512 | 664 |
| scalar-smoothstep | 512 | 22 |
| scalar-text | 512 | 664 |
| scalar-transcendental | 512 | 666 |
| smoke | 64 | 2153 |
| split | 512 | 10656 |
| split-divide | 512 | 646 |
| split-transform | 512 | 646 |
| square-grid | 512 | 99 |
| sublattice | 256 | 4583 |
| symmetric-apply2 | 512 | 256 |
| symmetric-apply3 | 512 | 256 |
| symmetric-invert2 | 256 | 299 |
| symmetric-invert3 | 256 | 299 |
| symmetric-solve2 | 512 | 299 |
| symmetric-solve3 | 512 | 300 |
| unit-fraction16 | 512 | 553 |
| unit-fraction32 | 512 | 671 |
| unsigned-scalar | 512 | 657 |
| vector | 512 | 641 |
| vector-compare-length | 512 | 16 |
| vector-componentwise-helpers | 512 | 35 |
| vector-direction | 512 | 641 |
| vector-lattice | 512 | 529 |
| vector-narrow | 512 | 641 |
| vector-norm | 512 | 529 |
| vector-orthonormal-basis | 512 | 192 |
| vector-ray-plane | 512 | 33 |
| vector-within | 512 | 21 |

- last run: 2026-10-07
