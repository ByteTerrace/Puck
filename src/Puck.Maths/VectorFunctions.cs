using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The float helpers presentation code shares: admission checks on <see cref="System.Numerics"/> vectors, angle
/// wraps and easings over any IEEE 754 type, the −Z-forward attitude readbacks, and the unorm packers the GPU decodes.
/// Everything here is presentation-only floating point, outside the determinism contract: nothing it returns flows
/// back into simulation state, and a document or engine boundary that admits an authored float quantizes it to fixed
/// point through <see cref="FixedQ4816.FromDouble"/> or a vector door afterwards.
/// </summary>
public static class VectorFunctions {
    private const float UnormByteScale = 255f;
    private const float UnormShortScale = 65535f;

    /// <summary>Returns whether every component of <paramref name="vector"/> is finite (neither NaN nor an
    /// infinity).</summary>
    /// <param name="vector">The vector to test.</param>
    /// <returns><see langword="true"/> when every component is finite.</returns>
    public static bool IsFinite(Vector2 vector) => Vector2.AllWhereAllBitsSet(vector: Vector2.IsFinite(vector: vector));
    /// <summary>Returns whether every component of <paramref name="vector"/> is finite (neither NaN nor an
    /// infinity).</summary>
    /// <param name="vector">The vector to test.</param>
    /// <returns><see langword="true"/> when every component is finite.</returns>
    public static bool IsFinite(Vector3 vector) => Vector3.AllWhereAllBitsSet(vector: Vector3.IsFinite(vector: vector));
    /// <summary>Returns whether every component of <paramref name="vector"/> is finite (neither NaN nor an
    /// infinity).</summary>
    /// <param name="vector">The vector to test.</param>
    /// <returns><see langword="true"/> when every component is finite.</returns>
    public static bool IsFinite(Vector4 vector) => Vector4.AllWhereAllBitsSet(vector: Vector4.IsFinite(vector: vector));
    /// <summary>Returns whether every component of <paramref name="rotation"/> is finite (neither NaN nor an
    /// infinity).</summary>
    /// <param name="rotation">The rotation to test.</param>
    /// <returns><see langword="true"/> when every component is finite.</returns>
    public static bool IsFinite(Quaternion rotation) => IsFinite(vector: new Vector4(
        w: rotation.W,
        x: rotation.X,
        y: rotation.Y,
        z: rotation.Z
    ));

    /// <summary>Reduces an angle onto <c>(−π, π]</c>.</summary>
    /// <typeparam name="T">The IEEE 754 floating-point type.</typeparam>
    /// <param name="radians">The angle in radians.</param>
    /// <returns>The IEEE remainder of <paramref name="radians"/> by <c>2π</c>, with an exact <c>−π</c> mapped to
    /// <c>+π</c>; a non-finite angle passes through as the remainder leaves it.</returns>
    public static T WrapAngle<T>(T radians) where T : IFloatingPointIeee754<T> {
        var wrapped = T.Ieee754Remainder(
            left: radians,
            right: T.Tau
        );

        return ((wrapped == -T.Pi)
            ? T.Pi
            : wrapped
        );
    }
    /// <summary>Reduces an angle onto <c>[0, 2π)</c>.</summary>
    /// <typeparam name="T">The IEEE 754 floating-point type.</typeparam>
    /// <param name="radians">The angle in radians.</param>
    /// <returns>The IEEE remainder of <paramref name="radians"/> by <c>2π</c>, lifted by <c>2π</c> when negative; a
    /// lift that rounds to exactly <c>2π</c> answers zero, so the result is strictly below <c>2π</c>.</returns>
    public static T WrapTurn<T>(T radians) where T : IFloatingPointIeee754<T> {
        var wrapped = T.Ieee754Remainder(
            left: radians,
            right: T.Tau
        );

        if (T.IsNegative(value: wrapped)) {
            wrapped += T.Tau;
        }

        return ((wrapped >= T.Tau)
            ? T.Zero
            : wrapped
        );
    }
    /// <summary>Evaluates the Hermite smoothstep <c>t²(3 − 2t)</c> on an already-clamped progress fraction.</summary>
    /// <typeparam name="T">The IEEE 754 floating-point type.</typeparam>
    /// <param name="t">The progress fraction, in <c>[0, 1]</c>.</param>
    /// <returns><c>(t · t) · (3 − 2 · t)</c>, formed in that order, so the single and double instantiations keep the
    /// bits of the expression each render clock and key resolver evaluates.</returns>
    public static T Smoothstep<T>(T t) where T : IFloatingPointIeee754<T> =>
        ((t * t) * (T.CreateTruncating(value: 3) - (T.CreateTruncating(value: 2) * t)));
    /// <summary>Evaluates the Hermite smoothstep of <paramref name="x"/> between two edges.</summary>
    /// <typeparam name="T">The IEEE 754 floating-point type.</typeparam>
    /// <param name="edge0">The edge where the result is zero.</param>
    /// <param name="edge1">The edge where the result is one.</param>
    /// <param name="x">The value to map.</param>
    /// <returns><see cref="Smoothstep{T}(T)"/> of <c>(x − edge0) / (edge1 − edge0)</c> clamped to <c>[0, 1]</c>;
    /// equal edges make the step <c>x &lt; edge0 ? 0 : 1</c>.</returns>
    public static T Smoothstep<T>(T edge0, T edge1, T x) where T : IFloatingPointIeee754<T> {
        if (edge0 == edge1) {
            return ((x < edge0)
                ? T.Zero
                : T.One
            );
        }

        return Smoothstep(t: T.Clamp(
            max: T.One,
            min: T.Zero,
            value: ((x - edge0) / (edge1 - edge0))
        ));
    }
    /// <summary>Returns the fractional part of a value, <c>x − ⌊x⌋</c>.</summary>
    /// <typeparam name="T">The IEEE 754 floating-point type.</typeparam>
    /// <param name="x">The value.</param>
    /// <returns>A value in <c>[0, 1)</c> for a finite <paramref name="x"/>, the same for both signs of a value with
    /// the same fraction below its floor.</returns>
    public static T Fract<T>(T x) where T : IFloatingPointIeee754<T> =>
        (x - T.Floor(x: x));

    /// <summary>Creates the shortest-arc rotation taking the direction of <paramref name="from"/> to the direction of
    /// <paramref name="to"/>.</summary>
    /// <param name="from">The start direction, of any non-zero length.</param>
    /// <param name="to">The end direction, of any non-zero length.</param>
    /// <returns>The unit rotation with <c>Transform(from, rotation)</c> along <paramref name="to"/>:
    /// <see cref="Quaternion.Identity"/> when either input is zero or not finite or the two already align, and a half
    /// turn about <see cref="Perpendicular"/> of the normalized <paramref name="from"/> when their cosine is within
    /// <c>1e-6</c> of <c>−1</c>, where no cross product names an axis; inside that band the half turn carries the
    /// start to within the band's angle of the end.</returns>
    public static Quaternion ShortestArc(Vector3 from, Vector3 to) {
        // Within this of antiparallel the rotor's scalar 1 + cosine is noise against the cross product's rounding,
        // so the half turn about a named perpendicular takes over; it carries the start to within the same gap of
        // the end. A nearly aligned pair needs no such guard: its rotor (cross, 1 + cosine) normalizes stably.
        const float AntiparallelTolerance = 1e-6f;

        var fromLengthSquared = from.LengthSquared();
        var toLengthSquared = to.LengthSquared();

        if (
            !float.IsFinite(f: fromLengthSquared) ||
            !float.IsFinite(f: toLengthSquared) ||
            (fromLengthSquared == 0f) ||
            (toLengthSquared == 0f)
        ) {
            return Quaternion.Identity;
        }

        var start = (from / MathF.Sqrt(x: fromLengthSquared));
        var end = (to / MathF.Sqrt(x: toLengthSquared));
        var cosine = Vector3.Dot(
            vector1: start,
            vector2: end
        );
        var axis = Vector3.Cross(
            vector1: start,
            vector2: end
        );

        // The cross product resolves a small angle the cosine has already rounded to one: only a vanished cross
        // product with a positive cosine is alignment.
        if (
            (axis == Vector3.Zero) &&
            (cosine > 0f)
        ) {
            return Quaternion.Identity;
        }
        if (cosine <= (-1f + AntiparallelTolerance)) {
            return Quaternion.CreateFromAxisAngle(
                angle: MathF.PI,
                axis: Perpendicular(direction: start)
            );
        }

        return Quaternion.Normalize(value: new Quaternion(
            w: (1f + cosine),
            x: axis.X,
            y: axis.Y,
            z: axis.Z
        ));
    }
    /// <summary>Returns a unit vector perpendicular to a direction.</summary>
    /// <param name="direction">The direction, of any non-zero length.</param>
    /// <returns>The normalized cross product of <paramref name="direction"/> with the basis axis it is least aligned
    /// with — the axis whose component has the smallest magnitude — so the cross product never collapses;
    /// <see cref="Vector3.UnitY"/> for a zero or non-finite direction, which has no perpendicular.</returns>
    public static Vector3 Perpendicular(Vector3 direction) {
        if (
            !IsFinite(vector: direction) ||
            (direction == Vector3.Zero)
        ) {
            return Vector3.UnitY;
        }

        var absolute = Vector3.Abs(value: direction);
        var axis = (((absolute.X <= absolute.Y) && (absolute.X <= absolute.Z))
            ? Vector3.UnitX
            : ((absolute.Y <= absolute.Z)
                ? Vector3.UnitY
                : Vector3.UnitZ)
        );

        return Vector3.Normalize(value: Vector3.Cross(
            vector1: direction,
            vector2: axis
        ));
    }

    /// <summary>Returns the yaw of an attitude's forward under the engine's −Z-forward convention: zero facing −Z, a
    /// positive yaw turning left about +Y.</summary>
    /// <param name="attitude">The attitude.</param>
    /// <returns><see cref="YawOf(Vector3)"/> of <c>Transform(−UnitZ, attitude)</c>, in <c>(−π, π]</c>.</returns>
    public static float YawOf(Quaternion attitude) =>
        YawOf(facing: Vector3.Transform(
            rotation: attitude,
            value: -Vector3.UnitZ
        ));
    /// <summary>Returns the yaw of a facing under the engine's −Z-forward convention.</summary>
    /// <param name="facing">The facing, of any non-zero length.</param>
    /// <returns><c>Atan2(−facing.X, −facing.Z)</c>, in <c>(−π, π]</c>; zero for a facing with no planar part, and
    /// <c>+π</c> for one facing +Z (the negations are subtractions from zero, so a zero X never reads as the
    /// negative zero that would put +Z at <c>−π</c>).</returns>
    public static float YawOf(Vector3 facing) =>
        MathF.Atan2(
            x: (0f - facing.Z),
            y: (0f - facing.X)
        );
    /// <summary>Returns the pitch of a facing: its elevation above the XZ plane, positive looking up.</summary>
    /// <param name="facing">The facing, of any non-zero length.</param>
    /// <returns><c>Atan2(facing.Y, √(facing.X² + facing.Z²))</c>, in <c>[−π/2, π/2]</c>.</returns>
    public static float PitchOf(Vector3 facing) =>
        MathF.Atan2(
            x: MathF.Sqrt(x: ((facing.X * facing.X) + (facing.Z * facing.Z))),
            y: facing.Y
        );
    /// <summary>Decomposes an attitude into the yaw, pitch and roll that <c>Ry(yaw)·Rx(pitch)·Rz(roll)</c> rebuilds
    /// it from, under the −Z-forward convention.</summary>
    /// <param name="attitude">The attitude.</param>
    /// <returns>The yaw of the rotated −Z (<see cref="YawOf(Quaternion)"/>), the pitch as the arcsine of the rotated
    /// forward's Y, and the roll as <c>Atan2(right.Y, up.Y)</c> over the rotated +X and +Y; all in radians.</returns>
    public static (float Yaw, float Pitch, float Roll) EulerOf(Quaternion attitude) {
        var forward = Vector3.Transform(
            rotation: attitude,
            value: -Vector3.UnitZ
        );
        var up = Vector3.Transform(
            rotation: attitude,
            value: Vector3.UnitY
        );
        var right = Vector3.Transform(
            rotation: attitude,
            value: Vector3.UnitX
        );

        return (
            YawOf(facing: forward),
            MathF.Asin(x: Math.Clamp(
                max: 1f,
                min: -1f,
                value: forward.Y
            )),
            MathF.Atan2(
                x: up.Y,
                y: right.Y
            )
        );
    }
    /// <summary>Returns the unit facing of a yaw and a pitch under the −Z-forward convention.</summary>
    /// <param name="yaw">The yaw in radians; zero faces −Z and a positive yaw turns left about +Y.</param>
    /// <param name="pitch">The pitch in radians, positive looking up.</param>
    /// <returns><c>(−sin yaw · cos pitch, sin pitch, −cos yaw · cos pitch)</c>, the inverse of
    /// <see cref="YawOf(Vector3)"/> and <see cref="PitchOf"/> up to rounding.</returns>
    public static Vector3 Heading(float yaw, float pitch) => new(
        x: (-MathF.Sin(x: yaw) * MathF.Cos(x: pitch)),
        y: MathF.Sin(x: pitch),
        z: (-MathF.Cos(x: yaw) * MathF.Cos(x: pitch))
    );

    /// <summary>Quantizes a unit-range value to an unsigned 8-bit normalized code.</summary>
    /// <param name="value">The value, clamped to <c>[0, 1]</c>; not-a-number codes as zero.</param>
    /// <returns><c>round(clamp(value) · 255)</c>, to nearest with ties to even, with the product formed in double
    /// where it is exact for every single-precision input, so the rounding is of the true product rather than of
    /// its single-precision rounding.</returns>
    public static byte ToUnorm8(float value) =>
        ToUnorm8(value: ((double)value));
    /// <summary>Quantizes a unit-range value to an unsigned 8-bit normalized code.</summary>
    /// <param name="value">The value, clamped to <c>[0, 1]</c>; not-a-number codes as zero.</param>
    /// <returns><c>round(clamp(value) · 255)</c>, to nearest with ties to even, the product formed in double: a
    /// product that itself rounds to a half in binary64 therefore takes the even code.</returns>
    public static byte ToUnorm8(double value) =>
        (double.IsNaN(d: value)
            ? ((byte)0)
            : ((byte)Math.Round(
                mode: MidpointRounding.ToEven,
                value: (Math.Clamp(
                    max: 1.0,
                    min: 0.0,
                    value: value
                ) * 255.0)
            )));
    /// <summary>Packs a colour into the <c>R8G8B8A8_UNORM</c> word a shader reads back as <c>rgba</c>: red in the
    /// low byte, alpha in the high.</summary>
    /// <param name="rgba">The colour, each lane clamped to <c>[0, 1]</c>.</param>
    /// <returns><c>r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24</c>, each lane <see cref="ToUnorm8(float)"/>.</returns>
    public static uint PackRgba8(Vector4 rgba) =>
        (((uint)ToUnorm8(value: rgba.X)) |
         (((uint)ToUnorm8(value: rgba.Y)) << 8) |
         (((uint)ToUnorm8(value: rgba.Z)) << 16) |
         (((uint)ToUnorm8(value: rgba.W)) << 24));
    /// <summary>Packs a texture coordinate into the unorm2x16 word <c>sdfGlyphUnpackUv</c> decodes: <c>u</c> in the
    /// low half, <c>v</c> in the high, each <c>round(clamp(x) · 65535)</c>.</summary>
    /// <param name="uv">The coordinate, each lane clamped to <c>[0, 1]</c>; not-a-number codes as zero.</param>
    /// <returns><c>u | v &lt;&lt; 16</c>, each lane rounded to nearest with ties to even.</returns>
    public static uint PackUnorm2x16(Vector2 uv) =>
        (ToUnorm16(value: uv.X) | (ToUnorm16(value: uv.Y) << 16));
    /// <summary>Unpacks an <c>R8G8B8A8_UNORM</c> word into its four unit-range lanes.</summary>
    /// <param name="packed">The word, red in the low byte and alpha in the high.</param>
    /// <returns>Each byte divided by <c>255</c>, as <c>(r, g, b, a)</c>.</returns>
    public static Vector4 UnpackRgba8(uint packed) => new(
        w: (((packed >> 24) & 0xFFU) / UnormByteScale),
        x: ((packed & 0xFFU) / UnormByteScale),
        y: (((packed >> 8) & 0xFFU) / UnormByteScale),
        z: (((packed >> 16) & 0xFFU) / UnormByteScale)
    );

    // The product is formed in double, where it is exact for every single-precision input.
    private static uint ToUnorm16(float value) =>
        (float.IsNaN(f: value)
            ? 0U
            : ((uint)Math.Round(
                mode: MidpointRounding.ToEven,
                value: (((double)Math.Clamp(
                    max: 1f,
                    min: 0f,
                    value: value
                )) * UnormShortScale)
            )));
}
