namespace Puck.HumbleGamingBrick.Post;

/// <summary>The shared per-run context handed to every <see cref="IPostStage{PostContext}"/>: the directory stages write their
/// artifacts to, the resolved roots of the external reference-ROM and SST vector corpora (or <see langword="null"/>
/// when one is absent, in which case its Tier-B stages skip rather than fail), the parallelism corpus stages run
/// their cases under, and whether an absent asset is an infrastructure failure rather than a skip. Tier-A stages need
/// none of this — they run on a self-contained synthetic ROM.</summary>
internal sealed class PostContext {
    /// <summary>Initializes a new instance of the <see cref="PostContext"/> class.</summary>
    /// <param name="artifactsDirectory">The directory stages write artifacts to.</param>
    /// <param name="testRomRoot">The resolved reference-ROM corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="sstRoot">The resolved SingleStepTests/sm83 corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="requireAssets">Whether an absent corpus asset is an infrastructure failure rather than a skip.</param>
    /// <param name="parallelism">How many cases a corpus stage runs at once; zero for one per logical processor.</param>
    /// <param name="linkRomPath">A link-capable commercial cartridge for the link-game replay stage, or <see langword="null"/> to skip it.</param>
    /// <param name="tradeRomPath">The cross-generation trade cartridge for the scripted trade stages, or <see langword="null"/> to skip them.</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public PostContext(string artifactsDirectory, string? testRomRoot, string? sstRoot = null, bool requireAssets = false, int parallelism = 0, string? linkRomPath = null, string? tradeRomPath = null) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        ArtifactsDirectory = artifactsDirectory;
        LinkRomPath = linkRomPath;
        TradeRomPath = tradeRomPath;
        Parallelism = ((parallelism > 0)
            ? parallelism
            : Environment.ProcessorCount
        );
        RequireAssets = requireAssets;
        SstRoot = sstRoot;
        TestRomRoot = testRomRoot;
    }

    /// <summary>Gets the directory stages write artifacts to.</summary>
    public string ArtifactsDirectory { get; }
    /// <summary>Gets the link-capable commercial cartridge the link-game replay stage boots, or <see langword="null"/> to skip it.</summary>
    public string? LinkRomPath { get; }
    /// <summary>Gets how many cases a corpus stage runs at once.</summary>
    public int Parallelism { get; }
    /// <summary>Gets a value indicating whether an absent corpus asset is an infrastructure failure (exit 2) rather
    /// than a skip.</summary>
    public bool RequireAssets { get; }
    /// <summary>Gets the resolved SingleStepTests/sm83 corpus root, or <see langword="null"/> when absent.</summary>
    public string? SstRoot { get; }
    /// <summary>Gets the resolved reference-ROM corpus root, or <see langword="null"/> when absent.</summary>
    public string? TestRomRoot { get; }
    /// <summary>Gets the cross-generation trade cartridge the scripted trade stages boot, or <see langword="null"/> to skip them.</summary>
    public string? TradeRomPath { get; }
}
