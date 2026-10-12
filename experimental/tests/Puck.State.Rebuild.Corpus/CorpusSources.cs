using Xunit;

namespace Puck.State.Rebuild.Corpus;

/// <summary>The one enumeration of the author-expression corpus: every <c>.puck</c> world this repository ships — the
/// worlds under <c>src/Puck.World/Assets/worlds</c> and the asset packages under <c>worlds/</c> — and every
/// <c>.puck</c> fixture the transpiler owns.</summary>
/// <remarks>A corpus member is addressed by its repository-relative forward-slashed path, which is also the theory
/// name a failure reports. Sources are read where they live, so one added to any tree joins the gate with no edit
/// here.</remarks>
public static class CorpusSources {
    private const string PackageDirectory = "worlds";

    /// <summary>The sources the round trip does not close on, each with the reason it cannot. The ledger only shrinks:
    /// <c>ExemptSourceStillFailsForItsReason</c> fails when an exemption stops being necessary, and a source that newly
    /// fails the round trip is a defect to fix, not a row to add.</summary>
    public static readonly IReadOnlyDictionary<string, CorpusExemption> RoundTripExemptions = new Dictionary<string, CorpusExemption>(comparer: StringComparer.Ordinal) {
        ["src/Puck.World/Assets/worlds/moth-courtyard.puck"] = CorpusExemption.DecompiledPastTheSourceLimit,
        ["worlds/genesis/cards.basis.puck"] = CorpusExemption.DecompiledPastTheSourceLimit,
    };

    /// <summary>Returns every source the round trip closes on, as xUnit theory data: the corpus and the asset packages,
    /// less the exemptions.</summary>
    /// <returns>One row per source, each a repository-relative forward-slashed path.</returns>
    public static TheoryData<string> All() =>
        new(values: Paths().Concat(second: Packages()).Where(predicate: path => !RoundTripExemptions.ContainsKey(key: path)));
    /// <summary>Returns the paths of <see cref="RoundTripExemptions"/> as xUnit theory data.</summary>
    /// <returns>One row per exempt source.</returns>
    public static TheoryData<string> Exempt() => new(values: RoundTripExemptions.Keys.Order(comparer: StringComparer.Ordinal));
    /// <summary>Returns every source of the asset packages under <c>worlds/</c>: each package's roots, its
    /// compositions, and the module fragments they import.</summary>
    /// <returns>Repository-relative forward-slashed paths, in ordinal order.</returns>
    public static IEnumerable<string> Packages() =>
        Enumerate(
            directory: PackageDirectory,
            option: SearchOption.AllDirectories
        );
}
