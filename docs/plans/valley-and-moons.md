# The valley, the moons and the flight between

The owner's direction: the gallery and its surroundings sit in a relatively
deep valley, under a sky with moons that carry portals and can be reached by
flying up through the atmosphere into space. Codex and Antigravity reviewed the
existing engine independently, reconciled their inventories against the code
over three rounds, and converged on the plan below. Phases land in order; each
ends with the check named in it.

## What exists, in part or whole

| Area | State | Evidence and limit |
|---|---|---|
| Fixed-point positions | Exists | `FixedPosition` origins with bounded fixed local coordinates; the presentation path narrows to float without subtracting an exact render origin, so precision falls off with distance from the world origin. |
| Static field contact | Exists, with admitted subsets | `GridCellSize` defaults to 0, which means exact evaluation with no distance grid (`WorldCollider.cs`); an oversized grid declines and falls back rather than throwing (`SdfDistanceGrid.TryCoverBox`). Solid placements skip authored sweeps and detail shapes (`CreationStampEmitter.EmitFixed`) and refuse non-detail revolve lifts (`CreationShapeLaw`), so walkable terrain is authored from admitted shapes. |
| Gravity | Partial | Authored planet sources by surface gravity and radius and local gravity areas exist (`WorldGravity`); wide arithmetic at planetary distances needs widening and unrounded composition. |
| Changing up | Exists | `WorldBodyUpPolicy` (Ambient, SurfaceFollowing) with the separate `GradientDerivedUp` contact requirement. |
| Flight | Partial | The Moth's Free and Lift motion (`avatars/moth.puck`) is body-frame 6DOF free integration by design, with no ground pin or gravity; assisted leveling and cruise are new authored policy, not a defect. `World` and `Heading` intent are composed correctly and must not be swapped. Grounding facts are written under every motion program; the ownership gate controls only write-back. |
| Terrain bake | Partial | The bake's reach is `max(1, farDistance)`, and an instance's radius is clamped to it, which can clip a huge instance; post-contour chunks do not stream. Coarse sampling is 65 samples on a shape's longest axis with conditional refinement (4, detail 8). |
| Draw distance | Partial | `farDistance` is capped at 8192 m (`WorldRenderDefaults.MaxFarDistance`); haze extinction is `-log(1 - haze) / farDistance`, which couples haze to draw distance. |
| Shadows | Partial | Key-light receiver splits at 16 and 120 m, second light at 40 m, casters reaching 200 m beyond each cascade toward the light (`SdfMeshShadowCascades`); macro terrain shadowing is missing. |
| Depth transport | Exists, to qualify | Reconstruction packs coverage-weighted inverse distance in half lanes for harmonic-mean filtering; single samples already carry float `t`. Longer ranges are qualified, and lanes widened together only if precision runs out. |
| Sky | Exists | `Disc` and `View` layers, stars, clouds, panorama; a `World` infinity view is opaque, while a far view uses geometry coverage. |
| Atmosphere | Partial | A dome seen from the ground (`SdfAtmosphere`, `sdf-atmosphere.hlsli`); no altitude dependence, no spherical shell, no transition to vacuum. |
| Worlds and portals | Partial | Portals are planes; adjacency seams are planar and horizontal, with prior art in the quarantined island root (four neighbours at 132 m); handoff of an arbitrary frame (orientation, up, velocities) is incomplete. |
| Regions | Partial | One physical field topology per world today (the combat and weather decisions widen it). |
| Streaming | Missing | No predictive residency of surface tiles. |

## Phases

0. **Baseline.** Drive the Moth's existing flight, portal round trips and the
   gallery routes; record whole-frame time, body, mesh, source and staging
   counts, cold and warm bake work, and a real C# and HLSL edit-to-running
   time.
1. **The valley.** Keep the gallery precinct at y 0 on the valley floor: about
   2 km across, 250 to 450 m of relief, a clear floor about 400 m across that
   keeps every exhibit, aperture, the studio, the camera course, the devices
   and the rate range, and reserves the combat yard (north) and the climate wing
   (east). Merge the old slab edge into the terrain, add an overlook, and
   replace the global fall floor with local recovery. Terrain uses the current
   SDF, contact and mesh systems in eight separately editable macro sectors
   with local detail; a heightfield source is added only if measured analytic
   terrain fails the frame or edit bar. Draw distance starts at 6000 m inside the
   8192 m cap, haze is decoupled from draw distance, macro terrain shadows are
   added, and displayed surface error stays inside the one-pixel rule with its
   hysteresis below one pixel.
2. **One real moon.** A moon source world with a surface arrival and a return
   portal, through the existing references, destinations and sessions. The
   valley sky shows that same world as a `View` layer with an authored coverage
   mode and intentional moon lighting. One moon is finished and measured before
   more.
3. **Exact frames for finite bodies.** One centre, radius, frame and source
   relationship gives the moon's sky image, physical placement, gravity,
   apparent size, phase and approach camera. The precision boundary moves: exact
   frame and local pose travel through snapshots, disclosure and transfer, and
   the renderer subtracts an exact render origin before narrowing to float,
   for interpolation, cameras, views, shading, shadows, GI and picking. Gravity
   arithmetic is widened. The 8192 m cap rises only after the precision work.
4. **Residency.** Canonical bounded source tiles with shared borders and coarse
   exteriors, admitted and evicted across all views, planned against the
   authored flight speed and braking; tile size is measured, not fixed.
5. **Atmosphere, clouds and flight.** A body-frame spherical-shell atmosphere
   with surface occlusion, keeping local valley mist; bounded clouds before
   flying through them; exposure continuous into vacuum. Flight reuses the
   Moth's local flight with optional assisted cruise and touchdown.
6. **Approach and handoff.** The ascent stays under the parent authority;
   the moon's far image and its detailed surface depict one surface with no
   duplicate silhouette; the frame handoff carries orientation, up, linear and
   angular velocity, integrator remainder, held input, identity and history
   exactly once.
7. **The round trip.** Gallery, rim, clouds, the atmosphere's limb, vacuum, moon
   approach, landing, portal, gallery, and the reverse by flight, including cold
   work and a delayed or refused transfer, at a steady 60 FPS on the RTX 4070 at
   the highest settings, both backends, edit-to-running under 30 s. More moons
   and content follow it.

## Questions for the owner

1. The valley's character: alpine basin, narrow canyon, eroded desert, volcanic
   caldera or another; close looming walls or distant peaks beyond open slopes.
2. Its relationship to the Nexus: a valley on a planetoid beneath the floating
   Nexus, or the gallery valley on the Nexus landmass itself.
3. The moons: how many, their apparent size, look (colour, rings, settlements),
   and daytime crescents or night dominance.
4. Flight: the Moth's wings, a craft or both; assisted horizon flight or freer
   attitude; a short ascent or a long cruise; open landing anywhere or authored
   destinations.
