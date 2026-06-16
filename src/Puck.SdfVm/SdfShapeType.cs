namespace Puck.SdfVm;

// Values must match Shaders/sdf-vm.glsl (SDF_SHAPE_*); the Data0 packing is decoded by each shader case.
public enum SdfShapeType : uint {
    Box = 0, // Data0 = (halfX, halfY, halfZ, roundingRadius)
    Sphere = 2, // Data0.x = radius
    Torus = 3, // Data0 = (majorRadius, minorRadius, _, _)
    Plane = 5, // Data0 = (normalX, normalY, normalZ, offset)
    RoundCone = 11, // Data0 = (lowerRadius, upperRadius, height, _)
    ScreenSlab = 14, // Data0 = (halfX, halfY, halfZ, roundingRadius)
}
