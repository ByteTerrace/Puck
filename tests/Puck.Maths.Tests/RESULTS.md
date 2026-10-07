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

- law cases executed: 695
- last run: 2026-10-07

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2217
- waived: 52
- uncovered: 634
- total public members: 2903
- last run: 2026-10-07

## Legs

| leg kind | legs |
| --- | --- |
| classical | 910 |
| in-tree-independent | 34 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 21 |
| shared-substrate:shared-upstream | 23 |
| shared-substrate:transcription | 28 |
| structural | 1234 |
| **total** | **2441** |

- statements: 853
- statements with no independent leg: 222
- last run: 2026-10-07

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10669 |
| binary-field | 256 | 502 |
| binary-field-axioms | 256 | 502 |
| binary-field-group | 256 | 597 |
| binary-polynomial | 256 | 600 |
| binary-polynomial-division | 256 | 600 |
| binary-polynomial-gcd | 256 | 600 |
| clifford-motor | 512 | 419 |
| clifford-multivector | 512 | 419 |
| clifford-planar-complex | 512 | 419 |
| clifford-planar-dual | 512 | 419 |
| clifford-planar-split | 512 | 419 |
| clifford-quaternion-even | 512 | 419 |
| clifford-reverse | 512 | 419 |
| closed-unit | 512 | 653 |
| complex | 512 | 10670 |
| complex-direction | 512 | 547 |
| complex-divide | 512 | 660 |
| complex-rotate | 512 | 660 |
| contribution-fold-analog | 512 | 338 |
| contribution-fold-formula | 512 | 338 |
| contribution-fold-no-pool | 512 | 338 |
| contribution-fold-order | 512 | 338 |
| contribution-fold-quantization | 512 | 338 |
| core-word-modular | 512 | 44 |
| cost-bound-arithmetic | 512 | 85 |
| cost-model-budgets | 512 | 85 |
| cost-model-conversions | 512 | 85 |
| directed-magnitude | 512 | 270 |
| directed-product | 512 | 270 |
| directed-product-sum | 512 | 270 |
| directed-quotient | 512 | 270 |
| directed-root | 512 | 270 |
| dual | 512 | 8437 |
| dual-divide | 512 | 547 |
| dual-generic | 512 | 547 |
| dual-quaternion | 512 | 660 |
| dynamics | 512 | 155 |
| extension-field | 256 | 586 |
| extension-field-inverse | 256 | 495 |
| extension-field-norm | 256 | 586 |
| extension-field-power | 256 | 495 |
| extension-field-product | 256 | 586 |
| fixed-saturate | 512 | 85 |
| integer-bit-align | 256 | 59 |
| integer-bit-morton | 128 | 59 |
| integer-bit-scatter | 128 | 59 |
| integer-bit-smear | 256 | 59 |
| integer-hexagonal-index | 512 | 124 |
| integer-magic-constants | 512 | 116 |
| integer-try-arithmetic | 512 | 49 |
| integer-try-multiplication | 512 | 44 |
| interval-arc-functions | 512 | 29 |
| interval-arctangent | 512 | 29 |
| interval-arithmetic | 512 | 29 |
| interval-circular | 512 | 29 |
| interval-isotonic | 512 | 29 |
| interval-power | 512 | 29 |
| mass-box | 256 | 270 |
| mass-capsule | 256 | 270 |
| mass-compound | 256 | 270 |
| mass-cylinder | 256 | 270 |
| mass-parallel-axis | 256 | 270 |
| mass-sphere | 256 | 270 |
| mass-volume | 256 | 270 |
| meet-associative | 512 | 329 |
| meet-bottom-absorption | 512 | 329 |
| meet-commutative | 512 | 329 |
| meet-idempotent | 512 | 329 |
| meet-monotonicity | 512 | 329 |
| meet-order-coherence | 512 | 329 |
| meet-product-composition | 512 | 329 |
| meet-top-identity | 512 | 329 |
| mixed-scale | 512 | 270 |
| mixed-scale-triple | 512 | 270 |
| mobius | 512 | 8437 |
| monogenic-exact | 512 | 419 |
| monogenic-fusion | 512 | 419 |
| position | 512 | 534 |
| position-delta | 512 | 643 |
| position-translate | 512 | 643 |
| presented | 512 | 941 |
| prime-exploration-primality | 512 | 25 |
| prime-field | 256 | 591 |
| prime-field-chain | 256 | 591 |
| prime-field-lucas | 256 | 591 |
| prime-field-primality | 256 | 591 |
| prime-field-root | 256 | 591 |
| q1648-scalar | 512 | 320 |
| q1648-scalar-division | 512 | 318 |
| q3232-scalar | 512 | 283 |
| q3232-scalar-division | 512 | 282 |
| quaternion | 512 | 660 |
| quaternion-antiparallel | 512 | 34 |
| quaternion-arc | 512 | 34 |
| quaternion-direction | 512 | 547 |
| quaternion-rotate | 512 | 547 |
| quaternion-sublattice | 256 | 547 |
| rate | 512 | 644 |
| rigid | 512 | 683 |
| rigid-direction | 512 | 558 |
| rigid-exp-series | 512 | 32 |
| rigid-log-series | 512 | 32 |
| rigid-point | 512 | 683 |
| scalar | 512 | 8441 |
| scalar-division | 512 | 678 |
| scalar-smoothstep | 512 | 36 |
| scalar-text | 512 | 678 |
| scalar-transcendental | 512 | 680 |
| smoke | 64 | 2167 |
| split | 512 | 10670 |
| split-divide | 512 | 660 |
| split-transform | 512 | 660 |
| square-grid | 512 | 113 |
| sublattice | 256 | 4597 |
| symmetric-apply2 | 512 | 270 |
| symmetric-apply3 | 512 | 270 |
| symmetric-invert2 | 256 | 313 |
| symmetric-invert3 | 256 | 313 |
| symmetric-solve2 | 512 | 313 |
| symmetric-solve3 | 512 | 314 |
| unit-fraction16 | 512 | 567 |
| unit-fraction32 | 512 | 685 |
| unsigned-scalar | 512 | 671 |
| vector | 512 | 655 |
| vector-compare-length | 512 | 30 |
| vector-componentwise-helpers | 512 | 49 |
| vector-direction | 512 | 655 |
| vector-lattice | 512 | 543 |
| vector-narrow | 512 | 655 |
| vector-norm | 512 | 543 |
| vector-orthonormal-basis | 512 | 206 |
| vector-ray-plane | 512 | 47 |
| vector-within | 512 | 35 |

- last run: 2026-10-07
