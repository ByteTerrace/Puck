namespace Puck.SignedDistance;

internal enum SdfPointAction : byte {
    ResetPoint,
    Translate,
    Rotate,
    Scale,
    Fold,
    Warp,
    RenderWarp,
    LaneErode,
    EditField,
    WeightedRelief,
    Identity,
    RenderEditField,
    PushField,
    PopField,
    ShapeBlend,
    Unsupported,
}

internal enum SdfIntervalAction : byte {
    ResetPoint,
    Translate,
    Rotate,
    Scale,
    Fold,
    Warp,
    RenderWarp,
    LaneErode,
    EditField,
    RenderEditField,
    WeightedRelief,
    Identity,
    PushField,
    PopField,
    ShapeBlend,
    Unsupported,
}

internal enum SdfPointFoldAction {
    Repeat,
    RepeatLimited,
    SymmetryPlane,
    Elongate,
    Unsupported,
}

internal enum SdfIntervalFoldAction {
    Repeat,
    RepeatLimited,
    SymmetryPlane,
    Elongate,
    Unsupported,
}

internal enum SdfPointFieldEditAction {
    Onion,
    CellDisplace,
    Dilate,
    Unsupported,
}

internal enum SdfIntervalFieldEditAction {
    Onion,
    CellDisplace,
    Dilate,
    Unsupported,
}

internal enum SdfRenderWarpAction {
    Shear,
    GaussianPush,
    DomainWarp,
    RepeatPolar,
    WallpaperFold,
    LogSphere,
    Unsupported,
}

internal enum SdfRenderWarpBoundsAction {
    Shear,
    GaussianPush,
    DomainWarp,
    RepeatPolar,
    WallpaperFold,
    LogSphere,
    Unbounded,
}

internal enum SdfFieldStructureAction {
    ShapeBlend,
    PushField,
    PopField,
    Other,
}

internal enum SdfDeferredScopeAction {
    PopField,
    ShapeBlend,
    DisruptsScope,
    Ignore,
}

internal enum SdfCullChainAction {
    Allowed,
    PushField,
    PopField,
    ShapeBlend,
    Relief,
    Disable,
}

internal enum SdfCullInstancesAction {
    PushField,
    PopField,
    ShapeBlend,
    EditsField,
    Ignore,
}

internal enum SdfCullBoundAction {
    Union,
    SmoothUnion,
    Local,
    LocalWithRadius,
    Disable,
}

internal enum SdfCullGuardAction {
    Union,
    SmoothUnion,
    Subtraction,
    Disable,
}

internal enum SdfCertificateChainAction {
    Identity,
    Translate,
    Rotate,
    Scale,
    ShapeBlend,
    Unknown,
}

internal enum SdfCertificateAction {
    Sphere,
    Box,
    Cylinder,
    Torus,
    Capsule,
    Superellipsoid,
    Trapezoid,
    Unavailable,
}

internal enum SdfShapePointAction : byte {
    Sphere,
    Box,
    Torus,
    Plane,
    RoundCone,
    Capsule,
    Cylinder,
    Vesica,
    Ellipse,
    RoundedRectangle,
    Trapezoid,
    ChamferedRectangle,
    Superellipsoid,
    ConvexPolygon,
    Sweep,
    Path,
    Unsupported,
}

internal enum SdfShapeIntervalAction : byte {
    Sphere,
    Box,
    Torus,
    Plane,
    RoundCone,
    Capsule,
    Cylinder,
    Vesica,
    LiftedWithCap,
    Trapezoid,
    ChamferedRectangle,
    Superellipsoid,
    Sweep,
    Path,
    ConvexPolygon,
    Entire,
}

internal enum SdfProfileIntervalAction {
    RoundedRectangle,
    Ellipse,
    Trapezoid,
    ChamferedRectangle,
    ConvexPolygon,
    Unsupported,
}

internal enum SdfAdmissionAction {
    Zero,
    Translate,
    Rotate,
    Scale,
    TransformDynamic,
    RotatePlane,
    Elongate,
    ShapeBlend,
    Repeat,
    RepeatLimited,
    Onion,
    Dilate,
    WallpaperFold,
    LogSphere,
    CellJitter,
    RepeatPolar,
    Displacement,
    SymmetryPlane,
    PopField,
    NoiseDisplace,
    AxialProfile,
    Shear,
    GaussianPush,
    LaneErode,
    CellDisplace,
    ReliefRow,
    Unknown,
}

internal enum SdfPopAdmissionAction {
    Morph,
    Stairs,
    Radius,
}

internal enum SdfReliefRowAdmissionAction {
    Band,
    Pose,
    Primitive,
    Unknown,
}

internal enum SdfShapeAdmissionAction {
    Box,
    Capsule,
    Sphere,
    Torus,
    Cylinder,
    Plane,
    Vesica,
    Rectangle,
    RegularPolygon,
    Star,
    RoundCone,
    Trapezoid,
    Ellipse,
    Glyph,
    SampledRegion,
    Superellipsoid,
    ConvexPolygon,
    Sweep,
    Path,
    Unknown,
}

internal enum SdfReliefPointFactorAction {
    ResetPoint,
    Scale,
    DomainWarp,
    GaussianPush,
    Identity,
    NoGlobalBound,
    SampleRelief,
    Unsupported,
}

internal enum SdfStepScaleBinderAction {
    PushField,
    PopField,
    ShapeBlend,
    Relief,
    Ignore,
}

internal enum SdfCompositionLipschitzAction {
    ShapeBlend,
    PushField,
    Displace,
    NoiseDisplace,
    CellDisplace,
    PopField,
    Ignore,
}

internal enum SdfChainLipschitzAction {
    Scale,
    Translate,
    RotatePlane,
    ShapeBlend,
    LogSphere,
    CellJitter,
    DomainWarp,
    LaneErode,
    AxialProfile,
    Shear,
    GaussianPush,
    Ignore,
}

internal enum SdfSkipSphereAction {
    ResetPoint,
    Translate,
    Rotate,
    Scale,
    TransformDynamic,
    ShapeBlend,
    ScopeBoundary,
    Ignore,
    Disable,
}

internal enum SdfDistanceGainAction {
    ResetPoint,
    Identity,
    Scale,
    PushField,
    ShapeBlend,
    PopField,
    Uncertified,
}

internal enum SdfBlendDistanceGainAction {
    Union,
    SmoothUnion,
    Subject,
    Smaller,
    Uncertified,
}

internal enum SdfShapeDistanceGainAction {
    Superellipsoid,
    Uncertified,
    FromLocalBound,
}

internal enum SdfRigidPlanAction {
    ResetPoint,
    Fold,
    Translate,
    Rotate,
    TransformDynamic,
    IdentityScale,
    ShapeBlend,
    Refuse,
}

internal enum SdfScopeClampAction {
    PushField,
    ShapeBlend,
    PopField,
    Ignore,
}

internal enum SdfWarpBoundAction {
    GaussianPush,
    Shear,
    AxialProfile,
    Ignore,
}

internal enum SdfLocalBoundAction {
    Sweep,
    Box,
    Capsule,
    Sphere,
    Torus,
    Cylinder,
    Vesica,
    RoundedRectangle,
    ChamferedRectangle,
    RegularPolygon,
    Trapezoid,
    Ellipse,
    RoundCone,
    SampledRegion,
    Path,
    ConvexPolygon,
    Unbounded,
}

internal enum SdfScopeReachAction {
    Data0X,
    Data0W,
    Data0Y,
    HalfData0Y,
    None,
}

internal enum SdfUnboundedFoldAction {
    Unbounded,
    RepeatLimit,
    WallpaperLimit,
    Bounded,
}

internal enum SdfPointBlendAction {
    SmoothUnion,
    Subtraction,
    Intersection,
    Xor,
    SmoothIntersection,
    SmoothSubtraction,
    GrooveUnion,
    PipeUnion,
    GrooveSubtraction,
    PipeSubtraction,
    ChamferUnion,
    ChamferIntersection,
    ChamferSubtraction,
    Union,
}

internal enum SdfMaterialWinnerAction {
    Farther,
    Cut,
    Nearer,
}

internal enum SdfIntervalBlendAction {
    SmoothUnion,
    Subtraction,
    Intersection,
    Xor,
    SmoothIntersection,
    SmoothSubtraction,
    GrooveUnion,
    PipeUnion,
    GrooveSubtraction,
    PipeSubtraction,
    ChamferUnion,
    ChamferIntersection,
    ChamferSubtraction,
    Union,
}

internal enum SdfBlendGrowthAction {
    None,
    QuarterRadiusFloor,
    HalfRadius,
    Radius,
}

internal enum SdfBlendLipschitzAction { Maximum, RoundSeam, Chamfer }
internal enum SdfBlendHaloAction { None, Radius, Pipe, ChamferUnion }
