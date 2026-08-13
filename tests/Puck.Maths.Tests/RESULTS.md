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
| algebra-fractional | 512 | 10393 |
| binary-field | 256 | 259 |
| binary-field-axioms | 256 | 259 |
| binary-field-group | 256 | 321 |
| binary-polynomial | 256 | 324 |
| binary-polynomial-division | 256 | 324 |
| binary-polynomial-gcd | 256 | 324 |
| clifford-motor | 512 | 176 |
| clifford-multivector | 512 | 176 |
| clifford-planar-complex | 512 | 176 |
| clifford-planar-dual | 512 | 176 |
| clifford-planar-split | 512 | 176 |
| clifford-quaternion-even | 512 | 176 |
| clifford-reverse | 512 | 176 |
| closed-unit | 512 | 410 |
| complex | 512 | 10394 |
| complex-direction | 512 | 304 |
| complex-divide | 512 | 384 |
| complex-rotate | 512 | 384 |
| contribution-fold-analog | 512 | 95 |
| contribution-fold-formula | 512 | 95 |
| contribution-fold-no-pool | 512 | 95 |
| contribution-fold-order | 512 | 95 |
| contribution-fold-quantization | 512 | 95 |
| directed-magnitude | 512 | 27 |
| directed-product | 512 | 27 |
| directed-product-sum | 512 | 27 |
| directed-quotient | 512 | 27 |
| directed-root | 512 | 27 |
| dual | 512 | 8194 |
| dual-divide | 512 | 304 |
| dual-generic | 512 | 304 |
| dual-quaternion | 512 | 384 |
| extension-field | 256 | 310 |
| extension-field-inverse | 256 | 252 |
| extension-field-norm | 256 | 310 |
| extension-field-power | 256 | 252 |
| extension-field-product | 256 | 310 |
| mass-box | 256 | 27 |
| mass-capsule | 256 | 27 |
| mass-compound | 256 | 27 |
| mass-cylinder | 256 | 27 |
| mass-parallel-axis | 256 | 27 |
| mass-sphere | 256 | 27 |
| mass-volume | 256 | 27 |
| meet-associative | 512 | 86 |
| meet-bottom-absorption | 512 | 86 |
| meet-commutative | 512 | 86 |
| meet-idempotent | 512 | 86 |
| meet-monotonicity | 512 | 86 |
| meet-order-coherence | 512 | 86 |
| meet-product-composition | 512 | 86 |
| meet-top-identity | 512 | 86 |
| mixed-scale | 512 | 27 |
| mixed-scale-triple | 512 | 27 |
| mobius | 512 | 8194 |
| monogenic-exact | 512 | 176 |
| monogenic-fusion | 512 | 176 |
| position | 512 | 291 |
| position-delta | 512 | 367 |
| position-translate | 512 | 367 |
| presented | 512 | 665 |
| prime-field | 256 | 315 |
| prime-field-chain | 256 | 315 |
| prime-field-lucas | 256 | 315 |
| prime-field-primality | 256 | 315 |
| prime-field-root | 256 | 315 |
| q1648-scalar | 512 | 76 |
| q1648-scalar-division | 512 | 75 |
| q3232-scalar | 512 | 39 |
| q3232-scalar-division | 512 | 39 |
| quaternion | 512 | 384 |
| quaternion-direction | 512 | 304 |
| quaternion-rotate | 512 | 304 |
| quaternion-sublattice | 256 | 304 |
| rate | 512 | 368 |
| rigid | 512 | 407 |
| rigid-direction | 512 | 315 |
| rigid-point | 512 | 407 |
| scalar | 512 | 8197 |
| scalar-division | 512 | 402 |
| scalar-text | 512 | 402 |
| scalar-transcendental | 512 | 402 |
| smoke | 64 | 1924 |
| split | 512 | 10394 |
| split-divide | 512 | 384 |
| split-transform | 512 | 384 |
| sublattice | 256 | 4354 |
| symmetric-apply2 | 512 | 27 |
| symmetric-apply3 | 512 | 27 |
| symmetric-invert2 | 256 | 70 |
| symmetric-invert3 | 256 | 70 |
| symmetric-solve2 | 512 | 70 |
| symmetric-solve3 | 512 | 71 |
| unit-fraction16 | 512 | 324 |
| unit-fraction32 | 512 | 409 |
| unsigned-scalar | 512 | 395 |
| vector | 512 | 379 |
| vector-direction | 512 | 379 |
| vector-lattice | 512 | 300 |
| vector-narrow | 512 | 379 |
| vector-norm | 512 | 300 |

- last run: 2026-08-13
