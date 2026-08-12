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
- last run: 2026-08-11

## Default

- law cases executed: 500
- last run: 2026-08-11

## Deep

- law cases executed: 99
- last run: 2026-08-09

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1654
- waived: 10
- uncovered: 634
- total public members: 2298
- last run: 2026-08-11

## Legs

| leg kind | legs |
| --- | --- |
| classical | 744 |
| in-tree-independent | 27 |
| presented-twin | 9 |
| relative-canary | 17 |
| shared-substrate:delegation-twin | 42 |
| shared-substrate:fused-substrate | 34 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 17 |
| shared-substrate:shared-upstream | 16 |
| shared-substrate:transcription | 26 |
| structural | 1065 |
| **total** | **2079** |

- statements: 628
- statements with no independent leg: 149
- last run: 2026-08-11

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10384 |
| binary-field | 256 | 250 |
| binary-field-axioms | 256 | 250 |
| binary-field-group | 256 | 312 |
| binary-polynomial | 256 | 315 |
| binary-polynomial-division | 256 | 315 |
| binary-polynomial-gcd | 256 | 315 |
| clifford-motor | 512 | 167 |
| clifford-multivector | 512 | 167 |
| clifford-planar-complex | 512 | 167 |
| clifford-planar-dual | 512 | 167 |
| clifford-planar-split | 512 | 167 |
| clifford-quaternion-even | 512 | 167 |
| clifford-reverse | 512 | 167 |
| closed-unit | 512 | 401 |
| complex | 512 | 10385 |
| complex-direction | 512 | 295 |
| complex-divide | 512 | 375 |
| complex-rotate | 512 | 375 |
| contribution-fold-analog | 512 | 86 |
| contribution-fold-formula | 512 | 86 |
| contribution-fold-no-pool | 512 | 86 |
| contribution-fold-order | 512 | 86 |
| contribution-fold-quantization | 512 | 86 |
| directed-magnitude | 512 | 18 |
| directed-product | 512 | 18 |
| directed-product-sum | 512 | 18 |
| directed-quotient | 512 | 18 |
| directed-root | 512 | 18 |
| dual | 512 | 8185 |
| dual-divide | 512 | 295 |
| dual-generic | 512 | 295 |
| dual-quaternion | 512 | 375 |
| extension-field | 256 | 301 |
| extension-field-inverse | 256 | 243 |
| extension-field-norm | 256 | 301 |
| extension-field-power | 256 | 243 |
| extension-field-product | 256 | 301 |
| mass-box | 256 | 18 |
| mass-capsule | 256 | 18 |
| mass-compound | 256 | 18 |
| mass-cylinder | 256 | 18 |
| mass-parallel-axis | 256 | 18 |
| mass-sphere | 256 | 18 |
| mass-volume | 256 | 18 |
| meet-associative | 512 | 77 |
| meet-bottom-absorption | 512 | 77 |
| meet-commutative | 512 | 77 |
| meet-idempotent | 512 | 77 |
| meet-monotonicity | 512 | 77 |
| meet-order-coherence | 512 | 77 |
| meet-product-composition | 512 | 77 |
| meet-top-identity | 512 | 77 |
| mixed-scale | 512 | 18 |
| mixed-scale-triple | 512 | 18 |
| mobius | 512 | 8185 |
| monogenic-exact | 512 | 167 |
| monogenic-fusion | 512 | 167 |
| position | 512 | 282 |
| position-delta | 512 | 358 |
| position-translate | 512 | 358 |
| presented | 512 | 656 |
| prime-field | 256 | 306 |
| prime-field-chain | 256 | 306 |
| prime-field-lucas | 256 | 306 |
| prime-field-primality | 256 | 306 |
| prime-field-root | 256 | 306 |
| q1648-scalar | 512 | 67 |
| q1648-scalar-division | 512 | 66 |
| q3232-scalar | 512 | 30 |
| q3232-scalar-division | 512 | 30 |
| quaternion | 512 | 375 |
| quaternion-direction | 512 | 295 |
| quaternion-rotate | 512 | 295 |
| quaternion-sublattice | 256 | 295 |
| rate | 512 | 359 |
| rigid | 512 | 398 |
| rigid-direction | 512 | 306 |
| rigid-point | 512 | 398 |
| scalar | 512 | 8188 |
| scalar-division | 512 | 393 |
| scalar-text | 512 | 393 |
| scalar-transcendental | 512 | 393 |
| smoke | 64 | 1915 |
| split | 512 | 10385 |
| split-divide | 512 | 375 |
| split-transform | 512 | 375 |
| sublattice | 256 | 4345 |
| symmetric-apply2 | 512 | 18 |
| symmetric-apply3 | 512 | 18 |
| symmetric-invert2 | 256 | 61 |
| symmetric-invert3 | 256 | 61 |
| symmetric-solve2 | 512 | 61 |
| symmetric-solve3 | 512 | 62 |
| unit-fraction16 | 512 | 315 |
| unit-fraction32 | 512 | 400 |
| unsigned-scalar | 512 | 386 |
| vector | 512 | 370 |
| vector-direction | 512 | 370 |
| vector-lattice | 512 | 291 |
| vector-narrow | 512 | 370 |
| vector-norm | 512 | 291 |

- last run: 2026-08-11
