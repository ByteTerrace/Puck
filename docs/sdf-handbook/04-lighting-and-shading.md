# Lighting and shading

After reading this you'll understand the shading epilogue: the code that runs
*once the march has found a surface* and turns a point, a normal, and a
material id into a lit pixel. You'll know why Puck computes its surface normal
with a single "dual" field evaluation instead of four probe taps, how soft
shadows get their penumbra, why the diegetic CRT screens are lights as well as
pictures, and which shading terms a user can switch off to measure or restyle
the frame.

## The epilogue and its one recurring discipline

The march ([chapter 3](03-the-frame.md)) is a loop that answers one question: *where does this ray
first touch a surface?* Everything in this chapter runs **after** that loop
breaks with a hit. At that moment the shader holds a hit position, the material
id captured at the accepting sample, and the accepted distance. It has **no
normal, no shadow, no occlusion yet** — those are the epilogue's job.

Every technique here re-queries the same field function the march used, `map()`
(and its tile-masked twin `mapMasked()`). So there is one discipline that
threads through the whole chapter, and it's worth stating once as a rule:

> **The field is pre-scaled; de-scale it before any world-space comparison.**
> `map()` returns a distance already multiplied by the program's baked
> `stepScale = 1/L` (the Lipschitz clamp that keeps the march safe — see
> [chapter 2](02-the-program-model.md)). Any shading term that compares a returned distance against a
> real-world length — a shadow ray's clearance, an AO rung's height, a cone
> radius — must divide `stepScale` back out first. Skip it and the term
> silently tracks each program's Lipschitz bake instead of its geometry.

The engine reads `stepScale` once at the top of the epilogue and hands the same
value to every term, each of which de-scales exactly where its math meets world
units — and *only* there. (Coverage anti-aliasing is the deliberate exception:
its ratio lives in the march's own clamped units, so de-scaling it would be the
bug, not the fix.) Keep this rule in your pocket; it explains a comment on
nearly every function below.

## The surface normal: one dual walk, not four taps

Shading needs a surface normal — the field's gradient, normalized. The textbook
way to get it from an SDF is a **finite-difference tetrahedron**: evaluate the
field at four points slightly offset around the hit, subtract, and you have the
gradient direction. It works, and Puck keeps it compiled as an A/B reference.
But it is *not* the default, for two reasons that are both specific to Puck's
architecture.

Puck's field isn't a closed-form function — it's a **program** the shader
interprets, walking a `uint[]` tape op by op. The interpreter's real cost is
*memory traffic*: fetching and decoding each program word. A four-tap normal
re-walks that whole tape four times, four separate fetch streams for one normal.

The default instead carries a **forward-mode dual** through a single walk: a
`float3` tangent rides alongside the scalar distance, and each op updates the
tangent with a hand-written derivative rule at the same moment it updates the
distance — reusing the subexpressions the distance already computed. One fetch
of each word updates all four accumulator components. One shared fetch stream
beats four.

```
  Four-tap FD:                          Analytic dual:
    walk tape → f(p+dx)                   walk tape ONCE, carrying
    walk tape → f(p+dy)                     (distance, ∂x, ∂y, ∂z)
    walk tape → f(p+dz)                   each op updates all four
    walk tape → f(p+dw)                   → gradient falls out directly
    subtract, normalize
    (4 fetch streams)                     (1 fetch stream)
```

The second reason is **determinism**, and it's the stronger one. A finite
difference computes `f(p+h) − f(p−h)`: a subtraction of two nearly-equal
numbers whose *low bits* carry the answer. The two GPU backends are free to
contract and reorder fused multiply-adds differently, so those two evaluations
round slightly differently and their *cancelled difference* diverges more than
the raw distance ever does — the "±1-LSB clusters along gradients" that lit
scenes lean on the relaxed parity threshold to absorb. A dual
walk never subtracts: the tangent is *built up* by the same multiplies, adds,
and orthonormal transforms the distance channel already makes, so it is as
parity-stable as the distance itself.

Two design guardrails keep this cheap. The dual path is a **hit-only
specialization** — a separate shader entry point, so the march kernel's lean
register footprint never carries the 4×-wide accumulator (a normal is computed
once per pixel; the march runs it dozens of times). And non-uniform scale is
*forbidden* in the op set, because a correct gradient under it would need an
inverse-transpose the exact-distance property can't survive. Translations leave
the gradient untouched; rotations multiply it by their orthonormal matrix;
folds apply their own reflection — each derivative hand-derived once per op.

## Soft shadows: the penumbra cone

The only occlusion term Puck marches is the shadow of the sun. From the lit
point it marches the field toward the light and tracks, over the whole march,
the **closest approach** to any occluder — the ratio of how near the ray came to
something solid against how far it has travelled. A ray that sails clean stays
fully lit; one that grazes an edge gets a soft penumbra proportional to how
close it came. The ambient term still fills shadowed regions, so shadows read
soft and colored, never black.

The subtlety is *where* the closest approach happens. Sampling the clearance
only *at* discrete march steps means that at a sharp occluder edge the true
nearest point falls *between* two steps — producing step-frequency banding and
light-leaking along penumbra edges. The fix treats the previous and current
samples as a local parabola and solves for the perpendicular miss distance at
the estimated nearest point *between* the samples. It removes the banding at
essentially the same cost: two extra arithmetic ops and one `sqrt`, no extra
field evaluations. This is the load-bearing home of the de-scale rule — the
parabola's miss distance must be computed from the *true, unscaled* field value,
or shadows silently darken (a chamfer program's `1/√2` clamp reads as roughly
30% extra shadow).

## The shadow gather: correct shadows, culled per pixel

A shadow ray leaves the camera's cone, so it needs a *different* set of nearby
occluders than the primary march narrowed to. Getting this wrong is a
correctness bug, not just a speed one: reuse the camera's tile mask for the
shadow march and **off-frustum occluders never cast into frame** — the frame is
wrong-fast, and nothing about it looks broken until an occluder leaves the
frustum.

The honest answer is a **per-pixel shadow gather**. Before the shadow march, the
shader walks the world-space instance grid along the sun ray and collects into a
small local mask exactly the instances the shadow ray can actually reach, then
marches *that*. This is bit-identical to marching every instance in the scene —
proven by a dedicated cross-backend gate — but restricted to the handful the ray
can touch.

The gather cone matters. It is **not** a bare ray: it is the *penumbra cone*,
wider than the ray itself, because the parabola refinement couples each sample
to its neighbor's clearance, so an occluder just *beside* the ray still softens
the shadow. A wider cone is always safe (a superset can't drop a needed
occluder); too narrow leaks light. The chord was widened until the penumbra edge
matched the flat reference to zero disagreeing pixels.

**When the gather wins and when it doesn't** is a clean story about density:

| Scene shape | What the gather does | Verdict |
|---|---|---|
| Spread content (a real room) | Narrows each shadow ray to a few local occluders | Correct shadows with fewer field evaluations than a flat march |
| Dense clustering (everything stacked in one spot) | Nothing to narrow; gather ≈ flat, plus the static-mask occupancy tax | Pays the price of correctness — not a regression against a *correct* baseline |

For dense scenes, performance work must accelerate the shadow march itself;
changing the occluder mask would make the clearance proof unsound.

## Ambient occlusion: three taps into the ambient fill

Puck's AO is the classic normal-ladder technique: from the hit, step fixed
rungs outward along the surface normal, and at each rung compare the distance
the field *should* read against what it *actually* reports; the deficit
accumulates as occlusion. The textbook ladder uses five rungs; Puck's uses
**three**, re-spaced to span the same reach at double pitch with the falloff
and gain re-tuned so the fully-occluded floor matches the five-tap look — the
same AO at 60% of the taps, and two fewer unrolled field walks relieving the
kernel's register pressure. It reads convincingly as contact shadowing in
creases and under overhangs for the cost of three field evaluations, paid only
on lit hits.

Two rules give it its shape. It multiplies into **ambient light only, never
direct** — folding occlusion into the whole lighting equation causes ghosting;
the sun stays governed by the soft shadow above. And the `(h − d)` deficit mixes
a world-space rung height with a scaled field sample, so `d` is de-scaled first
(the chapter's recurring rule again).

There is *no* screen-space AO, no hemisphere sampling, no temporal history — all
of that fights the no-RNG / no-screen-history posture that keeps the renderer
deterministic across backends. The three-tap ladder is the whole AO story today:
the cheapest technique with the largest perceptual gain, and zero architectural
disturbance.

## Screen lights and the CRT treatment

The demo's diegetic screens — the console cabinets' CRTs — are the most
distinctive light source in the engine, because each screen is *both a picture
and a light*.

As a **picture**, a bound screen is emissive: it is its own light source, like a
real display, so no scene lighting dims or tints it. Its pixels are the
emulator's framebuffer, sampled through a CRT glass-face model — a subtle bezel
mask, soft cosine scanlines, an aperture-grille phosphor-stripe tint, optional
bloom on the bright regions, and (all off by default) pincushion curvature,
vignette, and a fresnel rim glint. The tuned look is a flat square tube: a hint
of CRT, not a heavy filter.

As a **light**, every bound screen is a colored area light illuminating the
room. Its position and orientation come from the screen-surface table; its
color is the per-frame average of what it's displaying — so a screen showing a
green field spills green onto the wall beside it. A `dot(screenNormal, −L)` gate
enforces "light through the glass": a screen only lights what sits in front of
its face, never behind it. This is why the overworld dims its ambient and sun
per frame — so the diegetic glow dominates and the room reads as lit *by the
machines*.

## The shadow-proxy switch: shadows against the union hull

Destructible geometry creates a specific, expensive shape: a dense cluster of
subtraction ("carve") instances, each one a hole punched in the world. Shadow
rays through such a cluster re-march every carve, and the frame becomes
shadow-bound on damage the player caused.

The **shadow-proxy** switch trades a little visual fidelity for a lot of speed
here. When enabled, the shadow gather *omits* carve-family instances — it
evaluates the **pre-carve union hull** for shadowing purposes. On a dense carve
cluster this collapses the occluder set from many to few by construction. It is
*conservative*: skipping a pure subtraction can only make the field more solid,
so shadows go slightly darker or fill holes that light would otherwise thread —
they never leak the wrong way. The visual tradeoff is exactly that: light that
"should" pass through a freshly-carved gap is instead blocked by the hull the
gap was cut from. Off by default; a lever for scenes where destruction density
is the frame's bottleneck.

## The shading switches you can flip

Several shading terms are exposed as **feature switches** — runtime toggles a
user (or the benchmark, [chapter 8](08-performance.md)) flips to isolate a term's cost or restyle the
frame with no restart. They ride reserved lanes in a per-frame parameter row, so
an unset frame uploads zero and every default is preserved.

| Switch | Effect when flipped | Why you'd flip it |
|---|---|---|
| `sdf.soft-shadows` off | Sun goes unshadowed; ambient untouched | Isolates the single most expensive shading term — visually loud, intentionally |
| `sdf.ao` off | Occlusion forced to 1; creases brighten | Isolates three `map()` evals per lit pixel |
| `sdf.shadow-distance` full/half/quarter | Scales the shadow ray's reach *and* its cull cone together | Measures shorter-reach shadows as a tier (both must scale by the same length or the culled set is unsound) |
| `sdf.screen-lights` off | The per-screen area-light loop is skipped; CRTs stop spilling glow | Directly measures the lit CRTs' cost, which scales with how many cabinets are booted |
| `sdf.normals` analytic/finite-diff | Picks the dual normal or the four-tap probe at runtime | The A/B lever behind the normal argument above |

One more shading style lives behind a *compile-time* switch rather than a
runtime one: **curvature / NPR shading** — cavity darkening in creases, rim
light on ridges, ink-line outlines where the field's curvature spikes. It's
stylized, not physically based, and off by default. It's compile-time because
enabling it re-introduces the finite-difference taps a discrete Laplacian needs
(the analytic dual gives the normal but not the second derivative), and a
dead-branch guard lets the compiler strip the whole thing — extra tap included —
from the shipped build on both backends, byte-for-byte.

---

## Related resources

- The forward-mode dual normal, its parity argument, and the four-tap comparison
  path: [../sdf-wiki/gradients-and-normals.md](../sdf-wiki/gradients-and-normals.md).
- Soft shadows (classic penumbra + closest-approach parabola), the normal-ladder
  AO, the shadow-proxy, curvature/NPR, and the surveyed-but-unbuilt occlusion
  family: [../sdf-wiki/shading-ao-shadows.md](../sdf-wiki/shading-ao-shadows.md).
- The de-scale invariant and why coverage AA is the exception:
  [../sdf-wiki/lipschitz-and-field-correctness.md](../sdf-wiki/lipschitz-and-field-correctness.md).
- The shadow-cull correctness finding and the win/lose-on-density measurements:
  [../sdf-bench-notes.md](../sdf-bench-notes.md) (the shadow-cull section).
- The shading feature switches as measured levers:
  [../engine-bench-plan.md](../engine-bench-plan.md) (§4, the v1 switch roster).
