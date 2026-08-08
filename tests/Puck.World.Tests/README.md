# Puck.World.Tests

Executable laws for the **settled** Puck.World substrate. This project exists to
FAIL when the substrate's observable contract breaks — not to describe the code.

## What it gates

- `Puck.World.Data` — the document model, validators, serialization, the Protocol wire surface.
- `Puck.World.Server` — the sim contract: authority/grants, the mutation pipeline, the ordered domain, replay determinism.

Nothing else.

## Red-lines (a test that violates one is a DEFECT, not a style nit)

1. **Laws, not values or structure.** Assert a property that must hold across
   every valid change — never a count, an enum's cardinality, a field list, an
   exact string, or any internal shape. If a mutation-kind count or a
   refusal-catalog size appears in an assertion, the test is wrong: it punishes
   the next person for adding a kind or deleting a dead refusal.
2. **Every test can fail for a real reason.** A denial test carries a passing
   control, with actor ≠ target; a new law is proven once by breaking it. A test
   that passes regardless of correctness is worse than none — delete it.
3. **Determinism is the only hash gate, and it is self-referential.** Pin the
   MAPPING — same document + same input → bit-identical state across runs,
   machines, backends. A deliberate logic change is EXPECTED to move the hash:
   re-record it in the same change. Never pin a historical value; never preserve
   a wrong result to stay green.
4. **Settled substrate only.** No test of the overworld, the reveal ladder,
   arcade content, HUD/presentation, or any game feature — those are greenfield,
   verified by RUNNING the game (`CLAUDE.md` rule 3). No console-output goldens.

## Enforcement is architectural first, this file second

The guard lives in the test base, not in this prose: a law is written against a
base that REQUIRES its control, so the wrong shape is the awkward one to write.
Mirror `tests/Puck.Maths.Tests` (Domains / Oracles / Laws + a coverage ratchet
that lifts without pinning structure) — extend that architecture, do not invent a
parallel one. This file records the red-lines for review; it is not the primary
guard.

## This file's own limits (so it stays a decision record, not pollution)

- It lives here, and NOTHING else references it — no skill, no `CLAUDE.md` edit,
  no cross-doc tendrils.
- It records only what cannot be re-derived — the red-lines and the scope. It
  does not list the tests (the code shows them), restate the framework, or grow
  per test.
- If it ever disagrees with the code it is hostile, not stale: delete or correct
  it in the same change. It earns its place only by staying small enough to be
  obviously right.
