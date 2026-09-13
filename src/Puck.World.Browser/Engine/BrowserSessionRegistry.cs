namespace Puck.World.Browser.Engine;

/// <summary>Holds every live <see cref="BrowserSession"/> behind an opaque integer handle — the single-threaded,
/// process-lifetime table <c>Compile</c>/<c>Release</c>/every handle-taking export shares, since each
/// <c>[JSExport]</c> call is otherwise stateless from the JS side.</summary>
public static class BrowserSessionRegistry {
    private static readonly Dictionary<long, BrowserSession> Sessions = [];

    private static long NextHandle = 1L;

    /// <summary>Installs a session and returns its handle.</summary>
    /// <param name="session">The session to install.</param>
    /// <returns>The handle.</returns>
    public static long Add(BrowserSession session) {
        ArgumentNullException.ThrowIfNull(argument: session);

        var handle = NextHandle++;

        Sessions[handle] = session;

        return handle;
    }
    /// <summary>Releases a session's handle.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns><see langword="true"/> when the handle named a live session.</returns>
    public static bool Release(long handle) => Sessions.Remove(key: handle);
    /// <summary>Finds a live session by handle.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="session">The session, or <see langword="null"/> when the handle is not (or is no longer) live.</param>
    /// <returns><see langword="true"/> when the handle names a live session.</returns>
    public static bool TryGet(long handle, out BrowserSession? session) => Sessions.TryGetValue(
        key: handle,
        value: out session
    );
}
