using System.Text.RegularExpressions;
using Puck.Abstractions.Counting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfKernelSet"/>: a load reads one file per kernel, named by its stem, and counts once into the
/// <c>shaders.sdf-kernels</c> counts it was handed, with exactly the bytes it read; the kernels it returns are those
/// files' bytes; a load that finds a kernel missing still counts as a load that started; both kinds are classified as
/// deterministic within one backend, since SPIR-V and DXIL sets differ in size; changing any one kernel changes the
/// set's content key, which keys a host's persistent pipeline cache; every kernel the build compiles reads its host's
/// interface on both backends, stamp included; and the stamp moves with the instruction set.
/// </summary>
public sealed class SdfKernelSetLawTests {
    private static readonly string[] Stems = [
        "sdf-world-ambient", "sdf-beam", "sdf-tape", "sdf-brick-bake", "sdf-cull-args",
        "sdf-instance-cull", "sdf-world-primary", "sdf-world-shadow", "sdf-sky-runs", "sdf-world-surface", "sdf-world-views",
        "sdf-world-views-core", "sdf-world-views-folds", "sdf-resolve", "sdf-composite",
        "sdf-sky-environment", "sdf-sky-environment-reduce", "sdf-screen-emission",
        "sdf-indirect-classify", "sdf-indirect-trace", "sdf-indirect-shade", "sdf-light-primary", "sdf-light-depth",
        "sdf-world-receiver", "sdf-world-receiver-comparison",
    ];

    private static WorkCounterSet Work() =>
        new(
            kinds: [SdfKernelSet.Loads, SdfKernelSet.BytecodeBytes],
            name: SdfKernelSet.LoadWorkSourceName
        );

    [Fact]
    public void ALoadCountsOnceWithEveryByteItRead() {
        using var scratch = new TemporaryDirectory(prefix: "puck-test-");
        var directory = scratch.RootPath;

        var expected = 0L;

        for (var index = 0; (index < Stems.Length); index++) {
            var bytes = Enumerable.Repeat(count: (index + 1), element: ((byte)index)).ToArray();

            File.WriteAllBytes(
                bytes: bytes,
                path: Path.Combine(
                    path1: directory,
                    path2: $"{Stems[index]}.comp.spv"
                )
            );
            expected += bytes.Length;
        }

        var work = Work();
        var kernels = SdfKernelSet.Load(
            bytecodeExtension: ".spv",
            directory: directory,
            work: work
        );

        Assert.Equal(expected: 1L, actual: work.Read(kind: SdfKernelSet.Loads));
        Assert.Equal(expected: expected, actual: work.Read(kind: SdfKernelSet.BytecodeBytes));
        // Passed through: each kernel is its own file's bytes.
        Assert.Equal(expected: new byte[] { 0 }, actual: kernels[SdfKernel.Ambient].ToArray());
        Assert.Equal(expected: Enumerable.Repeat(count: 13, element: ((byte)12)), actual: kernels[SdfKernel.ViewsFolds].ToArray());

        _ = SdfKernelSet.Load(
            bytecodeExtension: ".spv",
            directory: directory,
            work: work
        );

        Assert.Equal(expected: 2L, actual: work.Read(kind: SdfKernelSet.Loads));
        Assert.Equal(expected: (2L * expected), actual: work.Read(kind: SdfKernelSet.BytecodeBytes));
    }
    [Fact]
    public void ALoadThatFindsAKernelMissingStillCountsAsALoad() {
        using var scratch = new TemporaryDirectory(prefix: "puck-test-");
        var directory = scratch.RootPath;

        var work = Work();

        _ = Assert.ThrowsAny<IOException>(testCode: () => SdfKernelSet.Load(
            bytecodeExtension: ".spv",
            directory: directory,
            work: work
        ));
        Assert.Equal(expected: 1L, actual: work.Read(kind: SdfKernelSet.Loads));
        Assert.Equal(expected: 0L, actual: work.Read(kind: SdfKernelSet.BytecodeBytes));
    }
    [Fact]
    public void TheProcessSourceIsNamedAndClassified() {
        Assert.Equal(expected: "shaders.sdf-kernels", actual: SdfKernelSet.LoadWork.Name);
        Assert.Equal(
            expected: ["shaders.sdf-kernels.loads", "shaders.sdf-kernels.bytecode-bytes"],
            actual: SdfKernelSet.LoadWork.WorkKinds.ToArray().Select(selector: static kind => kind.Name)
        );
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: SdfKernelSet.Loads.Class);
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: SdfKernelSet.BytecodeBytes.Class);
    }
    [Fact]
    public void ChangingAnyOneKernelChangesTheContentKey() {
        var baseline = SdfTestPipelines.Kernels();
        var keys = SdfKernelSet.Kernels
            .Select(selector: kernel => baseline.With(bytecode: new byte[] { 2 }, kernel: kernel).ContentKey())
            .ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(expected: SdfKernelSet.Kernels.Count, actual: keys.Count);
        Assert.DoesNotContain(collection: keys, expected: baseline.ContentKey());
    }
    // What the build compiles is what a host installs: every kernel reflects, on both backends, exactly the bindings its
    // interface places, and its pass block under the name this host's instruction-set stamp gives it.
    [Fact]
    public void EveryCompiledKernelReadsItsHostsInterfaceOnBothBackends() {
        using var reflector = SdfTestPipelines.Reflector();

        foreach (var kernel in SdfKernelSet.Kernels) {
            var stem = SdfKernelSet.StemOf(kernel: kernel);

            foreach (var extension in (OperatingSystem.IsWindows() ? [".spv", ".dxil"] : new[] { ".spv" })) {
                var reflected = reflector.Read(bytecode: File.ReadAllBytes(path: Path.Combine(
                    path1: SdfKernelSet.DefaultDirectory,
                    path2: $"{stem}.comp{extension}"
                )));

                Assert.Null(@object: SdfKernelSet.InterfaceMismatch(kernel: kernel, reflected: reflected));
                Assert.Contains(
                    collection: reflected,
                    filter: static binding => (binding.Name == ("passGroup" + SdfWorldInterfaces.Stamp))
                );
            }
        }
    }
    // Both interfaces the kernels read carry this host's instruction set in their pass blocks' variable names.
    [Fact]
    public void TheKernelInterfacesCarryTheHostsInstructionSetStamp() {
        foreach (var layout in ((ReadOnlySpan<ShaderInterfaceLayout>)[SdfWorldInterfaces.WorldLayout, SdfWorldInterfaces.BrickBakeLayout, SdfWorldInterfaces.ResolveParameters.Layout, SdfWorldInterfaces.IndirectParameters.Layout])) {
            Assert.Equal(expected: SdfWorldInterfaces.Stamp, actual: layout.Interface.Stamp);
            Assert.Equal(
                expected: ("passGroup" + SdfWorldInterfaces.Stamp),
                actual: layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)).BlockVariableName
            );
        }

        // The recorded fingerprint is the model's: the host names the instruction set its kernels were generated against.
        Assert.Equal(expected: SdfIsaFingerprint.Value, actual: SdfIsaHlsl.DescribeFingerprint());
        Assert.Equal(expected: SdfIsaHlsl.StampOf(fingerprint: SdfIsaFingerprint.Value), actual: SdfWorldInterfaces.Stamp);
    }
    // Kernels compiled against another instruction set carry another stamp, so no include beside them can make them pass
    // for this host's: an instruction set whose two opcodes, or whose two cell modes, trade values; one whose instruction
    // headers carry the shape and the blend in each other's lanes; and one whose builder or packer puts a field
    // elsewhere (a rotation's Y and W, a sampled region's Y and Z dimension bitfields, a weathering's Edge and Lines, a
    // sweep's start and end radii, a cell displacement's frequency and amplitude, a stroked path's start and end radii, a
    // rigid leaf's rotation X and Y, a path edge's two radii).
    [Fact]
    public void AnInstructionSetEncodedOtherwiseCarriesAnotherStamp() {
        var include = SdfIsaHlsl.Generate();
        var encoding = SdfEncodingProbe.Describe();
        var calls = SdfEncodingProbe.Calls();

        foreach (var (otherInclude, otherEncoding) in ((ReadOnlySpan<(string, string)>)[
            (Swapped(first: "SDF_OP_TRANSLATE", include: include, second: "SDF_OP_ROTATE"), encoding),
            (Swapped(first: "SDF_CELL_MODE_F1", include: include, second: "SDF_CELL_MODE_F2_MINUS_F1"), encoding),
            (Swapped(first: "SDF_INSTRUCTION_SHAPE(v)", include: include, second: "SDF_INSTRUCTION_BLEND(v)"), encoding),
        ])) {
            Assert.NotEqual(expected: SdfWorldInterfaces.Stamp, actual: SdfIsaHlsl.StampOf(fingerprint: SdfIsaHlsl.FingerprintOf(encoding: otherEncoding, include: otherInclude)));
        }
        foreach (var other in SdfEncodingTrades.Traded(calls: calls)) {
            var otherEncoding = SdfEncodingProbe.Describe(calls: [.. calls.Select(selector: call => ((call.Name == other.Name) ? other : call))]);

            Assert.NotEqual(expected: SdfWorldInterfaces.Stamp, actual: SdfIsaHlsl.StampOf(fingerprint: SdfIsaHlsl.FingerprintOf(encoding: otherEncoding, include: include)));
        }
        // A packer that writes a rigid leaf's rotation X and Y, or a stroked path edge's two radii, in each other's places.
        foreach (var wordsOf in ((ReadOnlySpan<Func<SdfProgram, uint[]>>)[SdfEncodingTrades.RigidLeafRotationXySwapped, SdfEncodingTrades.PathRadiiSwapped])) {
            Assert.NotEqual(expected: SdfWorldInterfaces.Stamp, actual: SdfIsaHlsl.StampOf(fingerprint: SdfIsaHlsl.FingerprintOf(encoding: SdfEncodingProbe.Describe(calls: calls, wordsOf: wordsOf), include: include)));
        }
    }

    // The include with two defines' values traded.
    private static string Swapped(string include, string first, string second) {
        var define = new Regex(options: RegexOptions.Multiline, pattern: $@"^(#define (?:{Regex.Escape(str: first)}|{Regex.Escape(str: second)}) +)(.+)$");
        var values = define.Matches(input: include).ToDictionary(elementSelector: static match => match.Groups[2].Value, keySelector: static match => match.Groups[1].Value.TrimEnd().Split(separator: ' ')[1]);
        var swapped = define.Replace(
            evaluator: match => (match.Groups[1].Value + values[((match.Groups[1].Value.TrimEnd().Split(separator: ' ')[1] == first) ? second : first)]),
            input: include
        );

        Assert.Equal(expected: 2, actual: values.Count);
        Assert.NotEqual(actual: swapped, expected: include);

        return swapped;
    }
}
