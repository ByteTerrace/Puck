using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>
/// The certified bounds of the union of two fields — the lesser distance at every point — so a sweep over them
/// (<see cref="CertifiedFieldSweep"/>) proves a body clear of both at once.
/// </summary>
/// <remarks>The union's interval over a box is the lesser of each endpoint, which encloses the lesser of the two
/// fields' answers at every point of the box, and its step scale is the lesser of the two. When either part refuses
/// the box, so does the union: a refusal says the part cannot bound it (it has nothing to answer, or the box leaves
/// its frame), and a union that dropped the part would claim a clearance it never proved. A part with no geometry is
/// left out of the union where it is built, not here.</remarks>
public sealed class FieldBoundsUnion : IFieldBounds {
    private readonly IFieldBounds m_a;
    private readonly IFieldBounds m_b;

    /// <summary>Initializes a new instance of the <see cref="FieldBoundsUnion"/> class.</summary>
    /// <param name="a">One field's bounds.</param>
    /// <param name="b">The other field's bounds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="a"/> or <paramref name="b"/> is
    /// <see langword="null"/>.</exception>
    public FieldBoundsUnion(IFieldBounds a, IFieldBounds b) {
        ArgumentNullException.ThrowIfNull(argument: a);
        ArgumentNullException.ThrowIfNull(argument: b);

        m_a = a;
        m_b = b;
    }

    /// <inheritdoc/>
    public FixedQ4816 StepScale => FixedQ4816.Min(
        x: m_a.StepScale,
        y: m_b.StepScale
    );

    /// <inheritdoc/>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
        distance = FixedInterval.FromPoint(value: FixedQ4816.Zero);

        if (
            !m_a.TryDistanceBounds(
                distance: out var a,
                lower: lower,
                upper: upper
            ) ||
            !m_b.TryDistanceBounds(
                distance: out var b,
                lower: lower,
                upper: upper
            )
        ) {
            return false;
        }

        distance = FixedInterval.Min(
            first: a,
            second: b
        );

        return true;
    }
}
