namespace Puck.World.Browser.Engine;

/// <summary>Holds every live <see cref="BrowserSession"/> behind an opaque integer handle — the single-threaded,
/// process-lifetime table <c>Compile</c>/<c>Release</c>/every handle-taking export shares, since each
/// <c>[JSExport]</c> call is otherwise stateless from the JS side.</summary>
public static class BrowserSessionRegistry {
    private static readonly Dictionary<long, BrowserSession> s_sessions = [];
    private static long s_nextHandle = 1L;

    /// <summary>Installs a session and returns its handle.</summary>
    /// <param name="session">The session to install.</param>
    /// <returns>The handle.</returns>
    public static long Add(BrowserSession session) {
        ArgumentNullException.ThrowIfNull(argument: session);

        var handle = s_nextHandle++;

        s_sessions[handle] = session;

        return handle;
    }
    /// <summary>Finds a live session by handle.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="session">The session, or <see langword="null"/> when the handle is not (or is no longer) live.</param>
    /// <returns><see langword="true"/> when the handle names a live session.</returns>
    public static bool TryGet(long handle, out BrowserSession? session) => s_sessions.TryGetValue(key: handle, value: out session);
    /// <summary>Releases a session's handle.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns><see langword="true"/> when the handle named a live session.</returns>
    public static bool Release(long handle) => s_sessions.Remove(key: handle);
}
