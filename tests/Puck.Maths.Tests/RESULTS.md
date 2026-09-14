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
- last run: 2026-09-14

## Default

- law cases executed: 616
- last run: 2026-09-14

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
- last run: 2026-09-14

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
- last run: 2026-09-14

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10594 |
| binary-field | 256 | 432 |
| binary-field-axioms | 256 | 432 |
| binary-field-group | 256 | 522 |
| binary-polynomial | 256 | 525 |
| binary-polynomial-division | 256 | 525 |
| binary-polynomial-gcd | 256 | 525 |
| clifford-motor | 512 | 349 |
| clifford-multivector | 512 | 349 |
| clifford-planar-complex | 512 | 349 |
| clifford-planar-dual | 512 | 349 |
| clifford-planar-split | 512 | 349 |
| clifford-quaternion-even | 512 | 349 |
| clifford-reverse | 512 | 349 |
| closed-unit | 512 | 583 |
| complex | 512 | 10595 |
| complex-direction | 512 | 477 |
| complex-divide | 512 | 585 |
| complex-rotate | 512 | 585 |
| contribution-fold-analog | 512 | 268 |
| contribution-fold-formula | 512 | 268 |
| contribution-fold-no-pool | 512 | 268 |
| contribution-fold-order | 512 | 268 |
| contribution-fold-quantization | 512 | 268 |
| cost-bound-arithmetic | 512 | 15 |
| cost-model-budgets | 512 | 15 |
| cost-model-conversions | 512 | 15 |
| directed-magnitude | 512 | 200 |
| directed-product | 512 | 200 |
| directed-product-sum | 512 | 200 |
| directed-quotient | 512 | 200 |
| directed-root | 512 | 200 |
| dual | 512 | 8367 |
| dual-divide | 512 | 477 |
| dual-generic | 512 | 477 |
| dual-quaternion | 512 | 585 |
| dynamics | 512 | 85 |
| extension-field | 256 | 511 |
| extension-field-inverse | 256 | 425 |
| extension-field-norm | 256 | 511 |
| extension-field-power | 256 | 425 |
| extension-field-product | 256 | 511 |
| fixed-saturate | 512 | 15 |
| integer-hexagonal-index | 512 | 49 |
| integer-magic-constants | 512 | 46 |
| mass-box | 256 | 200 |
| mass-capsule | 256 | 200 |
| mass-compound | 256 | 200 |
| mass-cylinder | 256 | 200 |
| mass-parallel-axis | 256 | 200 |
| mass-sphere | 256 | 200 |
| mass-volume | 256 | 200 |
| meet-associative | 512 | 259 |
| meet-bottom-absorption | 512 | 259 |
| meet-commutative | 512 | 259 |
| meet-idempotent | 512 | 259 |
| meet-monotonicity | 512 | 259 |
| meet-order-coherence | 512 | 259 |
| meet-product-composition | 512 | 259 |
| meet-top-identity | 512 | 259 |
| mixed-scale | 512 | 200 |
| mixed-scale-triple | 512 | 200 |
| mobius | 512 | 8367 |
| monogenic-exact | 512 | 349 |
| monogenic-fusion | 512 | 349 |
| position | 512 | 464 |
| position-delta | 512 | 568 |
| position-translate | 512 | 568 |
| presented | 512 | 866 |
| prime-field | 256 | 516 |
| prime-field-chain | 256 | 516 |
| prime-field-lucas | 256 | 516 |
| prime-field-primality | 256 | 516 |
| prime-field-root | 256 | 516 |
| q1648-scalar | 512 | 250 |
| q1648-scalar-division | 512 | 248 |
| q3232-scalar | 512 | 213 |
| q3232-scalar-division | 512 | 212 |
| quaternion | 512 | 585 |
| quaternion-direction | 512 | 477 |
| quaternion-rotate | 512 | 477 |
| quaternion-sublattice | 256 | 477 |
| rate | 512 | 569 |
| rigid | 512 | 608 |
| rigid-direction | 512 | 488 |
| rigid-point | 512 | 608 |
| scalar | 512 | 8371 |
| scalar-division | 512 | 603 |
| scalar-text | 512 | 603 |
| scalar-transcendental | 512 | 605 |
| smoke | 64 | 2097 |
| split | 512 | 10595 |
| split-divide | 512 | 585 |
| split-transform | 512 | 585 |
| square-grid | 512 | 38 |
| sublattice | 256 | 4527 |
| symmetric-apply2 | 512 | 200 |
| symmetric-apply3 | 512 | 200 |
| symmetric-invert2 | 256 | 243 |
| symmetric-invert3 | 256 | 243 |
| symmetric-solve2 | 512 | 243 |
| symmetric-solve3 | 512 | 244 |
| unit-fraction16 | 512 | 497 |
| unit-fraction32 | 512 | 610 |
| unsigned-scalar | 512 | 596 |
| vector | 512 | 580 |
| vector-direction | 512 | 580 |
| vector-lattice | 512 | 473 |
| vector-narrow | 512 | 580 |
| vector-norm | 512 | 473 |
| vector-orthonormal-basis | 512 | 136 |

- last run: 2026-09-14
