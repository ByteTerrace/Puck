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
- last run: 2026-08-07

## Default

- law cases executed: 416
- last run: 2026-08-07

## Deep

- law cases executed: 99
- last run: 2026-08-07

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1476
- waived: 10
- uncovered: 634
- total public members: 2120
- last run: 2026-08-07

## Legs

| leg kind | legs |
| --- | --- |
| classical | 670 |
| in-tree-independent | 27 |
| presented-twin | 9 |
| relative-canary | 17 |
| shared-substrate:delegation-twin | 42 |
| shared-substrate:fused-substrate | 34 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 17 |
| shared-substrate:shared-upstream | 16 |
| shared-substrate:transcription | 25 |
| structural | 984 |
| **total** | **1923** |

- statements: 544
- statements with no independent leg: 127
- last run: 2026-08-07

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10305 |
| binary-field | 256 | 183 |
| binary-field-axioms | 256 | 183 |
| binary-field-group | 256 | 233 |
| binary-polynomial | 256 | 236 |
| binary-polynomial-division | 256 | 236 |
| binary-polynomial-gcd | 256 | 236 |
| clifford-motor | 512 | 100 |
| clifford-multivector | 512 | 100 |
| clifford-planar-complex | 512 | 100 |
| clifford-planar-dual | 512 | 100 |
| clifford-planar-split | 512 | 100 |
| clifford-quaternion-even | 512 | 100 |
| clifford-reverse | 512 | 100 |
| closed-unit | 512 | 334 |
| complex | 512 | 10306 |
| complex-direction | 512 | 228 |
| complex-divide | 512 | 296 |
| complex-rotate | 512 | 296 |
| contribution-fold-analog | 512 | 19 |
| contribution-fold-formula | 512 | 19 |
| contribution-fold-no-pool | 512 | 19 |
| contribution-fold-order | 512 | 19 |
| contribution-fold-quantization | 512 | 19 |
| dual | 512 | 8118 |
| dual-divide | 512 | 228 |
| dual-generic | 512 | 228 |
| dual-quaternion | 512 | 296 |
| extension-field | 256 | 222 |
| extension-field-inverse | 256 | 176 |
| extension-field-norm | 256 | 222 |
| extension-field-power | 256 | 176 |
| extension-field-product | 256 | 222 |
| meet-associative | 512 | 10 |
| meet-bottom-absorption | 512 | 10 |
| meet-commutative | 512 | 10 |
| meet-idempotent | 512 | 10 |
| meet-monotonicity | 512 | 10 |
| meet-order-coherence | 512 | 10 |
| meet-product-composition | 512 | 10 |
| meet-top-identity | 512 | 10 |
| mobius | 512 | 8118 |
| monogenic-exact | 512 | 100 |
| monogenic-fusion | 512 | 100 |
| position | 512 | 215 |
| position-delta | 512 | 279 |
| position-translate | 512 | 279 |
| presented | 512 | 577 |
| prime-field | 256 | 227 |
| prime-field-chain | 256 | 227 |
| prime-field-lucas | 256 | 227 |
| prime-field-primality | 256 | 227 |
| prime-field-root | 256 | 227 |
| quaternion | 512 | 296 |
| quaternion-direction | 512 | 228 |
| quaternion-rotate | 512 | 228 |
| quaternion-sublattice | 256 | 228 |
| rate | 512 | 280 |
| rigid | 512 | 319 |
| rigid-direction | 512 | 239 |
| rigid-point | 512 | 319 |
| scalar | 512 | 8120 |
| scalar-division | 512 | 314 |
| scalar-text | 512 | 314 |
| scalar-transcendental | 512 | 314 |
| smoke | 64 | 1848 |
| split | 512 | 10306 |
| split-divide | 512 | 296 |
| split-transform | 512 | 296 |
| sublattice | 256 | 4278 |
| unit-fraction16 | 512 | 248 |
| unit-fraction32 | 512 | 321 |
| unsigned-scalar | 512 | 307 |
| vector | 512 | 291 |
| vector-direction | 512 | 291 |
| vector-lattice | 512 | 224 |
| vector-narrow | 512 | 291 |
| vector-norm | 512 | 224 |

- last run: 2026-08-07
