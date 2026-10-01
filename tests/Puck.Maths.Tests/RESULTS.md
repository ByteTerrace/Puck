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

- law cases executed: 635
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
| classical | 837 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1199 |
| **total** | **2327** |

- statements: 776
- statements with no independent leg: 213
- last run: 2026-10-01

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10629 |
| binary-field | 256 | 462 |
| binary-field-axioms | 256 | 462 |
| binary-field-group | 256 | 557 |
| binary-polynomial | 256 | 560 |
| binary-polynomial-division | 256 | 560 |
| binary-polynomial-gcd | 256 | 560 |
| clifford-motor | 512 | 379 |
| clifford-multivector | 512 | 379 |
| clifford-planar-complex | 512 | 379 |
| clifford-planar-dual | 512 | 379 |
| clifford-planar-split | 512 | 379 |
| clifford-quaternion-even | 512 | 379 |
| clifford-reverse | 512 | 379 |
| closed-unit | 512 | 613 |
| complex | 512 | 10630 |
| complex-direction | 512 | 507 |
| complex-divide | 512 | 620 |
| complex-rotate | 512 | 620 |
| contribution-fold-analog | 512 | 298 |
| contribution-fold-formula | 512 | 298 |
| contribution-fold-no-pool | 512 | 298 |
| contribution-fold-order | 512 | 298 |
| contribution-fold-quantization | 512 | 298 |
| core-word-modular | 512 | 4 |
| cost-bound-arithmetic | 512 | 45 |
| cost-model-budgets | 512 | 45 |
| cost-model-conversions | 512 | 45 |
| directed-magnitude | 512 | 230 |
| directed-product | 512 | 230 |
| directed-product-sum | 512 | 230 |
| directed-quotient | 512 | 230 |
| directed-root | 512 | 230 |
| dual | 512 | 8397 |
| dual-divide | 512 | 507 |
| dual-generic | 512 | 507 |
| dual-quaternion | 512 | 620 |
| dynamics | 512 | 115 |
| extension-field | 256 | 546 |
| extension-field-inverse | 256 | 455 |
| extension-field-norm | 256 | 546 |
| extension-field-power | 256 | 455 |
| extension-field-product | 256 | 546 |
| fixed-saturate | 512 | 45 |
| integer-bit-align | 256 | 19 |
| integer-bit-morton | 128 | 19 |
| integer-bit-scatter | 128 | 19 |
| integer-bit-smear | 256 | 19 |
| integer-hexagonal-index | 512 | 84 |
| integer-magic-constants | 512 | 76 |
| integer-try-arithmetic | 512 | 9 |
| integer-try-multiplication | 512 | 4 |
| mass-box | 256 | 230 |
| mass-capsule | 256 | 230 |
| mass-compound | 256 | 230 |
| mass-cylinder | 256 | 230 |
| mass-parallel-axis | 256 | 230 |
| mass-sphere | 256 | 230 |
| mass-volume | 256 | 230 |
| meet-associative | 512 | 289 |
| meet-bottom-absorption | 512 | 289 |
| meet-commutative | 512 | 289 |
| meet-idempotent | 512 | 289 |
| meet-monotonicity | 512 | 289 |
| meet-order-coherence | 512 | 289 |
| meet-product-composition | 512 | 289 |
| meet-top-identity | 512 | 289 |
| mixed-scale | 512 | 230 |
| mixed-scale-triple | 512 | 230 |
| mobius | 512 | 8397 |
| monogenic-exact | 512 | 379 |
| monogenic-fusion | 512 | 379 |
| position | 512 | 494 |
| position-delta | 512 | 603 |
| position-translate | 512 | 603 |
| presented | 512 | 901 |
| prime-field | 256 | 551 |
| prime-field-chain | 256 | 551 |
| prime-field-lucas | 256 | 551 |
| prime-field-primality | 256 | 551 |
| prime-field-root | 256 | 551 |
| q1648-scalar | 512 | 280 |
| q1648-scalar-division | 512 | 278 |
| q3232-scalar | 512 | 243 |
| q3232-scalar-division | 512 | 242 |
| quaternion | 512 | 620 |
| quaternion-direction | 512 | 507 |
| quaternion-rotate | 512 | 507 |
| quaternion-sublattice | 256 | 507 |
| rate | 512 | 604 |
| rigid | 512 | 643 |
| rigid-direction | 512 | 518 |
| rigid-point | 512 | 643 |
| scalar | 512 | 8401 |
| scalar-division | 512 | 638 |
| scalar-text | 512 | 638 |
| scalar-transcendental | 512 | 640 |
| smoke | 64 | 2127 |
| split | 512 | 10630 |
| split-divide | 512 | 620 |
| split-transform | 512 | 620 |
| square-grid | 512 | 73 |
| sublattice | 256 | 4557 |
| symmetric-apply2 | 512 | 230 |
| symmetric-apply3 | 512 | 230 |
| symmetric-invert2 | 256 | 273 |
| symmetric-invert3 | 256 | 273 |
| symmetric-solve2 | 512 | 273 |
| symmetric-solve3 | 512 | 274 |
| unit-fraction16 | 512 | 527 |
| unit-fraction32 | 512 | 645 |
| unsigned-scalar | 512 | 631 |
| vector | 512 | 615 |
| vector-componentwise-helpers | 512 | 9 |
| vector-direction | 512 | 615 |
| vector-lattice | 512 | 503 |
| vector-narrow | 512 | 615 |
| vector-norm | 512 | 503 |
| vector-orthonormal-basis | 512 | 166 |
| vector-ray-plane | 512 | 7 |

- last run: 2026-10-01
