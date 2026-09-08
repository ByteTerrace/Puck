namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The shared per-run context handed to every <see cref="IPostStage{PostContext}"/>: the directory stages write their
/// artifacts to, the resolved roots of the external corpora and the commercial-ROM directory (each may be
/// <see langword="null"/> when absent, in which case the stages that need it skip rather than fail), the per-run
/// commercial cartridges named on the command line, the machine BIOS image every built machine boots with, and how
/// many cases a corpus stage measures at once. Tier-A stages need none of the ROM roots — they run on hand-assembled
/// vectors and a self-contained synthetic cartridge.</summary>
internal sealed class PostContext {
    /// <summary>Initializes a new instance of the <see cref="PostContext"/> class.</summary>
    /// <param name="artifactsDirectory">The directory stages write artifacts to.</param>
    /// <param name="testRomRoot">The resolved reference conformance-corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="fuzzRoot">The resolved ARM/Thumb fuzz-corpus root, or <see langword="null"/> when absent.</param>
    /// <param name="gamesRoot">The resolved commercial-ROM directory (render-hash floors), or <see langword="null"/> when absent.</param>
    /// <param name="biosImage">The BIOS image every built machine boots with (a zeroed 16&#160;KiB stub when no replacement BIOS was supplied).</param>
    /// <param name="accuracySuitePath">The accuracy-suite ROM, or <see langword="null"/> to skip its stage.</param>
    /// <param name="agsPath">The AGS aging-cartridge ROM, or <see langword="null"/> to skip its stage.</param>
    /// <param name="linkGamePath">A commercial multiplayer cartridge for the link-game replay, or <see langword="null"/> to skip it.</param>
    /// <param name="solarRomPath">A commercial solar-sensor cartridge for the solar replay, or <see langword="null"/> to skip it.</param>
    /// <param name="parallelism">How many cases a corpus stage measures at once; zero for one per logical processor.</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public PostContext(string artifactsDirectory, string? testRomRoot, string? fuzzRoot, string? gamesRoot, ReadOnlyMemory<byte> biosImage, string? accuracySuitePath = null, string? agsPath = null, string? linkGamePath = null, string? solarRomPath = null, int parallelism = 0) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        AccuracySuitePath = accuracySuitePath;
        AgsPath = agsPath;
        ArtifactsDirectory = artifactsDirectory;
        BiosImage = biosImage;
        FuzzRoot = fuzzRoot;
        GamesRoot = gamesRoot;
        LinkGamePath = linkGamePath;
        Parallelism = ((parallelism > 0)
            ? parallelism
            : Environment.ProcessorCount);
        SolarRomPath = solarRomPath;
        TestRomRoot = testRomRoot;
    }

    /// <summary>Gets the accuracy-suite ROM, or <see langword="null"/> to skip its stage.</summary>
    public string? AccuracySuitePath { get; }
    /// <summary>Gets the AGS aging-cartridge ROM, or <see langword="null"/> to skip its stage.</summary>
    public string? AgsPath { get; }
    /// <summary>Gets the directory stages write artifacts to.</summary>
    public string ArtifactsDirectory { get; }
    /// <summary>Gets the BIOS image every built machine boots with; a zeroed 16&#160;KiB stub when no replacement BIOS was
    /// supplied (the stages that genuinely need a real BIOS check for it and skip when only the stub is present).</summary>
    public ReadOnlyMemory<byte> BiosImage { get; }
    /// <summary>Gets the resolved ARM/Thumb fuzz-corpus root, or <see langword="null"/> when absent.</summary>
    public string? FuzzRoot { get; }
    /// <summary>Gets the resolved commercial-ROM directory (for render-hash floors), or <see langword="null"/> when absent.</summary>
    public string? GamesRoot { get; }
    /// <summary>Gets the commercial multiplayer cartridge for the link-game replay, or <see langword="null"/> to skip it.</summary>
    public string? LinkGamePath { get; }
    /// <summary>Gets how many cases a corpus stage measures at once.</summary>
    public int Parallelism { get; }
    /// <summary>Gets the commercial solar-sensor cartridge for the solar replay, or <see langword="null"/> to skip it.</summary>
    public string? SolarRomPath { get; }
    /// <summary>Gets the resolved reference conformance-corpus root, or <see langword="null"/> when absent.</summary>
    public string? TestRomRoot { get; }
}
