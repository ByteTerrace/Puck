using Puck.Maths;

namespace Puck.Physics;

/// <summary>A world-axis box about a static collider that the collider's exact query never pushes a body outside of:
/// six raw Q48.16 words, read without touching the collider record.</summary>
/// <param name="Center">The box's world center: the collider's <see cref="FixedStaticCollider.Center"/>.</param>
/// <param name="Extent">The collider's padded <see cref="FixedStaticCollider.WorldHalfExtent"/>, or a negative half-extent
/// for a collider without bounds.</param>
public readonly record struct FixedColliderBox(FixedVector3 Center, FixedVector3 Extent) {
    private static readonly FixedVector3 NoBounds = new(X: -FixedQ4816.One, Y: -FixedQ4816.One, Z: -FixedQ4816.One);

    /// <summary>Gets whether the collider has bounds; a half-space or an unbounded field has none and may meet any
    /// body.</summary>
    public bool IsBounded => (Extent.X >= FixedQ4816.Zero);

    /// <summary>Creates the box of one collider.</summary>
    /// <param name="collider">The collider.</param>
    /// <returns>The collider's centre and padded world half-extent, or a box without bounds when the collider has
    /// none.</returns>
    public static FixedColliderBox Of(in FixedStaticCollider collider) =>
        new(Center: collider.Center, Extent: (collider.WorldHalfExtent() ?? NoBounds));
}
/// <summary>Static colliders compiled once with their <see cref="FixedColliderBox"/> bounds: the only form in which
/// <see cref="FixedStaticContactSolver"/> takes static colliders, so every bound it culls by is the collider's own and
/// no caller supplies one.</summary>
public sealed class FixedStaticColliderSet {
    private readonly FixedStaticCollider[] m_colliders;
    private readonly FixedColliderBox[] m_boxes;

    /// <summary>Initializes a new instance of the <see cref="FixedStaticColliderSet"/> class over an array it keeps.</summary>
    /// <param name="colliders">The colliders, in the order every query visits them; the set reads the array and the
    /// caller must not change it afterwards.</param>
    /// <exception cref="ArgumentNullException"><paramref name="colliders"/> is <see langword="null"/>.</exception>
    public FixedStaticColliderSet(FixedStaticCollider[] colliders) {
        ArgumentNullException.ThrowIfNull(argument: colliders);
        m_colliders = colliders;
        m_boxes = new FixedColliderBox[colliders.Length];

        for (var index = 0; (index < colliders.Length); index++) {
            m_boxes[index] = FixedColliderBox.Of(collider: in colliders[index]);
        }
    }

    /// <summary>Gets the set of no colliders.</summary>
    public static FixedStaticColliderSet Empty { get; } = new(colliders: []);

    /// <summary>Gets the colliders, in query order.</summary>
    public ReadOnlySpan<FixedStaticCollider> Colliders => m_colliders;
    /// <summary>Gets each collider's bounds, at the collider's index.</summary>
    public ReadOnlySpan<FixedColliderBox> Boxes => m_boxes;
    /// <summary>Gets the number of colliders.</summary>
    public int Count => m_colliders.Length;
}
