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

The sources and scripts have not been compiled or run yet. An executable
`canary.json` waits for the production G9 counters and reference observation;
there is no independent near-field console switch. The no-bleed leg must reject
the same positive bleed bound and hold its opposite while preserving geometry,
light and tier. The furnace needs the shader mutation that adds near light to
the cache; the gloss/fog case needs the mutation that reads colour history.
Neither is proved by merely switching to medium, which also changes cache layout.
Keep the existing temporal ghosting and disocclusion canaries in qualification.

Before landing executable assertions, compile and validate the sources with the
current CLI, obtain the G1 reference and its tolerance, observe the real captures
and counter fields on both backends, and demonstrate each named discriminator.
The RTX 4070 qualification and measured rows remain owed.
