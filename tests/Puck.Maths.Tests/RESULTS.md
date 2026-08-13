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
- last run: 2026-08-13

## Default

- law cases executed: 500
- last run: 2026-08-13

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
- last run: 2026-08-13

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
- last run: 2026-08-13

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10394 |
| binary-field | 256 | 260 |
| binary-field-axioms | 256 | 260 |
| binary-field-group | 256 | 322 |
| binary-polynomial | 256 | 325 |
| binary-polynomial-division | 256 | 325 |
| binary-polynomial-gcd | 256 | 325 |
| clifford-motor | 512 | 177 |
| clifford-multivector | 512 | 177 |
| clifford-planar-complex | 512 | 177 |
| clifford-planar-dual | 512 | 177 |
| clifford-planar-split | 512 | 177 |
| clifford-quaternion-even | 512 | 177 |
| clifford-reverse | 512 | 177 |
| closed-unit | 512 | 411 |
| complex | 512 | 10395 |
| complex-direction | 512 | 305 |
| complex-divide | 512 | 385 |
| complex-rotate | 512 | 385 |
| contribution-fold-analog | 512 | 96 |
| contribution-fold-formula | 512 | 96 |
| contribution-fold-no-pool | 512 | 96 |
| contribution-fold-order | 512 | 96 |
| contribution-fold-quantization | 512 | 96 |
| directed-magnitude | 512 | 28 |
| directed-product | 512 | 28 |
| directed-product-sum | 512 | 28 |
| directed-quotient | 512 | 28 |
| directed-root | 512 | 28 |
| dual | 512 | 8195 |
| dual-divide | 512 | 305 |
| dual-generic | 512 | 305 |
| dual-quaternion | 512 | 385 |
| extension-field | 256 | 311 |
| extension-field-inverse | 256 | 253 |
| extension-field-norm | 256 | 311 |
| extension-field-power | 256 | 253 |
| extension-field-product | 256 | 311 |
| mass-box | 256 | 28 |
| mass-capsule | 256 | 28 |
| mass-compound | 256 | 28 |
| mass-cylinder | 256 | 28 |
| mass-parallel-axis | 256 | 28 |
| mass-sphere | 256 | 28 |
| mass-volume | 256 | 28 |
| meet-associative | 512 | 87 |
| meet-bottom-absorption | 512 | 87 |
| meet-commutative | 512 | 87 |
| meet-idempotent | 512 | 87 |
| meet-monotonicity | 512 | 87 |
| meet-order-coherence | 512 | 87 |
| meet-product-composition | 512 | 87 |
| meet-top-identity | 512 | 87 |
| mixed-scale | 512 | 28 |
| mixed-scale-triple | 512 | 28 |
| mobius | 512 | 8195 |
| monogenic-exact | 512 | 177 |
| monogenic-fusion | 512 | 177 |
| position | 512 | 292 |
| position-delta | 512 | 368 |
| position-translate | 512 | 368 |
| presented | 512 | 666 |
| prime-field | 256 | 316 |
| prime-field-chain | 256 | 316 |
| prime-field-lucas | 256 | 316 |
| prime-field-primality | 256 | 316 |
| prime-field-root | 256 | 316 |
| q1648-scalar | 512 | 77 |
| q1648-scalar-division | 512 | 76 |
| q3232-scalar | 512 | 40 |
| q3232-scalar-division | 512 | 40 |
| quaternion | 512 | 385 |
| quaternion-direction | 512 | 305 |
| quaternion-rotate | 512 | 305 |
| quaternion-sublattice | 256 | 305 |
| rate | 512 | 369 |
| rigid | 512 | 408 |
| rigid-direction | 512 | 316 |
| rigid-point | 512 | 408 |
| scalar | 512 | 8198 |
| scalar-division | 512 | 403 |
| scalar-text | 512 | 403 |
| scalar-transcendental | 512 | 403 |
| smoke | 64 | 1925 |
| split | 512 | 10395 |
| split-divide | 512 | 385 |
| split-transform | 512 | 385 |
| sublattice | 256 | 4355 |
| symmetric-apply2 | 512 | 28 |
| symmetric-apply3 | 512 | 28 |
| symmetric-invert2 | 256 | 71 |
| symmetric-invert3 | 256 | 71 |
| symmetric-solve2 | 512 | 71 |
| symmetric-solve3 | 512 | 72 |
| unit-fraction16 | 512 | 325 |
| unit-fraction32 | 512 | 410 |
| unsigned-scalar | 512 | 396 |
| vector | 512 | 380 |
| vector-direction | 512 | 380 |
| vector-lattice | 512 | 301 |
| vector-narrow | 512 | 380 |
| vector-norm | 512 | 301 |

- last run: 2026-08-13
