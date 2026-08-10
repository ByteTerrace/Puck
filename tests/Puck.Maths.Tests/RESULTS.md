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
| algebra-fractional | 512 | 10380 |
| binary-field | 256 | 246 |
| binary-field-axioms | 256 | 246 |
| binary-field-group | 256 | 308 |
| binary-polynomial | 256 | 311 |
| binary-polynomial-division | 256 | 311 |
| binary-polynomial-gcd | 256 | 311 |
| clifford-motor | 512 | 163 |
| clifford-multivector | 512 | 163 |
| clifford-planar-complex | 512 | 163 |
| clifford-planar-dual | 512 | 163 |
| clifford-planar-split | 512 | 163 |
| clifford-quaternion-even | 512 | 163 |
| clifford-reverse | 512 | 163 |
| closed-unit | 512 | 397 |
| complex | 512 | 10381 |
| complex-direction | 512 | 291 |
| complex-divide | 512 | 371 |
| complex-rotate | 512 | 371 |
| contribution-fold-analog | 512 | 82 |
| contribution-fold-formula | 512 | 82 |
| contribution-fold-no-pool | 512 | 82 |
| contribution-fold-order | 512 | 82 |
| contribution-fold-quantization | 512 | 82 |
| directed-magnitude | 512 | 14 |
| directed-product | 512 | 14 |
| directed-product-sum | 512 | 14 |
| directed-quotient | 512 | 14 |
| directed-root | 512 | 14 |
| dual | 512 | 8181 |
| dual-divide | 512 | 291 |
| dual-generic | 512 | 291 |
| dual-quaternion | 512 | 371 |
| extension-field | 256 | 297 |
| extension-field-inverse | 256 | 239 |
| extension-field-norm | 256 | 297 |
| extension-field-power | 256 | 239 |
| extension-field-product | 256 | 297 |
| mass-box | 256 | 14 |
| mass-capsule | 256 | 14 |
| mass-compound | 256 | 14 |
| mass-cylinder | 256 | 14 |
| mass-parallel-axis | 256 | 14 |
| mass-sphere | 256 | 14 |
| mass-volume | 256 | 14 |
| meet-associative | 512 | 73 |
| meet-bottom-absorption | 512 | 73 |
| meet-commutative | 512 | 73 |
| meet-idempotent | 512 | 73 |
| meet-monotonicity | 512 | 73 |
| meet-order-coherence | 512 | 73 |
| meet-product-composition | 512 | 73 |
| meet-top-identity | 512 | 73 |
| mixed-scale | 512 | 14 |
| mixed-scale-triple | 512 | 14 |
| mobius | 512 | 8181 |
| monogenic-exact | 512 | 163 |
| monogenic-fusion | 512 | 163 |
| position | 512 | 278 |
| position-delta | 512 | 354 |
| position-translate | 512 | 354 |
| presented | 512 | 652 |
| prime-field | 256 | 302 |
| prime-field-chain | 256 | 302 |
| prime-field-lucas | 256 | 302 |
| prime-field-primality | 256 | 302 |
| prime-field-root | 256 | 302 |
| q1648-scalar | 512 | 63 |
| q1648-scalar-division | 512 | 62 |
| q3232-scalar | 512 | 26 |
| q3232-scalar-division | 512 | 26 |
| quaternion | 512 | 371 |
| quaternion-direction | 512 | 291 |
| quaternion-rotate | 512 | 291 |
| quaternion-sublattice | 256 | 291 |
| rate | 512 | 355 |
| rigid | 512 | 394 |
| rigid-direction | 512 | 302 |
| rigid-point | 512 | 394 |
| scalar | 512 | 8184 |
| scalar-division | 512 | 389 |
| scalar-text | 512 | 389 |
| scalar-transcendental | 512 | 389 |
| smoke | 64 | 1911 |
| split | 512 | 10381 |
| split-divide | 512 | 371 |
| split-transform | 512 | 371 |
| sublattice | 256 | 4341 |
| symmetric-apply2 | 512 | 14 |
| symmetric-apply3 | 512 | 14 |
| symmetric-invert2 | 256 | 57 |
| symmetric-invert3 | 256 | 57 |
| symmetric-solve2 | 512 | 57 |
| symmetric-solve3 | 512 | 58 |
| unit-fraction16 | 512 | 311 |
| unit-fraction32 | 512 | 396 |
| unsigned-scalar | 512 | 382 |
| vector | 512 | 366 |
| vector-direction | 512 | 366 |
| vector-lattice | 512 | 287 |
| vector-narrow | 512 | 366 |
| vector-norm | 512 | 287 |

- last run: 2026-08-10
