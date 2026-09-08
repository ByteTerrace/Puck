namespace Puck.World;

/// <summary>
/// The boot-time override for <c>captures.directory</c> — the <c>--state-dir</c> pattern applied to capture output:
/// a developer/deployment reflection, needed so two backend legs of the SAME document (a cross-backend parity run)
/// can target sibling directories without two document copies.
/// </summary>
internal static class WorldCaptureRoot {
    private static string? OverridePath;

    /// <summary>Applies the boot-time override. Call at most once, before <see cref="WorldCaptureScheduler"/> is
    /// constructed.</summary>
    /// <param name="path">The capture output directory (created on first use).</param>
    /// <exception cref="InvalidOperationException">An override was already applied.</exception>
    public static void Override(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        if (OverridePath is not null) {
            throw new InvalidOperationException(message: "the capture directory was already overridden this boot");
        }

        OverridePath = Path.GetFullPath(path: path);
    }
    /// <summary>Resolves the effective capture directory: the boot override when present, else
    /// <paramref name="authored"/> resolved against the current directory.</summary>
    /// <param name="authored">The document's <c>captures.directory</c>.</param>
    public static string Resolve(string authored) =>
        (OverridePath ?? Path.GetFullPath(path: authored));
}
