namespace Puck.SignedDistance;

/// <summary>Identifies an SDF primitive. Values and data layouts must match the <c>SDF_SHAPE_*</c> definitions and
/// shape decoders in <c>Assets/Shaders/Sdf/sdf-vm.hlsli</c>.</summary>
public enum SdfShapeType : uint {
    Box = 0, // Data0 = (halfX, halfY, halfZ, roundingRadius)
    Capsule = 1, // Data0 = (endX, endY, endZ, radius); the segment runs from the local origin to the endpoint
    Sphere = 2, // Data0.x = radius
    Torus = 3, // Data0 = (majorRadius, minorRadius, _, _)
    Cylinder = 4, // Data0 = (radius, halfHeight, _, _) inset by Data1.w, Data1.w = edge-rounding radius; upright, centered on the local origin
    Plane = 5, // Data0 = (normalX, normalY, normalZ, offset)
    Ellipsoid = 6, // Data0 = (radiusX, radiusY, radiusZ, _)
    Vesica = 7, // Data0 = (radius, halfSeparation, halfHeight[baked √(r²−d²)], _); exact 2D vesica revolved to a lens (d < r)
    // --- The 2D-primitive family (an exact 2D SDF lifted to 3D by revolve/extrude). SHARED lane layout for all of
    // them: Data0.xyz = the 2D shape params, Data0.w = the lift amount (revolve offset o OR extrude half-height h),
    // Data1.x = smooth radius, Data1.y = the lift MODE (0 = revolve around Y, 1 = extrude along Z), Data1.z = per shape
    // (RegularPolygon/Star: the baked ecs.y constant; RoundedRectangle/Trapezoid/Ellipse: the cap-chamfer radius that
    // bevels an extrude's two cap rims through sdfExtrudeChamfer2D, zero = the plain join; ChamferedRectangle: unused,
    // its own chamfer doubles as the cap bevel), Data1.w = the edge-rounding radius (family-wide lane: the Data0
    // profile params and, for an extrude, the lift amount arrive already inset by it, and the kernel offsets the whole
    // field back out by it).
    // Each is exact + 1-Lipschitz (no step clamp): extrusion is always exact; revolution is exact
    // when the profile clears the axis (offset ≥ its radial extent) and a harmless conservative bound near the axis.
    RoundedRectangle = 8, // Data0 = (halfX, halfY, cornerRadius, lift), Data1.z = cap chamfer; exact rounded-box 2D SDF
    RegularPolygon = 9,   // Data0 = (circumRadius, π/n[baked], 0[ecs.x], lift), Data1.z = 1[ecs.y]; exact star-polygon SDF with m=2
    Star = 10,            // Data0 = (outerRadius, π/n[baked], cos(π/m)[baked ecs.x], lift), Data1.z = sin(π/m)[baked ecs.y]; exact star-polygon SDF
    RoundCone = 11, // Data0 = (lowerRadius, upperRadius, height, _)
    Trapezoid = 12,       // Data0 = (bottomHalfWidth r1, topHalfWidth r2, halfHeight, lift), Data1.z = cap chamfer; exact isosceles-trapezoid 2D SDF
    Ellipse = 13,         // Data0 = (semiX, semiY, _, lift), Data1.z = cap chamfer; exact ellipse 2D SDF (revolve→spheroid, extrude→elliptic prism)
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
    // A member of the 2D-primitive family (SAME lane layout as RoundedRectangle): a 45-degree-beveled rectangle (the
    // box field intersected with a diagonal bevel half-plane per corner) lifted to 3D by revolve/extrude. Data0 =
    // (halfX, halfY, chamfer c, lift); Data1 = (smooth, lift mode, UNUSED, edge-rounding radius r — the family-wide
    // fillet lane, applied ON TOP of the chamfer). The extrude lift additionally bevels the two cap edges at the SAME c
    // (see sdfExtrudeChamfer2D): a chamfered box therefore reads chamfered on all twelve edges, not just the four the
    // 2D profile itself cuts. c = 0 is the identity — the 2D core and the extrude join both reduce to the plain
    // rectangle/box forms to the bit — so an unchamfered program stays byte-identical. The field is exact inside and
    // on the surface and a conservative lower bound outside in the wedge past each bevel vertex (like the chamfer
    // blend); 1-Lipschitz (no AnalyzeLipschitz step clamp), like the rest of the family. KEEP IN SYNC with
    // SDF_SHAPE_CHAMFERED_RECT.
    ChamferedRectangle = 17,
    // Superellipsoid = 18: NOT a member of the 2D-primitive family above (it lifts nothing — a solid 3D formula
    // directly). Data0 = (radiusX, radiusY, radiusZ, exponent e in [2, 8]); Data1 = (smooth [ISA-wide], 1/radiusX,
    // 1/radiusY, 1/radiusZ [host-baked, KEEP IN SYNC with sdfSuperellipsoid]). e = 2 reduces the formula to the plain
    // Ellipsoid shape's own field, so SdfProgramBuilder.Superellipsoid emits SHAPE 6 (Ellipsoid) directly at e == 2
    // rather than this id — a program never carries this shape at the ellipsoid limit.
    Superellipsoid = 18,
    // ConvexPolygon = 19: a member of the 2D-primitive family (revolve/extrude lift, family-wide smooth/lift-mode/
    // cap-chamfer/edge-rounding lanes), but its 2D profile is a validated convex vertex list too large to fit inline
    // — the vertices live in a side table appended to the packed program's own word stream (the SAME sdfWords buffer
    // every other table already lives in, so no new GPU binding is needed), and the shape instruction carries only a
    // packed (tableOffset, vertexCount) reference. Data0.x = asfloat(packed uint: (tableOffset << 4) | vertexCount),
    // Data0.w = lift amount; Data1 = (smooth [ISA-wide], lift mode, cap chamfer, edge-rounding radius) — the same
    // family lanes RoundedRectangle carries. The table itself is <c>ceil(vertexCount/2)</c> uvec4 entries, two
    // packed (x, y) float-bit vertices each, vertices in the shape's local XY plane, clockwise. KEEP IN SYNC with
    // SDF_SHAPE_CONVEX_POLYGON / sdfConvexPolygon2D / sdfPolygonVertex.
    ConvexPolygon = 19,
}
