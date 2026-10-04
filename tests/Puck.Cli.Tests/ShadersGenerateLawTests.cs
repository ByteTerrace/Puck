using Puck.Cli.Shaders;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <c>puck shaders generate</c>: it owns every generated shader interface, not only <c>sdf-isa.hlsli</c>.
/// Over a tree whose includes match, the check passes; a drifted package include fails by name; an interface include
/// no package or engine kernel owns fails by name; a package whose include is missing fails by name; a drifted or missing
/// SDF engine kernel include fails by name; and on the real tree the overlay, place, film-grain and SDF engine interfaces,
/// and the build's shader recipe, are among the files it checks.
/// </summary>
public sealed class ShadersGenerateLawTests {
    private const string FilmGrainPath = "src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-film-grain.interface.hlsli";
    private const string IsaPath = "src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-isa.hlsli";
    private const string OverlayPath = "src/Puck.Overlays/Assets/Shaders/overlay.interface.hlsli";
    private const string PlacePath = "src/Puck.Shaders/Assets/Shaders/Graph/place.interface.hlsli";
    private const string SkyKindTablePath = $"{SdfKernelInterfaces.KernelDirectory}/sky/{SdfSkyKindsHlsl.TableFileName}";
    private const string SkyKindsPath = $"{SdfKernelInterfaces.KernelDirectory}/isa/{SdfSkyKindsHlsl.FileName}";
    private const string WorldPath = "src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-world.interface.hlsli";

    // The SDF engine kernels' includes, each with the text its interface generates, the instruction set's recorded
    // fingerprint, the indirect cache's layout, the sky's kind declarations and table, and the build's shader recipe: owned whatever the tree holds.
    private static readonly (string Path, string Text)[] EngineKernels = [
        .. SdfWorldInterfaces.Includes.Select(selector: static include => (include.Path, ShaderInterfaceHlsl.Generate(shaderInterface: include.Interface))),
        (SdfIndirectHlsl.Path, SdfIndirectHlsl.Generate()),
        (SdfIsaHlsl.FingerprintSourcePath, SdfIsaHlsl.GenerateFingerprintSource(fingerprint: SdfIsaFingerprint.Value)),
        (SkyKindsPath, SdfSkyKindsHlsl.Generate()),
        (SkyKindTablePath, SdfSkyKindsHlsl.GenerateTable()),
        (ShaderCompiler.BuildRecipePath, ShaderCompiler.GenerateBuildRecipe()),
    ];

    // Each conversion package's interface include, beside its kernel.
    private static (string Path, string Text)[] SourceIncludes => [.. RenderGraphPackageCatalog.SourceConversions.Concat(second: RenderGraphPackageCatalog.ImageConversions).Select(selector: static id => ($"src/Puck.Shaders/Assets/Shaders/Sources/{id}.interface.hlsli", InterfaceOf(id: id)))];

    private static string InterfaceOf(string id) {
        Assert.True(condition: RenderGraphPackageCatalog.Engine.TryGet(id: id, package: out var package));

        return ShaderInterfaceHlsl.Generate(shaderInterface: ShaderPipelineParameterLayout.ForPackage(
            config: package.Config,
            members: package.Members,
            package: package.Id
        ).Interface);
    }
    // A tree holding the given files and every SDF engine kernel include the files do not replace, checked by the verb.
    private static (int ExitCode, string Error) Check(params (string Path, string Text)[] files) =>
        CheckTree(files: [.. files, .. EngineKernels.Where(predicate: kernel => !files.Any(predicate: file => string.Equals(a: file.Path, b: kernel.Path, comparisonType: StringComparison.Ordinal)))]);
    // A tree holding exactly the given files, each written with its text, checked by the verb over exactly those files.
    private static (int ExitCode, string Error) CheckTree((string Path, string Text)[] files) {
        using var root = new TemporaryDirectory(prefix: "puck-shaders-generate-");

        foreach (var (path, text) in files) {
            var full = Path.Combine(path1: root.RootPath, path2: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
            File.WriteAllText(contents: text, path: full);
        }

        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => GenerateCommand.Run(
            check: true,
            files: [.. files.Select(selector: static file => file.Path)],
            packages: RenderGraphPackageCatalog.Engine,
            repositoryRoot: root.RootPath
        ));

        return (exitCode, error);
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
        Assert.Contains(actualString: error, expectedSubstring: "src/Puck.Stray/Assets/stray.interface.hlsli is named as a generated interface, but no engine package or engine kernel owns it");
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
    public void ADriftedEngineKernelIncludeFailsByName() {
        var world = EngineKernels.Single(predicate: static kernel => string.Equals(a: kernel.Path, b: WorldPath, comparisonType: StringComparison.Ordinal));
        var drifted = world.Text.Replace(comparisonType: StringComparison.Ordinal, newValue: "staleSources", oldValue: "screenSources");

        // The drift must move the text, or the law holds a checked-in interface to itself and proves nothing.
        Assert.NotEqual(actual: drifted, expected: world.Text);

        var (exitCode, error) = Check([
            (IsaPath, SdfIsaHlsl.Generate()),
            (OverlayPath, InterfaceOf(id: RenderGraphPackageCatalog.Overlay)),
            (FilmGrainPath, InterfaceOf(id: RenderGraphPackageCatalog.SdfFilmGrain)),
            (PlacePath, InterfaceOf(id: RenderGraphPackageCatalog.Place)),
            (WorldPath, drifted),
            .. SourceIncludes
        ]);

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: WorldPath);
        Assert.DoesNotContain(actualString: error, expectedSubstring: OverlayPath);
    }
    [Fact]
    public void AMissingEngineKernelIncludeFailsByName() {
        // Every include but the world kernels' is in the tree, so the missing one is the only problem.
        var (exitCode, error) = CheckTree(files: [
            (IsaPath, SdfIsaHlsl.Generate()),
            (OverlayPath, InterfaceOf(id: RenderGraphPackageCatalog.Overlay)),
            (FilmGrainPath, InterfaceOf(id: RenderGraphPackageCatalog.SdfFilmGrain)),
            (PlacePath, InterfaceOf(id: RenderGraphPackageCatalog.Place)),
            .. EngineKernels.Where(predicate: static kernel => !string.Equals(a: kernel.Path, b: WorldPath, comparisonType: StringComparison.Ordinal)),
            .. SourceIncludes,
        ]);

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: WorldPath);
        Assert.DoesNotContain(actualString: error, expectedSubstring: "brick-bake");
        Assert.DoesNotContain(actualString: error, expectedSubstring: "package '");
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AFileThatMatchesTheModelOnlyInTheWorkingTreeFailsTheCheckUntilItIsStaged(bool restoredMissingDeclaration) {
        // A kernel build writes every declaration the model changed, so after a build the working tree matches the model
        // whatever the change staged: the check holds each file to the index too.
        (string Path, string Text)[] files = [
            (IsaPath, SdfIsaHlsl.Generate()),
            (FilmGrainPath, InterfaceOf(id: RenderGraphPackageCatalog.SdfFilmGrain)),
            (OverlayPath, InterfaceOf(id: RenderGraphPackageCatalog.Overlay)),
            (PlacePath, InterfaceOf(id: RenderGraphPackageCatalog.Place)),
            .. SourceIncludes,
            .. EngineKernels,
        ];
        using var scratch = new TemporaryDirectory(prefix: "puck-shaders-generate-index-");
        var root = scratch.RootPath;
        var unstagedPath = (restoredMissingDeclaration ? IsaPath : OverlayPath);

        try {
            foreach (var (path, text) in files) {
                var full = Path.Combine(path1: root, path2: path);

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
                File.WriteAllText(contents: ((!restoredMissingDeclaration && string.Equals(a: path, b: OverlayPath, comparisonType: StringComparison.Ordinal)) ? "// stale\n" : text), path: full);
            }
            GitScratchCheckout.Initialize(repository: root);
            Assert.Equal(actual: CliGit.Run(root, "add", "-A").ExitCode, expected: 0);
            if (restoredMissingDeclaration) {
                Assert.Equal(actual: CliGit.Run(root, "rm", "-f", "--", unstagedPath).ExitCode, expected: 0);
                var problems = new List<string>();

                ShaderDeclarations.Reconcile(problems: problems, repositoryRoot: root, written: []);
                Assert.Empty(collection: problems);
            } else {
                File.WriteAllText(contents: InterfaceOf(id: RenderGraphPackageCatalog.Overlay), path: Path.Combine(path1: root, path2: OverlayPath));
            }

            (int ExitCode, string Error) Check() {
                var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => GenerateCommand.Run(
                    check: true,
                    files: [.. files.Select(selector: static file => file.Path)],
                    packages: RenderGraphPackageCatalog.Engine,
                    repositoryRoot: root
                ));

                return (exitCode, error);
            }

            var unstaged = Check();

            Assert.Equal(actual: unstaged.ExitCode, expected: 1);
            Assert.Contains(actualString: unstaged.Error, expectedSubstring: $"{unstagedPath} matches the model only in the working tree");
            Assert.DoesNotContain(actualString: unstaged.Error, expectedSubstring: PlacePath);

            Assert.Equal(actual: CliGit.Run(root, "add", "--", unstagedPath).ExitCode, expected: 0);

            var staged = Check();

            Assert.Equal(actual: staged.ExitCode, expected: 0);
            Assert.Empty(collection: staged.Error.Trim());
        } finally {
            // Git writes its objects read-only.
            foreach (var file in Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
                File.SetAttributes(fileAttributes: FileAttributes.Normal, path: file);
            }
        }
    }
    [Fact]
    public void OnTheTreeEveryGeneratedInterfaceIsChecked() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var listed = CliGit.Run(repositoryRoot, "ls-files", "--", "*.interface.hlsli");
        var tracked = listed.Stdout.Split(separator: '\n').Select(selector: static line => line.TrimEnd(trimChar: '\r')).Where(predicate: static line => (line.Length > 0)).ToList();
        var problems = new List<string>();
        var includes = GenerateCommand.Includes(
            files: tracked,
            packages: RenderGraphPackageCatalog.Engine,
            problems: problems
        );

        Assert.Empty(collection: problems);
        // The checked set is the tracked interface includes plus the six generated files that are not named
        // *.interface.hlsli: the tree and the generator's own declaration are the two sources, so a new interface
        // needs no edit here, and one the generator skips or one nobody tracked fails.
        Assert.Equal(
            actual: includes.Select(selector: static include => include.Path).Order(comparer: StringComparer.Ordinal),
            expected: tracked.Concat(second: [IsaPath, SdfIndirectHlsl.Path, SdfIsaHlsl.FingerprintSourcePath, SkyKindsPath, SkyKindTablePath, ShaderCompiler.BuildRecipePath]).Order(comparer: StringComparer.Ordinal)
        );
    }
}
