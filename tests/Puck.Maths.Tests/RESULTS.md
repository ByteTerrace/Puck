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
- last run: 2026-08-19

## Default

- law cases executed: 525
- last run: 2026-08-19

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
- waived: 35
- uncovered: 634
- total public members: 2457
- last run: 2026-08-19

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
- last run: 2026-08-19

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10495 |
| binary-field | 256 | 347 |
| binary-field-axioms | 256 | 347 |
| binary-field-group | 256 | 423 |
| binary-polynomial | 256 | 426 |
| binary-polynomial-division | 256 | 426 |
| binary-polynomial-gcd | 256 | 426 |
| clifford-motor | 512 | 264 |
| clifford-multivector | 512 | 264 |
| clifford-planar-complex | 512 | 264 |
| clifford-planar-dual | 512 | 264 |
| clifford-planar-split | 512 | 264 |
| clifford-quaternion-even | 512 | 264 |
| clifford-reverse | 512 | 264 |
| closed-unit | 512 | 498 |
| complex | 512 | 10496 |
| complex-direction | 512 | 392 |
| complex-divide | 512 | 486 |
| complex-rotate | 512 | 486 |
| contribution-fold-analog | 512 | 183 |
| contribution-fold-formula | 512 | 183 |
| contribution-fold-no-pool | 512 | 183 |
| contribution-fold-order | 512 | 183 |
| contribution-fold-quantization | 512 | 183 |
| directed-magnitude | 512 | 115 |
| directed-product | 512 | 115 |
| directed-product-sum | 512 | 115 |
| directed-quotient | 512 | 115 |
| directed-root | 512 | 115 |
| dual | 512 | 8282 |
| dual-divide | 512 | 392 |
| dual-generic | 512 | 392 |
| dual-quaternion | 512 | 486 |
| extension-field | 256 | 412 |
| extension-field-inverse | 256 | 340 |
| extension-field-norm | 256 | 412 |
| extension-field-power | 256 | 340 |
| extension-field-product | 256 | 412 |
| mass-box | 256 | 115 |
| mass-capsule | 256 | 115 |
| mass-compound | 256 | 115 |
| mass-cylinder | 256 | 115 |
| mass-parallel-axis | 256 | 115 |
| mass-sphere | 256 | 115 |
| mass-volume | 256 | 115 |
| meet-associative | 512 | 174 |
| meet-bottom-absorption | 512 | 174 |
| meet-commutative | 512 | 174 |
| meet-idempotent | 512 | 174 |
| meet-monotonicity | 512 | 174 |
| meet-order-coherence | 512 | 174 |
| meet-product-composition | 512 | 174 |
| meet-top-identity | 512 | 174 |
| mixed-scale | 512 | 115 |
| mixed-scale-triple | 512 | 115 |
| mobius | 512 | 8282 |
| monogenic-exact | 512 | 264 |
| monogenic-fusion | 512 | 264 |
| position | 512 | 379 |
| position-delta | 512 | 469 |
| position-translate | 512 | 469 |
| presented | 512 | 767 |
| prime-field | 256 | 417 |
| prime-field-chain | 256 | 417 |
| prime-field-lucas | 256 | 417 |
| prime-field-primality | 256 | 417 |
| prime-field-root | 256 | 417 |
| q1648-scalar | 512 | 165 |
| q1648-scalar-division | 512 | 163 |
| q3232-scalar | 512 | 128 |
| q3232-scalar-division | 512 | 127 |
| quaternion | 512 | 486 |
| quaternion-direction | 512 | 392 |
| quaternion-rotate | 512 | 392 |
| quaternion-sublattice | 256 | 392 |
| rate | 512 | 470 |
| rigid | 512 | 509 |
| rigid-direction | 512 | 403 |
| rigid-point | 512 | 509 |
| scalar | 512 | 8286 |
| scalar-division | 512 | 504 |
| scalar-text | 512 | 504 |
| scalar-transcendental | 512 | 504 |
| smoke | 64 | 2012 |
| split | 512 | 10496 |
| split-divide | 512 | 486 |
| split-transform | 512 | 486 |
| sublattice | 256 | 4442 |
| symmetric-apply2 | 512 | 115 |
| symmetric-apply3 | 512 | 115 |
| symmetric-invert2 | 256 | 158 |
| symmetric-invert3 | 256 | 158 |
| symmetric-solve2 | 512 | 158 |
| symmetric-solve3 | 512 | 159 |
| unit-fraction16 | 512 | 412 |
| unit-fraction32 | 512 | 511 |
| unsigned-scalar | 512 | 497 |
| vector | 512 | 481 |
| vector-direction | 512 | 481 |
| vector-lattice | 512 | 388 |
| vector-narrow | 512 | 481 |
| vector-norm | 512 | 388 |
| vector-orthonormal-basis | 512 | 51 |

- last run: 2026-08-19
