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
- last run: 2026-09-19

## Default

- law cases executed: 616
- last run: 2026-09-19

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

- covered: 2118
- waived: 43
- uncovered: 634
- total public members: 2795
- last run: 2026-09-19

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
- last run: 2026-09-19

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10601 |
| binary-field | 256 | 439 |
| binary-field-axioms | 256 | 439 |
| binary-field-group | 256 | 529 |
| binary-polynomial | 256 | 532 |
| binary-polynomial-division | 256 | 532 |
| binary-polynomial-gcd | 256 | 532 |
| clifford-motor | 512 | 356 |
| clifford-multivector | 512 | 356 |
| clifford-planar-complex | 512 | 356 |
| clifford-planar-dual | 512 | 356 |
| clifford-planar-split | 512 | 356 |
| clifford-quaternion-even | 512 | 356 |
| clifford-reverse | 512 | 356 |
| closed-unit | 512 | 590 |
| complex | 512 | 10602 |
| complex-direction | 512 | 484 |
| complex-divide | 512 | 592 |
| complex-rotate | 512 | 592 |
| contribution-fold-analog | 512 | 275 |
| contribution-fold-formula | 512 | 275 |
| contribution-fold-no-pool | 512 | 275 |
| contribution-fold-order | 512 | 275 |
| contribution-fold-quantization | 512 | 275 |
| cost-bound-arithmetic | 512 | 22 |
| cost-model-budgets | 512 | 22 |
| cost-model-conversions | 512 | 22 |
| directed-magnitude | 512 | 207 |
| directed-product | 512 | 207 |
| directed-product-sum | 512 | 207 |
| directed-quotient | 512 | 207 |
| directed-root | 512 | 207 |
| dual | 512 | 8374 |
| dual-divide | 512 | 484 |
| dual-generic | 512 | 484 |
| dual-quaternion | 512 | 592 |
| dynamics | 512 | 92 |
| extension-field | 256 | 518 |
| extension-field-inverse | 256 | 432 |
| extension-field-norm | 256 | 518 |
| extension-field-power | 256 | 432 |
| extension-field-product | 256 | 518 |
| fixed-saturate | 512 | 22 |
| integer-hexagonal-index | 512 | 56 |
| integer-magic-constants | 512 | 53 |
| mass-box | 256 | 207 |
| mass-capsule | 256 | 207 |
| mass-compound | 256 | 207 |
| mass-cylinder | 256 | 207 |
| mass-parallel-axis | 256 | 207 |
| mass-sphere | 256 | 207 |
| mass-volume | 256 | 207 |
| meet-associative | 512 | 266 |
| meet-bottom-absorption | 512 | 266 |
| meet-commutative | 512 | 266 |
| meet-idempotent | 512 | 266 |
| meet-monotonicity | 512 | 266 |
| meet-order-coherence | 512 | 266 |
| meet-product-composition | 512 | 266 |
| meet-top-identity | 512 | 266 |
| mixed-scale | 512 | 207 |
| mixed-scale-triple | 512 | 207 |
| mobius | 512 | 8374 |
| monogenic-exact | 512 | 356 |
| monogenic-fusion | 512 | 356 |
| position | 512 | 471 |
| position-delta | 512 | 575 |
| position-translate | 512 | 575 |
| presented | 512 | 873 |
| prime-field | 256 | 523 |
| prime-field-chain | 256 | 523 |
| prime-field-lucas | 256 | 523 |
| prime-field-primality | 256 | 523 |
| prime-field-root | 256 | 523 |
| q1648-scalar | 512 | 257 |
| q1648-scalar-division | 512 | 255 |
| q3232-scalar | 512 | 220 |
| q3232-scalar-division | 512 | 219 |
| quaternion | 512 | 592 |
| quaternion-direction | 512 | 484 |
| quaternion-rotate | 512 | 484 |
| quaternion-sublattice | 256 | 484 |
| rate | 512 | 576 |
| rigid | 512 | 615 |
| rigid-direction | 512 | 495 |
| rigid-point | 512 | 615 |
| scalar | 512 | 8378 |
| scalar-division | 512 | 610 |
| scalar-text | 512 | 610 |
| scalar-transcendental | 512 | 612 |
| smoke | 64 | 2104 |
| split | 512 | 10602 |
| split-divide | 512 | 592 |
| split-transform | 512 | 592 |
| square-grid | 512 | 45 |
| sublattice | 256 | 4534 |
| symmetric-apply2 | 512 | 207 |
| symmetric-apply3 | 512 | 207 |
| symmetric-invert2 | 256 | 250 |
| symmetric-invert3 | 256 | 250 |
| symmetric-solve2 | 512 | 250 |
| symmetric-solve3 | 512 | 251 |
| unit-fraction16 | 512 | 504 |
| unit-fraction32 | 512 | 617 |
| unsigned-scalar | 512 | 603 |
| vector | 512 | 587 |
| vector-direction | 512 | 587 |
| vector-lattice | 512 | 480 |
| vector-narrow | 512 | 587 |
| vector-norm | 512 | 480 |
| vector-orthonormal-basis | 512 | 143 |

- last run: 2026-09-19
