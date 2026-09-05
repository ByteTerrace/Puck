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

- law cases executed: 583
- last run: 2026-09-05

## Deep

- law cases executed: 108
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

- covered: 2005
- waived: 40
- uncovered: 634
- total public members: 2679
- last run: 2026-09-05

## Legs

| leg kind | legs |
| --- | --- |
| classical | 786 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 44 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 27 |
| structural | 1162 |
| **total** | **2236** |

- statements: 720
- statements with no independent leg: 200
- last run: 2026-09-05

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10556 |
| binary-field | 256 | 398 |
| binary-field-axioms | 256 | 398 |
| binary-field-group | 256 | 484 |
| binary-polynomial | 256 | 487 |
| binary-polynomial-division | 256 | 487 |
| binary-polynomial-gcd | 256 | 487 |
| clifford-motor | 512 | 315 |
| clifford-multivector | 512 | 315 |
| clifford-planar-complex | 512 | 315 |
| clifford-planar-dual | 512 | 315 |
| clifford-planar-split | 512 | 315 |
| clifford-quaternion-even | 512 | 315 |
| clifford-reverse | 512 | 315 |
| closed-unit | 512 | 549 |
| complex | 512 | 10557 |
| complex-direction | 512 | 443 |
| complex-divide | 512 | 547 |
| complex-rotate | 512 | 547 |
| contribution-fold-analog | 512 | 234 |
| contribution-fold-formula | 512 | 234 |
| contribution-fold-no-pool | 512 | 234 |
| contribution-fold-order | 512 | 234 |
| contribution-fold-quantization | 512 | 234 |
| directed-magnitude | 512 | 166 |
| directed-product | 512 | 166 |
| directed-product-sum | 512 | 166 |
| directed-quotient | 512 | 166 |
| directed-root | 512 | 166 |
| dual | 512 | 8333 |
| dual-divide | 512 | 443 |
| dual-generic | 512 | 443 |
| dual-quaternion | 512 | 547 |
| dynamics | 512 | 51 |
| extension-field | 256 | 473 |
| extension-field-inverse | 256 | 391 |
| extension-field-norm | 256 | 473 |
| extension-field-power | 256 | 391 |
| extension-field-product | 256 | 473 |
| integer-hexagonal-index | 512 | 11 |
| integer-magic-constants | 512 | 12 |
| mass-box | 256 | 166 |
| mass-capsule | 256 | 166 |
| mass-compound | 256 | 166 |
| mass-cylinder | 256 | 166 |
| mass-parallel-axis | 256 | 166 |
| mass-sphere | 256 | 166 |
| mass-volume | 256 | 166 |
| meet-associative | 512 | 225 |
| meet-bottom-absorption | 512 | 225 |
| meet-commutative | 512 | 225 |
| meet-idempotent | 512 | 225 |
| meet-monotonicity | 512 | 225 |
| meet-order-coherence | 512 | 225 |
| meet-product-composition | 512 | 225 |
| meet-top-identity | 512 | 225 |
| mixed-scale | 512 | 166 |
| mixed-scale-triple | 512 | 166 |
| mobius | 512 | 8333 |
| monogenic-exact | 512 | 315 |
| monogenic-fusion | 512 | 315 |
| position | 512 | 430 |
| position-delta | 512 | 530 |
| position-translate | 512 | 530 |
| presented | 512 | 828 |
| prime-field | 256 | 478 |
| prime-field-chain | 256 | 478 |
| prime-field-lucas | 256 | 478 |
| prime-field-primality | 256 | 478 |
| prime-field-root | 256 | 478 |
| q1648-scalar | 512 | 216 |
| q1648-scalar-division | 512 | 214 |
| q3232-scalar | 512 | 179 |
| q3232-scalar-division | 512 | 178 |
| quaternion | 512 | 547 |
| quaternion-direction | 512 | 443 |
| quaternion-rotate | 512 | 443 |
| quaternion-sublattice | 256 | 443 |
| rate | 512 | 531 |
| rigid | 512 | 570 |
| rigid-direction | 512 | 454 |
| rigid-point | 512 | 570 |
| scalar | 512 | 8337 |
| scalar-division | 512 | 565 |
| scalar-text | 512 | 565 |
| scalar-transcendental | 512 | 565 |
| smoke | 64 | 2063 |
| split | 512 | 10557 |
| split-divide | 512 | 547 |
| split-transform | 512 | 547 |
| sublattice | 256 | 4493 |
| symmetric-apply2 | 512 | 166 |
| symmetric-apply3 | 512 | 166 |
| symmetric-invert2 | 256 | 209 |
| symmetric-invert3 | 256 | 209 |
| symmetric-solve2 | 512 | 209 |
| symmetric-solve3 | 512 | 210 |
| unit-fraction16 | 512 | 463 |
| unit-fraction32 | 512 | 572 |
| unsigned-scalar | 512 | 558 |
| vector | 512 | 542 |
| vector-direction | 512 | 542 |
| vector-lattice | 512 | 439 |
| vector-narrow | 512 | 542 |
| vector-norm | 512 | 439 |
| vector-orthonormal-basis | 512 | 102 |

- last run: 2026-09-05
