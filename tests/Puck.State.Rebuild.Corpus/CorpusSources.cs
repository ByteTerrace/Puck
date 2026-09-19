using Xunit;

namespace Puck.State.Rebuild.Corpus;

/// <summary>The one enumeration of the author-expression corpus: every <c>.puck</c> world this repository ships and
/// every <c>.puck</c> fixture the transpiler owns.</summary>
/// <remarks>A corpus member is addressed by its repository-relative forward-slashed path, which is also the theory
/// name a failure reports. Sources are read where they live, so one added to either tree joins the gate with no edit
/// here.</remarks>
public static class CorpusSources {
    private const string FixtureDirectory = "src/Puck.World.Transpiler/Samples";
    private const string WorldDirectory = "src/Puck.World/Assets/worlds";

    /// <summary>Sources whose decompiled form is longer than the parser accepts, so the round trip cannot close on
    /// them. They stay under the document gate. <c>ExcludedSourceDecompilesPastTheParserSourceLimit</c> fails when an
    /// exclusion stops being necessary.</summary>
    public static readonly IReadOnlyList<string> RoundTripExclusions = [
        "src/Puck.World/Assets/worlds/moth-courtyard.puck",
    ];

    private static IEnumerable<string> Enumerate(string directory, SearchOption option) {
        var root = FindRepositoryRoot();

        return Directory.GetFiles(
            path: Path.Combine(
                path1: root,
                path2: directory
            ),
            searchOption: option,
            searchPattern: "*.puck"
        )
            .Select(selector: path => Path.GetRelativePath(
                path: path,
                relativeTo: root
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            ))
            .Order(comparer: StringComparer.Ordinal);
    }

    /// <summary>Returns every source the round trip closes on, as xUnit theory data.</summary>
    /// <returns>One row per source, each a repository-relative forward-slashed path.</returns>
    public static TheoryData<string> All() =>
        new(values: Paths().Where(predicate: path => !RoundTripExclusions.Contains(value: path)));
    /// <summary>Returns <see cref="RoundTripExclusions"/> as xUnit theory data.</summary>
    /// <returns>One row per excluded source.</returns>
    public static TheoryData<string> Excluded() => new(values: RoundTripExclusions);
    /// <summary>Returns the absolute path of the directory holding <c>Puck.slnx</c>, walked up from the test
    /// runner's own directory.</summary>
    /// <returns>The repository root.</returns>
    /// <exception cref="DirectoryNotFoundException">The walk reached the filesystem root.</exception>
    public static string FindRepositoryRoot() {
        var dir = AppContext.BaseDirectory;

        while (dir is not null) {
            if (File.Exists(path: Path.Combine(
                path1: dir,
                path2: "Puck.slnx"
            ))) {
                return dir;
            }

            dir = Path.GetDirectoryName(path: dir);
        }

        throw new DirectoryNotFoundException(message: "Could not locate the repository root (the directory holding Puck.slnx) from the test runner.");
    }
    /// <summary>Returns every source in the corpus: the shipped worlds recursively, then the transpiler fixtures.
    /// Fixture subdirectories hold module fragments, which carry no <c>schema</c> and are not root documents.</summary>
    /// <returns>Repository-relative forward-slashed paths, each tree in ordinal order.</returns>
    public static IEnumerable<string> Paths() =>
        Enumerate(
            directory: WorldDirectory,
            option: SearchOption.AllDirectories
        ).Concat(second: Enumerate(
            directory: FixtureDirectory,
            option: SearchOption.TopDirectoryOnly
        ));
    /// <summary>Resolves a corpus path against the repository root.</summary>
    /// <param name="relativePath">A repository-relative forward-slashed path.</param>
    /// <returns>The absolute path.</returns>
    public static string Resolve(string relativePath) =>
        Path.Combine(
            path1: FindRepositoryRoot(),
            path2: relativePath
        );
    /// <summary>Returns every shipped world source, as xUnit theory data.</summary>
    /// <returns>One row per world source, each a repository-relative forward-slashed path.</returns>
    public static TheoryData<string> Worlds() =>
        new(values: Enumerate(
            directory: WorldDirectory,
            option: SearchOption.AllDirectories
        ));
}
