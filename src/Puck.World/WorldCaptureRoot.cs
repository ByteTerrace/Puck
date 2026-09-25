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
    /// <paramref name="captures"/>' own directory (<see cref="WorldCapturesSection.ResolveDirectory"/>), which is
    /// under the run's state root unless the document names one beside itself.</summary>
    /// <param name="captures">The document's <c>captures</c> section.</param>
    /// <param name="documentDirectory">The document's directory.</param>
    /// <param name="stateRoot">The run's state root.</param>
    /// <returns>The rooted capture directory.</returns>
    public static string Resolve(WorldCapturesSection captures, string? documentDirectory, Server.WorldStateRoot stateRoot) =>
        (OverridePath ?? captures.ResolveDirectory(
            documentDirectory: documentDirectory,
            stateRoot: stateRoot.FullPath
        ));
}
