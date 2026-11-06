using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One reusable sweep row for broadphase and dynamic-body contact.</summary>
/// <param name="Index">The body's population index.</param>
/// <param name="MinimumX">The least world X the row's sphere reaches.</param>
/// <param name="MaximumX">The greatest world X the row's sphere reaches.</param>
/// <param name="Radius">The sphere's radius: the body's broadphase radius, grown by <paramref name="Travel"/> and its
/// share of the pair contact margin where the sweep finds speculative contacts.</param>
/// <param name="Travel">The farthest the body can move over the step, in metres.</param>
internal readonly record struct WorldSweepBody(
    int Index,
    FixedQ4816 MinimumX,
    FixedQ4816 MaximumX,
    FixedQ4816 Radius,
    FixedQ4816 Travel = default
) : IComparable<WorldSweepBody> {
    public int CompareTo(WorldSweepBody other) {
        return LexicographicOrder.Compare(
            leftPrimary: MinimumX,
            leftSecondary: Index,
            rightPrimary: other.MinimumX,
            rightSecondary: other.Index
        );
    }
}
