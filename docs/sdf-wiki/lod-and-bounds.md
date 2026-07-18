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

## Sampled carve regions

`SampledRegion` is an invalidatable render cache for dense subtractive carve
sets. The analytic program and authored carve list remain authoritative. The
cache is bounded, versioned by bake state, and safe to discard or rebuild.

The stored field includes its conservative scale and boundary floor. A missing
or unavailable brick must fall back to an uncarved conservative result, never a
hole.

Per-segment bounds for placed creations remain an open priority. Track
implementation work in [the SDF backlog](../sdf-backlog.md).
