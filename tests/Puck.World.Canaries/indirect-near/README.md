# Indirect near-field fixtures

These sources prepare the G9 scenes for the real offscreen World. They use the
existing creation, camera, material and indirect-tier vocabulary. The production
near-field contract is high only: at most one ray per four render pixels, 0.5m
reach and 12 steps, replacing the cache estimate over that interval.

| Source | Purpose |
|---|---|
| `scenes.puck` | Shared geometry and three state-selected camera programs |
| `fixture.puck` | Matte reference scenes at 128×128 |
| `gloss-fog.puck` | The same geometry with glossy materials and fog |
| `no-bleed.puck` | The same matte geometry with only source bleed changed to black |
| `positive.script.txt` | High captures of the near source, furnace and sealed room |
| `no-bleed.script.txt` | Captures the no-bleed source at high tier |
| `medium.script.txt` | The below-high tier control |
| `emission.puck`, `emission-off.puck` | Identical small metallic source and receiver; only emission changes |
| `emission.script.txt`, `emission-off.script.txt` | One shared-picker explanation, capture and actual Near work row |
| `canary.json` | The initial paired emission and fixed-first-ray reference contract |

The sibling [furnace](../indirect-near-furnace/README.md),
[bleed](../indirect-near-bleed/README.md),
[gloss/fog](../indirect-near-gloss/README.md) and
[sealed-room](../indirect-near-sealed/README.md) canaries reuse these prepared
scenes with explicit source masks, finite depths and fenced explanation
assertions. Their manifests strictly load, but the actual receiver stations,
source values, backend observations and native shader discriminators remain
owed. The sealed Near station adjusts its camera to put the unchanged exterior
emitter inside the receiver's sampling hemisphere. The separate
[shared-cache sealed canary](../gi-sealed/README.md) observes planar and curved
partitions.

The near source is a red wall 0.2m tall and 0.02m thick, beside a white floor,
plus a 0.12m-diameter red object. Both are smaller than the finest 0.5m cache
spacing. A horizontal white light illuminates their side without directly
lighting the horizontal receiver. Source receive gain is zero; floor receive
gain is one. Fill and environment gains are zero, and the sky is black.

The furnace uses the closed shell construction in
[IrradianceScenes](../../Puck.SignedDistance.Tests/IrradianceScenes.cs), scaled to
inner radius 0.3m and wall thickness 0.02m so near rays encounter the enclosure.
Every surface has the same material. Its reference is the existing finite
series `e(1 + rho + … + rho^n)`, using the actual emitted material's linear
albedo, emission and solve bounce count. The source colour is not a replacement
for reading those values from the current material contract.

The sealed room uses G1's outer-box minus inner-box construction, with a 0.025m
wall, a small interior receiver and a bright exterior emitter. The camera is
inside. This checks leakage without introducing a different scene mechanism.
Creation X and Z positions negate at emission; the source documents the one
offset whose engine-space identity matters.

The glossy source changes only the palette and fog. Its bounce must be compared
before specular and fog composition against G1's diffuse reference. Equality of
the final fogged and unfogged images would be the wrong assertion.

The sources and scripts have not been compiled or run yet. The executable
canaries use `world.wait indirect`: a newer produced frame and every active shared cache's
current source fence, rather than a fixed elapsed tick count. Cadence is off so
the bounded Near phase sequence continues to render. The actual GPU detail row
is `indirect-near`; it counts field queries, loads, hashes and indirect
hits/samples/unresolved separately from the cache row.

The initial `canary.json` uses `emission.puck` and `emission-off.puck` for a
small red source above a white receiver. Source metalness is one, so reflected
feedback is zero while nonzero emission and white bleed remain. Feedback is
still enabled: the current source's predecessor and secondary proof remain
required. The companion changes only emissive strength from one to zero. Both
legs require a Hit, resolved status and the source/reference in the same fenced
explanation. The incoming source, echoed source and reference source sequences
must agree; the read and published stamps must agree; the predecessor is nonzero,
and the independent reference has no unresolved paths. These fields come from
the captured transport seam, never a later live publication. The pointer at
64,64 and first request's admission are provisional
until a real World run confirms the authored station; an unavailable answer
fails rather than borrowing another response. The manifest covers this emission
isolator, not every prepared scene below.

`world.view.pointer` followed by `world.explain` uses the existing shared picker
and exposes fenced Near outcomes and incident source categories before material,
specular and fog composition. The pick retains the actual launched origin,
sampled direction, current stamp, exact predecessor and immutable current source
under that same fence. Missing or mismatched provenance still names the incoming
reference as unsupported. The existing
independent reference uses that fixed first ray, then Halton paths for feedback;
a cache mean cannot stand in for the observation. A numeric reference refusal
fails the manifest. The actual `views`/`indirect-near` work row must show queried,
sampled and hit work after the world node. Its six extracted counters belong to
the newest completed submission, not cumulative warm-up totals. There is no independent
near-field console switch. The no-bleed leg must reject
the same positive bleed bound and hold its opposite while preserving geometry,
light and tier. The furnace needs the shader mutation that adds near light to
the cache; the gloss/fog case needs the mutation that reads colour history.
Neither is proved by merely switching to medium, which also changes cache layout.
Keep the existing temporal ghosting and disocclusion canaries in qualification.

A Near answer is one cosine sample, not the reference's integrated mean.
Homogeneous furnace energy has an independent finite-series oracle; the small
object emission isolator uses the captured first direction: a hit emits exactly
linear red and its independent reference must stay within 20% of that value.
The separate diffuse-bleed scene needs a bounded ensemble and an independently
justified envelope. The
existing capture `converge` field renders a bounded temporal sample sequence with
presentation values held, without introducing a second observation harness.

Before qualifying the executable assertions, compile and validate the sources with the
current CLI, obtain the G1 reference and its tolerance, observe the real captures
and counter fields on both backends, and demonstrate each named discriminator.
The RTX 4070 qualification and measured rows remain owed.
