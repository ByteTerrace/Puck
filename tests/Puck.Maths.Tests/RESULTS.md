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
- last run: 2026-09-03

## Default

- law cases executed: 572
- last run: 2026-09-03

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

- covered: 1975
- waived: 40
- uncovered: 634
- total public members: 2649
- last run: 2026-09-03

## Legs

| leg kind | legs |
| --- | --- |
| classical | 774 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 44 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 27 |
| structural | 1157 |
| **total** | **2219** |

- statements: 707
- statements with no independent leg: 199
- last run: 2026-09-03

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10540 |
| binary-field | 256 | 387 |
| binary-field-axioms | 256 | 387 |
| binary-field-group | 256 | 468 |
| binary-polynomial | 256 | 471 |
| binary-polynomial-division | 256 | 471 |
| binary-polynomial-gcd | 256 | 471 |
| clifford-motor | 512 | 304 |
| clifford-multivector | 512 | 304 |
| clifford-planar-complex | 512 | 304 |
| clifford-planar-dual | 512 | 304 |
| clifford-planar-split | 512 | 304 |
| clifford-quaternion-even | 512 | 304 |
| clifford-reverse | 512 | 304 |
| closed-unit | 512 | 538 |
| complex | 512 | 10541 |
| complex-direction | 512 | 432 |
| complex-divide | 512 | 531 |
| complex-rotate | 512 | 531 |
| contribution-fold-analog | 512 | 223 |
| contribution-fold-formula | 512 | 223 |
| contribution-fold-no-pool | 512 | 223 |
| contribution-fold-order | 512 | 223 |
| contribution-fold-quantization | 512 | 223 |
| directed-magnitude | 512 | 155 |
| directed-product | 512 | 155 |
| directed-product-sum | 512 | 155 |
| directed-quotient | 512 | 155 |
| directed-root | 512 | 155 |
| dual | 512 | 8322 |
| dual-divide | 512 | 432 |
| dual-generic | 512 | 432 |
| dual-quaternion | 512 | 531 |
| dynamics | 512 | 40 |
| extension-field | 256 | 457 |
| extension-field-inverse | 256 | 380 |
| extension-field-norm | 256 | 457 |
| extension-field-power | 256 | 380 |
| extension-field-product | 256 | 457 |
| mass-box | 256 | 155 |
| mass-capsule | 256 | 155 |
| mass-compound | 256 | 155 |
| mass-cylinder | 256 | 155 |
| mass-parallel-axis | 256 | 155 |
| mass-sphere | 256 | 155 |
| mass-volume | 256 | 155 |
| meet-associative | 512 | 214 |
| meet-bottom-absorption | 512 | 214 |
| meet-commutative | 512 | 214 |
| meet-idempotent | 512 | 214 |
| meet-monotonicity | 512 | 214 |
| meet-order-coherence | 512 | 214 |
| meet-product-composition | 512 | 214 |
| meet-top-identity | 512 | 214 |
| mixed-scale | 512 | 155 |
| mixed-scale-triple | 512 | 155 |
| mobius | 512 | 8322 |
| monogenic-exact | 512 | 304 |
| monogenic-fusion | 512 | 304 |
| position | 512 | 419 |
| position-delta | 512 | 514 |
| position-translate | 512 | 514 |
| presented | 512 | 812 |
| prime-field | 256 | 462 |
| prime-field-chain | 256 | 462 |
| prime-field-lucas | 256 | 462 |
| prime-field-primality | 256 | 462 |
| prime-field-root | 256 | 462 |
| q1648-scalar | 512 | 205 |
| q1648-scalar-division | 512 | 203 |
| q3232-scalar | 512 | 168 |
| q3232-scalar-division | 512 | 167 |
| quaternion | 512 | 531 |
| quaternion-direction | 512 | 432 |
| quaternion-rotate | 512 | 432 |
| quaternion-sublattice | 256 | 432 |
| rate | 512 | 515 |
| rigid | 512 | 554 |
| rigid-direction | 512 | 443 |
| rigid-point | 512 | 554 |
| scalar | 512 | 8326 |
| scalar-division | 512 | 549 |
| scalar-text | 512 | 549 |
| scalar-transcendental | 512 | 549 |
| smoke | 64 | 2052 |
| split | 512 | 10541 |
| split-divide | 512 | 531 |
| split-transform | 512 | 531 |
| sublattice | 256 | 4482 |
| symmetric-apply2 | 512 | 155 |
| symmetric-apply3 | 512 | 155 |
| symmetric-invert2 | 256 | 198 |
| symmetric-invert3 | 256 | 198 |
| symmetric-solve2 | 512 | 198 |
| symmetric-solve3 | 512 | 199 |
| unit-fraction16 | 512 | 452 |
| unit-fraction32 | 512 | 556 |
| unsigned-scalar | 512 | 542 |
| vector | 512 | 526 |
| vector-direction | 512 | 526 |
| vector-lattice | 512 | 428 |
| vector-narrow | 512 | 526 |
| vector-norm | 512 | 428 |
| vector-orthonormal-basis | 512 | 91 |

- last run: 2026-09-03
