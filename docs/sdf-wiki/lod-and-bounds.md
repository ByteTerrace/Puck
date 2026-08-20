# Level of detail and bounds

Puck uses conservative bounds to skip work without changing the rendered
field. A skip is valid only when the omitted candidate cannot affect the
accumulated result for the current sample.

## Bound levels

- Instance bounds cover a contiguous instruction range in world space.
- Segment analysis identifies portions of an instance that can be bounded and
  skipped independently.
- Sampled-region bounds cover a brick-backed carve cache.
- Screen-surface frames bound texture parameterization, not field influence.

Bounds must include transform reach, smooth-blend radius, and scoped field
expansion. Operations whose influence cannot be bounded conservatively make the
affected range ineligible for exact skipping.

## Proxy and distance-dependent detail

Distance-dependent fidelity is safe only when the proxy has a documented
relationship to the authoritative field. A visual approximation may be useful
for a separate content source or far-field tier, but it cannot silently replace
the field used for collision, deterministic queries, or close rendering.

Prefer author-provided proxy nodes when their error can be bounded. Automatic
simplification should expose its error metric and transition policy in data.

## Domain-fold copy expansion

The contact paths take a domain fold as the finite set of rigid copies
`SdfDomainExpansion` derives, so every fold is measured against a copy budget
before it becomes colliders. That budget is judged against the count a branch
set *would* have, in closed form — `(2l_x+1)(2l_y+1)(2l_z+1)` cells,
`count·(mirror ? 2 : 1)` sectors — never against a materialized list.

The values reaching that judgement are authored, so they are hostile-document
scale: a repeat limit of 120 is fourteen million frames, and any limit at or
past 645 exceeds `Array.MaxLength`, which makes a measure-after-materializing
refusal a certain `OutOfMemoryException` rather than a refusal. A refusal that
costs what it refuses is not a refusal, and this one runs inside the world
validator, whose job is to say no.

The document doors carry the ceilings the expansion cannot infer: a repeat
limit above the unbounded sentinel and a polar sector count above the largest
integer the packed program represents exactly are both creation document
validation errors.

## Sampled carve regions

`SampledRegion` is an invalidatable render cache for dense subtractive carve
sets. The analytic program and authored carve list remain authoritative. The
cache is bounded, versioned by bake state, and safe to discard or rebuild.

The stored field includes its conservative scale and boundary floor. A missing
or unavailable brick must fall back to an uncarved conservative result, never a
hole.

Per-segment bounds for placed creations remain an open priority, tracked
nowhere: this paragraph is the whole record of the item.
