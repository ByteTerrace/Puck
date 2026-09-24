namespace Puck.Assets.Documents;

/// <summary>
/// The one spelling of a name/source/hash asset reference row's family — <c>puck.music.v1</c>, <c>puck.table.v1</c>,
/// <c>puck.tune.v1</c>, and <c>puck.synthesizer-patch.v1</c> — read by every project that names one: the row-kind
/// noun a load or hash-verify refusal quotes (<c>Puck.World.WorldAssetRowLoader</c>, <c>Puck.World.Server</c>'s
/// mutation composition), and the official manifest's <c>OfficialAssetEntry.Family</c> tag,
/// which admits exactly these four (<c>Puck.Launcher.Release.OfficialCanonicalizer</c>,
/// <c>Puck.Cli.Official.OfficialAssetResolver</c>). This is the
/// lowest project a row-declaring project (<c>Puck.World.Schema</c>) and the release/packaging projects
/// (<c>Puck.Launcher</c>, <c>Puck.Cli</c>) both already reference, so the four names are spelled once here instead
/// of once per consumer.
/// </summary>
public static class AssetRowFamilies {
    /// <summary>A <c>WorldMusicRow</c> (<c>puck.music.v1</c>).</summary>
    public const string Music = "music";
    /// <summary>A <c>WorldPatch</c> row (<c>puck.synthesizer-patch.v1</c>).</summary>
    public const string Patch = "patch";
    /// <summary>A <c>TableRow</c> (<c>puck.table.v1</c>).</summary>
    public const string Table = "table";
    /// <summary>A <c>WorldTune</c> row (<c>puck.tune.v1</c>).</summary>
    public const string Tune = "tune";

    /// <summary>Every family, the set an official manifest entry's family must belong to.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(comparer: StringComparer.Ordinal) { Music, Patch, Table, Tune };
}
