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

- law cases executed: 591
- last run: 2026-09-05

## Deep

- law cases executed: 111
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

- covered: 2047
- waived: 40
- uncovered: 634
- total public members: 2721
- last run: 2026-09-05

## Legs

| leg kind | legs |
| --- | --- |
| classical | 797 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 44 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 27 |
| structural | 1163 |
| **total** | **2248** |

- statements: 731
- statements with no independent leg: 200
- last run: 2026-09-05

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10563 |
| binary-field | 256 | 404 |
| binary-field-axioms | 256 | 404 |
| binary-field-group | 256 | 491 |
| binary-polynomial | 256 | 494 |
| binary-polynomial-division | 256 | 494 |
| binary-polynomial-gcd | 256 | 494 |
| clifford-motor | 512 | 321 |
| clifford-multivector | 512 | 321 |
| clifford-planar-complex | 512 | 321 |
| clifford-planar-dual | 512 | 321 |
| clifford-planar-split | 512 | 321 |
| clifford-quaternion-even | 512 | 321 |
| clifford-reverse | 512 | 321 |
| closed-unit | 512 | 555 |
| complex | 512 | 10564 |
| complex-direction | 512 | 449 |
| complex-divide | 512 | 554 |
| complex-rotate | 512 | 554 |
| contribution-fold-analog | 512 | 240 |
| contribution-fold-formula | 512 | 240 |
| contribution-fold-no-pool | 512 | 240 |
| contribution-fold-order | 512 | 240 |
| contribution-fold-quantization | 512 | 240 |
| directed-magnitude | 512 | 172 |
| directed-product | 512 | 172 |
| directed-product-sum | 512 | 172 |
| directed-quotient | 512 | 172 |
| directed-root | 512 | 172 |
| dual | 512 | 8339 |
| dual-divide | 512 | 449 |
| dual-generic | 512 | 449 |
| dual-quaternion | 512 | 554 |
| dynamics | 512 | 57 |
| extension-field | 256 | 480 |
| extension-field-inverse | 256 | 397 |
| extension-field-norm | 256 | 480 |
| extension-field-power | 256 | 397 |
| extension-field-product | 256 | 480 |
| integer-hexagonal-index | 512 | 18 |
| integer-magic-constants | 512 | 18 |
| mass-box | 256 | 172 |
| mass-capsule | 256 | 172 |
| mass-compound | 256 | 172 |
| mass-cylinder | 256 | 172 |
| mass-parallel-axis | 256 | 172 |
| mass-sphere | 256 | 172 |
| mass-volume | 256 | 172 |
| meet-associative | 512 | 231 |
| meet-bottom-absorption | 512 | 231 |
| meet-commutative | 512 | 231 |
| meet-idempotent | 512 | 231 |
| meet-monotonicity | 512 | 231 |
| meet-order-coherence | 512 | 231 |
| meet-product-composition | 512 | 231 |
| meet-top-identity | 512 | 231 |
| mixed-scale | 512 | 172 |
| mixed-scale-triple | 512 | 172 |
| mobius | 512 | 8339 |
| monogenic-exact | 512 | 321 |
| monogenic-fusion | 512 | 321 |
| position | 512 | 436 |
| position-delta | 512 | 537 |
| position-translate | 512 | 537 |
| presented | 512 | 835 |
| prime-field | 256 | 485 |
| prime-field-chain | 256 | 485 |
| prime-field-lucas | 256 | 485 |
| prime-field-primality | 256 | 485 |
| prime-field-root | 256 | 485 |
| q1648-scalar | 512 | 222 |
| q1648-scalar-division | 512 | 220 |
| q3232-scalar | 512 | 185 |
| q3232-scalar-division | 512 | 184 |
| quaternion | 512 | 554 |
| quaternion-direction | 512 | 449 |
| quaternion-rotate | 512 | 449 |
| quaternion-sublattice | 256 | 449 |
| rate | 512 | 538 |
| rigid | 512 | 577 |
| rigid-direction | 512 | 460 |
| rigid-point | 512 | 577 |
| scalar | 512 | 8343 |
| scalar-division | 512 | 572 |
| scalar-text | 512 | 572 |
| scalar-transcendental | 512 | 574 |
| smoke | 64 | 2069 |
| split | 512 | 10564 |
| split-divide | 512 | 554 |
| split-transform | 512 | 554 |
| square-grid | 512 | 7 |
| sublattice | 256 | 4499 |
| symmetric-apply2 | 512 | 172 |
| symmetric-apply3 | 512 | 172 |
| symmetric-invert2 | 256 | 215 |
| symmetric-invert3 | 256 | 215 |
| symmetric-solve2 | 512 | 215 |
| symmetric-solve3 | 512 | 216 |
| unit-fraction16 | 512 | 469 |
| unit-fraction32 | 512 | 579 |
| unsigned-scalar | 512 | 565 |
| vector | 512 | 549 |
| vector-direction | 512 | 549 |
| vector-lattice | 512 | 445 |
| vector-narrow | 512 | 549 |
| vector-norm | 512 | 445 |
| vector-orthonormal-basis | 512 | 108 |

- last run: 2026-09-05
