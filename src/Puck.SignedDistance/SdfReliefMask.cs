using System.Globalization;
using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The kind of a relief mask's data row (<see cref="SdfOp.ReliefRow"/>), which its Blend lane carries.</summary>
public enum SdfReliefRowKind {
    /// <summary>A mask's band: Data0 holds its start, width, inside weight and outside weight.</summary>
    Band = 0,
    /// <summary>A region's pose in the relief's frame: Data0.xyz holds its origin and Data1 its unit quaternion.</summary>
    Pose = 1,
    /// <summary>A region's primitive: its Shape lane and data vectors as a shape instruction packs them, Data1.x zero.</summary>
    Primitive = 2,
}
/// <summary>
/// A relief mask's band: the weight is <see cref="Inside"/> where the region's distance is at or below
/// <see cref="Start"/>, <see cref="Outside"/> where it is at or beyond <see cref="End"/>, and
/// <c>inside + (outside − inside)·t²·(3 − 2t)</c> between, with <c>t = (m − start) / width</c>. Every value lies on a grid
/// both carriers hold exactly: the start, the width and the end are multiples of 2⁻¹² units within 2¹² units of zero,
/// which single-precision float and Q48.16 both represent without rounding, and the weights are multiples of 2⁻¹⁶ in
/// [0, 1]. So the kernel and the fixed-point evaluator compare a region's distance with the same three numbers, and a
/// band that exists in one exists in the other. Made only by <see cref="Of"/> or <see cref="TryOf"/>, which snap an
/// author's values to the grids, and read back from a row only when the row already holds them exactly.
/// </summary>
public readonly record struct SdfReliefBand {
    /// <summary>The grid steps per unit of a band's start, width and end.</summary>
    public const int StepsPerUnit = 4096;
    /// <summary>The grid steps per unit of a band's weights.</summary>
    public const int WeightStepsPerUnit = 65536;
    /// <summary>The largest magnitude, in units, a band's start, width or end may have.</summary>
    public const float Limit = 4096f;

    private const int LimitSteps = (1 << 24);

    private SdfReliefBand(int startSteps, int widthSteps, int insideSteps, int outsideSteps) {
        StartSteps = startSteps;
        WidthSteps = widthSteps;
        InsideSteps = insideSteps;
        OutsideSteps = outsideSteps;
    }

    /// <summary>Gets the band's start, in 2⁻¹² unit steps.</summary>
    public int StartSteps { get; }
    /// <summary>Gets the band's width, in 2⁻¹² unit steps; at least one.</summary>
    public int WidthSteps { get; }
    /// <summary>Gets the weight on and inside the region, in 2⁻¹⁶ steps.</summary>
    public int InsideSteps { get; }
    /// <summary>Gets the weight past the band, in 2⁻¹⁶ steps.</summary>
    public int OutsideSteps { get; }
    /// <summary>Gets the band's start, as a distance in units from the region's surface; negative inside it.</summary>
    public float Start => (StartSteps / ((float)StepsPerUnit));
    /// <summary>Gets the band's width, in units.</summary>
    public float Width => (WidthSteps / ((float)StepsPerUnit));
    /// <summary>Gets the band's end, <see cref="Start"/> plus <see cref="Width"/>, which both carriers add exactly.</summary>
    public float End => ((StartSteps + WidthSteps) / ((float)StepsPerUnit));
    /// <summary>Gets the weight on and inside the region, in [0, 1].</summary>
    public float Inside => (InsideSteps / ((float)WeightStepsPerUnit));
    /// <summary>Gets the weight past the band, in [0, 1].</summary>
    public float Outside => (OutsideSteps / ((float)WeightStepsPerUnit));
    /// <summary>Gets whether the band's two weights are equal, which makes its mask the identity everywhere.</summary>
    public bool IsIdentity => (InsideSteps == OutsideSteps);
    /// <summary>Gets the most the weight changes per unit of the region's distance: smoothstep's largest slope, 1.5 per
    /// width, times the weights' span.</summary>
    public float Slope => ((1.5f * MathF.Abs(x: (Outside - Inside))) / Width);

    /// <summary>Returns the band an author's values describe, snapped to the band's grids.</summary>
    /// <param name="start">The start, as a distance in units from the region's surface.</param>
    /// <param name="width">The width, in units.</param>
    /// <param name="inside">The weight on and inside the region, in [0, 1].</param>
    /// <param name="outside">The weight past the band, in [0, 1].</param>
    /// <returns>The band.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, the width snaps below one step, the start, the
    /// width or the end snaps past <see cref="Limit"/>, or a weight lies outside [0, 1].</exception>
    public static SdfReliefBand Of(float start, float width, float inside = 0f, float outside = 1f) =>
        ((TryOf(band: out var band, inside: inside, outside: outside, start: start, width: width) is { } refusal)
            ? throw new ArgumentOutOfRangeException(message: refusal, paramName: nameof(start))
            : band);
    /// <summary>Snaps an author's values to the band's grids, or names the value that cannot be.</summary>
    /// <param name="start">The start, as a distance in units from the region's surface.</param>
    /// <param name="width">The width, in units.</param>
    /// <param name="inside">The weight on and inside the region, in [0, 1].</param>
    /// <param name="outside">The weight past the band, in [0, 1].</param>
    /// <param name="band">The band, when the values describe one.</param>
    /// <returns><see langword="null"/> for a band, or the refusal naming the value that describes none.</returns>
    public static string? TryOf(float start, float width, float inside, float outside, out SdfReliefBand band) {
        band = default;

        if (Steps(units: start, value: out var startSteps) is false) {
            return $"a relief band's start must be finite and within {Limit} units; got {Describe(value: start)}.";
        }
        if ((Steps(units: width, value: out var widthSteps) is false) || (widthSteps < 1)) {
            return $"a relief band's width must be finite, at least {Describe(value: (1f / StepsPerUnit))} and within {Limit} units; got {Describe(value: width)}.";
        }
        if (Math.Abs(value: (((long)startSteps) + widthSteps)) > LimitSteps) {
            return $"a relief band's end (start + width) must lie within {Limit} units; got {Describe(value: (start + width))}.";
        }
        if (WeightSteps(weight: inside) is not { } insideSteps) {
            return $"a relief band's inside weight must lie in [0, 1]; got {Describe(value: inside)}.";
        }
        if (WeightSteps(weight: outside) is not { } outsideSteps) {
            return $"a relief band's outside weight must lie in [0, 1]; got {Describe(value: outside)}.";
        }

        band = new SdfReliefBand(insideSteps: insideSteps, outsideSteps: outsideSteps, startSteps: startSteps, widthSteps: widthSteps);

        return null;
    }
    /// <summary>Reads the band a row holds, or names why it holds none: a row's values must already lie on the band's
    /// grids, so the words the kernel reads are the values the band states.</summary>
    /// <param name="row">The band row (<see cref="SdfReliefRowKind.Band"/>).</param>
    /// <param name="band">The band, when the row holds one.</param>
    /// <returns><see langword="null"/> for a band, or the refusal.</returns>
    public static string? TryRead(SdfInstruction row, out SdfReliefBand band) {
        var (start, width, inside, outside) = (row.Data0.X, row.Data0.Y, row.Data0.Z, row.Data0.W);

        if (TryOf(band: out band, inside: inside, outside: outside, start: start, width: width) is { } refusal) {
            return refusal;
        }
        if ((band.Start != start) || (band.Width != width) || (band.Inside != inside) || (band.Outside != outside)) {
            return $"a relief band's start and width must be multiples of 2^-12 and its weights multiples of 2^-16, the grids both carriers hold exactly; got ({Describe(value: start)}, {Describe(value: width)}, {Describe(value: inside)}, {Describe(value: outside)}).";
        }

        return null;
    }
    /// <summary>Returns the row this band packs as (<see cref="SdfReliefRowKind.Band"/>).</summary>
    /// <returns>The row.</returns>
    public SdfInstruction ToRow() => new(
        Blend: ((uint)SdfReliefRowKind.Band),
        Data0: new Vector4(x: Start, y: Width, z: Inside, w: Outside),
        Data1: Vector4.Zero,
        Material: 0u,
        Op: SdfOp.ReliefRow,
        Shape: 0u
    );

    // A value in units on the 2⁻¹² grid, rounding to nearest with ties to even: the scaling by a power of two is exact,
    // and a magnitude past the limit is refused before rounding can overflow.
    private static bool Steps(float units, out int value) {
        value = 0;

        if (!(float.IsFinite(f: units) && (MathF.Abs(x: units) <= (Limit + (0.5f / StepsPerUnit))))) {
            return false;
        }

        value = ((int)MathF.Round(x: (units * StepsPerUnit)));

        return (Math.Abs(value: value) <= LimitSteps);
    }
    private static int? WeightSteps(float weight) =>
        ((float.IsFinite(f: weight) && (weight >= 0f) && (weight <= 1f)) ? ((int)MathF.Round(x: (weight * WeightStepsPerUnit))) : null);
    private static string Describe(float value) => value.ToString(provider: CultureInfo.InvariantCulture);
}
/// <summary>
/// One region shape of a relief mask: a listed primitive (<see cref="IsListed"/>) at a rigid pose in the relief's frame.
/// Its distance at the relief's point <c>q</c> is <c>shape(turn_back(q − origin))</c>, the primitive evaluated as a shape
/// instruction evaluates it. A listed primitive packs natively at any scale, is 1-Lipschitz and is read by the
/// fixed-point evaluator, so a region is continuous, its slope is at most <see cref="MaxStretch"/>, and contact reads
/// the region the bake draws. Made by <see cref="Lower"/> from what an author writes, or read from a program's rows by
/// its door.
/// </summary>
public sealed record SdfReliefRegion {
    /// <summary>The largest magnitude a region's origin or any primitive lane may have, in units: well inside the range
    /// Q48.16 holds, so the fixed-point evaluator never saturates a value the kernel reads verbatim.</summary>
    public const float MaxMagnitude = (1 << 20);
    /// <summary>The most a region's distance changes per unit of the relief's point: one for an exact rotation and an
    /// exact plane normal, raised by the unit-length tolerance a pose quaternion and a plane normal are admitted at
    /// (<see cref="SdfProgram.UnitLengthTolerance"/>), which stretch a turned point by up to twice it and a plane's
    /// distance by half it.</summary>
    public const float MaxStretch = (1f + (4f * SdfProgram.UnitLengthTolerance));

    private SdfReliefRegion(Vector3 origin, Quaternion rotation, SdfShapeType shape, Vector4 data0, Vector4 data1) {
        Origin = origin;
        Rotation = rotation;
        Shape = shape;
        Data0 = data0;
        Data1 = data1;
    }

    /// <summary>Gets the region's origin in the relief's frame.</summary>
    public Vector3 Origin { get; }
    /// <summary>Gets the region's turn in the relief's frame, of unit length.</summary>
    public Quaternion Rotation { get; }
    /// <summary>Gets the region's primitive type, a listed one.</summary>
    public SdfShapeType Shape { get; }
    /// <summary>Gets the primitive's first data vector, as a shape instruction packs it.</summary>
    public Vector4 Data0 { get; }
    /// <summary>Gets the primitive's second data vector, as a shape instruction packs it, with no smooth radius.</summary>
    public Vector4 Data1 { get; }

    /// <summary>Returns whether a region may be a primitive of <paramref name="shape"/>: a type that packs natively at any
    /// admitted scale, is 1-Lipschitz and is read by the fixed-point evaluator. A type joins the list as one case here
    /// and one in the kernel's region switch.</summary>
    /// <param name="shape">The packed shape type.</param>
    /// <returns><see langword="true"/> for a listed type.</returns>
    public static bool IsListed(SdfShapeType shape) => shape switch {
        SdfShapeType.Box => true,
        SdfShapeType.Superellipsoid => true,
        SdfShapeType.Capsule => true,
        SdfShapeType.Plane => true,
        _ => false,
    };
    /// <summary>Lowers what an author writes for a region into one: the primitive through its native-dimension arm
    /// (<see cref="SdfSolidGeometry.AppendNativePrimitive"/>) at every scale, never through a scale transform, and the
    /// pose normalized. A box packs its scaled half-extents, a sphere, an ellipsoid or a superellipsoid its radii, a
    /// capsule its endpoint and radius, a plane its normal.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The primitive's scale; its magnitudes are taken (<see cref="SdfSolidGeometry.EffectiveScale"/>).</param>
    /// <param name="position">The region's origin in the relief's frame.</param>
    /// <param name="rotation">The region's turn in the relief's frame; normalized here.</param>
    /// <param name="rounding">The box's edge rounding, in units; ignored by the other primitives.</param>
    /// <param name="chamfer">The box's edge chamfer, in units; a chamfered box lowers to a chamfered rectangle, which is
    /// refused.</param>
    /// <param name="exponent">The superellipsoid's exponent; ignored by the other primitives.</param>
    /// <returns>The region.</returns>
    /// <exception cref="ArgumentException">The primitive lowers to anything but one listed shape instruction with no side
    /// table (a chamfered box, a capsule scaled unlike in X and Z, a torus, a cylinder, a cone, a prism, a sweep), the
    /// pose is not finite or the turn is zero, or a lane passes <see cref="MaxMagnitude"/>.</exception>
    public static SdfReliefRegion Lower(SdfSolidPrimitive type, Vector3 scale, Vector3 position, Quaternion rotation, float rounding = 0f, float chamfer = 0f, float exponent = SdfProgramBuilder.MinSuperellipsoidExponent) {
        var length = rotation.Length();

        if (!(Puck.Maths.VectorFunctions.IsFinite(vector: position) && float.IsFinite(f: length) && (length > 0f))) {
            throw new ArgumentException(message: $"A relief region's pose must be finite with a nonzero turn; got origin {position} and turn {rotation}.", paramName: nameof(rotation));
        }

        var builder = new SdfProgramBuilder();

        if (SdfSolidGeometry.AppendNativePrimitive(blend: SdfBlendOp.Union, chain: builder, chamfer: chamfer, detail: false, effectiveScale: SdfSolidGeometry.EffectiveScale(scale: scale),
            exponent: exponent, material: 0, rounding: rounding, smooth: 0f, type: type) is null) {
            throw new ArgumentException(message: $"A relief region cannot be a {type} scaled {scale}: it has no native lowering at that scale (a capsule must be scaled alike in X and Z).", paramName: nameof(type));
        }

        var emitted = builder.EmittedInstructions;

        if ((emitted.Count != 1) || (emitted[0].Op != SdfOp.ShapeBlend) || builder.EmittedSideTables) {
            throw new ArgumentException(message: $"A relief region's {type} lowers to {emitted.Count} instructions; a region is exactly one shape instruction with no side table.", paramName: nameof(type));
        }

        var primitive = emitted[0];
        var shape = ((SdfShapeType)primitive.Shape);

        if (!IsListed(shape: shape)) {
            throw new ArgumentException(message: $"A relief region's {type} lowers to a {shape}; a region is a Box, a Superellipsoid, a Capsule or a Plane.", paramName: nameof(type));
        }

        var region = new SdfReliefRegion(data0: primitive.Data0, data1: (primitive.Data1 with { X = 0f }), origin: position, rotation: Quaternion.Normalize(value: rotation), shape: shape);

        // The door's own checks, so an author's region is refused where it is written rather than when a program builds.
        _ = SdfProgram.LaneContractOf(instruction: region.PoseRow());
        _ = SdfProgram.LaneContractOf(instruction: region.PrimitiveRow());

        return region;
    }
    /// <summary>Returns the pose row this region packs as (<see cref="SdfReliefRowKind.Pose"/>).</summary>
    /// <returns>The row.</returns>
    public SdfInstruction PoseRow() => new(
        Blend: ((uint)SdfReliefRowKind.Pose),
        Data0: new Vector4(value: Origin, w: 0f),
        Data1: new Vector4(x: Rotation.X, y: Rotation.Y, z: Rotation.Z, w: Rotation.W),
        Material: 0u,
        Op: SdfOp.ReliefRow,
        Shape: 0u
    );
    /// <summary>Returns the primitive row this region packs as (<see cref="SdfReliefRowKind.Primitive"/>).</summary>
    /// <returns>The row.</returns>
    public SdfInstruction PrimitiveRow() => new(
        Blend: ((uint)SdfReliefRowKind.Primitive),
        Data0: Data0,
        Data1: Data1,
        Material: 0u,
        Op: SdfOp.ReliefRow,
        Shape: ((uint)Shape)
    );

    // The region a pose row and a primitive row hold, each already through the lane contract.
    internal static SdfReliefRegion Read(SdfInstruction pose, SdfInstruction primitive) => new(
        data0: primitive.Data0,
        data1: primitive.Data1,
        origin: new Vector3(x: pose.Data0.X, y: pose.Data0.Y, z: pose.Data0.Z),
        rotation: new Quaternion(x: pose.Data1.X, y: pose.Data1.Y, z: pose.Data1.Z, w: pose.Data1.W),
        shape: ((SdfShapeType)primitive.Shape)
    );
}
/// <summary>
/// A relief mask: a band and the region shapes whose least distance it reads. A relief carries its masks as data rows
/// directly before it (<see cref="SdfOp.ReliefRow"/>), a band row then a pose and a primitive row per region, and
/// multiplies its displacement by the product of their weights; every other reader of the stream steps over the rows.
/// The weight lies in [0, 1], so a masked relief reaches no farther than its amplitude, and its slope adds
/// <see cref="Slope"/> times the relief's amplitude to the relief's step bound. A relief takes at most
/// <see cref="MaxMasks"/> masks and <see cref="MaxRegions"/> regions in all, which give the kernel's row loop a
/// constant bound.
/// </summary>
public sealed class SdfReliefMask {
    /// <summary>The most masks one relief takes.</summary>
    public const int MaxMasks = 8;
    /// <summary>The most region shapes one relief's masks hold in all.</summary>
    public const int MaxRegions = 32;
    /// <summary>The most rows one relief reads: a band per mask and two rows per region.</summary>
    public const int MaxRows = (MaxMasks + (2 * MaxRegions));

    private readonly SdfReliefRegion[] m_regions;

    /// <summary>Initializes a new instance of the <see cref="SdfReliefMask"/> class, a band over the least distance of
    /// its regions.</summary>
    /// <param name="band">The band.</param>
    /// <param name="regions">The regions, between one and <see cref="MaxRegions"/>; copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="regions"/> or one of its entries is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="regions"/> holds no region or more than <see cref="MaxRegions"/>.</exception>
    public SdfReliefMask(SdfReliefBand band, IReadOnlyList<SdfReliefRegion> regions) {
        ArgumentNullException.ThrowIfNull(argument: regions);

        if (regions.Count is < 1 or > MaxRegions) {
            throw new ArgumentException(message: $"A relief mask holds 1..{MaxRegions} regions; got {regions.Count}.", paramName: nameof(regions));
        }

        m_regions = [.. regions];

        foreach (var region in m_regions) {
            ArgumentNullException.ThrowIfNull(argument: region, paramName: nameof(regions));
        }

        Band = band;
    }

    /// <summary>Gets the mask's band.</summary>
    public SdfReliefBand Band { get; }
    /// <summary>Gets the mask's regions, whose least distance the band reads.</summary>
    public IReadOnlyList<SdfReliefRegion> Regions => m_regions;
    /// <summary>Gets the most the mask's weight changes per unit of the relief's point: the band's slope times the
    /// regions' stretch (<see cref="SdfReliefRegion.MaxStretch"/>).</summary>
    public float Slope => BandSlope(band: Band);

    /// <summary>Returns how many rows <paramref name="masks"/> pack as, checking the relief's caps.</summary>
    /// <param name="masks">One relief's masks.</param>
    /// <returns>The row count, at most <see cref="MaxRows"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="masks"/> or one of its entries is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="masks"/> holds more than <see cref="MaxMasks"/> masks or more
    /// than <see cref="MaxRegions"/> regions in all.</exception>
    public static int RowCount(IReadOnlyList<SdfReliefMask> masks) {
        ArgumentNullException.ThrowIfNull(argument: masks);

        if (masks.Count > MaxMasks) {
            throw new ArgumentException(message: $"A relief takes at most {MaxMasks} masks; got {masks.Count}.", paramName: nameof(masks));
        }

        var regions = 0;

        foreach (var mask in masks) {
            ArgumentNullException.ThrowIfNull(argument: mask, paramName: nameof(masks));
            regions += mask.m_regions.Length;
        }

        if (regions > MaxRegions) {
            throw new ArgumentException(message: $"A relief's masks hold at most {MaxRegions} regions in all; got {regions}.", paramName: nameof(masks));
        }

        return (masks.Count + (2 * regions));
    }
    /// <summary>Returns the most a mask of <paramref name="band"/> changes its weight per unit of the relief's point: the
    /// band's slope times the regions' stretch (<see cref="SdfReliefRegion.MaxStretch"/>), whatever its regions.</summary>
    /// <param name="band">The mask's band.</param>
    /// <returns>The slope.</returns>
    public static float BandSlope(SdfReliefBand band) => (band.Slope * SdfReliefRegion.MaxStretch);
    /// <summary>Returns the summed slope of <paramref name="masks"/> (<see cref="Slope"/>), which times the relief's
    /// amplitude joins its step bound (<see cref="SdfProgram.NoiseDisplaceStepFactor"/>).</summary>
    /// <param name="masks">One relief's masks.</param>
    /// <returns>The summed slope, per unit of the relief's point; zero for none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="masks"/> is <see langword="null"/>.</exception>
    public static float SlopeOf(IReadOnlyList<SdfReliefMask> masks) {
        ArgumentNullException.ThrowIfNull(argument: masks);

        var slope = 0f;

        foreach (var mask in masks) {
            slope += mask.Slope;
        }

        return slope;
    }

    // Appends the rows `masks` pack as, in order: each mask's band, then a pose and a primitive row per region.
    internal static void AppendRows(IReadOnlyList<SdfReliefMask> masks, List<SdfInstruction> rows) {
        _ = RowCount(masks: masks);

        foreach (var mask in masks) {
            rows.Add(item: mask.Band.ToRow());

            foreach (var region in mask.m_regions) {
                rows.Add(item: region.PoseRow());
                rows.Add(item: region.PrimitiveRow());
            }
        }
    }
    // Decodes one relief's run into its masks, refusing by name a run that is not bands each followed by one or more pose
    // and primitive pairs, or that passes the caps. Each row already passed the lane contract.
    internal static SdfReliefMask[] Read(ReadOnlySpan<SdfInstruction> run, int firstIndex, string paramName) {
        var masks = new List<SdfReliefMask>();
        var regions = new List<SdfReliefRegion>();
        var band = default(SdfReliefBand);
        var bandAt = -1;
        var total = 0;

        for (var at = 0; (at < run.Length); at++) {
            var row = run[at];
            var kind = ((SdfReliefRowKind)row.Blend);

            if (kind == SdfReliefRowKind.Band) {
                Close();
                _ = SdfReliefBand.TryRead(band: out band, row: row);
                bandAt = (firstIndex + at);
                continue;
            }
            if ((kind != SdfReliefRowKind.Pose) || (at == 0) || ((at + 1) >= run.Length) || (((SdfReliefRowKind)run[(at + 1)].Blend) != SdfReliefRowKind.Primitive)) {
                throw new ArgumentException(message: $"Relief mask row {(firstIndex + at)} ({kind}) is out of place: a relief's rows are bands, each followed by one or more pairs of a pose row and a primitive row.", paramName: paramName);
            }
            if (++total > MaxRegions) {
                throw new ArgumentException(message: $"The relief mask rows from instruction {firstIndex} hold more than {MaxRegions} regions; a relief's masks hold at most {MaxRegions} in all.", paramName: paramName);
            }

            regions.Add(item: SdfReliefRegion.Read(pose: row, primitive: run[(at + 1)]));
            at++;
        }

        Close();

        return [.. masks];

        void Close() {
            if (bandAt < 0) { return; }
            if (regions.Count == 0) {
                throw new ArgumentException(message: $"The relief mask band at instruction {bandAt} has no region after it: a mask's band is followed by at least one pose and primitive pair.", paramName: paramName);
            }
            if (masks.Count == MaxMasks) {
                throw new ArgumentException(message: $"The relief mask rows from instruction {firstIndex} hold more than {MaxMasks} masks; a relief takes at most {MaxMasks}.", paramName: paramName);
            }

            masks.Add(item: new SdfReliefMask(band: band, regions: regions));
            regions.Clear();
        }
    }
}
