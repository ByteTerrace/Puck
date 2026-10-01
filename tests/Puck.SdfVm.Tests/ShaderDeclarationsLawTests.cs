using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="ShaderDeclarations"/>, the one list of HLSL declarations the C# model owns, as the kernel builds
/// run it: on the tree its interface files are owned with no problem and every declaration matches its checked-in file,
/// and over a tree holding every declaration it names exactly the one that drifted, rewriting it only when asked (a
/// continuous-integration build only names it) and leaving every other file's bytes and time untouched, so an unchanged
/// declaration recompiles no kernel.
/// </summary>
public sealed class ShaderDeclarationsLawTests {
    [Fact]
    public void OnTheTreeEveryDeclarationIsOwnedAndCurrent() {
        var root = RepositoryPaths.RequireRoot();
        var problems = new List<string>();
        var declarations = ShaderDeclarations.Of(files: ShaderDeclarations.InterfaceFiles(repositoryRoot: root), packages: RenderGraphPackageCatalog.Engine, problems: problems);

        Assert.Empty(collection: problems);
        Assert.Contains(collection: declarations, filter: static declaration => declaration.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: SdfIsaHlsl.FileName));
        Assert.Contains(collection: declarations, filter: static declaration => string.Equals(a: declaration.Path, b: SdfIsaHlsl.FingerprintSourcePath, comparisonType: StringComparison.Ordinal));

        foreach (var declaration in declarations) {
            Assert.Equal(
                actual: File.ReadAllText(path: Path.Combine(path1: root, path2: declaration.Path)).ReplaceLineEndings(replacementText: "\n"),
                expected: declaration.Generate()
            );
        }
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AReconcileNamesOnlyTheDriftedDeclarationAndWritesItOnlyWhenAsked(bool write) {
        var source = RepositoryPaths.RequireRoot();
        var declarations = ShaderDeclarations.Of(files: ShaderDeclarations.InterfaceFiles(repositoryRoot: source), packages: RenderGraphPackageCatalog.Engine, problems: []);
        var root = Directory.CreateTempSubdirectory(prefix: "puck-shader-declarations-");

        try {
            var settled = new DateTime(day: 1, hour: 0, kind: DateTimeKind.Utc, minute: 0, month: 1, second: 0, year: 2000);

            foreach (var declaration in declarations) {
                var path = Path.Combine(path1: root.FullName, path2: declaration.Path);

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
                File.WriteAllText(contents: declaration.Generate().ReplaceLineEndings(replacementText: "\r\n"), path: path);
                File.SetLastWriteTimeUtc(lastWriteTimeUtc: settled, path: path);
            }

            var drifted = declarations.Single(predicate: static declaration => declaration.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: SdfIsaHlsl.FileName));
            var driftedPath = Path.Combine(path1: root.FullName, path2: drifted.Path);
            var driftedText = (drifted.Generate() + "// drift\n");

            File.WriteAllText(contents: driftedText, path: driftedPath);

            var differing = new List<string>();
            var problems = new List<string>();

            ShaderDeclarations.Reconcile(differing: differing, problems: problems, repositoryRoot: root.FullName, write: write);

            Assert.Empty(collection: problems);
            Assert.Equal(actual: differing, expected: [drifted.Path]);
            Assert.Equal(actual: File.ReadAllText(path: driftedPath), expected: (write ? drifted.Generate() : driftedText));

            foreach (var declaration in declarations.Where(predicate: declaration => !ReferenceEquals(objA: declaration, objB: drifted))) {
                Assert.Equal(actual: File.GetLastWriteTimeUtc(path: Path.Combine(path1: root.FullName, path2: declaration.Path)), expected: settled);
            }
        } finally {
            root.Delete(recursive: true);
        }
    }
}
