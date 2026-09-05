namespace Puck.HumbleGamingBrick.Post;

/// <summary>The shared per-run context handed to every <see cref="IPostStage{PostContext}"/>: the directory stages write their
/// artifacts to, the resolved roots of the external reference-ROM and SST vector corpora (or <see langword="null"/>
/// when one is absent, in which case its Tier-B stages skip rather than fail), and the ledger-gated stages' shared
/// state — the loaded <c>Expectations.json</c> ledger, whether an absent asset is an infrastructure failure rather
/// than a skip, and (under <c>--record</c>) the sink every ledger stage appends its freshly measured entries to.
/// Tier-A stages need none of this — they run on a self-contained synthetic ROM.</summary>
internal sealed class PostContext {
    /// <summary>Initializes a new instance of the <see cref="PostContext"/> class.</summary>
    /// <param name="artifactsDirectory">The directory stages write artifacts to.</param>
    /// <param name="testRomRoot">The resolved reference-ROM corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="sstRoot">The resolved SingleStepTests/sm83 corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="ledger">The loaded ledger, keyed by (suite, path, model); empty when <paramref name="recordMode"/> is set.</param>
    /// <param name="recordMode">Whether ledger stages should measure and record actual outcomes instead of comparing against the ledger.</param>
    /// <param name="requireAssets">Whether an absent corpus asset a recorded case names is an infrastructure failure rather than a skip.</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public PostContext(string artifactsDirectory, string? testRomRoot, string? sstRoot = null, IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry>? ledger = null, bool recordMode = false, bool requireAssets = false) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        ArtifactsDirectory = artifactsDirectory;
        Ledger = (ledger ?? new Dictionary<(string, string, string), LedgerEntry>());
        RecordMode = recordMode;
        RequireAssets = requireAssets;
        SstRoot = sstRoot;
        TestRomRoot = testRomRoot;
    }

    /// <summary>The directory stages write artifacts to.</summary>
    public string ArtifactsDirectory { get; }
    /// <summary>The loaded <c>Expectations.json</c> ledger, keyed by (suite, path, model).</summary>
    public IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry> Ledger { get; }
    /// <summary>Gets a value indicating whether ledger stages should measure and record actual outcomes instead of
    /// gating against <see cref="Ledger"/>. Every ledger stage's recorded entries land in <see cref="RecordedEntries"/>.</summary>
    public bool RecordMode { get; }
    /// <summary>Under <c>--record</c>, every ledger stage's freshly measured entries, appended in stage-run order;
    /// <see cref="ExpectationsLedger.Save"/> sorts them before writing.</summary>
    public List<LedgerEntry> RecordedEntries { get; } = [];
    /// <summary>Gets a value indicating whether an absent corpus asset a recorded case names is an infrastructure
    /// failure (exit 2) rather than a skip.</summary>
    public bool RequireAssets { get; }
    /// <summary>The resolved SingleStepTests/sm83 corpus root, or <see langword="null"/> when absent.</summary>
    public string? SstRoot { get; }
    /// <summary>The resolved reference-ROM corpus root, or <see langword="null"/> when absent.</summary>
    public string? TestRomRoot { get; }
}
