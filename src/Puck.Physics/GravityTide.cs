using Puck.Maths;

namespace Puck.Physics;

/// <summary>The tide far point masses raise across a region: the symmetric Jacobian of their softened pull at one
/// origin. A point at an offset from the origin feels the tensor's product with that offset on top of the pull the
/// origin itself feels, which a frame falling with the origin never sees.</summary>
/// <remarks>The tensor is the same first-order term the adaptive multipole solver carries between cells
/// (<see cref="GravityGradient"/>), exposed for a caller that reduces a far source to one evaluation: it is exact to
/// first order in the offset over the source's distance, and deterministic in fixed point.</remarks>
public readonly struct GravityTide : IEquatable<GravityTide> {
    private readonly GravityGradient m_gradient;

    private GravityTide(GravityGradient gradient) => m_gradient = gradient;

    /// <summary>Gets the tensor's six independent entries as doubles, in the order xx, xy, xz, yy, yz, zz, for a
    /// read-back. Per second squared: an entry times an offset in world units is an acceleration.</summary>
    public (double Xx, double Xy, double Xz, double Yy, double Yz, double Zz) Entries {
        get {
            const double Scale = (1d / (1UL << FixedQ3232.FractionBitCount));

            return (
                (m_gradient.Xx * Scale),
                (m_gradient.Xy * Scale),
                (m_gradient.Xz * Scale),
                (m_gradient.Yy * Scale),
                (m_gradient.Yz * Scale),
                (m_gradient.Zz * Scale)
            );
        }
    }
    /// <summary>Gets a value indicating whether the tensor is zero, the tide of no source.</summary>
    public bool IsZero => (m_gradient == default);

    /// <summary>Applies the tide to an offset from its origin.</summary>
    /// <param name="offset">The point's offset from the origin the tide was formed at, in world units.</param>
    /// <param name="acceleration">The tidal acceleration on return, or zero when the product leaves the Q48.16
    /// grid.</param>
    /// <returns><see langword="true"/> when the product is representable.</returns>
    public bool TryAccelerationAt(FixedVector3 offset, out FixedVector3 acceleration) {
        acceleration = FixedVector3.Zero;

        if (IsZero) {
            return true;
        }

        try {
            acceleration = m_gradient.Apply(offset: offset);

            return true;
        } catch (OverflowException) {
            acceleration = FixedVector3.Zero;

            return false;
        }
    }
    /// <inheritdoc/>
    public bool Equals(GravityTide other) => (m_gradient == other.m_gradient);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is GravityTide other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => m_gradient.GetHashCode();

    /// <summary>Sums two tides formed at the same origin.</summary>
    /// <param name="left">The first tide.</param>
    /// <param name="right">The second tide.</param>
    /// <param name="sum">The sum on return, or the zero tide when an entry leaves the grid.</param>
    /// <returns><see langword="true"/> when every entry of the sum is representable.</returns>
    public static bool TryAdd(GravityTide left, GravityTide right, out GravityTide sum) {
        if (GravityGradient.TryAdd(left: left.m_gradient, right: right.m_gradient, sum: out var gradient)) {
            sum = new GravityTide(gradient: gradient);

            return true;
        }

        sum = default;

        return false;
    }
    /// <summary>Forms the tide one point mass raises at an origin.</summary>
    /// <param name="origin">The point the tide is formed at.</param>
    /// <param name="source">The source's position.</param>
    /// <param name="mass">The source's gravitational mass; zero or less raises no tide.</param>
    /// <param name="parameters">The gravitational constant and softening length the pull uses.</param>
    /// <param name="tide">The tide on return, or the zero tide when the source raises none or an intermediate leaves
    /// the grid.</param>
    /// <returns><see langword="true"/> when the tide is representable; <see langword="false"/> when the source is
    /// too near, too far or too massive for the fixed-point kernel.</returns>
    public static bool TryFromSource(FixedVector3 origin, FixedVector3 source, FixedQ4816 mass, in GravityParameters parameters, out GravityTide tide) {
        tide = default;

        if (
            (mass <= FixedQ4816.Zero) ||
            (parameters.GravitationalConstant <= FixedQ4816.Zero)
        ) {
            return true;
        }

        try {
            var interaction = GravityKernel.PrepareInteraction(
                softeningSquared: checked((parameters.SofteningLength * parameters.SofteningLength)),
                source: source,
                target: origin
            );

            if (!GravityGradient.TryFromInteraction(
                gradient: out var gradient,
                gravitationalConstant: parameters.GravitationalConstant,
                interaction: in interaction,
                sourceMass: mass
            )) {
                return false;
            }

            tide = new GravityTide(gradient: gradient);

            return true;
        } catch (OverflowException) {
            return false;
        }
    }

    /// <summary>Returns whether two tides hold the same tensor.</summary>
    /// <param name="left">The first tide.</param>
    /// <param name="right">The second tide.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(GravityTide left, GravityTide right) => left.Equals(other: right);
    /// <summary>Returns whether two tides hold different tensors.</summary>
    /// <param name="left">The first tide.</param>
    /// <param name="right">The second tide.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(GravityTide left, GravityTide right) => !left.Equals(other: right);
}
