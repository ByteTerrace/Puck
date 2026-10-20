using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>An authored surface tint and reflectance.</summary>
public sealed record PaletteSurfaceDocument(string Color, float Roughness, float Metal) {
    /// <summary>Resolves literal colors or containing-world color bindings.</summary>
    public SdfSurface ToSurface(Func<string, Vector3> resolve) => new(
        resolve(Color),
        Roughness,
        Metal
    );
}
/// <summary>One ascending coverage threshold and the surface it reveals.</summary>
public sealed record PaletteRevealDocument(float Threshold, PaletteSurfaceDocument Surface);
/// <summary>One ascending radial ramp stop.</summary>
public sealed record PaletteRadialStopDocument(float Radius, string Color);
/// <summary>One to four radial paint stops with optional angular modulation.</summary>
public sealed record PaletteRadialPaintDocument(IReadOnlyList<PaletteRadialStopDocument> Stops, float Softness = 0f,
    float ModulationAmplitude = 0f, float ModulationFrequency = 0f, uint Seed = 0);
/// <summary>A paint plane in the winning dynamic frame, or world space when no slot owns the hit.</summary>
public sealed record PaletteInsetDocument(DocumentVector3 Origin, DocumentQuaternion Rotation, float Depth, float Ior, PaletteRadialPaintDocument Paint) {
    /// <summary>Resolves the generic engine layer at a placement scale.</summary>
    /// <param name="resolve">The color resolver.</param>
    /// <param name="scale">The placement scale the layer's lengths (its origin, depth and stop radii) multiply by;
    /// positive.</param>
    /// <returns>The inset.</returns>
    public SdfInset ToInset(Func<string, Vector3> resolve, float scale = 1f) => new(
        (new Vector3(
            x: Origin.X,
            y: Origin.Y,
            z: Origin.Z
        ) * scale),
        new(
            Rotation.X,
            Rotation.Y,
            Rotation.Z,
            Rotation.W
        ),
        (Depth * scale),
        Ior,
        new(
            Paint.Stops.Select(selector: s => new SdfRadialStop(
                (s.Radius * scale),
                resolve(s.Color)
            )).ToArray(),
            Paint.Softness,
            Paint.ModulationAmplitude,
            Paint.ModulationFrequency,
            Paint.Seed
        )
    );
}
/// <summary>A paint over the palette slot's surface: a primitive of the shape vocabulary, placed like a shape, whose
/// inside (or, with a <paramref name="Stroke"/>, the band within that distance of its surface) takes
/// <paramref name="Color"/>. It never changes the field or the bake; the views pass draws it crisp at the pixel. A
/// plane stroked at a half-width paints a stripe, an extruded ellipse prism stroked paints a ring projected along its
/// axis, and a thin sweep paints a curved line where it crosses the surface.</summary>
/// <param name="Type">The primitive, read as a shape's <c>type</c>: every primitive but a path profile.</param>
/// <param name="Color">The paint's color, #RRGGBB or a state binding.</param>
/// <param name="Position">The primitive's position (null = the frame's origin).</param>
/// <param name="Rotation">The primitive's rotation (null = identity).</param>
/// <param name="Scale">The primitive's size, as a shape's <c>scale</c> (null = one).</param>
/// <param name="Stroke">The half-width of the band painted about the primitive's surface, in the frame's units; null
/// or zero fills the primitive.</param>
/// <param name="Parent">The shape whose frame carries the paint, in that shape's local frame as a shape-riding volume
/// is; null paints in the frame of the slot that owns the hit, or world space on a static placement. A static
/// placement draws no parented paint.</param>
/// <param name="Taper">Prism only: the top-to-bottom width ratio.</param>
/// <param name="Profile">Prism only: the cross-section; a path profile is refused.</param>
/// <param name="Lift">Prism only: extrude or revolve.</param>
/// <param name="Rounding">The edge-rounding radius, as a shape's.</param>
/// <param name="Chamfer">The cap chamfer radius, as a shape's.</param>
/// <param name="Exponent">Superellipsoid only: the exponent.</param>
/// <param name="Curve">Sweep only: the curve, in the frame's units.</param>
public sealed record PalettePaintDocument(
    SdfSolidPrimitive Type,
    string Color,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Position = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentQuaternion? Rotation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Scale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Stroke = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Parent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Taper = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfPrismProfile? Profile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfLift? Lift = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Rounding = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Chamfer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Exponent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ShapeCurveDocument? Curve = null
) {
    /// <summary>Resolves the engine paint at a placement scale.</summary>
    /// <param name="resolve">The color resolver.</param>
    /// <param name="scale">The placement scale the paint's lengths multiply by; positive.</param>
    /// <param name="slot">The dynamic transform slot of <see cref="Parent"/>, or <see cref="SdfPaint.WinningFrame"/>.</param>
    /// <returns>The paint.</returns>
    /// <exception cref="ArgumentException">The primitive emits something a paint cannot carry.</exception>
    public SdfPaint ToPaint(Func<string, Vector3> resolve, float scale = 1f, int slot = SdfPaint.WinningFrame) => new(
        Color: resolve(Color),
        Origin: ((Position?.Value ?? Vector3.Zero) * scale),
        Rotation: (Rotation?.Value ?? Quaternion.Identity),
        Shape: SdfPaintShape.Of(
            chamfer: ((Chamfer ?? 0f) * scale),
            curve: Curve?.Parameters(),
            exponent: (Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
            lift: (Lift ?? SdfLift.Extrude),
            profile: Profile,
            rounding: ((Rounding ?? 0f) * scale),
            scale: ((Scale?.Value ?? Vector3.One) * scale),
            taper: (Taper ?? 0.5f),
            type: Type
        ),
        Slot: slot,
        Stroke: ((Stroke ?? 0f) * scale)
    );
}
/// <summary>Generic weathering coverage and explicitly authored reveal/deposit surfaces.</summary>
public sealed record PaletteWeatheringDocument(float Edge = 0f, float Lines = 0f, float Settle = 0f, float Reach = 1f,
    uint Seed = 0, float Scale = 1f, float Floor = 0f, int Lane = 0,
    IReadOnlyList<PaletteRevealDocument>? Under = null, PaletteSurfaceDocument? Deposit = null) {
    /// <summary>Resolves the generic engine response at a placement scale.</summary>
    /// <param name="resolve">The color resolver.</param>
    /// <param name="scale">The placement scale: the curvature reach, a length, multiplies by it, and the pattern
    /// frequency divides by it, so the pattern keeps its place and size on the scaled surface; positive.</param>
    /// <returns>The weathering.</returns>
    public SdfWeathering ToWeathering(Func<string, Vector3> resolve, float scale = 1f) => new(
        Edge,
        Lines,
        Settle,
        (Reach * scale),
        Seed,
        (Scale / scale),
        Floor,
        Lane,
        Under?.Select(selector: s => new SdfRevealStage(
            s.Threshold,
            s.Surface.ToSurface(resolve: resolve)
        )).ToArray(),
        Deposit?.ToSurface(resolve: resolve)
    );
}
