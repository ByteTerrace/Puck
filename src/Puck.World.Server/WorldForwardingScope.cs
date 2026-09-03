namespace Puck.World.Server;

/// <summary>Bounds synchronous local forwarding recursion without retaining request or simulation state.</summary>
internal readonly struct WorldForwardingScope : IDisposable {
    // Stack-safety bound, not an authored gameplay rule. Remote request workers own their own traversal scope.
    private const int MaximumDepth = 64;
    [ThreadStatic] private static int s_depth;

    public static bool TryEnter(out WorldForwardingScope scope, out string reason) {
        scope = default;
        if (s_depth >= MaximumDepth) {
            reason = $"local forwarding exceeds {MaximumDepth} hops; the route may contain a cycle";
            return false;
        }
        s_depth++;
        reason = string.Empty;
        return true;
    }

    public void Dispose() => s_depth--;
}
