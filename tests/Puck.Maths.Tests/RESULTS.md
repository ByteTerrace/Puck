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

- covered: 2524
- waived: 106
- uncovered: 634
- total public members: 3264
- last run: 2026-10-16

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
- last run: 2026-10-16

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10678 |
| binary-field | 256 | 511 |
| binary-field-axioms | 256 | 511 |
| binary-field-group | 256 | 606 |
| binary-polynomial | 256 | 609 |
| binary-polynomial-division | 256 | 609 |
| binary-polynomial-gcd | 256 | 609 |
| clifford-motor | 512 | 428 |
| clifford-multivector | 512 | 428 |
| clifford-planar-complex | 512 | 428 |
| clifford-planar-dual | 512 | 428 |
| clifford-planar-split | 512 | 428 |
| clifford-quaternion-even | 512 | 428 |
| clifford-reverse | 512 | 428 |
| closed-unit | 512 | 662 |
| complex | 512 | 10679 |
| complex-direction | 512 | 556 |
| complex-divide | 512 | 669 |
| complex-rotate | 512 | 669 |
| contribution-fold-analog | 512 | 347 |
| contribution-fold-formula | 512 | 347 |
| contribution-fold-no-pool | 512 | 347 |
| contribution-fold-order | 512 | 347 |
| contribution-fold-quantization | 512 | 347 |
| core-word-modular | 512 | 53 |
| cost-bound-arithmetic | 512 | 94 |
| cost-model-budgets | 512 | 94 |
| cost-model-conversions | 512 | 94 |
| directed-magnitude | 512 | 279 |
| directed-product | 512 | 279 |
| directed-product-sum | 512 | 279 |
| directed-quotient | 512 | 279 |
| directed-root | 512 | 279 |
| dual | 512 | 8446 |
| dual-divide | 512 | 556 |
| dual-generic | 512 | 556 |
| dual-quaternion | 512 | 669 |
| dynamics | 512 | 164 |
| extension-field | 256 | 595 |
| extension-field-inverse | 256 | 504 |
| extension-field-norm | 256 | 595 |
| extension-field-power | 256 | 504 |
| extension-field-product | 256 | 595 |
| fixed-saturate | 512 | 94 |
| integer-bit-align | 256 | 68 |
| integer-bit-morton | 128 | 68 |
| integer-bit-scatter | 128 | 68 |
| integer-bit-smear | 256 | 68 |
| integer-hexagonal-index | 512 | 133 |
| integer-magic-constants | 512 | 125 |
| integer-try-arithmetic | 512 | 58 |
| integer-try-multiplication | 512 | 53 |
| interval-arc-functions | 512 | 38 |
| interval-arctangent | 512 | 38 |
| interval-arithmetic | 512 | 38 |
| interval-circular | 512 | 38 |
| interval-isotonic | 512 | 38 |
| interval-power | 512 | 38 |
| mass-box | 256 | 279 |
| mass-capsule | 256 | 279 |
| mass-compound | 256 | 279 |
| mass-cylinder | 256 | 279 |
| mass-parallel-axis | 256 | 279 |
| mass-sphere | 256 | 279 |
| mass-volume | 256 | 279 |
| meet-associative | 512 | 338 |
| meet-bottom-absorption | 512 | 338 |
| meet-commutative | 512 | 338 |
| meet-idempotent | 512 | 338 |
| meet-monotonicity | 512 | 338 |
| meet-order-coherence | 512 | 338 |
| meet-product-composition | 512 | 338 |
| meet-top-identity | 512 | 338 |
| mixed-scale | 512 | 279 |
| mixed-scale-triple | 512 | 279 |
| mobius | 512 | 8446 |
| monogenic-exact | 512 | 428 |
| monogenic-fusion | 512 | 428 |
| position | 512 | 543 |
| position-delta | 512 | 652 |
| position-translate | 512 | 652 |
| presented | 512 | 950 |
| prime-exploration-primality | 512 | 34 |
| prime-field | 256 | 600 |
| prime-field-chain | 256 | 600 |
| prime-field-lucas | 256 | 600 |
| prime-field-primality | 256 | 600 |
| prime-field-root | 256 | 600 |
| q1648-scalar | 512 | 329 |
| q1648-scalar-division | 512 | 327 |
| q3232-scalar | 512 | 292 |
| q3232-scalar-division | 512 | 291 |
| quaternion | 512 | 669 |
| quaternion-antiparallel | 512 | 43 |
| quaternion-arc | 512 | 43 |
| quaternion-direction | 512 | 556 |
| quaternion-rotate | 512 | 556 |
| quaternion-sublattice | 256 | 556 |
| rate | 512 | 653 |
| rigid | 512 | 692 |
| rigid-direction | 512 | 567 |
| rigid-exp-series | 512 | 41 |
| rigid-log-series | 512 | 41 |
| rigid-point | 512 | 692 |
| scalar | 512 | 8450 |
| scalar-division | 512 | 687 |
| scalar-smoothstep | 512 | 45 |
| scalar-text | 512 | 687 |
| scalar-transcendental | 512 | 689 |
| smoke | 64 | 2176 |
| split | 512 | 10679 |
| split-divide | 512 | 669 |
| split-transform | 512 | 669 |
| square-grid | 512 | 122 |
| sublattice | 256 | 4606 |
| symmetric-apply2 | 512 | 279 |
| symmetric-apply3 | 512 | 279 |
| symmetric-invert2 | 256 | 322 |
| symmetric-invert3 | 256 | 322 |
| symmetric-solve2 | 512 | 322 |
| symmetric-solve3 | 512 | 323 |
| unit-fraction16 | 512 | 576 |
| unit-fraction32 | 512 | 694 |
| unsigned-scalar | 512 | 680 |
| vector | 512 | 664 |
| vector-compare-length | 512 | 39 |
| vector-componentwise-helpers | 512 | 58 |
| vector-direction | 512 | 664 |
| vector-lattice | 512 | 552 |
| vector-narrow | 512 | 664 |
| vector-norm | 512 | 552 |
| vector-orthonormal-basis | 512 | 215 |
| vector-ray-plane | 512 | 56 |
| vector-within | 512 | 44 |

- last run: 2026-10-16
