namespace Puck.Abstractions.Presentation;

/// <summary>The vertex/instance count validation every backend's draw-parameters type needs before it can hand the
/// counts to a native draw call: both must be non-zero (an empty draw is a caller error, not a legal zero-work
/// draw).</summary>
public static class DrawCounts {
    /// <summary>Throws when either count is zero.</summary>
    /// <param name="vertexCount">The number of vertices per instance.</param>
    /// <param name="instanceCount">The number of instances.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="vertexCount"/> or
    /// <paramref name="instanceCount"/> is zero.</exception>
    public static void RequireNonZero(uint vertexCount, uint instanceCount) {
        if (vertexCount == 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: vertexCount,
                message: "Draw vertex count must be greater than zero.",
                paramName: nameof(vertexCount)
            );
        }

        if (instanceCount == 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: instanceCount,
                message: "Draw instance count must be greater than zero.",
                paramName: nameof(instanceCount)
            );
        }
    }
}
