# An impostor through its handover band

The existing material and visibility observations stay intact. The additional
camera traversal uses the same two-box prototype, bake, view and fixed target:
281 units selects the card, 170 enters its handover band, 120 returns to the
mesh, 170 stays on that mesh, and 281 selects the card again. The field-only
companion traverses the same stations and records neither representation.

For this authored prototype, the baker's actual source rule is
`max(length(shape.position) + BoxReach(shape.scale))`. Its radius is
`sqrt(0.6² + 0.5²) + sqrt(3)`, about 2.51308. At the fixture's 576-line grid and
0.9-radian field of view, the projected diameters at 281, 170 and 120 are about
10.66, 17.63 and 24.97 pixels. The Standard impostor's edge is 16 pixels and its
quarter hysteresis returns at 20. Those bounds distinguish the two visits to
170 without inventing a new LOD control.

Every station waits six actual offscreen ticks, reads `sdf.mesh.lod`, waits
four ticks and reads it again. The relations use those cumulative differences,
not the counts incurred while a camera edit was applying. Only the selected
representation increments. Presentation-only `world.bakes` is also read
between two authoritative hashes while simulation is paused; the hash and
reported simulation tick both stay equal. Rendering and bake readiness
continue during that pause; no tick wait is armed until resume.

These source additions have not run in World. The actual render grid, identity
retention across authored camera edits, settled station counts and paused
hashes remain qualification work on Vulkan and DirectX. Existing
`BakeImpostorCanaryGeometryLawTests`, `SdfMeshLodLawTests`,
`SdfImpostorLawTests` and `CreationBakeLawTests` own the independent geometry,
silhouette, handover and shipped-pack/cache controls. The three retained
`BakeSamplingDeviceLawTests` results cover unchanged shader sampling and do
not need another run for this data extension.
