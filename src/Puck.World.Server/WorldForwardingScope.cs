namespace Puck.World.Server;

/// <summary>Bounds synchronous local forwarding recursion without retaining request or simulation state.</summary>
internal readonly struct WorldForwardingScope : IDisposable {
    // Stack-safety bound, not an authored gameplay rule. Remote request workers own their own traversal scope.
    private const int MaximumDepth = 64;

    [ThreadStatic] private static int Depth;

    public void Dispose() => Depth--;
    public static bool TryEnter(out WorldForwardingScope scope, out string reason) {
        scope = default;
        if (Depth >= MaximumDepth) {
            reason = $"local forwarding exceeds {MaximumDepth} hops; the route may contain a cycle";
            return false;
        }
        Depth++;
        reason = string.Empty;
        return true;
    }
}
