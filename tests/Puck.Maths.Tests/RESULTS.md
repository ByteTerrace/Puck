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
| algebra-fractional | 512 | 10598 |
| binary-field | 256 | 436 |
| binary-field-axioms | 256 | 436 |
| binary-field-group | 256 | 526 |
| binary-polynomial | 256 | 529 |
| binary-polynomial-division | 256 | 529 |
| binary-polynomial-gcd | 256 | 529 |
| clifford-motor | 512 | 353 |
| clifford-multivector | 512 | 353 |
| clifford-planar-complex | 512 | 353 |
| clifford-planar-dual | 512 | 353 |
| clifford-planar-split | 512 | 353 |
| clifford-quaternion-even | 512 | 353 |
| clifford-reverse | 512 | 353 |
| closed-unit | 512 | 587 |
| complex | 512 | 10599 |
| complex-direction | 512 | 481 |
| complex-divide | 512 | 589 |
| complex-rotate | 512 | 589 |
| contribution-fold-analog | 512 | 272 |
| contribution-fold-formula | 512 | 272 |
| contribution-fold-no-pool | 512 | 272 |
| contribution-fold-order | 512 | 272 |
| contribution-fold-quantization | 512 | 272 |
| cost-bound-arithmetic | 512 | 19 |
| cost-model-budgets | 512 | 19 |
| cost-model-conversions | 512 | 19 |
| directed-magnitude | 512 | 204 |
| directed-product | 512 | 204 |
| directed-product-sum | 512 | 204 |
| directed-quotient | 512 | 204 |
| directed-root | 512 | 204 |
| dual | 512 | 8371 |
| dual-divide | 512 | 481 |
| dual-generic | 512 | 481 |
| dual-quaternion | 512 | 589 |
| dynamics | 512 | 89 |
| extension-field | 256 | 515 |
| extension-field-inverse | 256 | 429 |
| extension-field-norm | 256 | 515 |
| extension-field-power | 256 | 429 |
| extension-field-product | 256 | 515 |
| fixed-saturate | 512 | 19 |
| integer-hexagonal-index | 512 | 53 |
| integer-magic-constants | 512 | 50 |
| mass-box | 256 | 204 |
| mass-capsule | 256 | 204 |
| mass-compound | 256 | 204 |
| mass-cylinder | 256 | 204 |
| mass-parallel-axis | 256 | 204 |
| mass-sphere | 256 | 204 |
| mass-volume | 256 | 204 |
| meet-associative | 512 | 263 |
| meet-bottom-absorption | 512 | 263 |
| meet-commutative | 512 | 263 |
| meet-idempotent | 512 | 263 |
| meet-monotonicity | 512 | 263 |
| meet-order-coherence | 512 | 263 |
| meet-product-composition | 512 | 263 |
| meet-top-identity | 512 | 263 |
| mixed-scale | 512 | 204 |
| mixed-scale-triple | 512 | 204 |
| mobius | 512 | 8371 |
| monogenic-exact | 512 | 353 |
| monogenic-fusion | 512 | 353 |
| position | 512 | 468 |
| position-delta | 512 | 572 |
| position-translate | 512 | 572 |
| presented | 512 | 870 |
| prime-field | 256 | 520 |
| prime-field-chain | 256 | 520 |
| prime-field-lucas | 256 | 520 |
| prime-field-primality | 256 | 520 |
| prime-field-root | 256 | 520 |
| q1648-scalar | 512 | 254 |
| q1648-scalar-division | 512 | 252 |
| q3232-scalar | 512 | 217 |
| q3232-scalar-division | 512 | 216 |
| quaternion | 512 | 589 |
| quaternion-direction | 512 | 481 |
| quaternion-rotate | 512 | 481 |
| quaternion-sublattice | 256 | 481 |
| rate | 512 | 573 |
| rigid | 512 | 612 |
| rigid-direction | 512 | 492 |
| rigid-point | 512 | 612 |
| scalar | 512 | 8375 |
| scalar-division | 512 | 607 |
| scalar-text | 512 | 607 |
| scalar-transcendental | 512 | 609 |
| smoke | 64 | 2101 |
| split | 512 | 10599 |
| split-divide | 512 | 589 |
| split-transform | 512 | 589 |
| square-grid | 512 | 42 |
| sublattice | 256 | 4531 |
| symmetric-apply2 | 512 | 204 |
| symmetric-apply3 | 512 | 204 |
| symmetric-invert2 | 256 | 247 |
| symmetric-invert3 | 256 | 247 |
| symmetric-solve2 | 512 | 247 |
| symmetric-solve3 | 512 | 248 |
| unit-fraction16 | 512 | 501 |
| unit-fraction32 | 512 | 614 |
| unsigned-scalar | 512 | 600 |
| vector | 512 | 584 |
| vector-direction | 512 | 584 |
| vector-lattice | 512 | 477 |
| vector-narrow | 512 | 584 |
| vector-norm | 512 | 477 |
| vector-orthonormal-basis | 512 | 140 |

- last run: 2026-09-15
