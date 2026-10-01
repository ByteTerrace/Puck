namespace Puck.World.Tests.FidgetStudy;

/// <summary>A pinhole view and its screen tiles as the kernels build them (<c>cameraRayDirection</c>,
/// <c>buildTileCone</c> and <c>sdfInstancePassesTileCone</c> in <c>march/sdf-cone.hlsli</c>).</summary>
public sealed class SdfStudyView {
    /// <summary>Initializes a new instance of the <see cref="SdfStudyView"/> class.</summary>
    /// <param name="eye">The eye position.</param>
    /// <param name="target">The look-at point.</param>
    /// <param name="verticalFov">The vertical field of view, in radians.</param>
    /// <param name="width">The render width, in pixels.</param>
    /// <param name="height">The render height, in pixels.</param>
    /// <param name="farDistance">The far distance, in world units.</param>
    public SdfStudyView(V3 eye, V3 target, double verticalFov, int width, int height, double farDistance) {
        Eye = eye;
        Width = width;
        Height = height;
        FarDistance = farDistance;
        Forward = Normalize(v: (target - eye));
        Right = Normalize(v: V3.Cross(a: Forward, b: new V3(X: 0, Y: 1, Z: 0)));
        Up = V3.Cross(a: Right, b: Forward);
        TanHalfFov = Math.Tan(a: (0.5 * verticalFov));
        Aspect = (width / ((double)height));
        Footprint = ((2.0 * TanHalfFov) / height);
    }

    /// <summary>Gets the counters workload's view (<c>tests/Puck.Counters/counters.world.json</c>'s camera) at the
    /// floor tier's render extent: 1920x1080 at the low preset's half render scale, which the workload script states
    /// renders at 1440x810.</summary>
    public static SdfStudyView CountersFloor { get; } = new(
        eye: new V3(X: 0, Y: 2.5, Z: 7),
        farDistance: 40.0,
        height: 810,
        target: new V3(X: 0, Y: 0.8, Z: 0),
        verticalFov: 0.9,
        width: 1440
    );
    /// <summary>Gets the eye.</summary>
    public V3 Eye { get; }
    /// <summary>Gets the render width.</summary>
    public int Width { get; }
    /// <summary>Gets the render height.</summary>
    public int Height { get; }
    /// <summary>Gets the far distance.</summary>
    public double FarDistance { get; }
    /// <summary>Gets the forward axis.</summary>
    public V3 Forward { get; }
    /// <summary>Gets the right axis.</summary>
    public V3 Right { get; }
    /// <summary>Gets the up axis.</summary>
    public V3 Up { get; }
    /// <summary>Gets the tangent of half the vertical field of view.</summary>
    public double TanHalfFov { get; }
    /// <summary>Gets the aspect ratio.</summary>
    public double Aspect { get; }
    /// <summary>Gets the pixel footprint per unit depth the primary march accepts hits at.</summary>
    public double Footprint { get; }

    /// <summary>Returns the ray direction through a view-local UV.</summary>
    /// <param name="u">The horizontal UV.</param>
    /// <param name="v">The vertical UV, zero at the top.</param>
    /// <returns>The unit direction.</returns>
    public V3 Direction(double u, double v) {
        var nx = ((u * 2.0) - 1.0);
        var ny = -((v * 2.0) - 1.0);

        return Normalize(v: ((Forward + (Right * ((nx * Aspect) * TanHalfFov))) + (Up * (ny * TanHalfFov))));
    }
    /// <summary>Returns a tile's cone: its center direction, chord and inverse aperture.</summary>
    /// <param name="tileX">The tile column.</param>
    /// <param name="tileY">The tile row.</param>
    /// <param name="tileSize">The tile size, in pixels.</param>
    /// <returns>The cone.</returns>
    public (V3 Direction, double Chord, double InverseAperture) Cone(int tileX, int tileY, int tileSize) {
        var u0 = ((tileX * tileSize) / ((double)Width));
        var v0 = ((tileY * tileSize) / ((double)Height));
        var u1 = (Math.Min(val1: ((tileX + 1) * tileSize), val2: Width) / ((double)Width));
        var v1 = (Math.Min(val1: ((tileY + 1) * tileSize), val2: Height) / ((double)Height));
        var center = Direction(u: (0.5 * (u0 + u1)), v: (0.5 * (v0 + v1)));
        var chord = Math.Max(
            val1: Math.Max(val1: (Direction(u: u0, v: v0) - center).Length, val2: (Direction(u: u1, v: v0) - center).Length),
            val2: Math.Max(val1: (Direction(u: u0, v: v1) - center).Length, val2: (Direction(u: u1, v: v1) - center).Length)
        );

        return (center, chord, (1.0 / Math.Sqrt(d: Math.Max(val1: (1.0 - (chord * chord)), val2: 1.0e-6))));
    }
    /// <summary>Returns the segments a tile's masked walk visits: every world segment, and the segments of every
    /// instance whose bound passes the tile cone and is not camera-hidden.</summary>
    /// <param name="tape">The tape.</param>
    /// <param name="cone">The tile cone.</param>
    /// <returns>The segments, ascending.</returns>
    public int[] MaskedSegments(SdfStudyTape tape, (V3 Direction, double Chord, double InverseAperture) cone) {
        var segments = new List<int>();

        for (var segment = 0; (segment < tape.Segments.Length); segment++) {
            var owner = tape.Segments[segment].Owner;

            if ((owner < 0) || Passes(cone: cone, instance: owner, tape: tape)) {
                segments.Add(item: segment);
            }
        }

        return [.. segments];
    }

    private bool Passes(SdfStudyTape tape, int instance, (V3 Direction, double Chord, double InverseAperture) cone) {
        var entry = tape.Instances[instance];

        if ((entry.Bound.Radius < 0.0) || entry.CameraHidden) {
            return false;
        }

        var toCenter = (tape.CenterOf(bound: entry.Bound) - Eye);
        var along = Math.Max(val1: V3.Dot(a: toCenter, b: cone.Direction), val2: 0.0);
        var axis = (toCenter - (cone.Direction * along)).Length;

        return (axis <= ((entry.Bound.Radius + (cone.Chord * along)) * cone.InverseAperture));
    }
    private static V3 Normalize(V3 v) => (v * (1.0 / v.Length));
}
