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

- law cases executed: 666
- last run: 2026-10-06

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2240
- waived: 58
- uncovered: 634
- total public members: 2932
- last run: 2026-10-06

## Legs

| leg kind | legs |
| --- | --- |
| classical | 867 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1213 |
| **total** | **2373** |

- statements: 808
- statements with no independent leg: 217
- last run: 2026-10-06

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10653 |
| binary-field | 256 | 486 |
| binary-field-axioms | 256 | 486 |
| binary-field-group | 256 | 581 |
| binary-polynomial | 256 | 584 |
| binary-polynomial-division | 256 | 584 |
| binary-polynomial-gcd | 256 | 584 |
| clifford-motor | 512 | 403 |
| clifford-multivector | 512 | 403 |
| clifford-planar-complex | 512 | 403 |
| clifford-planar-dual | 512 | 403 |
| clifford-planar-split | 512 | 403 |
| clifford-quaternion-even | 512 | 403 |
| clifford-reverse | 512 | 403 |
| closed-unit | 512 | 637 |
| complex | 512 | 10654 |
| complex-direction | 512 | 531 |
| complex-divide | 512 | 644 |
| complex-rotate | 512 | 644 |
| contribution-fold-analog | 512 | 322 |
| contribution-fold-formula | 512 | 322 |
| contribution-fold-no-pool | 512 | 322 |
| contribution-fold-order | 512 | 322 |
| contribution-fold-quantization | 512 | 322 |
| core-word-modular | 512 | 28 |
| cost-bound-arithmetic | 512 | 69 |
| cost-model-budgets | 512 | 69 |
| cost-model-conversions | 512 | 69 |
| directed-magnitude | 512 | 254 |
| directed-product | 512 | 254 |
| directed-product-sum | 512 | 254 |
| directed-quotient | 512 | 254 |
| directed-root | 512 | 254 |
| dual | 512 | 8421 |
| dual-divide | 512 | 531 |
| dual-generic | 512 | 531 |
| dual-quaternion | 512 | 644 |
| dynamics | 512 | 139 |
| extension-field | 256 | 570 |
| extension-field-inverse | 256 | 479 |
| extension-field-norm | 256 | 570 |
| extension-field-power | 256 | 479 |
| extension-field-product | 256 | 570 |
| fixed-saturate | 512 | 69 |
| integer-bit-align | 256 | 43 |
| integer-bit-morton | 128 | 43 |
| integer-bit-scatter | 128 | 43 |
| integer-bit-smear | 256 | 43 |
| integer-hexagonal-index | 512 | 108 |
| integer-magic-constants | 512 | 100 |
| integer-try-arithmetic | 512 | 33 |
| integer-try-multiplication | 512 | 28 |
| interval-arc-functions | 512 | 13 |
| interval-arctangent | 512 | 13 |
| interval-arithmetic | 512 | 13 |
| interval-circular | 512 | 13 |
| interval-isotonic | 512 | 13 |
| interval-power | 512 | 13 |
| mass-box | 256 | 254 |
| mass-capsule | 256 | 254 |
| mass-compound | 256 | 254 |
| mass-cylinder | 256 | 254 |
| mass-parallel-axis | 256 | 254 |
| mass-sphere | 256 | 254 |
| mass-volume | 256 | 254 |
| meet-associative | 512 | 313 |
| meet-bottom-absorption | 512 | 313 |
| meet-commutative | 512 | 313 |
| meet-idempotent | 512 | 313 |
| meet-monotonicity | 512 | 313 |
| meet-order-coherence | 512 | 313 |
| meet-product-composition | 512 | 313 |
| meet-top-identity | 512 | 313 |
| mixed-scale | 512 | 254 |
| mixed-scale-triple | 512 | 254 |
| mobius | 512 | 8421 |
| monogenic-exact | 512 | 403 |
| monogenic-fusion | 512 | 403 |
| position | 512 | 518 |
| position-delta | 512 | 627 |
| position-translate | 512 | 627 |
| presented | 512 | 925 |
| prime-exploration-primality | 512 | 9 |
| prime-field | 256 | 575 |
| prime-field-chain | 256 | 575 |
| prime-field-lucas | 256 | 575 |
| prime-field-primality | 256 | 575 |
| prime-field-root | 256 | 575 |
| q1648-scalar | 512 | 304 |
| q1648-scalar-division | 512 | 302 |
| q3232-scalar | 512 | 267 |
| q3232-scalar-division | 512 | 266 |
| quaternion | 512 | 644 |
| quaternion-antiparallel | 512 | 18 |
| quaternion-arc | 512 | 18 |
| quaternion-direction | 512 | 531 |
| quaternion-rotate | 512 | 531 |
| quaternion-sublattice | 256 | 531 |
| rate | 512 | 628 |
| rigid | 512 | 667 |
| rigid-direction | 512 | 542 |
| rigid-exp-series | 512 | 16 |
| rigid-log-series | 512 | 16 |
| rigid-point | 512 | 667 |
| scalar | 512 | 8425 |
| scalar-division | 512 | 662 |
| scalar-smoothstep | 512 | 20 |
| scalar-text | 512 | 662 |
| scalar-transcendental | 512 | 664 |
| smoke | 64 | 2151 |
| split | 512 | 10654 |
| split-divide | 512 | 644 |
| split-transform | 512 | 644 |
| square-grid | 512 | 97 |
| sublattice | 256 | 4581 |
| symmetric-apply2 | 512 | 254 |
| symmetric-apply3 | 512 | 254 |
| symmetric-invert2 | 256 | 297 |
| symmetric-invert3 | 256 | 297 |
| symmetric-solve2 | 512 | 297 |
| symmetric-solve3 | 512 | 298 |
| unit-fraction16 | 512 | 551 |
| unit-fraction32 | 512 | 669 |
| unsigned-scalar | 512 | 655 |
| vector | 512 | 639 |
| vector-compare-length | 512 | 14 |
| vector-componentwise-helpers | 512 | 33 |
| vector-direction | 512 | 639 |
| vector-lattice | 512 | 527 |
| vector-narrow | 512 | 639 |
| vector-norm | 512 | 527 |
| vector-orthonormal-basis | 512 | 190 |
| vector-ray-plane | 512 | 31 |
| vector-within | 512 | 19 |

- last run: 2026-10-06
