namespace Puck.Hosting;

/// <summary>The exception-isolation shape a capture-to-PNG write needs around its one call into the optional
/// Puck.Assets-typed encoder: Puck.Assets is not part of the render contract, so an environment that blocks or
/// cannot load its assembly (an Application Control / code-integrity policy, a missing deployment file) must not
/// take the render loop down with it.</summary>
public static class CapturePngWriteGuard {
    /// <summary>Attempts <paramref name="writeCore"/>, surviving (and loudly reporting) a failure to load Puck.Assets.</summary>
    /// <param name="state">The caller's write arguments.</param>
    /// <param name="writeCore">The call into the Puck.Assets-typed encoder. Callers keep this in its own non-inlined
    /// method so the CLR resolves and loads Puck.Assets.dll only when a capture is actually attempted, not on every
    /// produced frame.</param>
    /// <typeparam name="TState">The caller's write-argument carrier.</typeparam>
    /// <returns><see langword="true"/> on success; <see langword="false"/> when the caller should latch a flag and
    /// stop retrying a doomed load.</returns>
    public static bool TryWrite<TState>(TState state, Action<TState> writeCore) {
        try {
            writeCore(obj: state);

            return true;
        } catch (Exception exception) when ((exception is FileLoadException or FileNotFoundException or TypeLoadException or BadImageFormatException or TypeInitializationException)) {
            Console.Error.WriteLine(value: $"[capture] WARNING: Puck.Assets is unavailable ({exception.GetType().Name}: {exception.Message}) — frame capture skipped, render continues without it.");

            return false;
        }
    }
}
