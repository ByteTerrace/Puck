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
- last run: 2026-08-29

## Default

- law cases executed: 552
- last run: 2026-08-29

## Deep

- law cases executed: 102
- last run: 2026-08-29

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1913
- waived: 40
- uncovered: 634
- total public members: 2587
- last run: 2026-08-29

## Legs

| leg kind | legs |
| --- | --- |
| classical | 766 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 43 |
| shared-substrate:fused-substrate | 35 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 17 |
| shared-substrate:shared-upstream | 20 |
| shared-substrate:transcription | 27 |
| structural | 1122 |
| **total** | **2170** |

- statements: 683
- statements with no independent leg: 181
- last run: 2026-08-29

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10523 |
| binary-field | 256 | 370 |
| binary-field-axioms | 256 | 370 |
| binary-field-group | 256 | 451 |
| binary-polynomial | 256 | 454 |
| binary-polynomial-division | 256 | 454 |
| binary-polynomial-gcd | 256 | 454 |
| clifford-motor | 512 | 287 |
| clifford-multivector | 512 | 287 |
| clifford-planar-complex | 512 | 287 |
| clifford-planar-dual | 512 | 287 |
| clifford-planar-split | 512 | 287 |
| clifford-quaternion-even | 512 | 287 |
| clifford-reverse | 512 | 287 |
| closed-unit | 512 | 521 |
| complex | 512 | 10524 |
| complex-direction | 512 | 415 |
| complex-divide | 512 | 514 |
| complex-rotate | 512 | 514 |
| contribution-fold-analog | 512 | 206 |
| contribution-fold-formula | 512 | 206 |
| contribution-fold-no-pool | 512 | 206 |
| contribution-fold-order | 512 | 206 |
| contribution-fold-quantization | 512 | 206 |
| directed-magnitude | 512 | 138 |
| directed-product | 512 | 138 |
| directed-product-sum | 512 | 138 |
| directed-quotient | 512 | 138 |
| directed-root | 512 | 138 |
| dual | 512 | 8305 |
| dual-divide | 512 | 415 |
| dual-generic | 512 | 415 |
| dual-quaternion | 512 | 514 |
| dynamics | 512 | 23 |
| extension-field | 256 | 440 |
| extension-field-inverse | 256 | 363 |
| extension-field-norm | 256 | 440 |
| extension-field-power | 256 | 363 |
| extension-field-product | 256 | 440 |
| mass-box | 256 | 138 |
| mass-capsule | 256 | 138 |
| mass-compound | 256 | 138 |
| mass-cylinder | 256 | 138 |
| mass-parallel-axis | 256 | 138 |
| mass-sphere | 256 | 138 |
| mass-volume | 256 | 138 |
| meet-associative | 512 | 197 |
| meet-bottom-absorption | 512 | 197 |
| meet-commutative | 512 | 197 |
| meet-idempotent | 512 | 197 |
| meet-monotonicity | 512 | 197 |
| meet-order-coherence | 512 | 197 |
| meet-product-composition | 512 | 197 |
| meet-top-identity | 512 | 197 |
| mixed-scale | 512 | 138 |
| mixed-scale-triple | 512 | 138 |
| mobius | 512 | 8305 |
| monogenic-exact | 512 | 287 |
| monogenic-fusion | 512 | 287 |
| position | 512 | 402 |
| position-delta | 512 | 497 |
| position-translate | 512 | 497 |
| presented | 512 | 795 |
| prime-field | 256 | 445 |
| prime-field-chain | 256 | 445 |
| prime-field-lucas | 256 | 445 |
| prime-field-primality | 256 | 445 |
| prime-field-root | 256 | 445 |
| q1648-scalar | 512 | 188 |
| q1648-scalar-division | 512 | 186 |
| q3232-scalar | 512 | 151 |
| q3232-scalar-division | 512 | 150 |
| quaternion | 512 | 514 |
| quaternion-direction | 512 | 415 |
| quaternion-rotate | 512 | 415 |
| quaternion-sublattice | 256 | 415 |
| rate | 512 | 498 |
| rigid | 512 | 537 |
| rigid-direction | 512 | 426 |
| rigid-point | 512 | 537 |
| scalar | 512 | 8309 |
| scalar-division | 512 | 532 |
| scalar-text | 512 | 532 |
| scalar-transcendental | 512 | 532 |
| smoke | 64 | 2035 |
| split | 512 | 10524 |
| split-divide | 512 | 514 |
| split-transform | 512 | 514 |
| sublattice | 256 | 4465 |
| symmetric-apply2 | 512 | 138 |
| symmetric-apply3 | 512 | 138 |
| symmetric-invert2 | 256 | 181 |
| symmetric-invert3 | 256 | 181 |
| symmetric-solve2 | 512 | 181 |
| symmetric-solve3 | 512 | 182 |
| unit-fraction16 | 512 | 435 |
| unit-fraction32 | 512 | 539 |
| unsigned-scalar | 512 | 525 |
| vector | 512 | 509 |
| vector-direction | 512 | 509 |
| vector-lattice | 512 | 411 |
| vector-narrow | 512 | 509 |
| vector-norm | 512 | 411 |
| vector-orthonormal-basis | 512 | 74 |

- last run: 2026-08-29
