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
- last run: 2026-10-01

## Default

- law cases executed: 636
- last run: 2026-10-01

## Deep

- law cases executed: 112
- last run: 2026-10-01

## Exhaustive

- law cases executed: 7
- last run: 2026-10-01

## Coverage

- covered: 2155
- waived: 43
- uncovered: 634
- total public members: 2832
- last run: 2026-10-01

## Legs

| leg kind | legs |
| --- | --- |
| classical | 838 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1200 |
| **total** | **2329** |

- statements: 777
- statements with no independent leg: 213
- last run: 2026-10-01

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10632 |
| binary-field | 256 | 465 |
| binary-field-axioms | 256 | 465 |
| binary-field-group | 256 | 560 |
| binary-polynomial | 256 | 563 |
| binary-polynomial-division | 256 | 563 |
| binary-polynomial-gcd | 256 | 563 |
| clifford-motor | 512 | 382 |
| clifford-multivector | 512 | 382 |
| clifford-planar-complex | 512 | 382 |
| clifford-planar-dual | 512 | 382 |
| clifford-planar-split | 512 | 382 |
| clifford-quaternion-even | 512 | 382 |
| clifford-reverse | 512 | 382 |
| closed-unit | 512 | 616 |
| complex | 512 | 10633 |
| complex-direction | 512 | 510 |
| complex-divide | 512 | 623 |
| complex-rotate | 512 | 623 |
| contribution-fold-analog | 512 | 301 |
| contribution-fold-formula | 512 | 301 |
| contribution-fold-no-pool | 512 | 301 |
| contribution-fold-order | 512 | 301 |
| contribution-fold-quantization | 512 | 301 |
| core-word-modular | 512 | 7 |
| cost-bound-arithmetic | 512 | 48 |
| cost-model-budgets | 512 | 48 |
| cost-model-conversions | 512 | 48 |
| directed-magnitude | 512 | 233 |
| directed-product | 512 | 233 |
| directed-product-sum | 512 | 233 |
| directed-quotient | 512 | 233 |
| directed-root | 512 | 233 |
| dual | 512 | 8400 |
| dual-divide | 512 | 510 |
| dual-generic | 512 | 510 |
| dual-quaternion | 512 | 623 |
| dynamics | 512 | 118 |
| extension-field | 256 | 549 |
| extension-field-inverse | 256 | 458 |
| extension-field-norm | 256 | 549 |
| extension-field-power | 256 | 458 |
| extension-field-product | 256 | 549 |
| fixed-saturate | 512 | 48 |
| integer-bit-align | 256 | 22 |
| integer-bit-morton | 128 | 22 |
| integer-bit-scatter | 128 | 22 |
| integer-bit-smear | 256 | 22 |
| integer-hexagonal-index | 512 | 87 |
| integer-magic-constants | 512 | 79 |
| integer-try-arithmetic | 512 | 12 |
| integer-try-multiplication | 512 | 7 |
| mass-box | 256 | 233 |
| mass-capsule | 256 | 233 |
| mass-compound | 256 | 233 |
| mass-cylinder | 256 | 233 |
| mass-parallel-axis | 256 | 233 |
| mass-sphere | 256 | 233 |
| mass-volume | 256 | 233 |
| meet-associative | 512 | 292 |
| meet-bottom-absorption | 512 | 292 |
| meet-commutative | 512 | 292 |
| meet-idempotent | 512 | 292 |
| meet-monotonicity | 512 | 292 |
| meet-order-coherence | 512 | 292 |
| meet-product-composition | 512 | 292 |
| meet-top-identity | 512 | 292 |
| mixed-scale | 512 | 233 |
| mixed-scale-triple | 512 | 233 |
| mobius | 512 | 8400 |
| monogenic-exact | 512 | 382 |
| monogenic-fusion | 512 | 382 |
| position | 512 | 497 |
| position-delta | 512 | 606 |
| position-translate | 512 | 606 |
| presented | 512 | 904 |
| prime-field | 256 | 554 |
| prime-field-chain | 256 | 554 |
| prime-field-lucas | 256 | 554 |
| prime-field-primality | 256 | 554 |
| prime-field-root | 256 | 554 |
| q1648-scalar | 512 | 283 |
| q1648-scalar-division | 512 | 281 |
| q3232-scalar | 512 | 246 |
| q3232-scalar-division | 512 | 245 |
| quaternion | 512 | 623 |
| quaternion-direction | 512 | 510 |
| quaternion-rotate | 512 | 510 |
| quaternion-sublattice | 256 | 510 |
| rate | 512 | 607 |
| rigid | 512 | 646 |
| rigid-direction | 512 | 521 |
| rigid-point | 512 | 646 |
| scalar | 512 | 8404 |
| scalar-division | 512 | 641 |
| scalar-text | 512 | 641 |
| scalar-transcendental | 512 | 643 |
| smoke | 64 | 2130 |
| split | 512 | 10633 |
| split-divide | 512 | 623 |
| split-transform | 512 | 623 |
| square-grid | 512 | 76 |
| sublattice | 256 | 4560 |
| symmetric-apply2 | 512 | 233 |
| symmetric-apply3 | 512 | 233 |
| symmetric-invert2 | 256 | 276 |
| symmetric-invert3 | 256 | 276 |
| symmetric-solve2 | 512 | 276 |
| symmetric-solve3 | 512 | 277 |
| unit-fraction16 | 512 | 530 |
| unit-fraction32 | 512 | 648 |
| unsigned-scalar | 512 | 634 |
| vector | 512 | 618 |
| vector-componentwise-helpers | 512 | 12 |
| vector-direction | 512 | 618 |
| vector-lattice | 512 | 506 |
| vector-narrow | 512 | 618 |
| vector-norm | 512 | 506 |
| vector-orthonormal-basis | 512 | 169 |
| vector-ray-plane | 512 | 10 |

- last run: 2026-10-01
