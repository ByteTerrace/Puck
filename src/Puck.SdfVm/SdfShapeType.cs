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
    RoundCone = 11, // Data0 = (lowerRadius, upperRadius, height, _)
    ScreenSlab = 14, // Data0 = (halfX, halfY, halfZ, roundingRadius)
}
