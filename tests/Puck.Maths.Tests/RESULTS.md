# Puck.Maths.Tests — RESULTS

Machine-written by the assembly ledger at run end. Each block records the last run that exercised it — a tier
block only when that tier ran law cases, coverage only when the ratchet gate ran, the frontier only when a run
consumed domains AND every law it ran passed — so every other block keeps the text its own last run left. The
last-run dates are the only volatile content; they do not by themselves trigger a rewrite.

Every figure below is MACHINE-INDEPENDENT by construction: the same commit produces the same counts and the same
frontier indices on every machine, so a difference here is a real difference and never a difference of hardware.
No duration is recorded, deliberately. One here would carry no machine identity, would span the whole session
rather than the block it sits under, and would be taken without a busy-machine guard — so it could not answer
any question asked of it. Cost is measured outside the suite, by `puck bench`.

## Invocations

| tier | command |
| --- | --- |
| Default (Smoke+Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` |
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings` |

## Smoke

- law cases executed: 22
- last run: 2026-10-02

## Default

- law cases executed: 652
- last run: 2026-10-02

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2189
- waived: 55
- uncovered: 634
- total public members: 2878
- last run: 2026-10-02

## Legs

| leg kind | legs |
| --- | --- |
| classical | 854 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1204 |
| **total** | **2351** |

- statements: 793
- statements with no independent leg: 215
- last run: 2026-10-02

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10641 |
| binary-field | 256 | 474 |
| binary-field-axioms | 256 | 474 |
| binary-field-group | 256 | 569 |
| binary-polynomial | 256 | 572 |
| binary-polynomial-division | 256 | 572 |
| binary-polynomial-gcd | 256 | 572 |
| clifford-motor | 512 | 391 |
| clifford-multivector | 512 | 391 |
| clifford-planar-complex | 512 | 391 |
| clifford-planar-dual | 512 | 391 |
| clifford-planar-split | 512 | 391 |
| clifford-quaternion-even | 512 | 391 |
| clifford-reverse | 512 | 391 |
| closed-unit | 512 | 625 |
| complex | 512 | 10642 |
| complex-direction | 512 | 519 |
| complex-divide | 512 | 632 |
| complex-rotate | 512 | 632 |
| contribution-fold-analog | 512 | 310 |
| contribution-fold-formula | 512 | 310 |
| contribution-fold-no-pool | 512 | 310 |
| contribution-fold-order | 512 | 310 |
| contribution-fold-quantization | 512 | 310 |
| core-word-modular | 512 | 16 |
| cost-bound-arithmetic | 512 | 57 |
| cost-model-budgets | 512 | 57 |
| cost-model-conversions | 512 | 57 |
| directed-magnitude | 512 | 242 |
| directed-product | 512 | 242 |
| directed-product-sum | 512 | 242 |
| directed-quotient | 512 | 242 |
| directed-root | 512 | 242 |
| dual | 512 | 8409 |
| dual-divide | 512 | 519 |
| dual-generic | 512 | 519 |
| dual-quaternion | 512 | 632 |
| dynamics | 512 | 127 |
| extension-field | 256 | 558 |
| extension-field-inverse | 256 | 467 |
| extension-field-norm | 256 | 558 |
| extension-field-power | 256 | 467 |
| extension-field-product | 256 | 558 |
| fixed-saturate | 512 | 57 |
| integer-bit-align | 256 | 31 |
| integer-bit-morton | 128 | 31 |
| integer-bit-scatter | 128 | 31 |
| integer-bit-smear | 256 | 31 |
| integer-hexagonal-index | 512 | 96 |
| integer-magic-constants | 512 | 88 |
| integer-try-arithmetic | 512 | 21 |
| integer-try-multiplication | 512 | 16 |
| interval-arc-functions | 512 | 1 |
| interval-arctangent | 512 | 1 |
| interval-arithmetic | 512 | 1 |
| interval-circular | 512 | 1 |
| interval-isotonic | 512 | 1 |
| interval-power | 512 | 1 |
| mass-box | 256 | 242 |
| mass-capsule | 256 | 242 |
| mass-compound | 256 | 242 |
| mass-cylinder | 256 | 242 |
| mass-parallel-axis | 256 | 242 |
| mass-sphere | 256 | 242 |
| mass-volume | 256 | 242 |
| meet-associative | 512 | 301 |
| meet-bottom-absorption | 512 | 301 |
| meet-commutative | 512 | 301 |
| meet-idempotent | 512 | 301 |
| meet-monotonicity | 512 | 301 |
| meet-order-coherence | 512 | 301 |
| meet-product-composition | 512 | 301 |
| meet-top-identity | 512 | 301 |
| mixed-scale | 512 | 242 |
| mixed-scale-triple | 512 | 242 |
| mobius | 512 | 8409 |
| monogenic-exact | 512 | 391 |
| monogenic-fusion | 512 | 391 |
| position | 512 | 506 |
| position-delta | 512 | 615 |
| position-translate | 512 | 615 |
| presented | 512 | 913 |
| prime-field | 256 | 563 |
| prime-field-chain | 256 | 563 |
| prime-field-lucas | 256 | 563 |
| prime-field-primality | 256 | 563 |
| prime-field-root | 256 | 563 |
| q1648-scalar | 512 | 292 |
| q1648-scalar-division | 512 | 290 |
| q3232-scalar | 512 | 255 |
| q3232-scalar-division | 512 | 254 |
| quaternion | 512 | 632 |
| quaternion-antiparallel | 512 | 6 |
| quaternion-arc | 512 | 6 |
| quaternion-direction | 512 | 519 |
| quaternion-rotate | 512 | 519 |
| quaternion-sublattice | 256 | 519 |
| rate | 512 | 616 |
| rigid | 512 | 655 |
| rigid-direction | 512 | 530 |
| rigid-exp-series | 512 | 4 |
| rigid-log-series | 512 | 4 |
| rigid-point | 512 | 655 |
| scalar | 512 | 8413 |
| scalar-division | 512 | 650 |
| scalar-smoothstep | 512 | 8 |
| scalar-text | 512 | 650 |
| scalar-transcendental | 512 | 652 |
| smoke | 64 | 2139 |
| split | 512 | 10642 |
| split-divide | 512 | 632 |
| split-transform | 512 | 632 |
| square-grid | 512 | 85 |
| sublattice | 256 | 4569 |
| symmetric-apply2 | 512 | 242 |
| symmetric-apply3 | 512 | 242 |
| symmetric-invert2 | 256 | 285 |
| symmetric-invert3 | 256 | 285 |
| symmetric-solve2 | 512 | 285 |
| symmetric-solve3 | 512 | 286 |
| unit-fraction16 | 512 | 539 |
| unit-fraction32 | 512 | 657 |
| unsigned-scalar | 512 | 643 |
| vector | 512 | 627 |
| vector-compare-length | 512 | 2 |
| vector-componentwise-helpers | 512 | 21 |
| vector-direction | 512 | 627 |
| vector-lattice | 512 | 515 |
| vector-narrow | 512 | 627 |
| vector-norm | 512 | 515 |
| vector-orthonormal-basis | 512 | 178 |
| vector-ray-plane | 512 | 19 |
| vector-within | 512 | 7 |

- last run: 2026-10-02
