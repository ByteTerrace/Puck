using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// A toolchain's identity, which every shader cache key includes, is the content of the <c>dxc</c> it runs and the
/// compiler and validator libraries in its standard release layout, never where they are installed or when their files were
/// written: a cache restored or extracted onto another machine is a hit for the same toolchain and a miss for any other.
/// A toolchain file that cannot be read refuses by name.
/// </summary>
public sealed class ShaderToolchainLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task AToolchainChangedAfterPlanningCannotPublishUnderThePlannedKey(bool duringCompile) {
        using var scratch = new TemporaryDirectory(prefix: "puck-toolchain-");
        var bin = Install(compiler: 6, root: scratch.RootPath, validator: 7, written: DateTime.UtcNow);
        var tool = Path.Combine(path1: bin, path2: DxcName);
        var runner = new ReplacingRunner(replace: () => File.WriteAllBytes(bytes: [1, 2, 9], path: tool));
        var compiler = new ShaderCompiler(cacheDirectory: Path.Combine(path1: scratch.RootPath, path2: "cache"), processRunner: runner, toolchainDirectory: bin);
        var plan = compiler.Plan(stage: new ShaderStageSource(Stage: ShaderStage.Compute, Path: Path.Combine(path1: scratch.RootPath, path2: "a.hlsl"), Source: "void main() {}"), target: ShaderTarget.Spirv);

        if (!duringCompile) { File.WriteAllBytes(bytes: [1, 2, 8], path: tool); }
        var failure = await Assert.ThrowsAsync<IOException>(testCode: () => compiler.CompileOutputAsync(plan: plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedSubstring: "toolchain changed", actualString: failure.Message);
        Assert.Equal(expected: (duringCompile ? 1 : 0), actual: runner.Runs);
        Assert.Null(@object: compiler.ReadCached(plan: plan));
        Assert.Empty(collection: Directory.EnumerateFiles(path: compiler.CacheDirectory, searchPattern: "*.spv"));
    }

    private sealed class ReplacingRunner(Action replace) : IShaderProcessRunner {
        public int Runs { get; private set; }

        public Task<Puck.Hosting.ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            Runs++;
            replace();
            var output = (arguments.ToList().IndexOf(item: "-Fo") + 1);

            File.WriteAllBytes(bytes: [1, 2, 3], path: arguments[output]);
            return Task.FromResult(result: new Puck.Hosting.ChildProcessResult(ExitCode: 0, Stdout: "", Stderr: ""));
        }
    }

    [InlineData("dxc")]
    [InlineData("compiler")]
    [InlineData("validator")]
    [Theory]
    public void ReplacingToolchainBytesInvalidatesIdentityEvenWhenLengthAndWriteTimeStayTheSame(string part) {
        using var scratch = new TemporaryDirectory(prefix: "puck-toolchain-");
        var bin = Install(compiler: 6, root: scratch.RootPath, validator: 7, written: DateTime.UtcNow);
        var toolchain = new ShaderToolchain(directory: bin);
        var before = toolchain.Identity;
        var file = part switch {
            "dxc" => Path.Combine(path1: bin, path2: DxcName),
            "compiler" => Path.Combine(path1: LibrariesOf(bin: bin), path2: CompilerName),
            _ => Path.Combine(path1: LibrariesOf(bin: bin), path2: ValidatorName),
        };
        var written = File.GetLastWriteTimeUtc(path: file);
        var bytes = File.ReadAllBytes(path: file);

        bytes[^1]++;
        File.WriteAllBytes(bytes: bytes, path: file);
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: written, path: file);

        Assert.NotEqual(expected: before, actual: toolchain.Identity);
    }
    [Fact]
    public void ACommandWithoutDirectorySeparatorsRetainsTheRequestedNameWhenAbsent() {
        const string Command = "puck-absent-dxc";
        var toolchain = ShaderToolchain.OfCommand(command: Command);

        Assert.Null(@object: toolchain.Locate(name: ShaderCompiler.DxcTool));
        Assert.Equal(expected: Command, actual: toolchain.Resolve(name: ShaderCompiler.DxcTool));
    }
    [Fact]
    public void AToolOnTheSearchPathRunsTheExactExecutableThatLocateIdentifies() {
        var toolchain = new ShaderToolchain();
        var located = toolchain.Locate(name: "dotnet");

        Assert.NotNull(@object: located);
        Assert.True(condition: Path.IsPathFullyQualified(path: located));
        Assert.Equal(expected: located, actual: toolchain.Resolve(name: "dotnet"));
        Assert.Equal(expected: located, actual: ShaderToolchain.OfCommand(command: "dotnet").Resolve(name: ShaderCompiler.DxcTool));
    }
    [Fact]
    public void TheIdentityIsTheToolchainsContentNotWhereOrWhenItWasInstalled() {
        using var scratch = new TemporaryDirectory(prefix: "puck-toolchain-");
        var early = new DateTime(day: 1, hour: 0, kind: DateTimeKind.Utc, minute: 0, month: 1, second: 0, year: 2020);
        var late = new DateTime(day: 29, hour: 12, kind: DateTimeKind.Utc, minute: 0, month: 9, second: 0, year: 2026);

        var one = IdentityOf(compiler: 6, root: Path.Combine(path1: scratch.RootPath, path2: "one"), validator: 7, written: early);
        var moved = IdentityOf(compiler: 6, root: Path.Combine(path1: scratch.RootPath, path2: "elsewhere", path3: "extracted"), validator: 7, written: late);
        var otherCompiler = IdentityOf(compiler: 9, root: Path.Combine(path1: scratch.RootPath, path2: "other-compiler"), validator: 7, written: early);
        var otherValidator = IdentityOf(compiler: 6, root: Path.Combine(path1: scratch.RootPath, path2: "other-validator"), validator: 9, written: early);

        Assert.Equal(actual: moved, expected: one);
        Assert.NotEqual(actual: otherCompiler, expected: one);
        Assert.NotEqual(actual: otherValidator, expected: one);
        Assert.NotEqual(actual: otherValidator, expected: otherCompiler);
    }
    [Fact]
    public void AToolchainFileThatCannotBeReadRefusesByName() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "Only Windows enforces an exclusive file lock on other readers.");

        using var scratch = new TemporaryDirectory(prefix: "puck-toolchain-");
        var bin = Install(compiler: 6, root: scratch.RootPath, validator: 7, written: DateTime.UtcNow);
        var library = Path.Combine(path1: LibrariesOf(bin: bin), path2: CompilerName);
        IOException? refused = null;

        using (new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Open, path: library, share: FileShare.None)) {
            try {
                _ = new ShaderToolchain(directory: bin).Identity;
            } catch (IOException exception) {
                refused = exception;
            }
        }

        Assert.NotNull(@object: refused);
        Assert.Contains(expectedSubstring: library, actualString: refused.Message);
    }
    [Fact]
    public void AToolchainWithNoDxcHasAnIdentityOfItsOwn() {
        using var scratch = new TemporaryDirectory(prefix: "puck-toolchain-");
        var empty = Path.Combine(path1: scratch.RootPath, path2: "empty");

        Directory.CreateDirectory(path: empty);

        var absent = new ShaderToolchain(directory: empty).Identity;

        Assert.Equal(expected: absent, actual: new ShaderToolchain(directory: Path.Combine(path1: scratch.RootPath, path2: "missing")).Identity);
        Assert.NotEqual(expected: absent, actual: IdentityOf(compiler: 6, root: Path.Combine(path1: scratch.RootPath, path2: "one"), validator: 7, written: DateTime.UtcNow));
    }

    private static string CompilerName => (OperatingSystem.IsWindows() ? "dxcompiler.dll" : (OperatingSystem.IsMacOS() ? "libdxcompiler.dylib" : "libdxcompiler.so"));
    private static string DxcName => (OperatingSystem.IsWindows() ? "dxc.exe" : "dxc");
    private static string ValidatorName => (OperatingSystem.IsWindows() ? "dxil.dll" : (OperatingSystem.IsMacOS() ? "libdxil.dylib" : "libdxil.so"));

    private static string IdentityOf(string root, byte compiler, byte validator, DateTime written) =>
        new ShaderToolchain(directory: Install(compiler: compiler, root: root, validator: validator, written: written)).Identity;
    // A DXC release's layout: the libraries beside the executable on Windows, in a sibling lib directory elsewhere.
    private static string LibrariesOf(string bin) => (OperatingSystem.IsWindows() ? bin : Path.Combine(path1: bin, path2: "..", path3: "lib"));
    private static string Install(string root, byte compiler, byte validator, DateTime written) {
        var bin = Path.Combine(path1: root, path2: "bin");
        var libraries = LibrariesOf(bin: bin);

        Directory.CreateDirectory(path: bin);
        Directory.CreateDirectory(path: libraries);
        foreach (var (path, content) in ((ReadOnlySpan<(string, byte[])>)[
            (Path.Combine(path1: bin, path2: DxcName), [1, 2, 3]),
            (Path.Combine(path1: libraries, path2: CompilerName), [4, 5, compiler]),
            (Path.Combine(path1: libraries, path2: ValidatorName), [8, validator]),
        ])) {
            File.WriteAllBytes(bytes: content, path: path);
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: written, path: path);
        }

        return bin;
    }
}
