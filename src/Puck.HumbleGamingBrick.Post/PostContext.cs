namespace Puck.HumbleGamingBrick.Post;

/// <summary>The shared per-run context handed to every <see cref="IPostStage"/>: the directory stages write their
/// artifacts to, and the resolved roots of the external reference-ROM and SST vector corpora (or <see langword="null"/>
/// when one is absent, in which case its Tier-B stages skip rather than fail). Tier-A stages need neither — they run
/// on a self-contained synthetic ROM.</summary>
internal sealed class PostContext {
    /// <summary>Initializes a new instance of the <see cref="PostContext"/> class.</summary>
    /// <param name="artifactsDirectory">The directory stages write artifacts to.</param>
    /// <param name="testRomRoot">The resolved reference-ROM corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="sstRoot">The resolved SingleStepTests/sm83 corpus root, or <see langword="null"/> when absent.</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public PostContext(string artifactsDirectory, string? testRomRoot, string? sstRoot = null) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        ArtifactsDirectory = artifactsDirectory;
        TestRomRoot = testRomRoot;
        SstRoot = sstRoot;
    }

    /// <summary>The directory stages write artifacts to.</summary>
    public string ArtifactsDirectory { get; }

    /// <summary>The resolved reference-ROM corpus root, or <see langword="null"/> when absent.</summary>
    public string? TestRomRoot { get; }

    /// <summary>The resolved SingleStepTests/sm83 corpus root, or <see langword="null"/> when absent.</summary>
    public string? SstRoot { get; }
}
