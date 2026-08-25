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
- last run: 2026-08-25

## Default

- law cases executed: 541
- last run: 2026-08-25

## Deep

- law cases executed: 102
- last run: 2026-08-25

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1851
- waived: 36
- uncovered: 634
- total public members: 2521
- last run: 2026-08-25

## Legs

| leg kind | legs |
| --- | --- |
| classical | 763 |
| in-tree-independent | 28 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 43 |
| shared-substrate:fused-substrate | 35 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 17 |
| shared-substrate:shared-upstream | 20 |
| shared-substrate:transcription | 27 |
| structural | 1113 |
| **total** | **2155** |

- statements: 672
- statements with no independent leg: 175
- last run: 2026-08-25

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10508 |
| binary-field | 256 | 358 |
| binary-field-axioms | 256 | 358 |
| binary-field-group | 256 | 436 |
| binary-polynomial | 256 | 439 |
| binary-polynomial-division | 256 | 439 |
| binary-polynomial-gcd | 256 | 439 |
| clifford-motor | 512 | 275 |
| clifford-multivector | 512 | 275 |
| clifford-planar-complex | 512 | 275 |
| clifford-planar-dual | 512 | 275 |
| clifford-planar-split | 512 | 275 |
| clifford-quaternion-even | 512 | 275 |
| clifford-reverse | 512 | 275 |
| closed-unit | 512 | 509 |
| complex | 512 | 10509 |
| complex-direction | 512 | 403 |
| complex-divide | 512 | 499 |
| complex-rotate | 512 | 499 |
| contribution-fold-analog | 512 | 194 |
| contribution-fold-formula | 512 | 194 |
| contribution-fold-no-pool | 512 | 194 |
| contribution-fold-order | 512 | 194 |
| contribution-fold-quantization | 512 | 194 |
| directed-magnitude | 512 | 126 |
| directed-product | 512 | 126 |
| directed-product-sum | 512 | 126 |
| directed-quotient | 512 | 126 |
| directed-root | 512 | 126 |
| dual | 512 | 8293 |
| dual-divide | 512 | 403 |
| dual-generic | 512 | 403 |
| dual-quaternion | 512 | 499 |
| dynamics | 512 | 11 |
| extension-field | 256 | 425 |
| extension-field-inverse | 256 | 351 |
| extension-field-norm | 256 | 425 |
| extension-field-power | 256 | 351 |
| extension-field-product | 256 | 425 |
| mass-box | 256 | 126 |
| mass-capsule | 256 | 126 |
| mass-compound | 256 | 126 |
| mass-cylinder | 256 | 126 |
| mass-parallel-axis | 256 | 126 |
| mass-sphere | 256 | 126 |
| mass-volume | 256 | 126 |
| meet-associative | 512 | 185 |
| meet-bottom-absorption | 512 | 185 |
| meet-commutative | 512 | 185 |
| meet-idempotent | 512 | 185 |
| meet-monotonicity | 512 | 185 |
| meet-order-coherence | 512 | 185 |
| meet-product-composition | 512 | 185 |
| meet-top-identity | 512 | 185 |
| mixed-scale | 512 | 126 |
| mixed-scale-triple | 512 | 126 |
| mobius | 512 | 8293 |
| monogenic-exact | 512 | 275 |
| monogenic-fusion | 512 | 275 |
| position | 512 | 390 |
| position-delta | 512 | 482 |
| position-translate | 512 | 482 |
| presented | 512 | 780 |
| prime-field | 256 | 430 |
| prime-field-chain | 256 | 430 |
| prime-field-lucas | 256 | 430 |
| prime-field-primality | 256 | 430 |
| prime-field-root | 256 | 430 |
| q1648-scalar | 512 | 176 |
| q1648-scalar-division | 512 | 174 |
| q3232-scalar | 512 | 139 |
| q3232-scalar-division | 512 | 138 |
| quaternion | 512 | 499 |
| quaternion-direction | 512 | 403 |
| quaternion-rotate | 512 | 403 |
| quaternion-sublattice | 256 | 403 |
| rate | 512 | 483 |
| rigid | 512 | 522 |
| rigid-direction | 512 | 414 |
| rigid-point | 512 | 522 |
| scalar | 512 | 8297 |
| scalar-division | 512 | 517 |
| scalar-text | 512 | 517 |
| scalar-transcendental | 512 | 517 |
| smoke | 64 | 2023 |
| split | 512 | 10509 |
| split-divide | 512 | 499 |
| split-transform | 512 | 499 |
| sublattice | 256 | 4453 |
| symmetric-apply2 | 512 | 126 |
| symmetric-apply3 | 512 | 126 |
| symmetric-invert2 | 256 | 169 |
| symmetric-invert3 | 256 | 169 |
| symmetric-solve2 | 512 | 169 |
| symmetric-solve3 | 512 | 170 |
| unit-fraction16 | 512 | 423 |
| unit-fraction32 | 512 | 524 |
| unsigned-scalar | 512 | 510 |
| vector | 512 | 494 |
| vector-direction | 512 | 494 |
| vector-lattice | 512 | 399 |
| vector-narrow | 512 | 494 |
| vector-norm | 512 | 399 |
| vector-orthonormal-basis | 512 | 62 |

- last run: 2026-08-25
