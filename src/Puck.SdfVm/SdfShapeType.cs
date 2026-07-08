namespace Puck.SdfVm;

// Values must match Assets/Shaders/Sdf/sdf-vm.hlsli (SDF_SHAPE_*); the Data0 packing is decoded by each shader case.
public enum SdfShapeType : uint {
    Box = 0, // Data0 = (halfX, halfY, halfZ, roundingRadius)
    Capsule = 1, // Data0 = (endX, endY, endZ, radius); the segment runs from the local origin to the endpoint
    Sphere = 2, // Data0.x = radius
    Torus = 3, // Data0 = (majorRadius, minorRadius, _, _)
    Cylinder = 4, // Data0 = (radius, halfHeight, _, _); upright, centered on the local origin
    Plane = 5, // Data0 = (normalX, normalY, normalZ, offset)
    Ellipsoid = 6, // Data0 = (radiusX, radiusY, radiusZ, _)
    Vesica = 7, // Data0 = (radius, halfSeparation, halfHeight[baked √(r²−d²)], _); iq's 2D vesica revolved to a lens (d < r)
    // --- The 2D-primitive family (an exact IQ 2D SDF lifted to 3D by revolve/extrude). SHARED lane layout for all of
    // them: Data0.xyz = the 2D shape params, Data0.w = the lift amount (revolve offset o OR extrude half-height h),
    // Data1.x = smooth radius, Data1.y = the lift MODE (0 = revolve around Y, 1 = extrude along Z), Data1.zw = per-shape
    // host-baked constants. Each is exact + 1-Lipschitz (no step clamp): extrusion is always exact; revolution is exact
    // when the profile clears the axis (offset ≥ its radial extent) and a harmless conservative bound near the axis.
    RoundedRectangle = 8, // Data0 = (halfX, halfY, cornerRadius, lift); iq sdRoundedBox
    RegularPolygon = 9,   // Data0 = (circumRadius, π/n[baked], 0[ecs.x], lift), Data1.z = 1[ecs.y]; iq sdStar with m=2
    Star = 10,            // Data0 = (outerRadius, π/n[baked], cos(π/m)[baked ecs.x], lift), Data1.z = sin(π/m)[baked ecs.y]; iq sdStar
    RoundCone = 11, // Data0 = (lowerRadius, upperRadius, height, _)
    Trapezoid = 12,       // Data0 = (bottomHalfWidth r1, topHalfWidth r2, halfHeight, lift); iq sdTrapezoid
    Ellipse = 13,         // Data0 = (semiX, semiY, _, lift); iq sdEllipse (revolve→spheroid, extrude→elliptic prism)
    ScreenSlab = 14, // Data0 = (halfX, halfY, halfZ, roundingRadius)
}
