namespace Puck.SdfVm;

/// <summary>Identifies an SDF primitive. Values and data layouts must match the <c>SDF_SHAPE_*</c> definitions and
/// shape decoders in <c>Assets/Shaders/Sdf/sdf-vm.hlsli</c>.</summary>
public enum SdfShapeType : uint {
    Box = 0, // Data0 = (halfX, halfY, halfZ, roundingRadius)
    Capsule = 1, // Data0 = (endX, endY, endZ, radius); the segment runs from the local origin to the endpoint
    Sphere = 2, // Data0.x = radius
    Torus = 3, // Data0 = (majorRadius, minorRadius, _, _)
    Cylinder = 4, // Data0 = (radius, halfHeight, _, _); upright, centered on the local origin
    Plane = 5, // Data0 = (normalX, normalY, normalZ, offset)
    Ellipsoid = 6, // Data0 = (radiusX, radiusY, radiusZ, _)
    Vesica = 7, // Data0 = (radius, halfSeparation, halfHeight[baked √(r²−d²)], _); exact 2D vesica revolved to a lens (d < r)
    // --- The 2D-primitive family (an exact 2D SDF lifted to 3D by revolve/extrude). SHARED lane layout for all of
    // them: Data0.xyz = the 2D shape params, Data0.w = the lift amount (revolve offset o OR extrude half-height h),
    // Data1.x = smooth radius, Data1.y = the lift MODE (0 = revolve around Y, 1 = extrude along Z), Data1.zw = per-shape
    // host-baked constants. Each is exact + 1-Lipschitz (no step clamp): extrusion is always exact; revolution is exact
    // when the profile clears the axis (offset ≥ its radial extent) and a harmless conservative bound near the axis.
    RoundedRectangle = 8, // Data0 = (halfX, halfY, cornerRadius, lift); exact rounded-box 2D SDF
    RegularPolygon = 9,   // Data0 = (circumRadius, π/n[baked], 0[ecs.x], lift), Data1.z = 1[ecs.y]; exact star-polygon SDF with m=2
    Star = 10,            // Data0 = (outerRadius, π/n[baked], cos(π/m)[baked ecs.x], lift), Data1.z = sin(π/m)[baked ecs.y]; exact star-polygon SDF
    RoundCone = 11, // Data0 = (lowerRadius, upperRadius, height, _)
    Trapezoid = 12,       // Data0 = (bottomHalfWidth r1, topHalfWidth r2, halfHeight, lift); exact isosceles-trapezoid 2D SDF
    Ellipse = 13,         // Data0 = (semiX, semiY, _, lift); exact ellipse 2D SDF (revolve→spheroid, extrude→elliptic prism)
    ScreenSlab = 14, // Data0 = (halfX, halfY, halfZ, roundingRadius)
    // A glyph SAMPLED FROM A FONT ATLAS as a DISTANCE-level field (not material-level like ScreenSlab): text becomes
    // real world geometry that marches, blends, extrudes, engraves (Subtraction) and takes shadows/AO. Data0 =
    // (packedUvMin, packedUvMax [each unorm2x16-packed atlas UV, host-baked], distanceScale, extrudeHalfDepth); Data1 =
    // (smooth [ISA-wide, header/Data1.x], halfWidth, halfHeight, _). Evaluated only where the glyph atlas is bound (the
    // world-views kernel, SDF_GLYPH_ATLAS); every other kernel falls back to the exact 2D quad's extruded box — a
    // conservative underestimate, since the glyph is strictly inside its cell. KEEP IN SYNC with SDF_SHAPE_GLYPH.
    Glyph = 15,
    // A SAMPLED distance-field brick: the settled-carve UNION field (min_i(|p-c_i|-r_i)), baked once into a cubic-voxel
    // lattice the kernels sample O(1) with manual trilinear interpolation, composed into the analytic program as ONE
    // ordinary Subtraction-blend instance so the primary/shadow/AO marches stop paying O(carve-count). Data0 =
    // (boxMinX, boxMinY, boxMinZ, cellSize); box extent derives as dims*cellSize. Data1 = (smooth [ISA-wide, = 0 for the
    // hard subtraction a brick composes with], packedDims [uint bits: 3x10-bit dims, <= 1023/axis, host-packed],
    // brickWordOffset [uint bits: the brick's base word in the sdfBrickPool buffer], boundaryFloor [= margin/lambda,
    // host-baked - the outside-box lower-bound offset]). The stored values are pre-scaled c/lambda (lambda = sqrt(3)
    // folded in at bake time), so the trilinear interpolant is 1-Lipschitz and march-safe with NO stepScale change and
    // an unchanged zero set. Evaluated only where the brick pool is bound (SDF_SAMPLED_REGIONS - the world-views + beam
    // kernels); every other kernel falls back to the conservative UNION
    // HULL - SDF_FAR_DISTANCE, so the subtraction never bites and the region renders uncarved (the Glyph quad-fallback
    // precedent: solid, never a hole). KEEP IN SYNC with SDF_SHAPE_SAMPLED_REGION.
    SampledRegion = 16,
}
