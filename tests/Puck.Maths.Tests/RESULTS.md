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
- last run: 2026-08-24

## Default

- law cases executed: 535
- last run: 2026-08-24

## Deep

- law cases executed: 102
- last run: 2026-08-23

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1848
- waived: 36
- uncovered: 634
- total public members: 2518
- last run: 2026-08-24

## Legs

| leg kind | legs |
| --- | --- |
| classical | 760 |
| in-tree-independent | 28 |
| presented-twin | 9 |
| relative-canary | 17 |
| shared-substrate:delegation-twin | 42 |
| shared-substrate:fused-substrate | 35 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 17 |
| shared-substrate:shared-upstream | 18 |
| shared-substrate:transcription | 27 |
| structural | 1107 |
| **total** | **2142** |

- statements: 666
- statements with no independent leg: 172
- last run: 2026-08-24

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10503 |
| binary-field | 256 | 354 |
| binary-field-axioms | 256 | 354 |
| binary-field-group | 256 | 431 |
| binary-polynomial | 256 | 434 |
| binary-polynomial-division | 256 | 434 |
| binary-polynomial-gcd | 256 | 434 |
| clifford-motor | 512 | 271 |
| clifford-multivector | 512 | 271 |
| clifford-planar-complex | 512 | 271 |
| clifford-planar-dual | 512 | 271 |
| clifford-planar-split | 512 | 271 |
| clifford-quaternion-even | 512 | 271 |
| clifford-reverse | 512 | 271 |
| closed-unit | 512 | 505 |
| complex | 512 | 10504 |
| complex-direction | 512 | 399 |
| complex-divide | 512 | 494 |
| complex-rotate | 512 | 494 |
| contribution-fold-analog | 512 | 190 |
| contribution-fold-formula | 512 | 190 |
| contribution-fold-no-pool | 512 | 190 |
| contribution-fold-order | 512 | 190 |
| contribution-fold-quantization | 512 | 190 |
| directed-magnitude | 512 | 122 |
| directed-product | 512 | 122 |
| directed-product-sum | 512 | 122 |
| directed-quotient | 512 | 122 |
| directed-root | 512 | 122 |
| dual | 512 | 8289 |
| dual-divide | 512 | 399 |
| dual-generic | 512 | 399 |
| dual-quaternion | 512 | 494 |
| dynamics | 512 | 7 |
| extension-field | 256 | 420 |
| extension-field-inverse | 256 | 347 |
| extension-field-norm | 256 | 420 |
| extension-field-power | 256 | 347 |
| extension-field-product | 256 | 420 |
| mass-box | 256 | 122 |
| mass-capsule | 256 | 122 |
| mass-compound | 256 | 122 |
| mass-cylinder | 256 | 122 |
| mass-parallel-axis | 256 | 122 |
| mass-sphere | 256 | 122 |
| mass-volume | 256 | 122 |
| meet-associative | 512 | 181 |
| meet-bottom-absorption | 512 | 181 |
| meet-commutative | 512 | 181 |
| meet-idempotent | 512 | 181 |
| meet-monotonicity | 512 | 181 |
| meet-order-coherence | 512 | 181 |
| meet-product-composition | 512 | 181 |
| meet-top-identity | 512 | 181 |
| mixed-scale | 512 | 122 |
| mixed-scale-triple | 512 | 122 |
| mobius | 512 | 8289 |
| monogenic-exact | 512 | 271 |
| monogenic-fusion | 512 | 271 |
| position | 512 | 386 |
| position-delta | 512 | 477 |
| position-translate | 512 | 477 |
| presented | 512 | 775 |
| prime-field | 256 | 425 |
| prime-field-chain | 256 | 425 |
| prime-field-lucas | 256 | 425 |
| prime-field-primality | 256 | 425 |
| prime-field-root | 256 | 425 |
| q1648-scalar | 512 | 172 |
| q1648-scalar-division | 512 | 170 |
| q3232-scalar | 512 | 135 |
| q3232-scalar-division | 512 | 134 |
| quaternion | 512 | 494 |
| quaternion-direction | 512 | 399 |
| quaternion-rotate | 512 | 399 |
| quaternion-sublattice | 256 | 399 |
| rate | 512 | 478 |
| rigid | 512 | 517 |
| rigid-direction | 512 | 410 |
| rigid-point | 512 | 517 |
| scalar | 512 | 8293 |
| scalar-division | 512 | 512 |
| scalar-text | 512 | 512 |
| scalar-transcendental | 512 | 512 |
| smoke | 64 | 2019 |
| split | 512 | 10504 |
| split-divide | 512 | 494 |
| split-transform | 512 | 494 |
| sublattice | 256 | 4449 |
| symmetric-apply2 | 512 | 122 |
| symmetric-apply3 | 512 | 122 |
| symmetric-invert2 | 256 | 165 |
| symmetric-invert3 | 256 | 165 |
| symmetric-solve2 | 512 | 165 |
| symmetric-solve3 | 512 | 166 |
| unit-fraction16 | 512 | 419 |
| unit-fraction32 | 512 | 519 |
| unsigned-scalar | 512 | 505 |
| vector | 512 | 489 |
| vector-direction | 512 | 489 |
| vector-lattice | 512 | 395 |
| vector-narrow | 512 | 489 |
| vector-norm | 512 | 395 |
| vector-orthonormal-basis | 512 | 58 |

- last run: 2026-08-24
