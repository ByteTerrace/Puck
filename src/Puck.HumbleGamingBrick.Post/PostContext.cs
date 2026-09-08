namespace Puck.HumbleGamingBrick.Post;

/// <summary>The shared per-run context handed to every <see cref="IPostStage{PostContext}"/>: the directory stages write their
/// artifacts to, the resolved roots of the external reference-ROM and SST vector corpora (or <see langword="null"/>
/// when one is absent, in which case its Tier-B stages skip rather than fail), and the ledger-gated stages' shared
/// state — the loaded <c>Expectations.json</c> ledger, the lane and parallelism the run measures under, whether an
/// absent asset is an infrastructure failure rather than a skip, and the sink every ledger stage reports its
/// discovery and its freshly measured rows to (from which the run's candidate ledger is built). Tier-A stages need
/// none of this — they run on a self-contained synthetic ROM.</summary>
internal sealed class PostContext {
    /// <summary>Initializes a new instance of the <see cref="PostContext"/> class.</summary>
    /// <param name="artifactsDirectory">The directory stages write artifacts to.</param>
    /// <param name="testRomRoot">The resolved reference-ROM corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="sstRoot">The resolved SingleStepTests/sm83 corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="ledger">The loaded ledger, keyed by (suite, path, model).</param>
    /// <param name="requireAssets">Whether an absent corpus asset a recorded case names is an infrastructure failure rather than a skip.</param>
    /// <param name="lane">Which ledger rows the run measures.</param>
    /// <param name="parallelism">How many cases a ledger stage measures at once; zero for one per logical processor.</param>
    /// <param name="linkRomPath">A link-capable commercial cartridge for the link-game replay stage, or <see langword="null"/> to skip it.</param>
    /// <param name="tradeRomPath">The cross-generation trade cartridge for the scripted trade stages, or <see langword="null"/> to skip them.</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public PostContext(string artifactsDirectory, string? testRomRoot, string? sstRoot = null, IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry>? ledger = null, bool requireAssets = false, PostLane lane = PostLane.All, int parallelism = 0, string? linkRomPath = null, string? tradeRomPath = null) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        ArtifactsDirectory = artifactsDirectory;
        Lane = lane;
        LinkRomPath = linkRomPath;
        TradeRomPath = tradeRomPath;
        Ledger = (ledger ?? new Dictionary<(string, string, string), LedgerEntry>());
        Parallelism = ((parallelism > 0)
            ? parallelism
            : Environment.ProcessorCount);
        RequireAssets = requireAssets;
        SstRoot = sstRoot;
        TestRomRoot = testRomRoot;
    }

    /// <summary>Gets the directory stages write artifacts to.</summary>
    public string ArtifactsDirectory { get; }
    /// <summary>Gets which ledger rows the run measures.</summary>
    public PostLane Lane { get; }
    /// <summary>Gets the loaded <c>Expectations.json</c> ledger, keyed by (suite, path, model).</summary>
    public IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry> Ledger { get; }
    /// <summary>Gets the link-capable commercial cartridge the link-game replay stage boots, or <see langword="null"/> to skip it.</summary>
    public string? LinkRomPath { get; }
    /// <summary>Gets the sink every ledger stage reports its discovery and measured rows to.</summary>
    public LedgerMeasurements Measurements { get; } = new();
    /// <summary>Gets how many cases a ledger stage measures at once.</summary>
    public int Parallelism { get; }
    /// <summary>Gets a value indicating whether an absent corpus asset a recorded case names is an infrastructure
    /// failure (exit 2) rather than a skip.</summary>
    public bool RequireAssets { get; }
    /// <summary>Gets the resolved SingleStepTests/sm83 corpus root, or <see langword="null"/> when absent.</summary>
    public string? SstRoot { get; }
    /// <summary>Gets the resolved reference-ROM corpus root, or <see langword="null"/> when absent.</summary>
    public string? TestRomRoot { get; }
    /// <summary>Gets the cross-generation trade cartridge the scripted trade stages boot, or <see langword="null"/> to skip them.</summary>
    public string? TradeRomPath { get; }
}
