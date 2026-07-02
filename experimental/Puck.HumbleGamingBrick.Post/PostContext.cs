namespace Puck.HumbleGamingBrick.Post;

/// <summary>The shared per-run context handed to every <see cref="IPostStage"/>: the directory stages write their
/// artifacts to, and the resolved root of the external reference-ROM corpus (or <see langword="null"/> when it is
/// absent, in which case Tier-B stages skip rather than fail). Tier-A stages need neither — they run on a self-contained
/// synthetic ROM.</summary>
internal sealed class PostContext {
    /// <summary>Initializes a new instance of the <see cref="PostContext"/> class.</summary>
    /// <param name="artifactsDirectory">The directory stages write artifacts to.</param>
    /// <param name="testRomRoot">The resolved reference-ROM corpus root, or <see langword="null"/> when absent.</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public PostContext(string artifactsDirectory, string? testRomRoot) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        ArtifactsDirectory = artifactsDirectory;
        TestRomRoot = testRomRoot;
    }

    /// <summary>The directory stages write artifacts to.</summary>
    public string ArtifactsDirectory { get; }

    /// <summary>The resolved reference-ROM corpus root, or <see langword="null"/> when absent.</summary>
    public string? TestRomRoot { get; }
}
