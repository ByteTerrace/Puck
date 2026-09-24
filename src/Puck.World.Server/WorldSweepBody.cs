using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One reusable sweep row for broadphase and dynamic-body contact.</summary>
internal readonly record struct WorldSweepBody(
    int Index,
    FixedQ4816 MinimumX,
    FixedQ4816 MaximumX,
    FixedQ4816 Radius
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
