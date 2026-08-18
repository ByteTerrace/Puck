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
- last run: 2026-08-16

## Default

- law cases executed: 525
- last run: 2026-08-16

## Deep

- law cases executed: 102
- last run: 2026-08-16

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1788
- waived: 10
- uncovered: 634
- total public members: 2432
- last run: 2026-08-16

## Legs

| leg kind | legs |
| --- | --- |
| classical | 756 |
| in-tree-independent | 27 |
| presented-twin | 9 |
| relative-canary | 17 |
| shared-substrate:delegation-twin | 42 |
| shared-substrate:fused-substrate | 35 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 17 |
| shared-substrate:shared-upstream | 16 |
| shared-substrate:transcription | 26 |
| structural | 1094 |
| **total** | **2121** |

- statements: 656
- statements with no independent leg: 167
- last run: 2026-08-16

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10493 |
| binary-field | 256 | 345 |
| binary-field-axioms | 256 | 345 |
| binary-field-group | 256 | 421 |
| binary-polynomial | 256 | 424 |
| binary-polynomial-division | 256 | 424 |
| binary-polynomial-gcd | 256 | 424 |
| clifford-motor | 512 | 262 |
| clifford-multivector | 512 | 262 |
| clifford-planar-complex | 512 | 262 |
| clifford-planar-dual | 512 | 262 |
| clifford-planar-split | 512 | 262 |
| clifford-quaternion-even | 512 | 262 |
| clifford-reverse | 512 | 262 |
| closed-unit | 512 | 496 |
| complex | 512 | 10494 |
| complex-direction | 512 | 390 |
| complex-divide | 512 | 484 |
| complex-rotate | 512 | 484 |
| contribution-fold-analog | 512 | 181 |
| contribution-fold-formula | 512 | 181 |
| contribution-fold-no-pool | 512 | 181 |
| contribution-fold-order | 512 | 181 |
| contribution-fold-quantization | 512 | 181 |
| directed-magnitude | 512 | 113 |
| directed-product | 512 | 113 |
| directed-product-sum | 512 | 113 |
| directed-quotient | 512 | 113 |
| directed-root | 512 | 113 |
| dual | 512 | 8280 |
| dual-divide | 512 | 390 |
| dual-generic | 512 | 390 |
| dual-quaternion | 512 | 484 |
| extension-field | 256 | 410 |
| extension-field-inverse | 256 | 338 |
| extension-field-norm | 256 | 410 |
| extension-field-power | 256 | 338 |
| extension-field-product | 256 | 410 |
| mass-box | 256 | 113 |
| mass-capsule | 256 | 113 |
| mass-compound | 256 | 113 |
| mass-cylinder | 256 | 113 |
| mass-parallel-axis | 256 | 113 |
| mass-sphere | 256 | 113 |
| mass-volume | 256 | 113 |
| meet-associative | 512 | 172 |
| meet-bottom-absorption | 512 | 172 |
| meet-commutative | 512 | 172 |
| meet-idempotent | 512 | 172 |
| meet-monotonicity | 512 | 172 |
| meet-order-coherence | 512 | 172 |
| meet-product-composition | 512 | 172 |
| meet-top-identity | 512 | 172 |
| mixed-scale | 512 | 113 |
| mixed-scale-triple | 512 | 113 |
| mobius | 512 | 8280 |
| monogenic-exact | 512 | 262 |
| monogenic-fusion | 512 | 262 |
| position | 512 | 377 |
| position-delta | 512 | 467 |
| position-translate | 512 | 467 |
| presented | 512 | 765 |
| prime-field | 256 | 415 |
| prime-field-chain | 256 | 415 |
| prime-field-lucas | 256 | 415 |
| prime-field-primality | 256 | 415 |
| prime-field-root | 256 | 415 |
| q1648-scalar | 512 | 163 |
| q1648-scalar-division | 512 | 161 |
| q3232-scalar | 512 | 126 |
| q3232-scalar-division | 512 | 125 |
| quaternion | 512 | 484 |
| quaternion-direction | 512 | 390 |
| quaternion-rotate | 512 | 390 |
| quaternion-sublattice | 256 | 390 |
| rate | 512 | 468 |
| rigid | 512 | 507 |
| rigid-direction | 512 | 401 |
| rigid-point | 512 | 507 |
| scalar | 512 | 8284 |
| scalar-division | 512 | 502 |
| scalar-text | 512 | 502 |
| scalar-transcendental | 512 | 502 |
| smoke | 64 | 2010 |
| split | 512 | 10494 |
| split-divide | 512 | 484 |
| split-transform | 512 | 484 |
| sublattice | 256 | 4440 |
| symmetric-apply2 | 512 | 113 |
| symmetric-apply3 | 512 | 113 |
| symmetric-invert2 | 256 | 156 |
| symmetric-invert3 | 256 | 156 |
| symmetric-solve2 | 512 | 156 |
| symmetric-solve3 | 512 | 157 |
| unit-fraction16 | 512 | 410 |
| unit-fraction32 | 512 | 509 |
| unsigned-scalar | 512 | 495 |
| vector | 512 | 479 |
| vector-direction | 512 | 479 |
| vector-lattice | 512 | 386 |
| vector-narrow | 512 | 479 |
| vector-norm | 512 | 386 |
| vector-orthonormal-basis | 512 | 49 |

- last run: 2026-08-16
