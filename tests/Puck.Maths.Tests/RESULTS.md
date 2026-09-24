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
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings` |

## Smoke

- law cases executed: 22
- last run: 2026-09-24

## Default

- law cases executed: 632
- last run: 2026-09-24

## Deep

- law cases executed: 112
- last run: 2026-09-24

## Exhaustive

- law cases executed: 7
- last run: 2026-09-24

## Coverage

- covered: 2150
- waived: 43
- uncovered: 634
- total public members: 2827
- last run: 2026-09-24

## Legs

| leg kind | legs |
| --- | --- |
| classical | 833 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1195 |
| **total** | **2319** |

- statements: 773
- statements with no independent leg: 213
- last run: 2026-09-24

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10625 |
| binary-field | 256 | 458 |
| binary-field-axioms | 256 | 458 |
| binary-field-group | 256 | 553 |
| binary-polynomial | 256 | 556 |
| binary-polynomial-division | 256 | 556 |
| binary-polynomial-gcd | 256 | 556 |
| clifford-motor | 512 | 375 |
| clifford-multivector | 512 | 375 |
| clifford-planar-complex | 512 | 375 |
| clifford-planar-dual | 512 | 375 |
| clifford-planar-split | 512 | 375 |
| clifford-quaternion-even | 512 | 375 |
| clifford-reverse | 512 | 375 |
| closed-unit | 512 | 609 |
| complex | 512 | 10626 |
| complex-direction | 512 | 503 |
| complex-divide | 512 | 616 |
| complex-rotate | 512 | 616 |
| contribution-fold-analog | 512 | 294 |
| contribution-fold-formula | 512 | 294 |
| contribution-fold-no-pool | 512 | 294 |
| contribution-fold-order | 512 | 294 |
| contribution-fold-quantization | 512 | 294 |
| cost-bound-arithmetic | 512 | 41 |
| cost-model-budgets | 512 | 41 |
| cost-model-conversions | 512 | 41 |
| directed-magnitude | 512 | 226 |
| directed-product | 512 | 226 |
| directed-product-sum | 512 | 226 |
| directed-quotient | 512 | 226 |
| directed-root | 512 | 226 |
| dual | 512 | 8393 |
| dual-divide | 512 | 503 |
| dual-generic | 512 | 503 |
| dual-quaternion | 512 | 616 |
| dynamics | 512 | 111 |
| extension-field | 256 | 542 |
| extension-field-inverse | 256 | 451 |
| extension-field-norm | 256 | 542 |
| extension-field-power | 256 | 451 |
| extension-field-product | 256 | 542 |
| fixed-saturate | 512 | 41 |
| integer-bit-align | 256 | 15 |
| integer-bit-morton | 128 | 15 |
| integer-bit-scatter | 128 | 15 |
| integer-bit-smear | 256 | 15 |
| integer-hexagonal-index | 512 | 80 |
| integer-magic-constants | 512 | 72 |
| integer-try-arithmetic | 512 | 5 |
| mass-box | 256 | 226 |
| mass-capsule | 256 | 226 |
| mass-compound | 256 | 226 |
| mass-cylinder | 256 | 226 |
| mass-parallel-axis | 256 | 226 |
| mass-sphere | 256 | 226 |
| mass-volume | 256 | 226 |
| meet-associative | 512 | 285 |
| meet-bottom-absorption | 512 | 285 |
| meet-commutative | 512 | 285 |
| meet-idempotent | 512 | 285 |
| meet-monotonicity | 512 | 285 |
| meet-order-coherence | 512 | 285 |
| meet-product-composition | 512 | 285 |
| meet-top-identity | 512 | 285 |
| mixed-scale | 512 | 226 |
| mixed-scale-triple | 512 | 226 |
| mobius | 512 | 8393 |
| monogenic-exact | 512 | 375 |
| monogenic-fusion | 512 | 375 |
| position | 512 | 490 |
| position-delta | 512 | 599 |
| position-translate | 512 | 599 |
| presented | 512 | 897 |
| prime-field | 256 | 547 |
| prime-field-chain | 256 | 547 |
| prime-field-lucas | 256 | 547 |
| prime-field-primality | 256 | 547 |
| prime-field-root | 256 | 547 |
| q1648-scalar | 512 | 276 |
| q1648-scalar-division | 512 | 274 |
| q3232-scalar | 512 | 239 |
| q3232-scalar-division | 512 | 238 |
| quaternion | 512 | 616 |
| quaternion-direction | 512 | 503 |
| quaternion-rotate | 512 | 503 |
| quaternion-sublattice | 256 | 503 |
| rate | 512 | 600 |
| rigid | 512 | 639 |
| rigid-direction | 512 | 514 |
| rigid-point | 512 | 639 |
| scalar | 512 | 8397 |
| scalar-division | 512 | 634 |
| scalar-text | 512 | 634 |
| scalar-transcendental | 512 | 636 |
| smoke | 64 | 2123 |
| split | 512 | 10626 |
| split-divide | 512 | 616 |
| split-transform | 512 | 616 |
| square-grid | 512 | 69 |
| sublattice | 256 | 4553 |
| symmetric-apply2 | 512 | 226 |
| symmetric-apply3 | 512 | 226 |
| symmetric-invert2 | 256 | 269 |
| symmetric-invert3 | 256 | 269 |
| symmetric-solve2 | 512 | 269 |
| symmetric-solve3 | 512 | 270 |
| unit-fraction16 | 512 | 523 |
| unit-fraction32 | 512 | 641 |
| unsigned-scalar | 512 | 627 |
| vector | 512 | 611 |
| vector-componentwise-helpers | 512 | 5 |
| vector-direction | 512 | 611 |
| vector-lattice | 512 | 499 |
| vector-narrow | 512 | 611 |
| vector-norm | 512 | 499 |
| vector-orthonormal-basis | 512 | 162 |
| vector-ray-plane | 512 | 3 |

- last run: 2026-09-24
