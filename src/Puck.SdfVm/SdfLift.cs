namespace Puck.SdfVm;

/// <summary>How a 2D-primitive family shape (<see cref="SdfShapeType.RoundedRectangle"/>,
/// <see cref="SdfShapeType.RegularPolygon"/>, <see cref="SdfShapeType.Star"/>, <see cref="SdfShapeType.Trapezoid"/>,
/// <see cref="SdfShapeType.Ellipse"/>) is lifted from its exact 2D signed-distance field into a 3D solid. Values MUST
/// match the <c>SDF_LIFT_*</c> lift-mode lane in Assets/Shaders/Sdf/sdf-vm.hlsli (packed into the shape instruction's
/// Data1.y and decoded as <c>&gt; 0.5</c>).</summary>
public enum SdfLift : uint {
    /// <summary>Revolve the 2D profile around the local Y axis (the revolve lift operator): the profile sits in the (radial, Y)
    /// half-plane at a radial offset (the shape's <c>liftAmount</c>). Exact when the offset clears the profile's radial
    /// extent (a torus-like solid); a harmless conservative underestimate near the axis when the profile crosses it
    /// (offset 0 = a solid of revolution centred on the axis, e.g. a spheroid from an ellipse). Always 1-Lipschitz.</summary>
    Revolve = 0,
    /// <summary>Extrude the 2D profile along the local Z axis (the extrude lift operator) to the given half-height
    /// (the shape's <c>liftAmount</c>): a prism/slab whose cross-section is the exact 2D shape. Exact for any exact 2D
    /// field, and 1-Lipschitz.</summary>
    Extrude = 1,
}
