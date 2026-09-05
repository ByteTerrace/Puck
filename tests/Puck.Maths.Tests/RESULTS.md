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
- last run: 2026-09-05

## Default

- law cases executed: 576
- last run: 2026-09-05

## Deep

- law cases executed: 106
- last run: 2026-09-05

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1977
- waived: 40
- uncovered: 634
- total public members: 2651
- last run: 2026-09-05

## Legs

| leg kind | legs |
| --- | --- |
| classical | 778 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 44 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 27 |
| structural | 1158 |
| **total** | **2224** |

- statements: 711
- statements with no independent leg: 199
- last run: 2026-09-05

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10545 |
| binary-field | 256 | 391 |
| binary-field-axioms | 256 | 391 |
| binary-field-group | 256 | 473 |
| binary-polynomial | 256 | 476 |
| binary-polynomial-division | 256 | 476 |
| binary-polynomial-gcd | 256 | 476 |
| clifford-motor | 512 | 308 |
| clifford-multivector | 512 | 308 |
| clifford-planar-complex | 512 | 308 |
| clifford-planar-dual | 512 | 308 |
| clifford-planar-split | 512 | 308 |
| clifford-quaternion-even | 512 | 308 |
| clifford-reverse | 512 | 308 |
| closed-unit | 512 | 542 |
| complex | 512 | 10546 |
| complex-direction | 512 | 436 |
| complex-divide | 512 | 536 |
| complex-rotate | 512 | 536 |
| contribution-fold-analog | 512 | 227 |
| contribution-fold-formula | 512 | 227 |
| contribution-fold-no-pool | 512 | 227 |
| contribution-fold-order | 512 | 227 |
| contribution-fold-quantization | 512 | 227 |
| directed-magnitude | 512 | 159 |
| directed-product | 512 | 159 |
| directed-product-sum | 512 | 159 |
| directed-quotient | 512 | 159 |
| directed-root | 512 | 159 |
| dual | 512 | 8326 |
| dual-divide | 512 | 436 |
| dual-generic | 512 | 436 |
| dual-quaternion | 512 | 536 |
| dynamics | 512 | 44 |
| extension-field | 256 | 462 |
| extension-field-inverse | 256 | 384 |
| extension-field-norm | 256 | 462 |
| extension-field-power | 256 | 384 |
| extension-field-product | 256 | 462 |
| integer-magic-constants | 512 | 5 |
| mass-box | 256 | 159 |
| mass-capsule | 256 | 159 |
| mass-compound | 256 | 159 |
| mass-cylinder | 256 | 159 |
| mass-parallel-axis | 256 | 159 |
| mass-sphere | 256 | 159 |
| mass-volume | 256 | 159 |
| meet-associative | 512 | 218 |
| meet-bottom-absorption | 512 | 218 |
| meet-commutative | 512 | 218 |
| meet-idempotent | 512 | 218 |
| meet-monotonicity | 512 | 218 |
| meet-order-coherence | 512 | 218 |
| meet-product-composition | 512 | 218 |
| meet-top-identity | 512 | 218 |
| mixed-scale | 512 | 159 |
| mixed-scale-triple | 512 | 159 |
| mobius | 512 | 8326 |
| monogenic-exact | 512 | 308 |
| monogenic-fusion | 512 | 308 |
| position | 512 | 423 |
| position-delta | 512 | 519 |
| position-translate | 512 | 519 |
| presented | 512 | 817 |
| prime-field | 256 | 467 |
| prime-field-chain | 256 | 467 |
| prime-field-lucas | 256 | 467 |
| prime-field-primality | 256 | 467 |
| prime-field-root | 256 | 467 |
| q1648-scalar | 512 | 209 |
| q1648-scalar-division | 512 | 207 |
| q3232-scalar | 512 | 172 |
| q3232-scalar-division | 512 | 171 |
| quaternion | 512 | 536 |
| quaternion-direction | 512 | 436 |
| quaternion-rotate | 512 | 436 |
| quaternion-sublattice | 256 | 436 |
| rate | 512 | 520 |
| rigid | 512 | 559 |
| rigid-direction | 512 | 447 |
| rigid-point | 512 | 559 |
| scalar | 512 | 8330 |
| scalar-division | 512 | 554 |
| scalar-text | 512 | 554 |
| scalar-transcendental | 512 | 554 |
| smoke | 64 | 2056 |
| split | 512 | 10546 |
| split-divide | 512 | 536 |
| split-transform | 512 | 536 |
| sublattice | 256 | 4486 |
| symmetric-apply2 | 512 | 159 |
| symmetric-apply3 | 512 | 159 |
| symmetric-invert2 | 256 | 202 |
| symmetric-invert3 | 256 | 202 |
| symmetric-solve2 | 512 | 202 |
| symmetric-solve3 | 512 | 203 |
| unit-fraction16 | 512 | 456 |
| unit-fraction32 | 512 | 561 |
| unsigned-scalar | 512 | 547 |
| vector | 512 | 531 |
| vector-direction | 512 | 531 |
| vector-lattice | 512 | 432 |
| vector-narrow | 512 | 531 |
| vector-norm | 512 | 432 |
| vector-orthonormal-basis | 512 | 95 |

- last run: 2026-09-05
