# Puck.Maths.Tests — RESULTS

Machine-written by the assembly ledger at run end. Each block records the last run that exercised it — a tier
block only when that tier ran law cases, coverage only when the ratchet gate ran, the frontier only when a run
consumed domains AND every law it ran passed — so every other block keeps the text its own last run left. The
last-run dates are the only volatile content; they do not by themselves trigger a rewrite.

Every figure below is MACHINE-INDEPENDENT by construction: the same commit produces the same counts and the same
frontier indices on every machine, so a difference here is a real difference and never a difference of hardware.
No duration is recorded, deliberately. One here would carry no machine identity, would span the whole session
rather than the block it sits under, and would be taken without a busy-machine guard — so it could not answer
any question asked of it. Cost is the bench tier's business: a RATIO against a baseline held per machine, which
records nothing at all when the environment is suspect, and which names the machine it ran on.

## Invocations

| tier | command |
| --- | --- |
| Default (Smoke+Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` |
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` |
| Bench | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/bench.runsettings` |

## Smoke

- law cases executed: 22
- last run: 2026-09-15

## Default

- law cases executed: 616
- last run: 2026-09-15

## Deep

- law cases executed: 112
- last run: 2026-09-14

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 2116
- waived: 43
- uncovered: 634
- total public members: 2793
- last run: 2026-09-15

## Legs

| leg kind | legs |
| --- | --- |
| classical | 812 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1186 |
| **total** | **2289** |

- statements: 757
- statements with no independent leg: 211
- last run: 2026-09-15

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10597 |
| binary-field | 256 | 435 |
| binary-field-axioms | 256 | 435 |
| binary-field-group | 256 | 525 |
| binary-polynomial | 256 | 528 |
| binary-polynomial-division | 256 | 528 |
| binary-polynomial-gcd | 256 | 528 |
| clifford-motor | 512 | 352 |
| clifford-multivector | 512 | 352 |
| clifford-planar-complex | 512 | 352 |
| clifford-planar-dual | 512 | 352 |
| clifford-planar-split | 512 | 352 |
| clifford-quaternion-even | 512 | 352 |
| clifford-reverse | 512 | 352 |
| closed-unit | 512 | 586 |
| complex | 512 | 10598 |
| complex-direction | 512 | 480 |
| complex-divide | 512 | 588 |
| complex-rotate | 512 | 588 |
| contribution-fold-analog | 512 | 271 |
| contribution-fold-formula | 512 | 271 |
| contribution-fold-no-pool | 512 | 271 |
| contribution-fold-order | 512 | 271 |
| contribution-fold-quantization | 512 | 271 |
| cost-bound-arithmetic | 512 | 18 |
| cost-model-budgets | 512 | 18 |
| cost-model-conversions | 512 | 18 |
| directed-magnitude | 512 | 203 |
| directed-product | 512 | 203 |
| directed-product-sum | 512 | 203 |
| directed-quotient | 512 | 203 |
| directed-root | 512 | 203 |
| dual | 512 | 8370 |
| dual-divide | 512 | 480 |
| dual-generic | 512 | 480 |
| dual-quaternion | 512 | 588 |
| dynamics | 512 | 88 |
| extension-field | 256 | 514 |
| extension-field-inverse | 256 | 428 |
| extension-field-norm | 256 | 514 |
| extension-field-power | 256 | 428 |
| extension-field-product | 256 | 514 |
| fixed-saturate | 512 | 18 |
| integer-hexagonal-index | 512 | 52 |
| integer-magic-constants | 512 | 49 |
| mass-box | 256 | 203 |
| mass-capsule | 256 | 203 |
| mass-compound | 256 | 203 |
| mass-cylinder | 256 | 203 |
| mass-parallel-axis | 256 | 203 |
| mass-sphere | 256 | 203 |
| mass-volume | 256 | 203 |
| meet-associative | 512 | 262 |
| meet-bottom-absorption | 512 | 262 |
| meet-commutative | 512 | 262 |
| meet-idempotent | 512 | 262 |
| meet-monotonicity | 512 | 262 |
| meet-order-coherence | 512 | 262 |
| meet-product-composition | 512 | 262 |
| meet-top-identity | 512 | 262 |
| mixed-scale | 512 | 203 |
| mixed-scale-triple | 512 | 203 |
| mobius | 512 | 8370 |
| monogenic-exact | 512 | 352 |
| monogenic-fusion | 512 | 352 |
| position | 512 | 467 |
| position-delta | 512 | 571 |
| position-translate | 512 | 571 |
| presented | 512 | 869 |
| prime-field | 256 | 519 |
| prime-field-chain | 256 | 519 |
| prime-field-lucas | 256 | 519 |
| prime-field-primality | 256 | 519 |
| prime-field-root | 256 | 519 |
| q1648-scalar | 512 | 253 |
| q1648-scalar-division | 512 | 251 |
| q3232-scalar | 512 | 216 |
| q3232-scalar-division | 512 | 215 |
| quaternion | 512 | 588 |
| quaternion-direction | 512 | 480 |
| quaternion-rotate | 512 | 480 |
| quaternion-sublattice | 256 | 480 |
| rate | 512 | 572 |
| rigid | 512 | 611 |
| rigid-direction | 512 | 491 |
| rigid-point | 512 | 611 |
| scalar | 512 | 8374 |
| scalar-division | 512 | 606 |
| scalar-text | 512 | 606 |
| scalar-transcendental | 512 | 608 |
| smoke | 64 | 2100 |
| split | 512 | 10598 |
| split-divide | 512 | 588 |
| split-transform | 512 | 588 |
| square-grid | 512 | 41 |
| sublattice | 256 | 4530 |
| symmetric-apply2 | 512 | 203 |
| symmetric-apply3 | 512 | 203 |
| symmetric-invert2 | 256 | 246 |
| symmetric-invert3 | 256 | 246 |
| symmetric-solve2 | 512 | 246 |
| symmetric-solve3 | 512 | 247 |
| unit-fraction16 | 512 | 500 |
| unit-fraction32 | 512 | 613 |
| unsigned-scalar | 512 | 599 |
| vector | 512 | 583 |
| vector-direction | 512 | 583 |
| vector-lattice | 512 | 476 |
| vector-narrow | 512 | 583 |
| vector-norm | 512 | 476 |
| vector-orthonormal-basis | 512 | 139 |

- last run: 2026-09-15
