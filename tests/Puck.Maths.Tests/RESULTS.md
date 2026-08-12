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
- last run: 2026-08-12

## Default

- law cases executed: 500
- last run: 2026-08-12

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
- last run: 2026-08-12

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
- last run: 2026-08-12

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10390 |
| binary-field | 256 | 256 |
| binary-field-axioms | 256 | 256 |
| binary-field-group | 256 | 318 |
| binary-polynomial | 256 | 321 |
| binary-polynomial-division | 256 | 321 |
| binary-polynomial-gcd | 256 | 321 |
| clifford-motor | 512 | 173 |
| clifford-multivector | 512 | 173 |
| clifford-planar-complex | 512 | 173 |
| clifford-planar-dual | 512 | 173 |
| clifford-planar-split | 512 | 173 |
| clifford-quaternion-even | 512 | 173 |
| clifford-reverse | 512 | 173 |
| closed-unit | 512 | 407 |
| complex | 512 | 10391 |
| complex-direction | 512 | 301 |
| complex-divide | 512 | 381 |
| complex-rotate | 512 | 381 |
| contribution-fold-analog | 512 | 92 |
| contribution-fold-formula | 512 | 92 |
| contribution-fold-no-pool | 512 | 92 |
| contribution-fold-order | 512 | 92 |
| contribution-fold-quantization | 512 | 92 |
| directed-magnitude | 512 | 24 |
| directed-product | 512 | 24 |
| directed-product-sum | 512 | 24 |
| directed-quotient | 512 | 24 |
| directed-root | 512 | 24 |
| dual | 512 | 8191 |
| dual-divide | 512 | 301 |
| dual-generic | 512 | 301 |
| dual-quaternion | 512 | 381 |
| extension-field | 256 | 307 |
| extension-field-inverse | 256 | 249 |
| extension-field-norm | 256 | 307 |
| extension-field-power | 256 | 249 |
| extension-field-product | 256 | 307 |
| mass-box | 256 | 24 |
| mass-capsule | 256 | 24 |
| mass-compound | 256 | 24 |
| mass-cylinder | 256 | 24 |
| mass-parallel-axis | 256 | 24 |
| mass-sphere | 256 | 24 |
| mass-volume | 256 | 24 |
| meet-associative | 512 | 83 |
| meet-bottom-absorption | 512 | 83 |
| meet-commutative | 512 | 83 |
| meet-idempotent | 512 | 83 |
| meet-monotonicity | 512 | 83 |
| meet-order-coherence | 512 | 83 |
| meet-product-composition | 512 | 83 |
| meet-top-identity | 512 | 83 |
| mixed-scale | 512 | 24 |
| mixed-scale-triple | 512 | 24 |
| mobius | 512 | 8191 |
| monogenic-exact | 512 | 173 |
| monogenic-fusion | 512 | 173 |
| position | 512 | 288 |
| position-delta | 512 | 364 |
| position-translate | 512 | 364 |
| presented | 512 | 662 |
| prime-field | 256 | 312 |
| prime-field-chain | 256 | 312 |
| prime-field-lucas | 256 | 312 |
| prime-field-primality | 256 | 312 |
| prime-field-root | 256 | 312 |
| q1648-scalar | 512 | 73 |
| q1648-scalar-division | 512 | 72 |
| q3232-scalar | 512 | 36 |
| q3232-scalar-division | 512 | 36 |
| quaternion | 512 | 381 |
| quaternion-direction | 512 | 301 |
| quaternion-rotate | 512 | 301 |
| quaternion-sublattice | 256 | 301 |
| rate | 512 | 365 |
| rigid | 512 | 404 |
| rigid-direction | 512 | 312 |
| rigid-point | 512 | 404 |
| scalar | 512 | 8194 |
| scalar-division | 512 | 399 |
| scalar-text | 512 | 399 |
| scalar-transcendental | 512 | 399 |
| smoke | 64 | 1921 |
| split | 512 | 10391 |
| split-divide | 512 | 381 |
| split-transform | 512 | 381 |
| sublattice | 256 | 4351 |
| symmetric-apply2 | 512 | 24 |
| symmetric-apply3 | 512 | 24 |
| symmetric-invert2 | 256 | 67 |
| symmetric-invert3 | 256 | 67 |
| symmetric-solve2 | 512 | 67 |
| symmetric-solve3 | 512 | 68 |
| unit-fraction16 | 512 | 321 |
| unit-fraction32 | 512 | 406 |
| unsigned-scalar | 512 | 392 |
| vector | 512 | 376 |
| vector-direction | 512 | 376 |
| vector-lattice | 512 | 297 |
| vector-narrow | 512 | 376 |
| vector-norm | 512 | 297 |

- last run: 2026-08-12
