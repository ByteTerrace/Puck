using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for a specialization's provenance: a set compiles exactly the source its running interpreter was compiled from
/// (<see cref="SdfKernelSet.SourceOf"/>), never the tree's files as they stand when it compiles, and publishes under an
/// identity that names that source (<see cref="SdfShadeKernels.InterpreterOf"/>).
/// </summary>
public sealed class SdfShadeKernelsSourceLawTests {
    private const string Helper = "float shadeHelper() { return 1.0; }\n";
    private const string EditedHelper = "float shadeHelper() { return 2.0; }\n";

    [Fact]
    public void AHelperEditAfterTheKernelsWereTakenNeitherCompilesNorKeepsTheirIdentity() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shade-provenance-");
        var helper = WriteTree(root: scratch.RootPath);
        var running = KernelsOf(directory: SdfKernelSet.PassesDirectory(tree: scratch.RootPath));

        File.WriteAllText(path: helper, contents: EditedHelper);

        var compiler = new ShaderCompiler(cacheDirectory: Path.Combine(path1: scratch.RootPath, path2: "cache"));
        var plans = SdfShadeKernels.Plan(
            compiler: compiler,
            programs: [],
            sources: [.. SdfShadeKernels.Specialized.Select(selector: kernel => running.SourceOf(kernel: kernel)!)],
            target: ShaderTarget.Dxil
        );

        Assert.Equal(expected: SdfShadeKernels.Specialized, actual: plans.Select(selector: static plan => plan.Kernel));
        foreach (var (_, plan) in plans) {
            Assert.Equal(expected: Helper, actual: plan.Contents[Path.GetFullPath(path: helper)]);
            Assert.Contains(collection: plan.Includes, filter: include => (include.Path == Path.GetFullPath(path: helper)) && (include.ContentHash == ShaderSourceClosure.HashOf(text: Helper)));
        }

        // The same bytecode with the edited tree's sources is another interpreter, so no set of the first publishes over it.
        var edited = KernelsOf(directory: SdfKernelSet.PassesDirectory(tree: scratch.RootPath));

        Assert.Equal(expected: running.ContentKey(), actual: edited.ContentKey());
        Assert.NotEqual(expected: SdfShadeKernels.InterpreterOf(kernels: running), actual: SdfShadeKernels.InterpreterOf(kernels: edited));
        Assert.NotEqual(expected: new SdfShadeKernels(kernels: running, programs: []).Identity, actual: new SdfShadeKernels(kernels: edited, programs: []).Identity);
    }
    [Fact]
    public async Task ReplacingAKernelsBytecodeReplacesItsSourceAndAnUnknownSourceRefusesTheSet() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shade-provenance-");
        _ = WriteTree(root: scratch.RootPath);
        var running = KernelsOf(directory: SdfKernelSet.PassesDirectory(tree: scratch.RootPath));
        var bytecodeOnly = running.With(bytecode: new byte[] { 0xAA }, kernel: SdfKernel.Views);

        Assert.NotNull(running.SourceOf(kernel: SdfKernel.Views));
        Assert.Null(bytecodeOnly.SourceOf(kernel: SdfKernel.Views));
        Assert.Same(expected: running.SourceOf(kernel: SdfKernel.Glass), actual: bytecodeOnly.SourceOf(kernel: SdfKernel.Glass));
        Assert.Null(running.SourceOf(kernel: SdfKernel.Surface));

        var request = new SdfShadeKernels(kernels: bytecodeOnly, programs: []);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => request.CompileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            compiler: new ShaderCompiler(cacheDirectory: Path.Combine(path1: scratch.RootPath, path2: "cache")),
            target: ShaderTarget.Dxil
        ));

        Assert.Contains(expectedSubstring: SdfKernelSet.StemOf(kernel: SdfKernel.Views), actualString: refusal.Message);
    }

    [Fact]
    public void TheDeployedKernelsCarryTheSourcesASpecializationPlansFrom() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shade-provenance-");
        var deployed = SdfKernelSet.Load(bytecodeExtension: ".spv");
        var sources = SdfShadeKernels.Specialized.Select(selector: kernel => deployed.SourceOf(kernel: kernel)).ToArray();

        Assert.All(collection: sources, action: source => Assert.NotNull(source));

        // The generated include resolves inside each real kernel's closure, which only the taken source supplies.
        var plans = SdfShadeKernels.Plan(
            compiler: new ShaderCompiler(cacheDirectory: Path.Combine(path1: scratch.RootPath, path2: "cache")),
            programs: [],
            sources: [.. sources.Select(selector: static source => source!)],
            target: ShaderTarget.Spirv
        );

        Assert.Equal(expected: SdfShadeKernels.Specialized.Count, actual: plans.Count);
        Assert.Equal(expected: SdfShadeKernels.InterpreterOf(kernels: deployed), actual: new SdfShadeKernels(kernels: deployed, programs: []).Interpreter);
    }

    // A kernel tree whose specialized entry points include the shade programs and one shared helper.
    private static string WriteTree(string root) {
        var passes = Directory.CreateDirectory(path: SdfKernelSet.PassesDirectory(tree: root)).FullName;
        var isa = Directory.CreateDirectory(path: Path.Combine(path1: root, path2: "isa")).FullName;
        var shade = Directory.CreateDirectory(path: Path.Combine(path1: root, path2: "shade")).FullName;
        var helper = Path.Combine(path1: shade, path2: "helper.hlsli");

        File.WriteAllText(path: Path.Combine(path1: isa, path2: SdfShadeHlsl.FileName), contents: "// the interpreter\n");
        File.WriteAllText(path: helper, contents: Helper);
        foreach (var kernel in SdfShadeKernels.Specialized) {
            File.WriteAllText(
                path: Path.Combine(path1: passes, path2: $"{SdfKernelSet.StemOf(kernel: kernel)}.comp.hlsl"),
                contents: "#include \"../shade/helper.hlsli\"\n#include \"../isa/sdf-shade.hlsli\"\n[numthreads(1, 1, 1)] void CSMain() { }\n"
            );
        }

        return helper;
    }
    // The running set as a load takes it: placeholder bytecode, and each specialized kernel's source read beside it.
    private static SdfKernelSet KernelsOf(string directory) => new(
        bytecode: [.. SdfKernelSet.Kernels.Select(selector: static kernel => new ReadOnlyMemory<byte>(array: [((byte)kernel)]))],
        sources: [.. SdfKernelSet.Kernels.Select(selector: kernel => (SdfShadeKernels.Specialized.Contains(value: kernel) ? SdfKernelSource.Read(directory: directory, kernel: kernel) : null))]
    );
}
