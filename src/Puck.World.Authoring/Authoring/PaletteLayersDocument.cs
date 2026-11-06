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
/// <summary>A palette slot's glass: the slot's shapes transmit what lies behind them, refracted and absorbed over the
/// distance the view ray travels through the shape, and reflect their surroundings by Fresnel. The slot's <c>color</c>
/// is the tint, the colour light takes on through <paramref name="Depth"/> of the glass, and its <c>roughness</c> the
/// frost. A glass slot's shapes bake and draw apart from the creation's opaque shapes: they never blend with them, a cut
/// or engraving never carves them, and a pooled creation keeps them out of any <c>group</c>.</summary>
/// <param name="Depth">The thickness, in creation units, over which light takes on the tint; finite and positive. A
/// part twice this thick reads twice as deep.</param>
/// <param name="Ior">The index of refraction, in [1, 4]: 1 bends and reflects nothing, 1.5 is window glass, 1.49
/// acrylic (null = 1.5).</param>
/// <param name="Scatter">The colour the glass scatters as a medium, the colour a deep body of it reads as under the
/// light arriving at its surface (null = none).</param>
/// <param name="Scattering">How much the medium scatters, per creation unit, finite and non-negative; it also dims what
/// lies behind (null = 0, no scattering).</param>
/// <param name="Contact">Foam or a wet margin where an opaque surface behind the glass comes within reach of its
/// surface, revealed through the slot's flow coverage (null = none).</param>
public sealed record PaletteGlassDocument(
    float Depth,
    float? Ior = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scatter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Scattering = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PaletteGlassContactDocument? Contact = null
) {
    /// <summary>The index of refraction an entry that names none takes: window glass.</summary>
    public const float DefaultIor = 1.5f;

    /// <summary>Resolves the engine glass at a placement scale.</summary>
    /// <param name="resolve">The color resolver.</param>
    /// <param name="scale">The placement scale: the depth and the contact's reach, lengths, multiply by it, and the
    /// scattering, a coefficient per unit, divides by it; positive.</param>
    /// <returns>The glass.</returns>
    public SdfGlass ToGlass(Func<string, Vector3> resolve, float scale = 1f) => new(
        Depth: (Depth * scale),
        Ior: (Ior ?? DefaultIor),
        Scatter: ((Scatter is { } scatter) ? resolve(scatter) : Vector3.Zero),
        Scattering: ((Scattering ?? 0f) / scale),
        ContactReach: ((Contact?.Reach ?? 0f) * scale),
        ContactCoverage: (Contact?.Coverage ?? 0f)
    );
}
/// <summary>A glass slot's contact: the slot's flow coverage rises toward <paramref name="Coverage"/> where the opaque
/// surface behind the glass lies within <paramref name="Reach"/> of the glass's surface along the view, so foam
/// gathers at a step, a pier or a bank in the flow's own pattern. The slot's flow must name the surface its coverage
/// reveals.</summary>
/// <param name="Reach">The distance, in creation units along the view from the glass surface to the opaque surface
/// behind it, over which the added coverage falls to zero; positive.</param>
/// <param name="Coverage">The coverage added where the opaque surface touches the glass's, in [0, 1].</param>
public sealed record PaletteGlassContactDocument(float Reach, float Coverage);
/// <summary>A palette slot's flow layer: the slot's shading moves along <paramref name="Velocity"/> while its geometry
/// stays still. A seeded noise pattern advects with the flow in two layers half a cycle apart, so neither restart shows,
/// tilting the shading normal by <paramref name="Tilt"/> times its slope and revealing <paramref name="Surface"/> over
/// <paramref name="Coverage"/> of the slot (streaks, foam). The velocity is authored in the shader's frame, as the other
/// palette layers are: for a creation, its engine frame (the author frame's x and z negated), turned with each placement;
/// for a field row, world space; <paramref name="Frame"/> <c>World</c> keeps it and the pattern in world space on any surface.</summary>
/// <param name="Velocity">The flow, in units per second.</param>
/// <param name="Scale">The pattern's frequency, in cells per unit; positive (null = 1).</param>
/// <param name="Stretch">How many times longer a pattern feature is along the flow than across; at least 1
/// (null = 1).</param>
/// <param name="Cycle">The seconds a layer advects before it restarts, in [0.25, 4096] (null = 1).</param>
/// <param name="Tilt">The normal tilt per unit slope of the pattern; non-negative (null = 0).</param>
/// <param name="Coverage">The share of the slot the pattern reveals <paramref name="Surface"/> over, in [0, 1]
/// (null = 0).</param>
/// <param name="Softness">The coverage edge's width, in [0, 1] (null = 0.1).</param>
/// <param name="Surface">The surface the coverage reveals; required when <paramref name="Coverage"/> is positive.</param>
/// <param name="Seed">The pattern's seed (null = 0).</param>
/// <param name="Lane">The render lane whose value scales the velocity: 0 to 3 an inhabited body's look lanes (a
/// placement without them reads zero and stands still), 4 to 7 the world's <c>render.lanes</c> (<see cref="SdfLanes"/>).
/// Null keeps the velocity constant.</param>
/// <param name="Frame">The frame the velocity is authored in (null = <c>Object</c>).</param>
/// <param name="CoverageLane">The render lane, as <paramref name="Lane"/>, whose value saturated to [0, 1] scales
/// <paramref name="Coverage"/>. Null keeps the coverage constant.</param>
public sealed record PaletteFlowDocument(
    DocumentVector3 Velocity,
    float? Scale = null,
    float? Stretch = null,
    float? Cycle = null,
    float? Tilt = null,
    float? Coverage = null,
    float? Softness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PaletteSurfaceDocument? Surface = null,
    uint? Seed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Lane = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfFlowFrame? Frame = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CoverageLane = null
) {
    /// <summary>Resolves the engine flow at a placement scale.</summary>
    /// <param name="resolve">The color resolver.</param>
    /// <param name="scale">The placement scale: the velocity, a length per second, multiplies by it, and the pattern
    /// frequency divides by it, so the pattern keeps its size on the scaled surface; positive.</param>
    /// <returns>The flow.</returns>
    public SdfFlow ToFlow(Func<string, Vector3> resolve, float scale = 1f) => new(
        Velocity: (Velocity.Value * scale),
        Scale: ((Scale ?? 1f) / scale),
        Stretch: (Stretch ?? 1f),
        Cycle: (Cycle ?? 1f),
        Tilt: (Tilt ?? 0f),
        Coverage: (Coverage ?? 0f),
        Softness: (Softness ?? 0.1f),
        Surface: Surface?.ToSurface(resolve: resolve),
        Seed: (Seed ?? 0u),
        Lane: Lane,
        Frame: (Frame ?? SdfFlowFrame.Object),
        CoverageLane: CoverageLane
    );
}
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
/// <summary>Generic weathering coverage and explicitly authored reveal/deposit surfaces. The pattern's character is
/// authored here too: <c>octaves</c> (1 or 2) sets how many lattice octaves the mask sums, <c>octaveRatio</c> the
/// second octave's frequency multiple, <c>octaveGain</c> its weight relative to the first (the sum is normalised, so
/// the mask keeps one octave's spread), and <c>turn</c> a rotation of the lattice off the pattern axes. A weathering
/// that says none of them reads one unturned octave.</summary>
public sealed record PaletteWeatheringDocument(float Edge = 0f, float Lines = 0f, float Settle = 0f, float Reach = 1f,
    uint Seed = 0, float Scale = 1f, float Floor = 0f, int Lane = 0,
    IReadOnlyList<PaletteRevealDocument>? Under = null, PaletteSurfaceDocument? Deposit = null, float Noise = 0f, DocumentVector3? PatternScale = null, float Softness = 0f,
    int Octaves = 1, float OctaveRatio = 2f, float OctaveGain = 0.5f, DocumentQuaternion? Turn = null) {
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
        Deposit?.ToSurface(resolve: resolve),
        Noise,
        PatternScale?.Value,
        Softness,
        Octaves,
        OctaveRatio,
        OctaveGain,
        Turn?.Value
    );
}
