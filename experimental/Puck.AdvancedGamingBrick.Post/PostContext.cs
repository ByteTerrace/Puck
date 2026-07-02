namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The shared per-run context handed to every <see cref="IPostStage"/>: the directory stages write their
/// artifacts to, the resolved roots of the external reference-ROM corpus and the commercial-ROM directory (either may be
/// <see langword="null"/> when absent, in which case the stages that need them skip rather than fail), and the machine
/// BIOS image every built machine boots with. Tier-A stages need none of the ROM roots — they run on hand-assembled
/// vectors and a self-contained synthetic cartridge.</summary>
internal sealed class PostContext {
    /// <summary>Initializes a new instance of the <see cref="PostContext"/> class.</summary>
    /// <param name="artifactsDirectory">The directory stages write artifacts to.</param>
    /// <param name="testRomRoot">The resolved reference-ROM corpus root (jsmolka gba-tests), or <see langword="null"/> when absent.</param>
    /// <param name="gamesRoot">The resolved commercial-ROM directory (render-hash floors), or <see langword="null"/> when absent.</param>
    /// <param name="biosImage">The BIOS image every built machine boots with (a zeroed 16&#160;KiB stub when no replacement BIOS was supplied).</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public PostContext(string artifactsDirectory, string? testRomRoot, string? gamesRoot, ReadOnlyMemory<byte> biosImage) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        ArtifactsDirectory = artifactsDirectory;
        BiosImage = biosImage;
        GamesRoot = gamesRoot;
        TestRomRoot = testRomRoot;
    }

    /// <summary>The directory stages write artifacts to.</summary>
    public string ArtifactsDirectory { get; }

    /// <summary>The BIOS image every built machine boots with; a zeroed 16&#160;KiB stub when no replacement BIOS was
    /// supplied (the stages that genuinely need a real BIOS check for it and skip when only the stub is present).</summary>
    public ReadOnlyMemory<byte> BiosImage { get; }

    /// <summary>The resolved commercial-ROM directory (for render-hash floors), or <see langword="null"/> when absent.</summary>
    public string? GamesRoot { get; }

    /// <summary>The resolved reference-ROM corpus root (jsmolka gba-tests), or <see langword="null"/> when absent.</summary>
    public string? TestRomRoot { get; }
}
