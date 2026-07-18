# The SDF handbook

This is the human-facing book about Puck's SDF world engine: what a signed
distance field is, how a whole world becomes one small interpreted program,
how that program becomes lit pixels five GPU passes later, and how the
engine stays deterministic while doing it. It is written to be read front to
back by a person — the engine's owner, a future contributor, anyone who
wants to *understand* the system rather than look up a contract fact. Each
chapter opens by saying what you'll understand after reading it, teaches
concept first and mechanism second, and stands alone well enough to revisit
out of order.

## Reading order

| Chapter | What it teaches |
|---|---|
| [1. The idea](01-the-idea.md) | What a distance field is, why fields compose into whole worlds, sphere tracing, and why Puck interprets scenes as data instead of compiling shaders. |
| [2. The program model](02-the-program-model.md) | The scene as a flat instruction stream: the one running accumulator, why intersection is dangerous where union is free, field scopes, materials, instances, the Lipschitz step clamp, and the ISA admission rule. |
| [3. The frame](03-the-frame.md) | The five compute passes (mask → beam → cull-args → views → composite), why mask-first flattened the instance-scaling wall, render-scale tiers, and the two-deep frame ring. |
| [4. Lighting and shading](04-lighting-and-shading.md) | The shading epilogue: the single-walk analytic normal, penumbra soft shadows, the per-pixel shadow gather, three-tap AO, CRT screens as picture-and-light, and the runtime shading switches. |
| [5. Authoring](05-authoring.md) | Building real scenes with `SdfProgramBuilder`: a worked plaza, emitter composition, material scopes, the capacity-probe doctrine, and the pitfalls stated as rules. |
| [6. Motion and views](06-motion-and-views.md) | Anchors as named presentation-only poses, the six camera rigs, `ViewStack` as the hypervisor primitive, view transitions, and the two diegetic-screen seams. |
| [7. Queries and determinism](07-queries-and-determinism.md) | `IWorldQuery` and the two providers (baked vs. exact fixed-point evaluator), what the determinism creed actually constrains, and gravity derived in one line from the field gradient. |
| [8. Performance](08-performance.md) | The honest cost model: where milliseconds go, views-bound vs. beam-bound vs. pace-bound, occupancy, the benchmark verbs, the diagnosis method, and measurement hygiene. |
| [9. Bricks and baking](09-bricks-and-baking.md) | The one sanctioned cache: baking settled carve clusters into sampled distance bricks, the `√3` march-safety trick, the settle/bake/swap/invalidate lifecycle, and the cache-not-representation doctrine. |

Chapters 1–2 are the foundation everything else references. From there,
readers who author content want 5 → 6; readers who work on the renderer want
3 → 4 → 8; readers who work on simulation or physics want 7. Chapter 9
assumes 2 and 3.

## Related technical references

The handbook teaches concepts and operational rules. The following references
provide API-level contracts, implementation detail, research context, and
reproducible measurements:

- **[docs/sdf-wiki/](../sdf-wiki/README.md)** — technique references and
  current Puck applicability.
- **[docs/sdf-bench-notes.md](../sdf-bench-notes.md)** — benchmark procedure
  and representative measurements.
- **[docs/engine-bench-plan.md](../engine-bench-plan.md)** — the benchmark's
  full design: timing seams, the switch registry, scoring math, the scene
  roster, and the hygiene machinery chapter 8 explains the *why* of.
- **[.agents/skills/sdf-world/SKILL.md](../../.agents/skills/sdf-world/SKILL.md)**
  — the agent-facing contract: the C#↔HLSL sync-pair tables and settled
  engine semantics, kept dense and exact for tooling. The handbook is the
  teaching view of the same system; the skill is the working contract.

If a reference disagrees with the source code, update the reference in the
same change.
