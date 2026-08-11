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
- last run: 2026-08-10

## Default

- law cases executed: 500
- last run: 2026-08-10

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
- last run: 2026-08-10

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
- last run: 2026-08-10

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10383 |
| binary-field | 256 | 249 |
| binary-field-axioms | 256 | 249 |
| binary-field-group | 256 | 311 |
| binary-polynomial | 256 | 314 |
| binary-polynomial-division | 256 | 314 |
| binary-polynomial-gcd | 256 | 314 |
| clifford-motor | 512 | 166 |
| clifford-multivector | 512 | 166 |
| clifford-planar-complex | 512 | 166 |
| clifford-planar-dual | 512 | 166 |
| clifford-planar-split | 512 | 166 |
| clifford-quaternion-even | 512 | 166 |
| clifford-reverse | 512 | 166 |
| closed-unit | 512 | 400 |
| complex | 512 | 10384 |
| complex-direction | 512 | 294 |
| complex-divide | 512 | 374 |
| complex-rotate | 512 | 374 |
| contribution-fold-analog | 512 | 85 |
| contribution-fold-formula | 512 | 85 |
| contribution-fold-no-pool | 512 | 85 |
| contribution-fold-order | 512 | 85 |
| contribution-fold-quantization | 512 | 85 |
| directed-magnitude | 512 | 17 |
| directed-product | 512 | 17 |
| directed-product-sum | 512 | 17 |
| directed-quotient | 512 | 17 |
| directed-root | 512 | 17 |
| dual | 512 | 8184 |
| dual-divide | 512 | 294 |
| dual-generic | 512 | 294 |
| dual-quaternion | 512 | 374 |
| extension-field | 256 | 300 |
| extension-field-inverse | 256 | 242 |
| extension-field-norm | 256 | 300 |
| extension-field-power | 256 | 242 |
| extension-field-product | 256 | 300 |
| mass-box | 256 | 17 |
| mass-capsule | 256 | 17 |
| mass-compound | 256 | 17 |
| mass-cylinder | 256 | 17 |
| mass-parallel-axis | 256 | 17 |
| mass-sphere | 256 | 17 |
| mass-volume | 256 | 17 |
| meet-associative | 512 | 76 |
| meet-bottom-absorption | 512 | 76 |
| meet-commutative | 512 | 76 |
| meet-idempotent | 512 | 76 |
| meet-monotonicity | 512 | 76 |
| meet-order-coherence | 512 | 76 |
| meet-product-composition | 512 | 76 |
| meet-top-identity | 512 | 76 |
| mixed-scale | 512 | 17 |
| mixed-scale-triple | 512 | 17 |
| mobius | 512 | 8184 |
| monogenic-exact | 512 | 166 |
| monogenic-fusion | 512 | 166 |
| position | 512 | 281 |
| position-delta | 512 | 357 |
| position-translate | 512 | 357 |
| presented | 512 | 655 |
| prime-field | 256 | 305 |
| prime-field-chain | 256 | 305 |
| prime-field-lucas | 256 | 305 |
| prime-field-primality | 256 | 305 |
| prime-field-root | 256 | 305 |
| q1648-scalar | 512 | 66 |
| q1648-scalar-division | 512 | 65 |
| q3232-scalar | 512 | 29 |
| q3232-scalar-division | 512 | 29 |
| quaternion | 512 | 374 |
| quaternion-direction | 512 | 294 |
| quaternion-rotate | 512 | 294 |
| quaternion-sublattice | 256 | 294 |
| rate | 512 | 358 |
| rigid | 512 | 397 |
| rigid-direction | 512 | 305 |
| rigid-point | 512 | 397 |
| scalar | 512 | 8187 |
| scalar-division | 512 | 392 |
| scalar-text | 512 | 392 |
| scalar-transcendental | 512 | 392 |
| smoke | 64 | 1914 |
| split | 512 | 10384 |
| split-divide | 512 | 374 |
| split-transform | 512 | 374 |
| sublattice | 256 | 4344 |
| symmetric-apply2 | 512 | 17 |
| symmetric-apply3 | 512 | 17 |
| symmetric-invert2 | 256 | 60 |
| symmetric-invert3 | 256 | 60 |
| symmetric-solve2 | 512 | 60 |
| symmetric-solve3 | 512 | 61 |
| unit-fraction16 | 512 | 314 |
| unit-fraction32 | 512 | 399 |
| unsigned-scalar | 512 | 385 |
| vector | 512 | 369 |
| vector-direction | 512 | 369 |
| vector-lattice | 512 | 290 |
| vector-narrow | 512 | 369 |
| vector-norm | 512 | 290 |

- last run: 2026-08-10
