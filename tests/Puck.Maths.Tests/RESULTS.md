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
- last run: 2026-09-20

## Default

- law cases executed: 616
- last run: 2026-09-20

## Deep

- law cases executed: 112
- last run: 2026-09-20

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
- last run: 2026-09-20

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
- last run: 2026-09-20

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10604 |
| binary-field | 256 | 441 |
| binary-field-axioms | 256 | 441 |
| binary-field-group | 256 | 532 |
| binary-polynomial | 256 | 535 |
| binary-polynomial-division | 256 | 535 |
| binary-polynomial-gcd | 256 | 535 |
| clifford-motor | 512 | 358 |
| clifford-multivector | 512 | 358 |
| clifford-planar-complex | 512 | 358 |
| clifford-planar-dual | 512 | 358 |
| clifford-planar-split | 512 | 358 |
| clifford-quaternion-even | 512 | 358 |
| clifford-reverse | 512 | 358 |
| closed-unit | 512 | 592 |
| complex | 512 | 10605 |
| complex-direction | 512 | 486 |
| complex-divide | 512 | 595 |
| complex-rotate | 512 | 595 |
| contribution-fold-analog | 512 | 277 |
| contribution-fold-formula | 512 | 277 |
| contribution-fold-no-pool | 512 | 277 |
| contribution-fold-order | 512 | 277 |
| contribution-fold-quantization | 512 | 277 |
| cost-bound-arithmetic | 512 | 24 |
| cost-model-budgets | 512 | 24 |
| cost-model-conversions | 512 | 24 |
| directed-magnitude | 512 | 209 |
| directed-product | 512 | 209 |
| directed-product-sum | 512 | 209 |
| directed-quotient | 512 | 209 |
| directed-root | 512 | 209 |
| dual | 512 | 8376 |
| dual-divide | 512 | 486 |
| dual-generic | 512 | 486 |
| dual-quaternion | 512 | 595 |
| dynamics | 512 | 94 |
| extension-field | 256 | 521 |
| extension-field-inverse | 256 | 434 |
| extension-field-norm | 256 | 521 |
| extension-field-power | 256 | 434 |
| extension-field-product | 256 | 521 |
| fixed-saturate | 512 | 24 |
| integer-hexagonal-index | 512 | 59 |
| integer-magic-constants | 512 | 55 |
| mass-box | 256 | 209 |
| mass-capsule | 256 | 209 |
| mass-compound | 256 | 209 |
| mass-cylinder | 256 | 209 |
| mass-parallel-axis | 256 | 209 |
| mass-sphere | 256 | 209 |
| mass-volume | 256 | 209 |
| meet-associative | 512 | 268 |
| meet-bottom-absorption | 512 | 268 |
| meet-commutative | 512 | 268 |
| meet-idempotent | 512 | 268 |
| meet-monotonicity | 512 | 268 |
| meet-order-coherence | 512 | 268 |
| meet-product-composition | 512 | 268 |
| meet-top-identity | 512 | 268 |
| mixed-scale | 512 | 209 |
| mixed-scale-triple | 512 | 209 |
| mobius | 512 | 8376 |
| monogenic-exact | 512 | 358 |
| monogenic-fusion | 512 | 358 |
| position | 512 | 473 |
| position-delta | 512 | 578 |
| position-translate | 512 | 578 |
| presented | 512 | 876 |
| prime-field | 256 | 526 |
| prime-field-chain | 256 | 526 |
| prime-field-lucas | 256 | 526 |
| prime-field-primality | 256 | 526 |
| prime-field-root | 256 | 526 |
| q1648-scalar | 512 | 259 |
| q1648-scalar-division | 512 | 257 |
| q3232-scalar | 512 | 222 |
| q3232-scalar-division | 512 | 221 |
| quaternion | 512 | 595 |
| quaternion-direction | 512 | 486 |
| quaternion-rotate | 512 | 486 |
| quaternion-sublattice | 256 | 486 |
| rate | 512 | 579 |
| rigid | 512 | 618 |
| rigid-direction | 512 | 497 |
| rigid-point | 512 | 618 |
| scalar | 512 | 8380 |
| scalar-division | 512 | 613 |
| scalar-text | 512 | 613 |
| scalar-transcendental | 512 | 615 |
| smoke | 64 | 2106 |
| split | 512 | 10605 |
| split-divide | 512 | 595 |
| split-transform | 512 | 595 |
| square-grid | 512 | 48 |
| sublattice | 256 | 4536 |
| symmetric-apply2 | 512 | 209 |
| symmetric-apply3 | 512 | 209 |
| symmetric-invert2 | 256 | 252 |
| symmetric-invert3 | 256 | 252 |
| symmetric-solve2 | 512 | 252 |
| symmetric-solve3 | 512 | 253 |
| unit-fraction16 | 512 | 506 |
| unit-fraction32 | 512 | 620 |
| unsigned-scalar | 512 | 606 |
| vector | 512 | 590 |
| vector-direction | 512 | 590 |
| vector-lattice | 512 | 482 |
| vector-narrow | 512 | 590 |
| vector-norm | 512 | 482 |
| vector-orthonormal-basis | 512 | 145 |

- last run: 2026-09-20
