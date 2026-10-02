namespace Puck.World.Server;

/// <summary>What put the journal base where it is.</summary>
public enum WorldBaseOriginKind : byte {
    /// <summary>The document the activation booted on.</summary>
    Boot,
    /// <summary>The last <c>world.save</c>, which compacted the journal into the base.</summary>
    Save,
    /// <summary>A <c>world.load</c> of a document file.</summary>
    Load,
    /// <summary>A <c>world.reload</c> of a document file.</summary>
    Reload,
    /// <summary>The journal depth horizon folding the oldest entries into the base.</summary>
    JournalHorizon,
}
/// <summary>Where the journal base came from — what <c>world.reset</c> names when it lands on the base.</summary>
/// <param name="Kind">What put the base there.</param>
/// <param name="Path">The document file a load or reload read, or <see langword="null"/> for every other kind. In
/// memory it is the path the rebuild read; a checkpoint writes it relative to the world's document directory.</param>
/// <param name="Depth">The <c>host.journalDepth</c> a horizon fold held to, or zero for every other kind.</param>
public readonly record struct WorldBaseOrigin(WorldBaseOriginKind Kind, string? Path = null, int Depth = 0) {
    /// <summary>Gets the origin of a boot document.</summary>
    public static WorldBaseOrigin Boot { get; } = new(Kind: WorldBaseOriginKind.Boot);

    /// <summary>Returns the origin as a builder reads it.</summary>
    /// <returns>The description.</returns>
    public override string ToString() => (Kind switch {
        WorldBaseOriginKind.Save => "the last world.save",
        WorldBaseOriginKind.Load => $"'{Path}' (world.load)",
        WorldBaseOriginKind.Reload => $"'{Path}' (world.reload)",
        WorldBaseOriginKind.JournalHorizon => $"the journal depth horizon (host.journalDepth {Depth})",
        _ => "the boot document",
    });
}
