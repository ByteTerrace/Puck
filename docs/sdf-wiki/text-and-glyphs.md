# Text and Glyphs

Every other renderer draws text in **screen space**: a glyph is coverage on a
quad, anti-aliased by fragment-shader derivatives, composited flat. Puck's move
is different, and it is the whole reason this page exists — a glyph lands as a
**world-geometry distance field**, an ISA shape sampled *inside* the
sphere-tracing compute march. Text therefore marches, CSG-**blends**, and
**ENGRAVES** (Subtraction carves the glyph into a surface); its shading normal
comes from the same forward-mode analytic dual every other primitive uses
([gradients-and-normals.md](gradients-and-normals.md), `mapGradCore`, commit
`ce36f80`). The question is never "does it draw crisp text" but **"does it yield
a valid marchable distance field that CSG-combines and survives cross-backend
determinism?"** That reframing is what makes almost every screen-space
competitor below a *reject* on architecture, not on quality.

This page is the reference companion to **backlog item 21's Tier-1 MTSDF glyph
op** — the in-flight engine work that gives `Puck.Text` its first GPU consumer.
Its reference implementation already exists in a sibling repo (see §Text
enrichment / the `sdfMsdfGlyph` prototype). Research date **2026-07-09**;
verdict vocabulary and effort scale as in [verdict-index.md](verdict-index.md).

Puck prior art confirmed in-repo: `src/Puck.Text` ships the atlas data model +
loader speaking msdf-atlas-gen conventions — `FontAtlasKind {HardMask, SoftMask,
Sdf, Psdf, Msdf, Mtsdf}`, `MtsdfSampling.Median(r,g,b)`,
`ComputeScreenPixelRange`, per-glyph `EmRange`/`PxRange` overrides,
`DistanceRange`/`Size` matching the JSON. No GPU consumer yet. The user-supplied
superset (`SignedDistanceTerminal/…/Text/`) carries the dropped pieces:
`RuntimeSdfFontAtlasGenerator` (coverage→chamfer SDF), `FontAtlasLoader` with
msdf-atlas-gen JSON compatibility, a GDI+ generator with kerning.

---

## The field-source question — which channel does geometry march?

This is the load-bearing decision for the whole glyph op, and it has three named
findings behind it. The net ISA recommendation: **sample a pre-baked
true-distance (SDF or MTSDF-alpha) atlas, kept a conservative lower bound,
wrapped in an extruded-slab proxy. The runtime chamfer generator and the median
channel are both off the geometry path.**

### C1 — the chamfer field is Lipschitz-1.0824, not Lipschitz-1, and it overestimates (the dangerous direction)

- **Source:** Hajdu, Hajdu & Tijdeman, "Approximations of the Euclidean distance
  by chamfer distances," arXiv:1201.0876 / *Acta Cybernetica* 20(3), 2012
  (https://arxiv.org/abs/1201.0876); general theory Butt & Maragos, "Optimum
  Design of Chamfer Distance Transforms," IEEE TIP 1998.
- **Finding:** A two-weight `(1, √2)` chamfer ("quasi-Euclidean") has worst-case
  error at 22.5°: estimate = true × sec(22.5°) = ×**1.08239** = **+8.24%**.
  Sphere tracing requires the field be a **lower** bound (gradient ≤ 1); a
  chamfer field reports *larger*-than-true distances, so the ray oversteps and
  tunnels through thin stems — the liar's-spiral failure class, holes in the
  glyph. This is exactly the `stepScale = 1/L` discipline the rest of the engine
  lives by ([lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md)),
  surfacing again at the atlas boundary.
- **Fix / verdict:** divide the sampled chamfer distance by 1.0824 (fold the
  Lipschitz factor), **or** swap the runtime generator to an exact EDT
  (Felzenszwalb & Huttenlocher, "Distance Transforms of Sampled Functions,"
  *Theory of Computing* 2012 — O(n), deterministic, Lipschitz-1), **or** — the
  clean escape below — don't use the runtime chamfer for the world op at all.
- **C1-corollary (the clean escape):** msdf-atlas-gen's `sdf`/`psdf`/`msdf`/
  `mtsdf` outputs are TRUE (Euclidean / true-perpendicular) distances to the
  vector outline — Lipschitz-1 by construction. The loader already speaks the
  JSON, so loading a pre-baked MTSDF atlas sidesteps the chamfer problem
  entirely. The runtime generator is a convenience-when-no-toolchain path, never
  the marchable path.

### C2 — median-of-3 is not a globally ~1-Lipschitz field (it is coverage-only)

- **Source:** Chlumský & Sloup, "Improved Corners with Multi-Channel Signed
  Distance Fields," *Computer Graphics Forum* 2018
  (https://onlinelibrary.wiley.com/doi/abs/10.1111/cgf.13265); confirmed in the
  wild by Godot issue #109434.
- **Finding:** Median-of-3 selects the middle of three per-channel
  pseudo-distances. Within one selection region it is smooth, but at the lines
  where the *selected channel changes* the field is only **C0** and can kink or
  jump — the "clash" artifact. The median was designed for crisp 2D coverage at
  a single 0.5 threshold, where those discontinuities are invisible; it was
  **never meant to be marched.** A C0 field is not a valid sphere-tracing
  operand.
- **Verdict / the correct move:** march / engrave / normal-sample the **MTSDF
  4th (true-distance) alpha channel**, not the median (msdf-atlas-gen README:
  mtsdf = "a combination of MSDF and true SDF in the alpha channel"). Reserve
  the median only for a flat 2D coverage pass, should one ever be added. **This
  amends backlog item 21's Tier-1 parenthetical "(median-of-3 is
  ~1-Lipschitz-bounded)"** — geometry marches the alpha; the median is
  coverage-only.

### C3 — the extrude + box-fallback combine is correct and *required* (atlas SDFs are band-limited)

- **Source:** iq extrude formula, "distance functions"
  (https://iquilezles.org/articles/distfunctions/): `d = length(max(vec2(d2d,
  |z|−h), 0)) + min(max(d2d, |z|−h), 0)`.
- **Finding:** Every atlas distance field is valid only within ±pxrange/2 of the
  outline; outside the band the stored distance **saturates** (8-bit texel,
  fixed `distanceRange`) — the field goes flat, gradient → 0, useless to a
  sphere tracer. A marchable glyph op MUST wrap the atlas sample in a
  conservative outer proxy (box / extruded slab) that carries the ray into the
  band. Riders: **(a)** iq's extrude assumes `d2d` is TRUE 2D Euclidean
  *everywhere* — PSDF/MSDF pseudo-distances are valid only near the edge, one
  more reason to extrude the true channel; **(b)** the box fallback must be a
  conservative **underestimate**, and the proxy→atlas handoff must never let the
  combined field exceed true distance (or C1's overshoot returns).
- **Verdict:** confirmed required. This is not optional polish — a glyph op
  without the proxy has no valid far field.

---

## The competitor matrix — why screen-space text is a reject, and the Slug patent is dead

The capability/limit matrix. The recurring disqualifier is the top-of-page
reframing: the competitors emit **coverage**, not a **distance field** — nothing
to march, nothing to CSG, no analytic normal, and several carry `fwidth`-class
derivative AA that is a cross-backend parity hazard.

| Technique | Output | Corners | Zoom | CSG / marchable? | Analytic normal? | Cost | Weight | Determinism |
|---|---|---|---|---|---|---|---|---|
| Valve SDF | 1-ch distance | rounded | band-limited | yes (Lip-1) | via dual | 1 tap | tiny | trivial |
| MSDF (median) | 3-ch median | sharp (clashes) | band-limited | **NO** (C0 clash) | no | 3 taps | small | coverage-only |
| **MTSDF** | 4-ch | sharp + true α | band-limited | **yes (use α)** | via dual | 4 taps | small | trivial |
| Chamfer runtime | 1-ch | rounded | full | IF /1.0824 | via dual | 1 tap | medium | overshoot risk |
| Slug | coverage α | perfect | infinite | no | no | curve bands | large | fwidth risk |
| Vello/Pathfinder | coverage fb | perfect | infinite | no | no | full pipe | huge | prefix-sum risk |
| Loop-Blinn | in/out | sharp | infinite | no | no | per-tri | medium | ok |

### Slug — the quality ceiling, and the licensing objection is GONE (dated)

- **Source:** Lengyel, "GPU-Centered Font Rendering Directly from Glyph
  Outlines," JCGT 2017 (http://jcgt.org/published/0006/02/02/); "A Decade of
  Slug," 2026-03 (https://terathon.com/blog/decade-slug.html); MIT reference
  shaders (https://github.com/EricLengyel/Slug).
- **⚠ Licensing flip — dated so the stale objection dies:** On **2026-03-17**
  Eric Lengyel **DEDICATED US Patent 10,373,352 TO THE PUBLIC DOMAIN** (USPTO
  form SB/43) and MIT-licensed the reference shaders (Hackaday 2026-03-20,
  Adafruit 2026-03-22). The patent objection that historically ruled Slug out is
  **no longer valid.** Slug is now license-free.
- **Digest:** per-pixel winding number over Bézier band structures in a fragment
  shader; dynamic dilation for projection-correct AA. The quality ceiling for
  flat text.
- **Verdict:** **reject** for world-geometry text — architectural, **not
  licensing**. Produces per-pixel coverage, not a distance field: cannot CSG,
  cannot engrave, nothing to march, no analytic normal; heavier per-pixel than a
  tap; `fwidth`-class derivative AA is a parity hazard. Record it as the
  now-license-free flat-text quality ceiling, in case the demoted 2D-arc "option
  B" flat kernel ever revives.

### The rest of the coverage family (all reject, architectural)

- **Pathfinder 3** (Walton/Servo; https://github.com/servo/pathfinder) — CPU
  tiling prepass + GPU exact fractional coverage into a framebuffer; huge
  surface. **reject.**
- **Vello / piet-gpu** (Levien/Linebender; sparse strips 2024–2026;
  https://github.com/linebender/vello,
  https://raphlinus.github.io/rust/graphics/gpu/2020/06/13/fast-2d-rendering.html)
  — compute-centric exact-area coverage; prefix-sums/atomics raise cross-backend
  determinism questions. **reject** for world text; it is the current SOTA for
  the flat 2D vector canvas Puck deliberately does not want.
- **Loop-Blinn** (SIGGRAPH 2005) — per-triangle implicit in/out; no distance, no
  built-in AA. **reject**, historical.
- **Valve 2007** (Chris Green, "Improved Alpha-Tested Magnification for Vector
  Textures and Special Effects," SIGGRAPH 2007;
  https://dl.acm.org/doi/10.1145/1281500.1281665) — single-channel SDF +
  smoothstep; corners round. This one is **not** a reject: it is the baseline
  world-op field (Lipschitz-1, marchable), and corner rounding is often
  acceptable-or-desirable for engraved signage. MTSDF is the upgrade for
  flat-facing crispness.

---

## World-space specifics — the analytic-dual AA and the engrave near-novelty

### Perspective-correct AA from the analytic dual, not `fwidth`

Fragment-shader glyph AA uses screen derivatives — `coverage = clamp(0.5 +
d/fwidth(d), 0, 1)` — which is perspective-correct automatically (pkh.me,
"Perfecting anti-aliasing on signed distance functions," 2025-07-26;
https://github.com/Blatko1/awesome-msdf). Puck's compute march has **no
`fwidth`**, and `fwidth` is a cross-backend parity hazard regardless. But the
analytic dual gradient (`mapGradCore`) projected to screen gives the **exact**
distance-per-screen-pixel that `fwidth` only estimates — deterministic, computed
from the walk already done. **Recommendation:** perspective-correct AA for world
text = ray-differential × |∇d| from the dual, not `fwidth`. This is a specific
instance of the general Tier-0 coverage-AA the engine already landed —
cross-ref [antialiasing-and-filtering.md](antialiasing-and-filtering.md) and
[gradients-and-normals.md](gradients-and-normals.md).

### Engrave-as-CSG is near-novel — the correctness spec *is* C1/C2/C3

Prior art for **extrude/bevel/engrave of atlas fonts as CSG operands is
essentially NONE.** iq has `opExtrusion`/`opRevolution`; SDF-Modeler/Substance
have emboss Booleans (analytic shapes, not font atlases); Red Blob has
bevel-from-gradient (screen-space); CAD embosses B-reps. Puck's
sample-atlas-as-distance-inside-the-march + **Subtract-to-engrave** is close to
genuinely novel — there is no citable technique to copy, only C3's riders to
obey. The transferable knowledge is the correctness spec (C1/C2/C3), not a paper.
**Verdict: adopt-now (it is the entire point of the Tier-1 op); the spec is
C1/C2/C3.**

---

## Quality details — the bake recipe

A compact recipe for baking the atlas the glyph op consumes. All of these are
bake-time or fixed-sampler-state choices — none is a runtime parity hazard the
way `fwidth` is.

- **Field kind:** bake **MTSDF** with edge-coloring and `-errorcorrection` (an
  analytic bilinear-interpolation predictor that removes MSDF clashes at bake, at
  zero runtime cost — always on). Skia preprocessing resolves self-overlap /
  winding. March the **alpha** channel (C2).
- **`-pxrange`:** default = 2 is too thin for effects. Echo to the shader as
  `screenPxRange = pxrange × drawPx / atlasPx`. Use **pxrange 4–8** for
  outline / glow / engrave headroom (larger ranges buy effect headroom at
  outline-precision cost; `aemrange` = asymmetric range). Practical guidance:
  Amit Patel (Red Blob Games), "msdfgen parameters," 2024-09 upd. 2026-01
  (https://www.redblobgames.com/x/2437-msdfgen-parameters/) — 511² is usable;
  thin serifs need resolution.
- **Padding:** pad **≥ full pxrange** so neighbor glyph bands don't bleed into
  bilinear taps that straddle a corner.
- **Sampling:** bilinear, **fixed sampler state** — parity-safe (unlike
  `fwidth`), identical on both backends. Explicit-LOD in the march (implicit
  derivatives are undefined in the non-uniform march control flow).
- **Small-size gamma:** composite in linear; small SDF text reads light/heavy vs
  hinted rasterizers — a coverage sigmoid / `out_bias` compensates (Red Blob;
  msdf-atlas-gen #69). This is a composite-stage concern, not the field's —
  pursue only when small UI text exists.
- **Decline for world text:** subpixel / LCD / hinting (no fixed subpixel axis
  under rotation/perspective; hinting is meaningless for marched geometry).
  Upscaling is graceful within the band, fails outside (clamp → flat) — a larger
  bake pxrange is the only lever, no runtime trick.
- **Foundational refs:** Chlumský, "Shape Decomposition for Multi-Channel
  Distance Fields" (MSc thesis, CTU Prague, 2015, linked from
  https://github.com/Chlumsky/msdfgen); Chlumský & Sloup CGF 2018;
  https://github.com/Chlumsky/msdf-atlas-gen. JSON fields the loader consumes:
  `atlas.{type,width,height,size,distanceRange}`, per-glyph
  `advance`/`planeBounds`/`atlasBounds`, kerning pairs — matching
  `FontAtlas`/`FontAtlasGlyph`/`FontKerningPair` exactly.

### Neural glyph fields — offline baker only

- **Source:** multi-implicit neural font representations (Reddy et al., NeurIPS
  2021; US patent 11,875,435; NVIDIA GDC 2025 messaging). Adjacent sweeps
  surveyed but not relevant: "Greed for the Spheres" (arXiv:2605.01919), UDF
  reconstruction MIND (arXiv:2506.02938).
- **Verdict:** **reject at runtime** — NN eval is float-heavy and
  non-deterministic cross-backend; defensible only as an OFFLINE baker feeding
  the conventional atlas path. No 2023–2026 primitive beats "bake MTSDF with
  error-correction"; the one relevant generation result is the
  chamfer-optimal-weights theory (arXiv:1201.0876) that justifies the
  1.0824 / EDT fix in C1.

---

## Text enrichment — markup, per-glyph effects, and the determinism fix

Beyond drawing a glyph, the demo wants **animated, marked-up** text —
typewriter reveals, shakes, waves, engraved signage that pulses. A complete
same-author prior-art body exists (`SignedDistanceTerminal/…/Text/` +
`…/Avatars/` shaders) and ports cleanly. **The whole "bring it back" is
de-risked by one small, mechanical determinism fix (below).** The field's
vocabulary (Godot, Febucci) and the SDF-space effect formulas fill in what the
prior art doesn't cover.

### The prior-art inventory (durable facts)

- **The markup grammar** (`TextEnrichmentTags.cs`): NOT BBCode —
  four ASCII **C0 control chars** as delimiters (collision-proof, trivially
  strippable): `TagStart`=RS, `TagEnd`=US, `TagFieldSeparator`=GS,
  `TagValueSeparator`=FS, binding sigil=SUB. A single left-to-right rune scan
  with a `Stack<TextEffect>`: start = Push, end = Pop, `reset` = Clear; each
  visible rune pairs with `effectStack.Peek()`. **Nesting is stack-based,
  innermost SHADOWS** (no composition); malformed tags degrade to literal text.
  Tag names: shake, dissolve, wave, pulse, jitter (+ reset); unknown → dropped.
- **Parameter late-binding** (`TextEffectParameter.cs`) — the most portable
  idea: every numeric param = `(BaseValue, VariableHash, BindingMode)`; value
  strings may be `base⟨SUB⟩variableName`, late-bound at render against an
  environment list (FNV-1a hash). Modes by sigil: trailing `+` = Additive,
  trailing `*` = Multiplicative, empty base = Replacement. `Evaluate()` is pure
  arithmetic with `IsFinite` fallbacks. **If "variable" becomes a Puck
  content-tick / frame channel, the whole binding system is determinism-clean
  as-is.**
- **The effect catalog + authored defaults:**

  | Kind | amp | freq | duration | animates |
  |---|---|---|---|---|
  | Shake | 2.5px | 10 Hz | 1.0 | X+Y (two decorrelated sines) |
  | Wave | 3.0px | 2 Hz | 1.0 | Y (travelling sine) |
  | Pulse | 0.08 | 2 Hz | 1.0 | scale (1+amp·sin) |
  | Jitter | 1.5px | 14 Hz | 1.0 | hash-random X+Y per cycle |
  | Dissolve | 1.0 | 1.0 | 1.5s | erode/burn (own subsystem) |

  Animation math (`TextGlyphVisualEffect.Create`): `phase = effectPhase +
  glyphPhasePixels·0.17` (baseline-X **is** the stagger source); `cycle =
  seconds·freq + phase`; `wave = sin(τ·cycle)`, `second = cos(τ·cycle·1.37)`.
  Applied as a pixel nudge + centered quad scale — **transform-channel
  animation, not a field op**. No easing curves; the feel is all sine
  phase-offset. The **dissolve** subsystem was CPU-modelled (per-glyph
  seed/stagger/`localProgress`, Devilish vs Sickly styles) but **the GPU half was
  never finished** — the shipped `text.frag` consumes only the glow term. Its
  natural re-expression is a distance-threshold erode animation on a real glyph
  field.
- **THE DETERMINISM FIX (small and mechanical):** the prior art drives animation
  from `TimeSpan.TotalSeconds` (wall clock), threaded from
  `TextWindowGpuDrawFactory.Create` → `TextGlyphVisualEffect.Create`. That is the
  *only* thing that isn't determinism-safe — the math is otherwise pure (sines,
  FNV/GLSL hashes, no RNG, no history). **Swap `seconds` → produced-frame /
  content-tick everywhere and the entire model becomes deterministic with zero
  structural change**; the env-var binding likewise (variables → content-time
  channels). State this explicitly — it de-risks the whole port.

### The `sdfMsdfGlyph` prototype — Tier-1's reference implementation

The sibling repo's `…/Avatars/Assets/Shaders/Characters/avatar-vm.glsl`
(~lines 886–933) already contains `sdfMsdfGlyph`: an **extruded MTSDF glyph
slab that is a working world-geometry SDF primitive** (it marches, takes a 6-tap
gradient; the header calls it the "tattoo / engraving / embossing primitive").
Backlog Tier-1 is therefore "adapt an existing sibling primitive," not "write an
algorithm." Its three tricks, all correctness-load-bearing:

1. **Band-cull before the tap:** compute the exact 2D box distance `dQuad`
   FIRST; tap the atlas **only if** `dQuad < 0.5·distanceFactor` — zero taps in
   empty space (mirrors C3's proxy).
2. **Conservative max-combine for the far field:** `dPlane = max((0.5 −
   encoded)·distanceFactor, dQuad)` keeps the combined field a conservative
   underestimate outside the band (C3 rider b), then extrudes via the standard
   rounded-box combine.
3. **True-channel flag:** `decode = trueSdfFlag ? texel.a : median(rgb)` — the
   flag selects the MTSDF alpha (C2's correct channel) over the coverage-only
   median. Encoding is msdfgen's `0.5 + d/range`. Gradient is **6-tap central
   differences once per hit, never during marching** (explicit-LOD; implicit
   derivatives undefined in march flow). Engrave = Subtraction of this field;
   emboss = blend + analytic normal.

The screen-space quad-batch draw stack (`TextQuadBatch`/`DrawFactory`) is
**not** the world path — but its layout math is reusable for the Tier-0 CPU-raster
diegetic console.

### The field vocabulary — what to port for the effect authoring surface

- **Godot** `RichTextLabel`/`RichTextEffect`
  (https://docs.godotengine.org/en/stable/tutorials/ui/bbcode_in_richtextlabel.html;
  `class_richtexteffect`): `[wave]`, `[shake]`, `[tornado]`, `[fade]`,
  `[rainbow]`, `[pulse]`; custom via `_process_custom_fx(CharFXTransform{
  elapsed_time, range, offset, color, visible, env, glyph_index})` — **the
  canonical per-glyph state struct** (elapsed_time-driven; Puck substitutes
  tick).
- **Text Animator for Unity** (Febucci;
  https://docs.febucci.com/text-animator-unity/effects/built-in-effects-list):
  12 behaviors (wiggle, wave, shake, swing, bounce, pendulum, dangle, rot,
  slideh, incr, rainb, fade); universal modifiers `a` (amplitude) / `s` (speed).
  **KEY PATTERN (v3): any effect plays as Appearance, Persistent behavior, or
  Disappearance** — the same vocabulary drives typewriter-reveal transitions.
  Adopt this three-way split.
- **Substrate refs:** TMP per-vertex quad modification (`VertexJitter.cs`;
  https://github.com/LeiQiaoZhi/Easy-Text-Effects-for-Unity;
  https://github.com/Abban/TextMeshAnimator).
- **The canonical offset formula:** `offset = amplitude·sin(time·speed +
  charIndex·spacing)` — `charIndex·spacing` **is** the stagger mechanism
  (identical to the prior art's `glyphPhasePixels·0.17`). Wobble = sin on X + cos
  on Y at different freqs; shake = per-char per-frame hash.

### The SDF-space effect catalog — the cost dichotomy is the headline

**Cost dichotomy (headline):** transform / param / frame-channel effects are the
**CHEAP class** — free on the post-grid-cull instance budget. **Per-frame field
rebuild is the EXPENSIVE class** (the carve-path profile). Glyph morphing is the
one to **decline**. Sources: Chris Green SIGGRAPH 2007
(https://steamcdn-a.akamaihd.net/apps/valve/2007/SIGGRAPH2007_AlphaTestedMagnification.pdf);
Red Blob SDF/MSDF fonts (https://www.redblobgames.com/articles/sdf-fonts/); GM
Shaders SDF (https://mini.gmshaders.com/p/sdf); Bad Echo MSDF
(https://badecho.com/index.php/2023/09/24/msdf-fonts/).

| Technique | Formula | Puck mapping & verdict |
|---|---|---|
| AA fill | `clamp(screenPxRange·(d−0.5)+0.5, 0, 1)` | inherent to the march + analytic normals — **adopt-now** (rides Tier-1), S |
| Outline / multi-outline | `abs(d)−r` / second threshold | = the ONION op (already shipped); per-shape param, no rebuild — **adopt-now** S; multi = stacked onions |
| Glow / halo | `1−smoothstep(0,R,outside d)` or `exp(−|d|·k)` | cheap fake = emissive rim keyed on `normal·view` + exterior distance — **adopt-now** S; true volumetric glow is a shading/participating-media question — **defer**. Prior-art ceiling: radius ≤ encoded band |
| Drop shadow | offset sample | MOOT — real geometry casts real shadows (softShadow + shadow-cull) — **reject** |
| Bevel / emboss | `N=normalize(∇d)`, `dot(N,L)` | THE STANDOUT: engrave = Subtraction(base, glyph); emboss = blend; real bevel lighting FREE from analytic normals — **adopt-now** (rides Tier-1), M; static geometry, lighting animates |
| Dissolve / erode | animate threshold `d−t(time)`; noise edge `d−t+noise·k` | animate the dilate/erode modifier via a FRAME CHANNEL; prior-art `localProgress` becomes `t` — **adopt-now (if param/frame-channel)**, M; expensive only if the field reshapes per frame |
| Stroke-width anim | animate dilate radius | frame channel — **adopt-now**, S |
| Gradient fill on distance | `albedo = ramp(d)` | rides the material-blend-factor roadmap item — **gated-on material channel**, M |
| Glyph morphing | `lerp(dA, dB, t)` | CLASSIC FAILURE: naive SDF interpolation violates SDF properties → ghosting / pinching / phantom zeros (Greed for the Spheres arXiv:2605.01919; VecFontSDF arXiv:2303.12675); atlas-to-atlas is worse; AND per-frame-rebuild class — **reject / defer**, L |

### Deterministic reveal — the recipe

Fixed-timestep/replay lineage: the reveal must be a **pure function of
content-tick** (rollback-netcode principle;
https://gamineai.com/blog/rollback-netcode-explained, arXiv:1810.11865). Godot
`visible_characters`/`visible_ratio` and Febucci appearances are
`elapsed_time`-driven — trivially rebased to tick. Recipe:

```
revealedGlyphs   = floor((tick − startTick) · charsPerTick)
localProgress[i] = clamp((tick − startTick − i·stagger) / duration, 0, 1)
```

Per-glyph `localProgress` drives a per-instance transform + a distance-threshold
**erode-in** (dissolve played in reverse = materialize). The prior-art
hash-seed model is RNG-free, so it is replay-safe the moment `seconds` → `tick`.
**Verdict: adopt-now, S–M.**

### Markup design and accessibility

- **Markup families:** BBCode `[wave]…[/wave]` (human-authorable; Godot/Febucci)
  vs rich-span side-tables (programmatic / localization-friendly) vs
  control-char inline escapes (collision-proof, ugly to author). Parse cost is
  negligible for all. **Recommendation:** a **BBCode-ish authoring front-end that
  COMPILES TO the prior-art control-char stream** (keep the robust stack parser)
  **plus a span side-table** for the console / JSON paths — agents shouldn't be
  embedding C0 control chars in JSON. **adopt-now, S.**
- **Accessibility:** WCAG 2.2.2 (Pause/Stop/Hide, level A) + 2.3.3 (Animation
  from Interactions, AAA) — non-essential motion must be disable-able; continuous
  motion is a vestibular risk (https://web.dev/learn/accessibility/motion; W3C
  C39 `prefers-reduced-motion`; A11Y Collective). WCAG 2.3.1: no flashing > 3/sec
  (bounds the rainbow/pulse rates). **Verdict: adopt-now, S** — reduced-motion as
  a **run-doc field + console verb** (`text.motion off|reduced|full`, per the
  env→verb doctrine): damps amplitudes / freezes motion while KEEPING static
  enrichment (color/outline/emboss). Restraint (below) and accessibility point
  the same way.

---

## The delight doctrine — DELIGHT ≠ MOTION

This finding is small but should shape **every future text build brief**, so it
gets its own section. The contrarian, load-bearing result from the games that do
animated text best is that **constant per-glyph motion is not what makes text
feel good — semantic, sparse, event-driven emphasis is.**

- **Toyful Games**, "Deep Dive: Animating Text"
  (https://www.toyfulgames.com/blog/animating-text): abandoned easing curves;
  Nintendo-style "characters just pop in," linear reveal at 3–4 chars/sec, ≤ 3
  lines on screen, and **SEMANTIC COLOR** (nouns vs verbs) carried the delight —
  not motion.
- **Celeste:** sparing, event-driven emphasis — highlighted words, size/spacing
  changes only at eventful moments (Shacknews
  https://www.shacknews.com/article/125697/; NME on the synthesized voice).
- **Undertale:** per-speaker typing **sound** + speed — the effect *is*
  character (https://github.com/SoupTaels/UTDR-SoupGen).
- **General:** "don't juice for juicing" (Designing Game Feel survey,
  arXiv:2011.09201).

**Doctrine:** default to **restraint** — sparse, semantic, event-driven emphasis;
per-speaker character; constant motion is **opt-in**, never the default. This
also satisfies the accessibility verdict for free (reduced-motion keeps the
static, semantic layer and drops only the constant motion). **adopt-now as a
posture, S.**

---

## Verdict summary

| Technique | Verdict | Effort | Gating dependency |
|---|---|---|---|
| MTSDF-alpha as the marchable field source (loader path) | adopt-now | S–M | pre-baked atlas; sample the **alpha**, never the median (C2) |
| Extruded-slab proxy + box-fallback combine (C3) | adopt-now | rides Tier-1 | required — atlas fields are band-limited |
| Engrave/emboss as CSG (Subtraction / blend) | adopt-now | M | analytic normals (`ce36f80`); correctness spec = C1/C2/C3 |
| Runtime chamfer→SDF generator | gated-on-1.0824-fold-or-EDT-swap | S–M | fold ÷1.0824 (C1) or swap exact EDT; no-toolchain fallback only |
| Analytic-dual perspective AA (not `fwidth`) | adopt-now | S | rides the shipped Tier-0 coverage AA + `mapGradCore` |
| Slug (coverage-from-outline) | reject (architectural) | — | coverage, not distance; patent public-domain 2026-03-17 — record as flat-text ceiling |
| Pathfinder / Vello / Loop-Blinn | reject (architectural) | — | coverage / framebuffer, not a marchable field |
| Neural glyph fields at runtime | reject (offline baker only) | — | non-deterministic cross-backend NN eval |
| Enrichment: transform/param/frame-channel effects | adopt-now | M | the determinism fix (`seconds`→tick); post-grid-cull ≈ free |
| Determinism fix (`TotalSeconds`→content-tick) | adopt-now | S | mechanical; math already RNG/history-free |
| Markup: BBCode front-end → control-char stream + span side-table | adopt-now | S | keep the prior-art stack parser |
| Deterministic typewriter reveal (tick-driven) | adopt-now | S–M | reveal = pure function of content-tick |
| Glyph morphing (`lerp(dA,dB,t)`) | reject / defer | L | violates SDF properties + per-frame-rebuild class |
| Reduced-motion (run-doc field + `text.motion` verb) | adopt-now | S | WCAG 2.2.2/2.3.1/2.3.3 |
| Delight doctrine (restraint; semantic, event-driven) | adopt-now (posture) | S | shapes every future text brief |

## See also

- [materials-and-primitives.md](materials-and-primitives.md) — the Valve-2007
  MSDF-lineage entry this page supersedes with the deep-reviewed field-source
  finding; the smin/blend taxonomy the engrave op rides.
- [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) — the
  `stepScale = 1/L` and exactness discipline C1/C3 are instances of.
- [gradients-and-normals.md](gradients-and-normals.md) — the analytic dual that
  supplies glyph normals and the perspective-AA gradient.
- [antialiasing-and-filtering.md](antialiasing-and-filtering.md) — the shipped
  Tier-0 coverage AA the analytic-dual text AA specializes.
- [verdict-index.md](verdict-index.md) — every deep-reviewed technique in one
  table.
- [../sdf-backlog.md](../sdf-backlog.md) — backlog item 21, the diegetic-UI arc
  whose Tier-1 glyph op this page is the reference for.
