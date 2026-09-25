namespace Puck.World;

/// <summary>
/// The capture output directory a boot's <c>--capture-dir</c> names, laid over <c>captures.directory</c>: the
/// <c>--state-dir</c> pattern applied to capture output, so two backend legs of the SAME document (a cross-backend
/// parity run) can target sibling directories without two document copies. Immutable; the boot registers one and
/// every consumer takes it from the service collection, so two compositions in one process never share one.
/// </summary>
public sealed class WorldCaptureRoot {
    /// <summary>Initializes a new instance of the <see cref="WorldCaptureRoot"/> class.</summary>
    /// <param name="path">The capture output directory the boot names (created on first use), made absolute here, or
    /// <see langword="null"/> to let the document's own <c>captures</c> section decide.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    public WorldCaptureRoot(string? path) {
        if (path is not null) {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        }

        OverridePath = ((path is null)
            ? null
            : Path.GetFullPath(path: path)
        );
    }

    /// <summary>Gets the absolute capture directory the boot names, or <see langword="null"/> when the document
    /// decides.</summary>
    public string? OverridePath { get; }

    /// <summary>Resolves the effective capture directory: the boot's directory when it names one, else
    /// <paramref name="captures"/>' own directory (<see cref="WorldCapturesSection.ResolveDirectory"/>), which is
    /// under the run's state root unless the document names one beside itself.</summary>
    /// <param name="captures">The document's <c>captures</c> section.</param>
    /// <param name="documentDirectory">The document's directory.</param>
    /// <param name="stateRoot">The run's state root.</param>
    /// <returns>The rooted capture directory.</returns>
    public string Resolve(WorldCapturesSection captures, string? documentDirectory, Server.WorldStateRoot stateRoot) {
        ArgumentNullException.ThrowIfNull(argument: captures);
        ArgumentNullException.ThrowIfNull(argument: stateRoot);

        return (OverridePath ?? captures.ResolveDirectory(
            documentDirectory: documentDirectory,
            stateRoot: stateRoot.FullPath
        ));
    }
}
