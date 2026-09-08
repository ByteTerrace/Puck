using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>
/// One isometry of creation space in fixed point: a translation, a proper rotation, and whether the isometry also
/// mirrors.
/// </summary>
/// <remarks>
/// <para>An improper isometry has determinant −1 and no quaternion holds it, so the mirror is factored out against the
/// fixed generator <c>H(x̂)</c> — reflection across the plane x = 0. The stored <see cref="Rotation"/> is the proper
/// remainder: the true isometry is <c>translate(Position) ∘ Rotation ∘ H(x̂)^Mirrored</c>.</para>
/// <para>Every primitive in <see cref="SdfSolidPrimitive"/> is symmetric under <c>x → −x</c> in its own local frame, so
/// a consumer placing one may emit <see cref="Position"/> and <see cref="Rotation"/> and ignore
/// <see cref="Mirrored"/> — the trailing reflection maps the primitive onto itself. A consumer transforming a POINT
/// may not: use <see cref="TransformPoint"/>, which applies the full isometry.</para>
/// </remarks>
/// <param name="Position">The image of the origin.</param>
/// <param name="Rotation">The proper rotation remaining once the mirror is factored out.</param>
/// <param name="Mirrored">Whether the isometry reverses handedness.</param>
public readonly record struct SdfRigidFrame(FixedVector3 Position, FixedQuaternion Rotation, bool Mirrored) {
    /// <summary>Gets the identity frame.</summary>
    public static SdfRigidFrame Identity { get; } = new(
        Mirrored: false,
        Position: FixedVector3.Zero,
        Rotation: FixedQuaternion.Identity
    );

    // Conjugation by H(x̂): a rotation about axis a by θ becomes one about (a.X, −a.Y, −a.Z) by θ.
    private static FixedQuaternion ConjugateByMirror(FixedQuaternion rotation) =>
        new(
            W: rotation.W,
            X: rotation.X,
            Y: -rotation.Y,
            Z: -rotation.Z
        );
    private static FixedVector3 MirrorPoint(FixedVector3 point) =>
        point with { X = -point.X };

    /// <summary>Returns the composition <c>this ∘ inner</c> — the isometry applying <paramref name="inner"/> first.</summary>
    /// <param name="inner">The isometry applied first.</param>
    /// <returns>The composed isometry.</returns>
    /// <remarks>Composing with <see cref="Identity"/> returns the other operand rather than routing it through a
    /// multiply and a renormalize, both of which round: geometry carrying no fold stays bit-identical to geometry that
    /// never visited a frame.</remarks>
    public SdfRigidFrame Compose(SdfRigidFrame inner) {
        if (this == Identity) {
            return inner;
        }

        if (inner == Identity) {
            return this;
        }

        return new SdfRigidFrame(
            Mirrored: Mirrored ^ inner.Mirrored,
            Position: TransformPoint(point: inner.Position),
            Rotation: (Rotation * (Mirrored
            ? ConjugateByMirror(rotation: inner.Rotation)
            : inner.Rotation)).Normalize()
        );
    }
    /// <summary>Returns a point carried through the full isometry, mirror included.</summary>
    /// <param name="point">The point to carry.</param>
    /// <returns>The transformed point.</returns>
    public FixedVector3 TransformPoint(FixedVector3 point) {
        if (this == Identity) {
            return point;
        }

        return (Position + Rotation.Rotate(vector: (Mirrored
            ? MirrorPoint(point: point)
            : point)));
    }
}
