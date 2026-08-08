---
name: maths-usage
description: Selects and verifies Puck.Maths primitives for deterministic simulation values. Use before numeric, random, or geometric simulation code; when reviewing src/Puck.Maths; for fixed-point, rounding, overflow, RNG streams or snapshots, sampling, finite fields, exact arithmetic, hashing, grids, transforms, rates, determinism, or library-discovery questions; and when routing Maths verification. Excludes presentation-only floating point, shaders, renderer or UI math, and presentation seams; maths-laws owns authoring the law suite.
---

# Puck.Maths: choose, obey, verify

Puck.Maths is the deterministic numerics library for values a simulation
advances, compares, hashes, snapshots, or replays. Presentation-only floats,
shaders, renderer/UI math, and explicit presentation conversions are outside
this contract.

## Choose

Before hand-rolling numeric behavior, inspect the library and the README for
the relevant wing. Match the primitive to the job:

- fixed-point scalars, vectors, rotations, transforms, positions, and rates;
- reproducible RNG state, streams, distributions, sampling, and noise;
- grids, index spaces, hashes, space-filling curves, and exact integers;
- modular arithmetic, finite fields, primes, and presented algebra.

Do not substitute a convenient-looking type until its overflow, rounding,
range, equality, serialization, and determinism contract matches the value
path.

## Obey

- Simulation results must be bit-identical for identical inputs.
- Make rounding and overflow policy explicit at narrowing boundaries.
- Treat RNG state and stream identity as simulation state. Preserve draw order;
  do not share or reseed streams casually.
- Keep presentation conversions at the presentation seam.
- Do not infer a primitive's contract from its name; verify its source, XML
  documentation, and tests.

## Branded members

A member carrying `[VerifiedCode("id", …)]` is settled: proven correct over its
whole input space. `VerifiedCode.json` records a fingerprint sealing the source
that proof was read against — the member's own declaration plus the declarations
its entry names under `dependencies`, one level deep and listed by hand. Editing
anything under the seal fails the build with **VER001** and the recomputed hash.
Do not silence that by deleting the attribute — re-establish the basis, update
the manifest hash, and say why in the commit; or if the member should stop being
branded, remove the attribute and its manifest entry together. Inspect the
current declarations and read each entry's `basis`, `argument`, and
`dependencies` before concluding a change keeps the proof intact.

## Verify

The floor is one command — `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release`,
about thirteen seconds. It is the Default tier, and the only one carrying the
coverage ratchet and both leg gates, so every change owes it and most changes
owe nothing more. Run `deep.runsettings` before committing; `exhaustive.runsettings`
and `bench.runsettings` on demand only.

**Do not reflexively run `Puck.Post` or the `exhaustive` tier.** They take
minutes and cannot run a subset. Run one only when the member you touched names
it as its gate of record. A public API change also requires a law or precise waiver
under `maths-laws`. Report the exact commands, and distinguish failures caused
by the change from pre-existing ones.

## Load the full reference selectively

Read [references/complete-reference.md](references/complete-reference.md) for
the complete primitive catalogue, sampling boundaries, finite-field and
presented-algebra guidance, determinism traps, exact verification commands,
tier routing, or governance.

## Route adjacent work

| Skill | Use when |
|---|---|
| [`maths-laws`](../maths-laws/SKILL.md) | Authoring or changing a Maths law, subject, oracle, leg, waiver, or public-member classification. |
| [`content-search`](../content-search/SKILL.md) | Finding textual occurrences, candidate hand-rollings, tokens, or patterns across the tree. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | Answering semantic C# questions about references, implementers, dead code, renames, or deletion safety. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | Verifying a Maths change that reaches emulator code and its dedicated batteries. |
