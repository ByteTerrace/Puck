using Puck.Cli.Shaders;
using Puck.SdfVm;
using Puck.Shaders;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <c>puck shaders generate</c>: it owns every generated shader interface, not only <c>sdf-isa.hlsli</c>.
/// Over a tree whose includes match, the check passes; a drifted package include fails by name; an interface include
/// no package owns fails by name; a package whose include is missing fails by name; and on the real tree
/// the overlay, place and film-grain interfaces are among the includes it checks.
/// </summary>
public sealed class ShadersGenerateLawTests {
    private const string FilmGrainPath = "src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-film-grain.interface.hlsli";
    private const string IsaPath = "src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-isa.hlsli";
    private const string OverlayPath = "src/Puck.Overlays/Assets/Shaders/overlay.interface.hlsli";
    private const string PlacePath = "src/Puck.Shaders/Assets/Shaders/Graph/place.interface.hlsli";

    // Each conversion package's interface include, beside its kernel.
    private static (string Path, string Text)[] SourceIncludes => [.. RenderGraphPackageCatalog.SourceConversions.Select(selector: static id => ($"src/Puck.Shaders/Assets/Shaders/Sources/{id}.interface.hlsli", InterfaceOf(id: id)))];

    private static string InterfaceOf(string id) {
        Assert.True(condition: RenderGraphPackageCatalog.Engine.TryGet(id: id, package: out var package));

        return ShaderInterfaceHlsl.Generate(shaderInterface: ShaderPipelineParameterLayout.ForPackage(
            config: package.Config,
            members: package.Members,
            package: package.Id
        ).Interface);
    }
    // A tree holding the given files, each written with its text, checked by the verb over exactly those files.
    private static (int ExitCode, string Error) Check(params (string Path, string Text)[] files) {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-shaders-generate-");

        try {
            foreach (var (path, text) in files) {
                var full = Path.Combine(path1: root.FullName, path2: path);

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
                File.WriteAllText(contents: text, path: full);
            }

            var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => GenerateCommand.Run(
                check: true,
                files: [.. files.Select(selector: static file => file.Path)],
                packages: RenderGraphPackageCatalog.Engine,
                repositoryRoot: root.FullName
            ));

            return (exitCode, error);
        } finally {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ATreeWhoseIncludesMatchPasses() {
        var (exitCode, error) = Check([
            (IsaPath, SdfIsaHlsl.Generate()),
            (FilmGrainPath, InterfaceOf(id: RenderGraphPackageCatalog.SdfFilmGrain)),
            (OverlayPath, InterfaceOf(id: RenderGraphPackageCatalog.Overlay)),
            (PlacePath, InterfaceOf(id: RenderGraphPackageCatalog.Place)),
            .. SourceIncludes
        ]);

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Empty(collection: error.Trim());
    }
    [Fact]
    public void ADriftedPackageIncludeFailsByName() {
        var (exitCode, error) = Check([
            (IsaPath, SdfIsaHlsl.Generate()),
            (FilmGrainPath, InterfaceOf(id: RenderGraphPackageCatalog.SdfFilmGrain)),
            (OverlayPath, InterfaceOf(id: RenderGraphPackageCatalog.Overlay).Replace(comparisonType: StringComparison.Ordinal, newValue: "staleSampler", oldValue: "linearSampler")),
            (PlacePath, InterfaceOf(id: RenderGraphPackageCatalog.Place)),
            .. SourceIncludes
        ]);

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: OverlayPath);
        Assert.DoesNotContain(actualString: error, expectedSubstring: PlacePath);
    }
    [Fact]
    public void AnInterfaceIncludeNoGeneratorOwnsFailsByName() {
        var (exitCode, error) = Check(
            (IsaPath, SdfIsaHlsl.Generate()),
            (OverlayPath, InterfaceOf(id: RenderGraphPackageCatalog.Overlay)),
            (PlacePath, InterfaceOf(id: RenderGraphPackageCatalog.Place)),
            ("src/Puck.Stray/Assets/stray.interface.hlsli", "// hand-written")
        );

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: "src/Puck.Stray/Assets/stray.interface.hlsli is named as a generated interface, but no engine package owns it");
    }
    [Fact]
    public void APackageWhoseIncludeIsMissingFailsByName() {
        var (exitCode, error) = Check(
            (IsaPath, SdfIsaHlsl.Generate()),
            (OverlayPath, InterfaceOf(id: RenderGraphPackageCatalog.Overlay))
        );

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: "package 'place' declares an interface, but no place.interface.hlsli is checked in");
    }
    [Fact]
    public void OnTheTreeEveryGeneratedInterfaceIsChecked() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var listed = CliGit.Run(repositoryRoot, "ls-files", "--", "*.interface.hlsli");
        var problems = new List<string>();
        var includes = GenerateCommand.Includes(
            files: [.. listed.Stdout.Split(separator: '\n').Select(selector: static line => line.TrimEnd(trimChar: '\r')).Where(predicate: static line => (line.Length > 0))],
            packages: RenderGraphPackageCatalog.Engine,
            problems: problems
        );

        Assert.Empty(collection: problems);
        Assert.Equal(
            actual: includes.Select(selector: static include => include.Path),
            expected: [IsaPath, OverlayPath, FilmGrainPath, PlacePath, .. SourceIncludes.Select(selector: static include => include.Path)]
        );
    }
}
